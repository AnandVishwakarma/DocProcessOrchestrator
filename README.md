# Document Processing Orchestration

ASP.NET Core 8 service that accepts customer KYC artefacts (PAN, Aadhaar, Letter of Authorization), runs them through a coordinator-owned workflow, and persists every state change in SQL Server (`imagema1_DocDB`).

Solution layout:

| Project | Responsibility |
| --- | --- |
| `DocumentProcessing.Api` | HTTP surface, request validation, Swagger |
| `DocumentProcessing.Agents` | `CoordinatorAgent` and the three specialist agents |
| `DocumentProcessing.Infrastructure` | Dapper tools, Polly pipelines, SQL Server access |
| `DocumentProcessing.Core` | Domain models, tool contracts, exceptions |

The API is synchronous per request: `POST /api/documents/upload` runs intake → OCR → compliance in one call and returns the terminal (or compensated) status.

---

## 1. System Architecture & Agentic Flow

Control is **orchestrator-driven**. Sub-agents do not call each other, do not negotiate a plan, and do not own the next hop. `CoordinatorAgent` is the only component that decides what runs next, and it does so from persisted `WorkflowStatus`, not from an LLM planner.

```
HTTP (DocumentsController)
        │
        ▼
CoordinatorAgent
        │  direct handoff (in-process method call)
        ├──► DocumentIntakeAgent  ──► SqlTool
        ├──► OcrAgent             ──► OcrTool, SqlTool
        └──► ComplianceAgent      ──► ComplianceRulesTool, SqlTool
```

### Tiers

**Coordinator.** `ProcessUploadAsync` hands the payload to intake, then `AdvanceAsync` walks the remaining pipeline. `ResumeAsync` reloads the document row and continues from the last non-terminal status. `GetStatusAsync` is a read-only snapshot for the status API.

**Specialized sub-agents.** Each agent does one step and writes its own status transitions through tools:

- `DocumentIntakeAgent` — validates `CustomerId` (required, ≤ 50 chars), `FileName`, and `FileContentBase64` (must decode as Base64). Allocates a `Guid` document id and inserts metadata as `RECEIVED`.
- `OcrAgent` — stamps `OCR_IN_PROGRESS`, calls `OcrTool.ExtractTextAsync`, persists text via `sp_SaveOCRText`, then stamps `OCR_Success`. On timeout or persistence failure it writes `OCR_FAILED` and rethrows so the coordinator can compensate.
- `ComplianceAgent` — stamps `COMPLIANCE_IN_PROGRESS`, loads rules, evaluates OCR text, then writes `APPROVED` or `REJECTED`.

**Encapsulated tools.** Agents never open `SqlConnection` themselves.

- `SqlTool` — `sp_SaveDocument`, `sp_UpdateWorkflowStatus`, and the status/replay SELECT against `Documents` / `DocumentWorkflowStatus`.
- `OcrTool` — simulated extraction plus `sp_SaveOCRText`.
- `ComplianceRulesTool` — `sp_GetComplianceRules` plus in-memory regex / length evaluation (built-in PAN / Aadhaar / LOA rules if the table returns no rows).

### Lifecycle

Persisted tokens are exactly what `WorkflowStatus.ToDbValue()` writes:

```
RECEIVED
    → OCR_IN_PROGRESS
        → OCR_Success
            → COMPLIANCE_IN_PROGRESS
                → APPROVED
                → REJECTED
        → OCR_FAILED
            → COMPENSATED   (coordinator, after the OCR exception is rethrown)
```

`COMPLIANCE_IN_PROGRESS` is the compliance-check-in-progress state. Terminal states are `APPROVED`, `REJECTED`, and `COMPENSATED`. `AdvanceAsync` returns immediately if the loaded status is already terminal.

### Plans and direct handoffs

There is no separate planner artefact. The plan is the status machine in `CoordinatorAgent`:

