using System.CommandLine;
using System.Text;
using System.Text.Json;
using EntraIDSecretRotator.Infrastructure;
using EntraIDSecretRotator.Models;
using EntraIDSecretRotator.Services;
using EntraIDSecretRotator.Telemetry;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace EntraIDSecretRotator.Commands;

/// <summary>
/// Command to clean up expired secrets with safety checks.
/// Only deletes secrets where we can prove they were previously rotated.
/// </summary>
public sealed class CleanupCommand
{
    private readonly IEntraIdService _entraIdService;
    private readonly IKeyVaultService _keyVaultService;
    private readonly IRotationPlanBuilder _planBuilder;
    private readonly Metrics _metrics;
    private readonly RotatorOptions _options;
    private readonly ILogger<CleanupCommand> _logger;

    public CleanupCommand(
        IEntraIdService entraIdService,
        IKeyVaultService keyVaultService,
        IRotationPlanBuilder planBuilder,
        Metrics metrics,
        RotatorOptions options,
        ILogger<CleanupCommand> logger)
    {
        _entraIdService = entraIdService;
        _keyVaultService = keyVaultService;
        _planBuilder = planBuilder;
        _metrics = metrics;
        _options = options;
        _logger = logger;
    }

    public Command BuildCommand()
    {
        var command = new Command("cleanup", "Remove expired secrets (with safety checks)");

        var filterOption = new Option<string?>("--filter", "-f") { Description = "Filter app registrations by name (case-insensitive contains)" };

        var outputOption = new Option<OutputFormat?>("--output", "-o") { Description = "Output format: table, json, or yaml" };

        var dryRunOption = new Option<bool?>("--dry-run") { Description = "Preview deletions without executing" };

        command.Options.Add(filterOption);
        command.Options.Add(outputOption);
        command.Options.Add(dryRunOption);

        command.SetAction(async (parseResult, _) =>
        {
            var filterCli = parseResult.GetValue(filterOption);
            var outputCli = parseResult.GetValue(outputOption);
            var dryRunCli = parseResult.GetValue(dryRunOption);
            var filter = filterCli ?? _options.GetAppFilter("myfilter");
            var output = outputCli ?? _options.GetOutput(OutputFormat.Table);
            var dryRun = dryRunCli ?? _options.GetDryRun(false);

            await ExecuteAsync(filter, output, dryRun);
        });

        return command;
    }

    private async Task ExecuteAsync(string filter, OutputFormat output, bool dryRun)
    {
        _metrics.ClearRotationState();

        _logger.LogInformation("Cleanup: Finding expired secrets to delete...");
        _logger.LogInformation("  App filter: '{Filter}'", filter);

        // Phase 1: Fetch data
        var apps = await _entraIdService.ListAppRegistrationsAsync(filter);
        _logger.LogInformation("  Found {Count} app registrations", apps.Count);

        var kvSecrets = await _keyVaultService.ListAllSecretsAsync();
        _logger.LogInformation("  Found {Count} Key Vault secrets", kvSecrets.Count);

        // Phase 2: Build plan (we only care about deletions)
        // Use days=0 to only get expired secrets (not expiring ones)
        var (plan, _) = _planBuilder.BuildPlan(apps, kvSecrets, thresholdDays: 0, excludeExpired: false);

        if (plan.Deletions.Count == 0)
        {
            _logger.LogInformation("No expired secrets found that are eligible for cleanup.");
            return;
        }

        var safeDeletions = plan.Deletions.Where(d => d.SafeToDelete).ToList();
        var unsafeDeletions = plan.Deletions.Where(d => !d.SafeToDelete).ToList();

        _logger.LogInformation("Found {Total} expired secrets: {Safe} safe to delete, {Unsafe} need manual intervention",
            plan.Deletions.Count, safeDeletions.Count, unsafeDeletions.Count);

        if (dryRun)
        {
            _logger.LogInformation("DRY RUN - showing plan without executing");
            DisplayPlan(safeDeletions, unsafeDeletions, output);
            return;
        }

        // Phase 3: Execute deletions
        var results = new CleanupResults();

        foreach (var action in plan.Deletions)
        {
            if (!action.SafeToDelete)
            {
                _logger.LogWarning(
                    "MANUAL INTERVENTION REQUIRED: Cannot safely delete {SecretId} from {AppName}",
                    action.SecretId, action.AppName);
                _metrics.RecordManualIntervention(action.AppName, action.ClientId, action.SecretId);
                results.ManualInterventionRequired.Add(action);
                continue;
            }

            try
            {
                _logger.LogInformation(
                    "Deleting expired secret {SecretId} from {AppName}",
                    action.SecretId, action.AppName);

                await _entraIdService.DeleteSecretAsync(action.ObjectId, action.SecretId);

                _logger.LogInformation(
                    "SUCCESS: Deleted expired secret {SecretId} from {AppName}",
                    action.SecretId, action.AppName);
                _metrics.RecordDeletion();
                results.SuccessfulDeletions.Add(action);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete secret {SecretId} from {AppName}",
                    action.SecretId, action.AppName);
                results.FailedDeletions.Add(action);
            }
        }

