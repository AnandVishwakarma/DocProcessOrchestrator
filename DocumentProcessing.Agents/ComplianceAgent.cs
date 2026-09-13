using DocumentProcessing.Agents.Contracts;
using DocumentProcessing.Core.Models;
using DocumentProcessing.Core.Tools;
using Microsoft.Extensions.Logging;

namespace DocumentProcessing.Agents;

public sealed class ComplianceAgent(
    ISqlTool sqlTool,
    IComplianceRulesTool rulesTool,
    ILogger<ComplianceAgent> logger)
{
    public async Task<ComplianceStepResult> EvaluateAsync(
        Guid docId,
        DocumentType documentType,
        string extractedText,
        CancellationToken ct = default)
    {
        await sqlTool.UpdateWorkflowStatusAsync(docId, WorkflowStatus.ComplianceInProgress, "Compliance review started.", ct);

        var rules = await rulesTool.GetRulesAsync(documentType, ct);
        var evaluation = rulesTool.Evaluate(documentType, extractedText, rules);

        if (evaluation.IsApproved)
        {
            const string remark = "Compliance approved. Extracted text satisfied RegexPattern and ExpectedLength.";
            await sqlTool.UpdateWorkflowStatusAsync(docId, WorkflowStatus.Approved, remark, ct);
            logger.LogInformation("Compliance approved {DocId}", docId);
            return new ComplianceStepResult(docId, WorkflowStatus.Approved, [remark]);
        }

        var remarks = evaluation.Violations.Count > 0
            ? evaluation.Violations
            : ["Extracted text did not satisfy compliance rules."];
        var message = $"Compliance rejected: {string.Join("; ", remarks)}";

        await sqlTool.UpdateWorkflowStatusAsync(docId, WorkflowStatus.Rejected, message, ct);
        logger.LogInformation("Compliance rejected {DocId}: {Message}", docId, message);
        return new ComplianceStepResult(docId, WorkflowStatus.Rejected, remarks);
    }
}