- `NeedsOcr` = `RECEIVED` | `OCR_IN_PROGRESS` | `OCR_FAILED` → hand off to `OcrAgent.ExtractAsync`.
- `NeedsCompliance` = `OCR_Success` | `COMPLIANCE_IN_PROGRESS` → hand off to `ComplianceAgent.EvaluateAsync`.

A “handoff” is a direct C# call after the previous step has committed its status row. If OCR throws `AgentTimeoutException` or `PersistenceException`, the coordinator does **not** continue to compliance; it runs compensation. That keeps the graph linear and auditable: the database, not an in-memory conversation, is the source of truth for where the flow stopped.

---

## 2. Database Setup & Persistence (SQL Server)

Database: **`imagema1_DocDB`**.

The application does not create schema. Tables and procedures are expected to already exist. Configure the connection under `ConnectionStrings:DocDb` (see §5).

### Tables

**`Documents`** — current document record. Columns used by the service: `DocumentId`, `CustomerId`, `DocumentType` (`PAN` | `AADHAR` | `LOA`), `FileName`, `FileContentBase64`, `ExtractedText`, `CurrentStatus`, `UploadedOn`.

**`DocumentWorkflowStatus`** — append-only audit trail. Each workflow mutation inserts a history row (`DocumentId`, `Status` equivalent, `Message`, `Timestamp`, `Id`). Status reads take the latest row (`ORDER BY Timestamp DESC, Id DESC`) and surface its `Message` / `Timestamp` alongside `Documents.CurrentStatus`. This is the audit trail for who/what moved the document and why (intake, OCR start/complete/fail, compliance remark, compensation reason). Message text is truncated to 1,000 characters before `sp_UpdateWorkflowStatus`.

**`ComplianceRules`** — per-document-type rules: `RuleId`, `DocumentType`, `RegexPattern`, `ExpectedLength`, `Description`. Loaded by `sp_GetComplianceRules`. If the procedure returns an empty set, `ComplianceRulesTool` falls back to built-in checks (PAN `^[A-Z]{5}[0-9]{4}[A-Z]{1}$` length 10; Aadhaar `^[0-9]{12}$` length 12; LOA substring `AUTHORIZATION`).

### Stored procedures

All **writes** go through Dapper `CommandType.StoredProcedure`. There is no inline `INSERT`/`UPDATE` from the application.

| Procedure | Called from | Parameters |
| --- | --- | --- |
| `sp_SaveDocument` | `SqlTool.SaveDocumentMetadataAsync` | `@DocumentId`, `@CustomerId`, `@DocumentType`, `@FileName`, `@FileContentBase64`, `@Status` |
| `sp_UpdateWorkflowStatus` | `SqlTool.UpdateWorkflowStatusAsync` | `@DocumentId`, `@Status`, `@Message` |
| `sp_SaveOCRText` | `OcrTool.SaveOcrTextAsync` | `@DocumentId`, `@ExtractedText` |
| `sp_GetComplianceRules` | `ComplianceRulesTool.GetRulesAsync` | `@DocumentType` |

`GetDocumentAsync` is a **read** (parameterized `SELECT` + `OUTER APPLY` for the latest audit row). It is not a mutation and is not a stored procedure.

Dapper `CommandTimeout` uses `DocDb:CommandTimeoutSeconds` (default 15).

---

## 3. Resilience, Timeouts & Compensation

Polly v8 `ResiliencePipeline` is built per tool. Timeout is registered **inside** retry, so each attempt is capped independently.

### SQL tools (`SqlTool`, `ComplianceRulesTool`, `OcrTool` persist path)

- Per-attempt timeout: `DocDb:CommandTimeoutSeconds` (default **15s**), mapped to `AgentTimeoutException` via `TimeoutRejectedException`.
- Retry: **3** retries, base delay **250 ms**, exponential backoff, jitter enabled.
- Retry predicate: `TimeoutException`, and `SqlException` numbers treated as transient in `SqlTransient` — including deadlock **1205**, client/command timeout **-2**, transport breaks **20 / 64 / 233 / 10053 / 10054 / 10060**, and common Azure SQL throttle / failover codes (**10928, 10929, 40143, 40197, 40501, 40540, 40613, 40615, 49918–49920**).
- After retries are exhausted, a remaining `SqlException` is wrapped as `PersistenceException` (procedure name included).

