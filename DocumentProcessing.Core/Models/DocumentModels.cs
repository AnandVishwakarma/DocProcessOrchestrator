namespace DocumentProcessing.Core.Models;

public sealed class DocumentRecord
{
    public Guid DocumentId { get; set; }
    public string CustomerId { get; set; } = string.Empty;
    public DocumentType DocumentType { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FileContentBase64 { get; set; } = string.Empty;
    public string? ExtractedText { get; set; }
    public WorkflowStatus Status { get; set; }
    public string? Message { get; set; }
    public DateTime UploadedOn { get; set; }
    public DateTime? UpdatedUtc { get; set; }
}

public sealed class DocumentStatusSnapshot
{
    public Guid DocumentId { get; set; }
    public WorkflowStatus Status { get; set; }
    public string? Message { get; set; }
    public string? ExtractedText { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class ComplianceRule
{
    public int RuleId { get; set; }
    public DocumentType DocumentType { get; set; }
    public string RegexPattern { get; set; } = string.Empty;
    public int ExpectedLength { get; set; }
    public string Description { get; set; } = string.Empty;
}

public sealed record ComplianceEvaluation(bool IsApproved, IReadOnlyList<string> Violations);
