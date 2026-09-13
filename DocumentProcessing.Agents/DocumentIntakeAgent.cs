using DocumentProcessing.Agents.Contracts;
using DocumentProcessing.Core.Exceptions;
using DocumentProcessing.Core.Models;
using DocumentProcessing.Core.Tools;
using Microsoft.Extensions.Logging;

namespace DocumentProcessing.Agents;

public sealed class DocumentIntakeAgent(ISqlTool sqlTool, ILogger<DocumentIntakeAgent> logger)
{
    public async Task<IntakeResult> AcceptAsync(DocumentUploadCommand cmd, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        Validate(cmd);

        var docId = Guid.NewGuid();
        await sqlTool.SaveDocumentMetadataAsync(
            docId,
            cmd.CustomerId.Trim(),
            cmd.DocumentType,
            cmd.FileName.Trim(),
            cmd.FileContentBase64.Trim(),
            WorkflowStatus.Received,
            ct);

        logger.LogInformation("Intake stored {DocId} for customer {CustomerId} as RECEIVED", docId, cmd.CustomerId);
        return new IntakeResult(docId, WorkflowStatus.Received);
    }

    private static void Validate(DocumentUploadCommand cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.CustomerId))
            throw new InvalidDocumentPayloadException("CustomerId is required.");

        if (cmd.CustomerId.Trim().Length > 50)
            throw new InvalidDocumentPayloadException("CustomerId exceeds 50 characters.");

        if (string.IsNullOrWhiteSpace(cmd.FileName))
            throw new InvalidDocumentPayloadException("FileName is required.");

        if (string.IsNullOrWhiteSpace(cmd.FileContentBase64))
            throw new InvalidDocumentPayloadException("FileContentBase64 is required.");

        try
        {
            Convert.FromBase64String(cmd.FileContentBase64.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidDocumentPayloadException("FileContentBase64 is not valid base64.");
        }
    }
}