### OCR extraction (`OcrTool.ExtractTextAsync`)

Simulation sleeps 1–3 seconds, then either decodes printable UTF-8 from `fileContentBase64` or returns a canned template for the document type.

- Per-attempt timeout: `DocDb:OcrTimeoutSeconds` (default **5s**).
- Retry: **2** retries, base delay **200 ms**, exponential + jitter, on `TimeoutRejectedException` / `TimeoutException` only.
- Exhaustion → `AgentTimeoutException` (`OcrTool`). `OcrAgent` writes `OCR_FAILED` and rethrows.

### Compensation

`CoordinatorAgent.AdvanceAsync` catches `AgentTimeoutException`, `PersistenceException`, and `ComplianceValidationException` (invalid `RegexPattern` in a loaded rule). Those are the non-recoverable pipeline faults after tool-level retries have already run.

`CompensateAsync` then:

1. Builds a reason string (tool name / procedure name + exception message).
2. Calls `sp_UpdateWorkflowStatus` with `COMPENSATED`.
3. Logs a warning. If the compensation write itself fails, that is logged as an error and the last successfully persisted status (often `OCR_FAILED` or `COMPLIANCE_IN_PROGRESS`) remains; there is no distributed undo of `Documents`.

Compensation is a **state stamp**, not a SQL transaction rollback. Intake rows are kept; the audit trail records why processing stopped.

### Replay / resume

`CoordinatorAgent.ResumeAsync(documentId)`:

1. Loads the document (`GetDocumentAsync`). Missing id → `DocumentNotFoundException`.
2. Rebuilds `DocumentUploadCommand` from stored `CustomerId`, `DocumentType`, `FileName`, `FileContentBase64`.
3. Calls `AdvanceAsync` with current status and any stored `ExtractedText`.

Behaviour by status:

- Terminal (`APPROVED` / `REJECTED` / `COMPENSATED`) — no work; message `Workflow already completed.`
- `RECEIVED`, `OCR_IN_PROGRESS`, `OCR_FAILED` — OCR is run again.
- `OCR_Success`, `COMPLIANCE_IN_PROGRESS` — OCR is skipped; compliance is evaluated from stored text.

`ResumeAsync` is implemented on the coordinator but is **not** exposed as an HTTP endpoint. Call it from an operator/host process, or add a secured API later.

---

## 4. API Reference & Swagger Usage

Base path: `/api/documents`. Swagger UI is enabled when `ASPNETCORE_ENVIRONMENT=Development` (`/swagger`). Launch profiles open Swagger automatically (`http://localhost:5222/swagger` or `https://localhost:7168/swagger`).

JSON property names follow ASP.NET Core default camelCase.

### `POST /api/documents/upload`

Runs the full pipeline. **200** with the terminal (or compensated) result. **400** for invalid payload (`CustomerId` / `FileName` / Base64 / unknown `documentType`).

Request body:

| Field | Notes |
| --- | --- |
| `customerId` | Required, trim, max 50 characters |
| `documentType` | `PAN`, `AADHAR`, or `LOA` (case-insensitive) |
| `fileName` | Required |
| `fileContentBase64` | Required, valid Base64. If the decoded bytes are printable UTF-8, that text is what OCR “extracts”; otherwise the simulator emits a type-specific template. |

Response: `documentId`, `status`, `message`, `extractedText`.

#### Sample — expected `APPROVED` (PAN)

UTF-8 payload `ABCDE1234F` (structurally valid PAN: five letters, four digits, one letter). Base64: `QUJDREUxMjM0Rg==`.

