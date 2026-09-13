namespace DocumentProcessing.Core.Exceptions;

public sealed class AgentTimeoutException : Exception
{
    public string ToolName { get; }

    public AgentTimeoutException(string toolName, string message, Exception? inner = null)
        : base(message, inner)
    {
        ToolName = toolName;
    }
}

public sealed class ComplianceValidationException : Exception
{
    public IReadOnlyList<string> Violations { get; }

    public ComplianceValidationException(string message, IReadOnlyList<string>? violations = null)
        : base(message)
    {
        Violations = violations ?? [];
    }
}

public sealed class PersistenceException : Exception
{
    public string Procedure { get; }

    public PersistenceException(string procedure, string message, Exception? inner = null)
        : base(message, inner)
    {
        Procedure = procedure;
    }
}

public sealed class InvalidDocumentPayloadException : Exception
{
    public InvalidDocumentPayloadException(string message) : base(message)
    {
    }
}

public sealed class DocumentNotFoundException : Exception
{
    public Guid DocumentId { get; }

    public DocumentNotFoundException(Guid documentId)
        : base($"Document '{documentId}' was not found.")
    {
        DocumentId = documentId;
    }
}
