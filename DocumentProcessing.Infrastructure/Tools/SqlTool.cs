using System.Data;
using Dapper;
using DocumentProcessing.Core.Exceptions;
using DocumentProcessing.Core.Models;
using DocumentProcessing.Core.Tools;
using DocumentProcessing.Infrastructure.Options;
using DocumentProcessing.Infrastructure.Resilience;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace DocumentProcessing.Infrastructure.Tools;

public sealed class SqlTool : ISqlTool
{
    private readonly string _connStr;
    private readonly int _cmdTimeout;
    private readonly ResiliencePipeline _pipeline;

    public SqlTool(IOptions<DocDbOptions> options, ILogger<SqlTool> logger)
    {
        var cfg = options.Value;
        _connStr = string.IsNullOrWhiteSpace(cfg.ConnectionString)
            ? throw new InvalidOperationException("DocDb connection string is not configured.")
            : cfg.ConnectionString;
        _cmdTimeout = cfg.CommandTimeoutSeconds;
        _pipeline = BuildPipeline(_cmdTimeout, logger);
    }

    public Task SaveDocumentMetadataAsync(
        Guid documentId,
        string customerId,
        DocumentType documentType,
        string fileName,
        string fileContentBase64,
        WorkflowStatus status,
        CancellationToken ct = default)
    {
        if (documentId == Guid.Empty)
            throw new ArgumentException("Document id is required.", nameof(documentId));
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileContentBase64);

        return ExecuteAsync("sp_SaveDocument", async (dbConn, token) =>
        {
            await dbConn.ExecuteAsync(new CommandDefinition(
                "sp_SaveDocument",
                new
                {
                    DocumentId = documentId,
                    CustomerId = customerId,
                    DocumentType = documentType.ToDbValue(),
                    FileName = fileName,
                    FileContentBase64 = fileContentBase64,
                    Status = status.ToDbValue()
                },
                commandType: CommandType.StoredProcedure,
                commandTimeout: _cmdTimeout,
                cancellationToken: token));

            return true;
        }, ct);
    }

    public Task UpdateWorkflowStatusAsync(
        Guid documentId,
        WorkflowStatus status,
        string? message = null,
        CancellationToken ct = default)
    {
        if (documentId == Guid.Empty)
            throw new ArgumentException("Document id is required.", nameof(documentId));

        return ExecuteAsync("sp_UpdateWorkflowStatus", async (dbConn, token) =>
        {
            await dbConn.ExecuteAsync(new CommandDefinition(
                "sp_UpdateWorkflowStatus",
                new
                {
                    DocumentId = documentId,
                    Status = status.ToDbValue(),
                    Message = TruncateMessage(message)
                },
                commandType: CommandType.StoredProcedure,
                commandTimeout: _cmdTimeout,
                cancellationToken: token));

            return true;
        }, ct);
    }

    public Task<DocumentRecord?> GetDocumentAsync(Guid documentId, CancellationToken ct = default)
    {
        if (documentId == Guid.Empty)
            throw new ArgumentException("Document id is required.", nameof(documentId));

        return ExecuteAsync("Documents", async (dbConn, token) =>
        {
            var row = await dbConn.QuerySingleOrDefaultAsync<DocumentRow>(new CommandDefinition(
                """
                SELECT
                    d.DocumentId,
                    d.CustomerId,
                    d.DocumentType,
                    d.FileName,
                    d.FileContentBase64,
                    d.ExtractedText,
                    d.CurrentStatus,
                    d.UploadedOn,
                    w.Message,
                    w.Timestamp
                FROM Documents d
                OUTER APPLY (
                    SELECT TOP 1 Message, Timestamp
                    FROM DocumentWorkflowStatus
                    WHERE DocumentId = d.DocumentId
                    ORDER BY Timestamp DESC, Id DESC
                ) w
                WHERE d.DocumentId = @DocumentId
                """,
                new { DocumentId = documentId },
                commandTimeout: _cmdTimeout,
                cancellationToken: token));

            if (row is null)
                return null;

            return new DocumentRecord
            {
                DocumentId = row.DocumentId,
                CustomerId = row.CustomerId,
                DocumentType = DomainValueMappings.ParseDocumentType(row.DocumentType),
                FileName = row.FileName,
                FileContentBase64 = row.FileContentBase64,
                ExtractedText = row.ExtractedText,
                Status = DomainValueMappings.ParseWorkflowStatus(row.CurrentStatus),
                Message = row.Message,
                UploadedOn = row.UploadedOn,
                UpdatedUtc = row.Timestamp
            };
        }, ct);
    }

    private async Task<T> ExecuteAsync<T>(
        string procedure,
        Func<SqlConnection, CancellationToken, Task<T>> action,
        CancellationToken ct)
    {
        try
        {
            return await _pipeline.ExecuteAsync(async token =>
            {
                await using var dbConn = new SqlConnection(_connStr);
                await dbConn.OpenAsync(token);
                return await action(dbConn, token);
            }, ct);
        }
        catch (TimeoutRejectedException ex)
        {
            throw new AgentTimeoutException(nameof(SqlTool), $"{procedure} exceeded the {_cmdTimeout}s command timeout.", ex);
        }
        catch (SqlException ex)
        {
            throw new PersistenceException(procedure, $"{procedure} failed after retries.", ex);
        }
    }

    private static string TruncateMessage(string? message)
    {
        var text = message ?? string.Empty;
        return text.Length <= 1000 ? text : text[..1000];
    }

    private static ResiliencePipeline BuildPipeline(int timeoutSeconds, ILogger logger)
    {
        return new ResiliencePipelineBuilder()
            .AddTimeout(TimeSpan.FromSeconds(timeoutSeconds))
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<SqlException>(SqlTransient.Matches)
                    .Handle<TimeoutException>(),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(250),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    logger.LogWarning(
                        args.Outcome.Exception,
                        "SqlTool retry {Attempt} after transient failure",
                        args.AttemptNumber + 1);
                    return default;
                }
            })
            .Build();
    }

    private sealed class DocumentRow
    {
        public Guid DocumentId { get; set; }
        public string CustomerId { get; set; } = string.Empty;
        public string DocumentType { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string FileContentBase64 { get; set; } = string.Empty;
        public string? ExtractedText { get; set; }
        public string CurrentStatus { get; set; } = string.Empty;
        public DateTime UploadedOn { get; set; }
        public string? Message { get; set; }
        public DateTime? Timestamp { get; set; }
    }
}