```json
{
  "customerId": "CUST-10028471",
  "documentType": "PAN",
  "fileName": "pan_card_front.jpg",
  "fileContentBase64": "QUJDREUxMjM0Rg=="
}
```

Example **200**:

```json
{
  "documentId": "3f2a9c1e-8b44-4d21-9c7a-1a2b3c4d5e6f",
  "status": "APPROVED",
  "message": "Compliance approved. Extracted text satisfied RegexPattern and ExpectedLength.",
  "extractedText": "EXTRACTED_CONTENT:\nABCDE1234F\nSource: pan_card_front.jpg"
}
```

Aadhaar approve: `documentType` `AADHAR` and Base64 of a 12-digit string (e.g. `234567890123` → `MjM0NTY3ODkwMTIz`). LOA approve: printable text containing `AUTHORIZATION`.

#### Sample — expected `REJECTED` (PAN)

Printable text that does not match the PAN pattern / length 10.

```json
{
  "customerId": "CUST-10028471",
  "documentType": "PAN",
  "fileName": "pan_card_blurred.jpg",
  "fileContentBase64": "SU5WQUxJRFBBTk5VTUJFUg=="
}
```

(`INVALIDPANNUMBER`)

Example **200** (business rejection is not an HTTP error):

```json
{
  "documentId": "9c18e0aa-2d55-4b01-ae10-77c1d2e3f4a5",
  "status": "REJECTED",
  "message": "Standard 10-digit Indian Permanent Account Number",
  "extractedText": "EXTRACTED_CONTENT:\nINVALIDPANNUMBER\nSource: pan_card_blurred.jpg"
}
```

`message` is the joined compliance violation list from `ComplianceAgent`.

Do not send opaque image bytes if you need a **rejection**. Non-printable Base64 falls through to the canned OCR template (`ABCDE1234F` / `234567890123` / LOA authorization wording), which **passes** the built-in rules.

### `GET /api/documents/status/{documentId}`

**200** snapshot, **404** if the id is not in `Documents`.

```json
{
  "documentId": "3f2a9c1e-8b44-4d21-9c7a-1a2b3c4d5e6f",
  "status": "APPROVED",
  "message": "Compliance approved. Extracted text satisfied RegexPattern and ExpectedLength.",
  "extractedText": "EXTRACTED_CONTENT:\nABCDE1234F\nSource: pan_card_front.jpg",
  "updatedUtc": "2026-09-14T09:01:22.123"
}
```

`updatedUtc` is the latest `DocumentWorkflowStatus.Timestamp`, or `UploadedOn` if no audit row is present.

| `status` | Meaning |
| --- | --- |
| `RECEIVED` | Intake committed; OCR not started (only visible if the process died between insert and OCR, or on a paused resume). |
| `OCR_IN_PROGRESS` | OCR started; extraction or `sp_SaveOCRText` not finished. |
| `OCR_Success` | Text stored; compliance not started or not finished. |
| `OCR_FAILED` | OCR timed out or persist failed; compensation may still be pending if the `COMPENSATED` write failed. |
| `COMPLIANCE_IN_PROGRESS` | Rules loaded / evaluation in flight. |
| `APPROVED` | Terminal. Regex + `ExpectedLength` satisfied. |
| `REJECTED` | Terminal. Rule violation(s) recorded in `message`. |
| `COMPENSATED` | Terminal. Pipeline exception after retries; see `message` for tool/procedure. |

On a healthy `POST /upload`, the caller normally only observes `APPROVED`, `REJECTED`, or `COMPENSATED`. Intermediate values matter for `GET` during a crash window and for `ResumeAsync`.

---

## 5. Local Setup & Running

### Prerequisites

- .NET 8 SDK
- Visual Studio 2022 (or `dotnet run`)
- SQL Server + SSMS (or equivalent) with database **`imagema1_DocDB`**, the three tables, and the four stored procedures already deployed
- Network access to the SQL host used in the connection string

### Connection string

`DocumentProcessing.Api/appsettings.json`:

