using DocumentProcessing.Agents;
using DocumentProcessing.Agents.Contracts;
using DocumentProcessing.Core.Exceptions;
using DocumentProcessing.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace DocumentProcessing.Api.Controllers;

[ApiController]
[Route("api/documents")]
public sealed class DocumentsController(CoordinatorAgent coordinator) : ControllerBase
{
    [HttpPost("upload")]
    public async Task<ActionResult<UploadDocumentResponse>> Upload(
        [FromBody] UploadDocumentRequest request,
        CancellationToken ct)
    {
        try
        {
            var cmd = request.ToCommand();
            var result = await coordinator.ProcessUploadAsync(cmd, ct);
            return Ok(new UploadDocumentResponse(
                result.DocumentId,
                result.Status.ToDbValue(),
                result.Message,
                result.ExtractedText));
        }
        catch (InvalidDocumentPayloadException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("status/{documentId:guid}")]
    public async Task<ActionResult<DocumentStatusResponse>> GetStatus(Guid documentId, CancellationToken ct)
    {
        try
        {
            var snap = await coordinator.GetStatusAsync(documentId, ct);
            return Ok(new DocumentStatusResponse(
                snap.DocumentId,
                snap.Status.ToDbValue(),
                snap.Message,
                snap.ExtractedText,
                snap.UpdatedUtc));
        }
        catch (DocumentNotFoundException)
        {
            return NotFound(new { error = $"Document '{documentId}' was not found." });
        }
    }
}

public sealed class UploadDocumentRequest
{
    public string CustomerId { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileContentBase64 { get; set; } = string.Empty;

    public DocumentUploadCommand ToCommand() => new(
        CustomerId,
        DomainValueMappings.ParseDocumentType(DocumentType),
        FileName,
        FileContentBase64);
}

public sealed record UploadDocumentResponse(
    Guid DocumentId,
    string Status,
    string Message,
    string? ExtractedText);

public sealed record DocumentStatusResponse(
    Guid DocumentId,
    string Status,
    string? Message,
    string? ExtractedText,
    DateTime UpdatedUtc);
