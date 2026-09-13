using DocumentProcessing.Agents.Contracts;
using DocumentProcessing.Core.Exceptions;
using DocumentProcessing.Core.Models;
using DocumentProcessing.Core.Tools;
using Microsoft.Extensions.Logging;

namespace DocumentProcessing.Agents;

public sealed class OcrAgent(ISqlTool sqlTool, IOcrTool ocrTool, ILogger<OcrAgent> logger)
{
    public async Task<OcrStepResult> ExtractAsync(
        Guid docId,
        DocumentType documentType,
        string fileName,
        string fileContentBase64,
        CancellationToken ct = default)
    {
        await sqlTool.UpdateWorkflowStatusAsync(docId, WorkflowStatus.OcrInProgress, "OCR extraction started.", ct);

        try
        {
            var text = await ocrTool.ExtractTextAsync(documentType, fileName, fileContentBase64, ct);
            await ocrTool.SaveOcrTextAsync(docId, text, ct);
            await sqlTool.UpdateWorkflowStatusAsync(docId, WorkflowStatus.OcrSuccess, "OCR extraction completed.", ct);

            logger.LogInformation("OCR succeeded for {DocId}", docId);
            return new OcrStepResult(docId, WorkflowStatus.OcrSuccess, text);
        }
        catch (Exception ex) when (ex is AgentTimeoutException or PersistenceException)
        {
            await TryMarkFailedAsync(docId, ex.Message, ct);
            throw;
        }
    }

    private async Task TryMarkFailedAsync(Guid docId, string reason, CancellationToken ct)
    {
        try
        {
            await sqlTool.UpdateWorkflowStatusAsync(docId, WorkflowStatus.OcrFailed, $"OCR failed: {reason}", ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist OCR_FAILED for {DocId}", docId);
        }
    }
}