```json
{
  "ConnectionStrings": {
    "DocDb": "Server=<sql-host>;Database=imagema1_DocDB;User Id=<user>;Password=<password>;TrustServerCertificate=True;"
  },
  "DocDb": {
    "CommandTimeoutSeconds": 15,
    "OcrTimeoutSeconds": 5
  }
}
```

`DocDb:ConnectionString` is also bound if present; otherwise the host copies `ConnectionStrings:DocDb`. Do not commit production passwords. Prefer user secrets or environment variables (`ConnectionStrings__DocDb`) on shared machines.

Confirm in SSMS:

```sql
USE imagema1_DocDB;
SELECT name FROM sys.tables WHERE name IN (N'Documents', N'DocumentWorkflowStatus', N'ComplianceRules');
SELECT name FROM sys.procedures WHERE name IN (N'sp_SaveDocument', N'sp_UpdateWorkflowStatus', N'sp_GetComplianceRules', N'sp_SaveOCRText');
```

### Run and test via Swagger

1. Open `DocProcessOrchestrator.sln`. Set **DocumentProcessing.Api** as the startup project.
2. Run the `http` profile (`http://localhost:5222`) or `https` (`https://localhost:7168`).
3. Browser opens `/swagger`.
4. `POST /api/documents/upload` — paste one of the JSON bodies in §4. Copy `documentId` from the response.
5. `GET /api/documents/status/{documentId}` — confirm `status` / `message` / `extractedText` match the audit row in `DocumentWorkflowStatus`.

CLI equivalent:

```bash
dotnet run --project DocumentProcessing.Api --launch-profile http
```

---

## 6. Production Considerations & Known Limitations

**OCR is a simulator.** `OcrTool` waits 1–3 seconds and either echoes printable UTF-8 from the upload or returns a hard-coded PAN / Aadhaar / LOA snippet. It is not Azure AI Vision, Google Document AI, or an on-prem OCR engine. For a bank deployment, replace `IOcrTool` with a client that calls the chosen cloud endpoint, keep the existing timeout/retry pipeline, and store engine correlation ids on the audit message. Do not treat canned `ABCDE1234F` / `234567890123` output as evidence of document authenticity.

**Payload storage.** `FileContentBase64` is written into `Documents`. That is acceptable for lab-sized strings; it is a poor fit for multi-page scans (row size, backups, PII sprawl). Production should land files in object storage (or a document DMS) and persist a URI plus checksum.

**In-process orchestration.** One HTTP request owns the saga. That simplifies local review and makes compensation straightforward, but it does not scale horizontally as-is:

- A request timeout at the load balancer can leave `OCR_IN_PROGRESS` / `COMPLIANCE_IN_PROGRESS` without a client; recovery depends on `ResumeAsync`, which is not on the public API.
- Two app instances calling resume for the same id can double-run OCR/compliance. There is no lease, inbox, or `sp_UpdateWorkflowStatus` compare-and-swap on expected current status.
- For multiple nodes, persist commands to an **outbox** (or `DocumentWorkflowStatus` as a work queue) and dispatch via **Azure Service Bus** (or equivalent) with a single competing consumer per document id. The coordinator then becomes a message handler, not the HTTP thread.

**Reads vs writes.** Mutations are stored procedures; status fetch is inline SQL. If DB access must stay 100% procedural, wrap the SELECT in `sp_GetDocument` and point `GetDocumentAsync` at it.

**Swagger** is Development-only. Do not enable it on an internet-facing bank zone without authentication in front of the API.

**Compliance fallback.** Empty `ComplianceRules` does not fail closed; built-in regexes apply. Production should fail closed if `sp_GetComplianceRules` returns nothing for a mandated product type, and rules should be versioned.

**PII.** PAN, Aadhaar, and LOA text sit in `ExtractedText` and in logs if log level is too verbose. Apply SQL encryption / masking and restrict `DocumentWorkflowStatus` access before any non-lab use.
)
