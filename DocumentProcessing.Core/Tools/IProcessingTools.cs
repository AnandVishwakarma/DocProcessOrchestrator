using DocumentProcessing.Core.Models;

namespace DocumentProcessing.Core.Tools;

/// <summary>
/// Persistence tool for document intake, workflow transitions, and status reads against imagema1_DocDB.
/// </summary>
public interface ISqlTool
{
    /// <summary>Inserts metadata via <c>sp_SaveDocument</c> and stamps the supplied workflow status (typically RECEIVED).</summary>
    Task SaveDocumentMetadataAsync(
        Guid documentId,
        string customerId,
        DocumentType documentType,
        string fileName,
        string fileContentBase64,
        WorkflowStatus status,
        CancellationToken ct = default);

    /// <summary>Persists a workflow transition via <c>sp_UpdateWorkflowStatus</c> (<c>@DocumentId</c>, <c>@Status</c>, <c>@Message</c>).</summary>
    Task UpdateWorkflowStatusAsync(
        Guid documentId,
        WorkflowStatus status,
        string? message = null,
        CancellationToken ct = default);

    /// <summary>Loads the document row plus latest <c>DocumentWorkflowStatus</c> message for status and replay.</summary>
    Task<DocumentRecord?> GetDocumentAsync(Guid documentId, CancellationToken ct = default);
}

/// <summary>
/// Simulated OCR extraction with a bounded timeout, persisting text via <c>sp_SaveOCRText</c>.
/// </summary>
public interface IOcrTool
{
    /// <summary>Runs the 1–3s OCR simulation. Throws <see cref="Exceptions.AgentTimeoutException"/> on timeout.</summary>
    Task<string> ExtractTextAsync(
        DocumentType documentType,
        string fileName,
        string fileContentBase64,
        CancellationToken ct = default);

    /// <summary>Stores OCR output via <c>sp_SaveOCRText</c> (<c>@DocumentId</c>, <c>@ExtractedText</c>).</summary>
    Task SaveOcrTextAsync(Guid documentId, string extractedText, CancellationToken ct = default);
}

/// <summary>
/// Loads <c>ComplianceRules</c> via <c>sp_GetComplianceRules</c> and evaluates OCR text against <c>RegexPattern</c> / <c>ExpectedLength</c>.
/// </summary>
public interface IComplianceRulesTool
{
    /// <summary>Fetches rules for the document type.</summary>
    Task<IReadOnlyList<ComplianceRule>> GetRulesAsync(DocumentType documentType, CancellationToken ct = default);

    /// <summary>Applies rule patterns to OCR tokens. Built-in PAN (10) / AADHAR (12) checks apply when the rule set is empty.</summary>
    ComplianceEvaluation Evaluate(DocumentType documentType, string ocrText, IReadOnlyList<ComplianceRule> rules);
}
