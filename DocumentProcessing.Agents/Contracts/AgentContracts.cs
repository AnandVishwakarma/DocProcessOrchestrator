using DocumentProcessing.Core.Models;

namespace DocumentProcessing.Agents.Contracts;

public sealed record DocumentUploadCommand(
    string CustomerId,
    DocumentType DocumentType,
    string FileName,
    string FileContentBase64);

public sealed record IntakeResult(Guid DocumentId, WorkflowStatus Status);

public sealed record OcrStepResult(Guid DocumentId, WorkflowStatus Status, string? ExtractedText);

public sealed record ComplianceStepResult(
    Guid DocumentId,
    WorkflowStatus Status,
    IReadOnlyList<string> Remarks);

public sealed record WorkflowRunResult(
    Guid DocumentId,
    WorkflowStatus Status,
    string? ExtractedText,
    string Message);
