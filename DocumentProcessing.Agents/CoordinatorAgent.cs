using DocumentProcessing.Agents.Contracts;
using DocumentProcessing.Core.Exceptions;
using DocumentProcessing.Core.Models;
using DocumentProcessing.Core.Tools;
using Microsoft.Extensions.Logging;

namespace DocumentProcessing.Agents;

public sealed class CoordinatorAgent(
    DocumentIntakeAgent intakeAgent,
    OcrAgent ocrAgent,
    ComplianceAgent complianceAgent,
    ISqlTool sqlTool,
    ILogger<CoordinatorAgent> logger)
{
    public async Task<WorkflowRunResult> ProcessUploadAsync(DocumentUploadCommand cmd, CancellationToken ct = default)
    {
        var intake = await intakeAgent.AcceptAsync(cmd, ct);
        return await AdvanceAsync(intake.DocumentId, cmd, extractedText: null, WorkflowStatus.Received, ct);
    }

    public async Task<WorkflowRunResult> ResumeAsync(Guid documentId, CancellationToken ct = default)
    {
        var doc = await sqlTool.GetDocumentAsync(documentId, ct)
            ?? throw new DocumentNotFoundException(documentId);

        var cmd = new DocumentUploadCommand(doc.CustomerId, doc.DocumentType, doc.FileName, doc.FileContentBase64);
        return await AdvanceAsync(doc.DocumentId, cmd, doc.ExtractedText, doc.Status, ct);
    }

    public async Task<DocumentStatusSnapshot> GetStatusAsync(Guid documentId, CancellationToken ct = default)
    {
        var doc = await sqlTool.GetDocumentAsync(documentId, ct)
            ?? throw new DocumentNotFoundException(documentId);

        return new DocumentStatusSnapshot
        {
            DocumentId = doc.DocumentId,
            Status = doc.Status,
            Message = doc.Message,
            ExtractedText = doc.ExtractedText,
            UpdatedUtc = doc.UpdatedUtc ?? doc.UploadedOn
        };
    }

    private async Task<WorkflowRunResult> AdvanceAsync(
        Guid docId,
        DocumentUploadCommand cmd,
        string? extractedText,
        WorkflowStatus status,
        CancellationToken ct)
    {
        if (IsTerminal(status))
        {
            return new WorkflowRunResult(docId, status, extractedText, "Workflow already completed.");
        }

        try
        {
            var text = extractedText;

            if (NeedsOcr(status))
            {
                var ocr = await ocrAgent.ExtractAsync(docId, cmd.DocumentType, cmd.FileName, cmd.FileContentBase64, ct);
                status = ocr.Status;
                text = ocr.ExtractedText;
            }

            if (NeedsCompliance(status))
            {
                var compliance = await complianceAgent.EvaluateAsync(docId, cmd.DocumentType, text ?? string.Empty, ct);
                return new WorkflowRunResult(docId, compliance.Status, text, string.Join("; ", compliance.Remarks));
            }

            return new WorkflowRunResult(docId, status, text, "Workflow advanced.");
        }
        catch (Exception ex) when (ex is AgentTimeoutException or PersistenceException or ComplianceValidationException)
        {
            await CompensateAsync(docId, ex, ct);
            return new WorkflowRunResult(docId, WorkflowStatus.Compensated, extractedText, ex.Message);
        }
    }

    private async Task CompensateAsync(Guid docId, Exception ex, CancellationToken ct)
    {
        var reason = ex switch
        {
            AgentTimeoutException timeout => $"Compensated after {timeout.ToolName} timeout: {timeout.Message}",
            PersistenceException persist => $"Compensated after {persist.Procedure} failure: {persist.Message}",
            ComplianceValidationException compliance => $"Compensated after invalid compliance configuration: {compliance.Message}",
            _ => $"Compensated after unexpected failure: {ex.Message}"
        };

        try
        {
            await sqlTool.UpdateWorkflowStatusAsync(docId, WorkflowStatus.Compensated, reason, ct);
            logger.LogWarning("Compensated {DocId}: {Reason}", docId, reason);
        }
        catch (Exception compensateEx)
        {
            logger.LogError(compensateEx, "Compensation write failed for {DocId}", docId);
        }
    }

    private static bool IsTerminal(WorkflowStatus status) =>
        status is WorkflowStatus.Approved or WorkflowStatus.Rejected or WorkflowStatus.Compensated;

    private static bool NeedsOcr(WorkflowStatus status) =>
        status is WorkflowStatus.Received or WorkflowStatus.OcrInProgress or WorkflowStatus.OcrFailed;

    private static bool NeedsCompliance(WorkflowStatus status) =>
        status is WorkflowStatus.OcrSuccess or WorkflowStatus.ComplianceInProgress;
}
