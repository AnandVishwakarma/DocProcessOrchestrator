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

public sealed class OcrTool : IOcrTool
{
    private readonly string _connStr;
    private readonly int _cmdTimeout;
    private readonly ResiliencePipeline _ocrPipeline;
    private readonly ResiliencePipeline _sqlPipeline;

    public OcrTool(IOptions<DocDbOptions> options, ILogger<OcrTool> logger)
    {
        var cfg = options.Value;
        _connStr = string.IsNullOrWhiteSpace(cfg.ConnectionString)
            ? throw new InvalidOperationException("DocDb connection string is not configured.")
            : cfg.ConnectionString;
        _cmdTimeout = cfg.CommandTimeoutSeconds;
        _ocrPipeline = BuildOcrPipeline(cfg.OcrTimeoutSeconds, logger);
        _sqlPipeline = BuildSqlPipeline(_cmdTimeout, logger);
    }

    public async Task<string> ExtractTextAsync(
        DocumentType documentType,
        string fileName,
        string fileContentBase64,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileContentBase64);

        try
        {
            return await _ocrPipeline.ExecuteAsync(async token =>
            {
                var delayMs = Random.Shared.Next(1000, 3001);
                await Task.Delay(delayMs, token);
                return SimulateExtraction(documentType, fileName, fileContentBase64);
            }, ct);
        }
        catch (TimeoutRejectedException ex)
        {
            throw new AgentTimeoutException(nameof(OcrTool), "OCR extraction exceeded the configured timeout.", ex);
        }
    }

    public Task SaveOcrTextAsync(Guid documentId, string extractedText, CancellationToken ct = default)
    {
        if (documentId == Guid.Empty)
            throw new ArgumentException("Document id is required.", nameof(documentId));
        ArgumentException.ThrowIfNullOrWhiteSpace(extractedText);

        return ExecuteSqlAsync("sp_SaveOCRText", async (dbConn, token) =>
        {
            await dbConn.ExecuteAsync(new CommandDefinition(
                "sp_SaveOCRText",
                new { DocumentId = documentId, ExtractedText = extractedText },
                commandType: CommandType.StoredProcedure,
                commandTimeout: _cmdTimeout,
                cancellationToken: token));

            return true;
        }, ct);
    }

    private async Task<T> ExecuteSqlAsync<T>(
        string procedure,
        Func<SqlConnection, CancellationToken, Task<T>> action,
        CancellationToken ct)
    {
        try
        {
            return await _sqlPipeline.ExecuteAsync(async token =>
            {
                await using var dbConn = new SqlConnection(_connStr);
                await dbConn.OpenAsync(token);
                return await action(dbConn, token);
            }, ct);
        }
        catch (TimeoutRejectedException ex)
        {
            throw new AgentTimeoutException(nameof(OcrTool), $"{procedure} exceeded the {_cmdTimeout}s command timeout.", ex);
        }
        catch (SqlException ex)
        {
            throw new PersistenceException(procedure, $"{procedure} failed after retries.", ex);
        }
    }

    private static string SimulateExtraction(DocumentType documentType, string fileName, string fileContentBase64)
    {
        // 1. Inspect the incoming base64 payload to allow testing dynamic inputs
        try
        {
            var rawBytes = Convert.FromBase64String(fileContentBase64);
            var decoded = System.Text.Encoding.UTF8.GetString(rawBytes).Trim();

            // If the decoded content is a direct test string (e.g. "INVALID", a test PAN, or custom text), use it
            if (!string.IsNullOrWhiteSpace(decoded) && decoded.All(c => !char.IsControl(c) || c == '\r' || c == '\n'))
            {
                return $"EXTRACTED_CONTENT:{Environment.NewLine}{decoded}{Environment.NewLine}Source: {fileName}";
            }
        }
        catch
        {
            // Ignore parsing errors and proceed to fallback templates
        }

        // 2. Default standard simulation fallback
        return documentType switch
        {
            DocumentType.Pan => $"INCOME TAX DEPARTMENT{Environment.NewLine}Permanent Account Number{Environment.NewLine}ABCDE1234F{Environment.NewLine}Source: {fileName}",
            DocumentType.Aadhar => $"GOVERNMENT OF INDIA{Environment.NewLine}Aadhaar{Environment.NewLine}234567890123{Environment.NewLine}Source: {fileName}",
            DocumentType.Loa => $"LETTER OF AUTHORIZATION{Environment.NewLine}I hereby authorize processing of the attached documents.{Environment.NewLine}Source: {fileName}",
            _ => throw new ArgumentOutOfRangeException(nameof(documentType), documentType, "Unknown document type.")
        };
    }

    private static ResiliencePipeline BuildOcrPipeline(int timeoutSeconds, ILogger logger)
    {
        // Timeout is inner so each attempt is capped; retry sits outside to re-run the 1–3s simulation.
        return new ResiliencePipelineBuilder()
            .AddTimeout(TimeSpan.FromSeconds(timeoutSeconds))
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<TimeoutRejectedException>()
                    .Handle<TimeoutException>(),
                MaxRetryAttempts = 2,
                Delay = TimeSpan.FromMilliseconds(200),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    logger.LogWarning(
                        args.Outcome.Exception,
                        "OcrTool retry {Attempt} after extraction timeout",
                        args.AttemptNumber + 1);
                    return default;
                }
            })
            .Build();
    }

    private static ResiliencePipeline BuildSqlPipeline(int timeoutSeconds, ILogger logger)
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
                        "OcrTool SQL retry {Attempt} after transient failure",
                        args.AttemptNumber + 1);
                    return default;
                }
            })
            .Build();
    }
}
