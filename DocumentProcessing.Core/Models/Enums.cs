namespace DocumentProcessing.Core.Models;

public enum DocumentType
{
    Pan,
    Aadhar,
    Loa
}

public enum WorkflowStatus
{
    Received,
    OcrInProgress,
    OcrSuccess,
    OcrFailed,
    ComplianceInProgress,
    Approved,
    Rejected,
    Compensated
}

public static class DomainValueMappings
{
    public static string ToDbValue(this DocumentType documentType) => documentType switch
    {
        DocumentType.Pan => "PAN",
        DocumentType.Aadhar => "AADHAR",
        DocumentType.Loa => "LOA",
        _ => throw new ArgumentOutOfRangeException(nameof(documentType), documentType, "Unknown document type.")
    };

    public static string ToDbValue(this WorkflowStatus status) => status switch
    {
        WorkflowStatus.Received => "RECEIVED",
        WorkflowStatus.OcrInProgress => "OCR_IN_PROGRESS",
        WorkflowStatus.OcrSuccess => "OCR_Success",
        WorkflowStatus.OcrFailed => "OCR_FAILED",
        WorkflowStatus.ComplianceInProgress => "COMPLIANCE_IN_PROGRESS",
        WorkflowStatus.Approved => "APPROVED",
        WorkflowStatus.Rejected => "REJECTED",
        WorkflowStatus.Compensated => "COMPENSATED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown workflow status.")
    };

    public static DocumentType ParseDocumentType(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "PAN" => DocumentType.Pan,
        "AADHAR" => DocumentType.Aadhar,
        "LOA" => DocumentType.Loa,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown document type.")
    };

    public static WorkflowStatus ParseWorkflowStatus(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "RECEIVED" => WorkflowStatus.Received,
        "OCR_IN_PROGRESS" => WorkflowStatus.OcrInProgress,
        "OCR_SUCCESS" => WorkflowStatus.OcrSuccess,
        "OCR_COMPLETED" => WorkflowStatus.OcrSuccess,
        "OCR_FAILED" => WorkflowStatus.OcrFailed,
        "COMPLIANCE_IN_PROGRESS" => WorkflowStatus.ComplianceInProgress,
        "APPROVED" => WorkflowStatus.Approved,
        "REJECTED" => WorkflowStatus.Rejected,
        "COMPENSATED" => WorkflowStatus.Compensated,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown workflow status.")
    };
}
