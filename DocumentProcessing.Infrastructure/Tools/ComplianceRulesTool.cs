using System.Data;
using System.Text.RegularExpressions;
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

public sealed class ComplianceRulesTool : IComplianceRulesTool
{
    private readonly string _connStr;
    private readonly int _cmdTimeout;
    private readonly ResiliencePipeline _pipeline;

    public ComplianceRulesTool(IOptions<DocDbOptions> options, ILogger<ComplianceRulesTool> logger)
    {
        var cfg = options.Value;
        _connStr = string.IsNullOrWhiteSpace(cfg.ConnectionString)
            ? throw new InvalidOperationException("DocDb connection string is not configured.")
            : cfg.ConnectionString;
        _cmdTimeout = cfg.CommandTimeoutSeconds;
        _pipeline = BuildPipeline(_cmdTimeout, logger);
    }

    public async Task<IReadOnlyList<ComplianceRule>> GetRulesAsync(DocumentType documentType, CancellationToken ct = default)
    {
        try
        {
            var rows = await _pipeline.ExecuteAsync(async token =>
            {
                await using var dbConn = new SqlConnection(_connStr);
                await dbConn.OpenAsync(token);

                return (await dbConn.QueryAsync<ComplianceRuleRow>(new CommandDefinition(
                    "sp_GetComplianceRules",
                    new { DocumentType = documentType.ToDbValue() },
                    commandType: CommandType.StoredProcedure,
                    commandTimeout: _cmdTimeout,
                    cancellationToken: token))).AsList();
            }, ct);

            return rows.Select(r => new ComplianceRule
            {
                RuleId = r.RuleId,
                DocumentType = DomainValueMappings.ParseDocumentType(r.DocumentType),
                RegexPattern = r.RegexPattern,
                ExpectedLength = r.ExpectedLength,
                Description = r.Description
            }).ToList();
        }
        catch (TimeoutRejectedException ex)
        {
            throw new AgentTimeoutException(nameof(ComplianceRulesTool), "sp_GetComplianceRules exceeded the command timeout.", ex);
        }
        catch (SqlException ex)
        {
            throw new PersistenceException("sp_GetComplianceRules", "sp_GetComplianceRules failed after retries.", ex);
        }
    }

    public ComplianceEvaluation Evaluate(DocumentType documentType, string ocrText, IReadOnlyList<ComplianceRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        if (string.IsNullOrWhiteSpace(ocrText))
            return new ComplianceEvaluation(false, ["OCR text is empty; required rules cannot be evaluated."]);

        var effectiveRules = rules.Count > 0 ? rules : BuiltInRules(documentType);
        if (effectiveRules.Count == 0)
            return new ComplianceEvaluation(false, ["No compliance rules configured for this document type."]);

        var violations = new List<string>();
        foreach (var rule in effectiveRules)
        {
            if (string.IsNullOrWhiteSpace(rule.RegexPattern))
            {
                violations.Add($"Rule {rule.RuleId} ({rule.Description}) has an empty RegexPattern.");
                continue;
            }

            if (!MatchesRule(ocrText, rule))
                violations.Add(rule.Description);
        }

        return new ComplianceEvaluation(violations.Count == 0, violations);
    }

    private static bool MatchesRule(string ocrText, ComplianceRule rule)
    {
        foreach (var candidate in Candidates(ocrText))
        {
            bool matched;
            try
            {
                matched = Regex.IsMatch(
                    candidate,
                    rule.RegexPattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(250));
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }
            catch (ArgumentException)
            {
                throw new ComplianceValidationException(
                    $"Rule {rule.RuleId} has an invalid RegexPattern.",
                    [rule.Description]);
            }

            if (!matched)
                continue;

            if (rule.ExpectedLength <= 0)
                return true;

            var compact = Regex.Replace(candidate, @"\s+", string.Empty);
            if (compact.Length == rule.ExpectedLength)
                return true;
        }

        return false;
    }

    private static IEnumerable<string> Candidates(string ocrText)
    {
        yield return ocrText.Trim();

        foreach (var line in ocrText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return line;

        foreach (var token in Regex.Split(ocrText, @"\s+"))
        {
            if (!string.IsNullOrWhiteSpace(token))
                yield return token;
        }

        var compact = Regex.Replace(ocrText, @"\s+", string.Empty);
        if (compact.Length > 0)
            yield return compact;

        // Aadhaar is often printed as 4-4-4 groups; digit-only scan still has to hit ExpectedLength 12.
        var digits = Regex.Replace(ocrText, @"\D", string.Empty);
        if (digits.Length >= 12)
            yield return digits;
    }

    private static IReadOnlyList<ComplianceRule> BuiltInRules(DocumentType documentType) => documentType switch
    {
        DocumentType.Pan =>
        [
            new ComplianceRule
            {
                RuleId = 0,
                DocumentType = DocumentType.Pan,
                RegexPattern = @"^[A-Z]{5}[0-9]{4}[A-Z]{1}$",
                ExpectedLength = 10,
                Description = "Standard 10-digit Indian Permanent Account Number"
            }
        ],
        DocumentType.Aadhar =>
        [
            new ComplianceRule
            {
                RuleId = 0,
                DocumentType = DocumentType.Aadhar,
                RegexPattern = @"^[0-9]{12}$",
                ExpectedLength = 12,
                Description = "12-digit Indian Identification Number"
            }
        ],
        DocumentType.Loa =>
        [
            new ComplianceRule
            {
                RuleId = 0,
                DocumentType = DocumentType.Loa,
                RegexPattern = @"AUTHORIZATION",
                ExpectedLength = 0,
                Description = "Letter of authorization language"
            }
        ],
        _ => []
    };

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
                        "ComplianceRulesTool retry {Attempt} after transient failure",
                        args.AttemptNumber + 1);
                    return default;
                }
            })
            .Build();
    }

    private sealed class ComplianceRuleRow
    {
        public int RuleId { get; set; }
        public string DocumentType { get; set; } = string.Empty;
        public string RegexPattern { get; set; } = string.Empty;
        public int ExpectedLength { get; set; }
        public string Description { get; set; } = string.Empty;
    }
}