        // Output results
        OutputResults(results, output);
    }

    private void DisplayPlan(
        IReadOnlyList<DeletionAction> safeDeletions,
        IReadOnlyList<DeletionAction> unsafeDeletions,
        OutputFormat output)
    {
        switch (output)
        {
            case OutputFormat.Json:
                OutputPlanJson(safeDeletions, unsafeDeletions);
                break;
            case OutputFormat.Yaml:
                OutputPlanYaml(safeDeletions, unsafeDeletions);
                break;
            default:
                OutputPlanTable(safeDeletions, unsafeDeletions);
                break;
        }
    }

    private void OutputPlanTable(
        IReadOnlyList<DeletionAction> safeDeletions,
        IReadOnlyList<DeletionAction> unsafeDeletions)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("CLEANUP PLAN (DRY RUN)");
        sb.AppendLine(new string('=', 65));
        sb.AppendLine();

        sb.AppendLine($"Total expired secrets: {safeDeletions.Count + unsafeDeletions.Count}");
        sb.AppendLine($"  Safe to delete: {safeDeletions.Count}");
        sb.AppendLine($"  Need manual intervention: {unsafeDeletions.Count}");
        sb.AppendLine();

        if (safeDeletions.Count > 0)
        {
            sb.AppendLine(new string('-', 65));
            sb.AppendLine("WILL BE DELETED (safe - rotation proof exists)");
            sb.AppendLine(new string('-', 65));
            foreach (var action in safeDeletions)
            {
                sb.AppendLine($"  [SAFE] {action.AppName}");
                sb.AppendLine($"      Client ID: {action.ClientId}");
                sb.AppendLine($"      Secret ID: {action.SecretId}");
                if (action.ProofReference != null)
                {
                    sb.AppendLine($"      Proof: {action.ProofReference.VaultName}/{action.ProofReference.SecretName}");
                }
                sb.AppendLine();
            }
        }

        if (unsafeDeletions.Count > 0)
        {
            sb.AppendLine(new string('-', 65));
            sb.AppendLine("WILL NOT BE DELETED (no rotation proof - manual intervention needed)");
            sb.AppendLine(new string('-', 65));
            foreach (var action in unsafeDeletions)
            {
                sb.AppendLine($"  [UNSAFE] {action.AppName}");
                sb.AppendLine($"      Client ID: {action.ClientId}");
                sb.AppendLine($"      Secret ID: {action.SecretId}");
                sb.AppendLine("      Reason: Cannot verify this secret was previously rotated");
                sb.AppendLine();
            }
        }

        sb.AppendLine(new string('=', 65));

        _logger.LogDebug("Cleanup plan output:\n{Output}", sb.ToString());
    }

    private static object ToPlanOutputModel(
        IReadOnlyList<DeletionAction> safeDeletions,
        IReadOnlyList<DeletionAction> unsafeDeletions)
    {
        return new
        {
            DryRun = true,
            TotalExpired = safeDeletions.Count + unsafeDeletions.Count,
            SafeToDelete = safeDeletions.Count,
            NeedManualIntervention = unsafeDeletions.Count,
            SafeDeletions = safeDeletions.Select(d => new
            {
                d.AppName,
                d.ClientId,
                d.SecretId,
                Proof = d.ProofReference != null
                    ? $"{d.ProofReference.VaultName}/{d.ProofReference.SecretName}"
                    : null
            }).ToList(),
            UnsafeDeletions = unsafeDeletions.Select(d => new
            {
                d.AppName,
                d.ClientId,
                d.SecretId
            }).ToList()
        };
    }

    private static void OutputPlanJson(
        IReadOnlyList<DeletionAction> safeDeletions,
        IReadOnlyList<DeletionAction> unsafeDeletions)
    {
        var model = ToPlanOutputModel(safeDeletions, unsafeDeletions);
        var json = JsonSerializer.Serialize(model, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        Console.WriteLine(json);
    }

    private static void OutputPlanYaml(
        IReadOnlyList<DeletionAction> safeDeletions,
        IReadOnlyList<DeletionAction> unsafeDeletions)
    {
        var model = ToPlanOutputModel(safeDeletions, unsafeDeletions);
        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        var yaml = serializer.Serialize(model);
        Console.WriteLine(yaml);
    }

    private void OutputResults(CleanupResults results, OutputFormat output)
    {
        switch (output)
        {
            case OutputFormat.Json:
                OutputResultsJson(results);
                break;
            case OutputFormat.Yaml:
                OutputResultsYaml(results);
                break;
            default:
                OutputResultsTable(results);
                break;
        }
    }

    private void OutputResultsTable(CleanupResults results)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("CLEANUP RESULTS");
        sb.AppendLine(new string('=', 65));
        sb.AppendLine();
        sb.AppendLine($"Deleted: {results.SuccessfulDeletions.Count}");
        sb.AppendLine($"Failed: {results.FailedDeletions.Count}");
        sb.AppendLine($"Manual intervention required: {results.ManualInterventionRequired.Count}");

        if (results.FailedDeletions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(new string('-', 65));
            sb.AppendLine("FAILED DELETIONS");
            sb.AppendLine(new string('-', 65));
            foreach (var action in results.FailedDeletions)
            {
                sb.AppendLine($"  [FAILED] {action.AppName}");
                sb.AppendLine($"      Client ID: {action.ClientId}");
                sb.AppendLine($"      Secret ID: {action.SecretId}");
                sb.AppendLine();
            }
        }

        if (results.ManualInterventionRequired.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(new string('-', 65));
            sb.AppendLine("MANUAL INTERVENTION REQUIRED");
            sb.AppendLine(new string('-', 65));
            foreach (var action in results.ManualInterventionRequired)
            {
                sb.AppendLine($"  [UNSAFE] {action.AppName}");
                sb.AppendLine($"      Client ID: {action.ClientId}");
                sb.AppendLine($"      Secret ID: {action.SecretId}");
                sb.AppendLine();
            }
        }

        sb.AppendLine(new string('=', 65));

        _logger.LogDebug("Cleanup results output:\n{Output}", sb.ToString());
    }

    private static object ToResultsOutputModel(CleanupResults results)
    {
        return new
        {
            DryRun = false,
            Deleted = results.SuccessfulDeletions.Count,
            Failed = results.FailedDeletions.Count,
            ManualInterventionRequired = results.ManualInterventionRequired.Count,
            SuccessfulDeletions = results.SuccessfulDeletions.Select(d => new
            {
                d.AppName,
                d.ClientId,
                d.SecretId
            }).ToList(),
            FailedDeletions = results.FailedDeletions.Select(d => new
            {
                d.AppName,
                d.ClientId,
                d.SecretId
            }).ToList(),
            ManualInterventionNeeded = results.ManualInterventionRequired.Select(d => new
            {
                d.AppName,
                d.ClientId,
                d.SecretId
            }).ToList()
        };
    }

    private static void OutputResultsJson(CleanupResults results)
    {
        var model = ToResultsOutputModel(results);
        var json = JsonSerializer.Serialize(model, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        Console.WriteLine(json);
    }

    private static void OutputResultsYaml(CleanupResults results)
    {
        var model = ToResultsOutputModel(results);
        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        var yaml = serializer.Serialize(model);
        Console.WriteLine(yaml);
    }

    /// <summary>
    /// Internal class to track cleanup results.
    /// </summary>
    private sealed class CleanupResults
    {
        public List<DeletionAction> SuccessfulDeletions { get; } = new();
        public List<DeletionAction> FailedDeletions { get; } = new();
        public List<DeletionAction> ManualInterventionRequired { get; } = new();
    }
}
