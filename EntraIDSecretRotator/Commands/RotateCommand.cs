using System.CommandLine;
using System.Diagnostics;
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
/// Command to rotate expiring secrets - the core rotation functionality.
/// </summary>
public sealed class RotateCommand
{
    private readonly IEntraIdService _entraIdService;
    private readonly IKeyVaultService _keyVaultService;
    private readonly IRotationPlanBuilder _planBuilder;
    private readonly Metrics _metrics;
    private readonly RotatorOptions _options;
    private readonly ILogger<RotateCommand> _logger;

    public RotateCommand(
        IEntraIdService entraIdService,
        IKeyVaultService keyVaultService,
        IRotationPlanBuilder planBuilder,
        Metrics metrics,
        RotatorOptions options,
        ILogger<RotateCommand> logger)
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
        var command = new Command("rotate", "Rotate expiring secrets (respects --dry-run)");

        var filterOption = new Option<string?>("--filter", "-f") { Description = "Filter app registrations by name (case-insensitive contains)" };

        var daysOption = new Option<int?>("--days", "-d") { Description = "Rotate secrets expiring within N days" };

        var notExpiredOption = new Option<bool?>("--not-expired", "-n") { Description = "Exclude already expired secrets" };

        var outputOption = new Option<OutputFormat?>("--output", "-o") { Description = "Output format: table, json, or yaml" };

        var dryRunOption = new Option<bool?>("--dry-run") { Description = "Preview changes without executing" };

        var validityDaysOption = new Option<int?>("--validity-days") { Description = "Validity period for new secrets in days (default: 180)" };

        command.Options.Add(filterOption);
        command.Options.Add(daysOption);
        command.Options.Add(notExpiredOption);
        command.Options.Add(outputOption);
        command.Options.Add(dryRunOption);
        command.Options.Add(validityDaysOption);

        command.SetAction(async (parseResult, _) =>
        {
            var filterCli = parseResult.GetValue(filterOption);
            var daysCli = parseResult.GetValue(daysOption);
            var notExpiredCli = parseResult.GetValue(notExpiredOption);
            var outputCli = parseResult.GetValue(outputOption);
            var dryRunCli = parseResult.GetValue(dryRunOption);
            var validityDaysCli = parseResult.GetValue(validityDaysOption);
            // CLI args override everything when explicitly provided
            // Otherwise: env var > appsettings > default
            var filter = filterCli ?? _options.GetAppFilter("myfilter");
            var days = daysCli ?? _options.GetDays(30);
            var notExpired = notExpiredCli ?? _options.GetNotExpired(false);
            var output = outputCli ?? _options.GetOutput(OutputFormat.Table);
            var dryRun = dryRunCli ?? _options.GetDryRun(false);
            var validityDays = validityDaysCli ?? _options.GetValidityDays(180);

            await ExecuteAsync(filter, days, notExpired, output, dryRun, validityDays);
        });

        return command;
    }

    private async Task ExecuteAsync(string filter, int days, bool notExpired,
        OutputFormat output, bool dryRun, int validityDays)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Clear rotation state from previous runs
            _metrics.ClearRotationState();
            _metrics.ClearOrphanSecrets();
            _metrics.ClearSecretExpiryInfo();

            // Phase 1: Fetch data
            _logger.LogInformation("Phase 1: Fetching data...");
            _logger.LogInformation("  App filter: '{Filter}'", filter);
            _logger.LogInformation("  Expiry threshold: {Days} days", days);
            _logger.LogInformation("  Exclude expired: {NotExpired}", notExpired);
            _logger.LogInformation("  Validity for new secrets: {ValidityDays} days", validityDays);

            var apps = await _entraIdService.ListAppRegistrationsAsync(filter);
            _logger.LogInformation("  Found {Count} app registrations", apps.Count);

            // Record app registration and per-secret metrics
            _metrics.RecordAppRegistrationCount(apps.Count);
            var expiryBuckets = new Dictionary<string, int>
            {
                ["expired"] = 0,
                ["30d"] = 0,
                ["60d"] = 0,
                ["90d"] = 0,
                ["90d+"] = 0
            };
            foreach (var app in apps)
            {
                foreach (var secret in app.Secrets)
                {
                    expiryBuckets[secret.ExpiryBucket]++;
                    _metrics.RecordSecretExpiryInfo(app.DisplayName, app.AppId, secret.ExpiryBucket, secret.DaysUntilExpiry);
                }
            }
            _metrics.RecordSecretsExpiringByBucket(expiryBuckets);

            var kvSecrets = await _keyVaultService.ListAllSecretsAsync();
            _logger.LogInformation("  Found {Count} Key Vault secrets", kvSecrets.Count);

            // Phase 2: Build plan
            _logger.LogInformation("Phase 2: Building rotation plan...");
            var (plan, skippedHealthy) = _planBuilder.BuildPlan(apps, kvSecrets, days, notExpired);

            if (skippedHealthy > 0)
            {
                _logger.LogInformation("  Skipped {Count} healthy secrets (not expiring within {Days} days)", skippedHealthy, days);
            }

            // Record duplicate mapping metrics
            foreach (var dup in plan.DuplicateRotations)
            {
                _metrics.RecordDuplicateMapping(dup.ClientId, dup.SecretId, dup.KeyVaultRefs.Count);
            }

            // Record orphan metrics
            foreach (var orphan in plan.Orphans)
            {
                _metrics.RecordOrphanSecret(orphan.AppName, orphan.ClientId, orphan.DaysUntilExpiry, orphan.PendingDeletion);
            }

            if (dryRun)
            {
                _logger.LogInformation("DRY RUN - showing plan without executing");
                DisplayPlan(plan, output);
                return;
            }

            if (!plan.HasActions)
            {
                _logger.LogInformation("No actions needed - all secrets are healthy!");
                DisplayPlan(plan, output);
                return;
            }

            // Phase 3: Execute plan
            _logger.LogInformation("Phase 3: Executing rotation plan...");

            // Track results for output
            var results = new RotationResults();

            // 3.1 Process unique rotations
            await ProcessUniqueRotationsAsync(plan.UniqueRotations, validityDays, results);

            // 3.2 Process duplicate rotations
            await ProcessDuplicateRotationsAsync(plan.DuplicateRotations, validityDays, results);

            // 3.3 Process deletions (only safe ones)
            await ProcessDeletionsAsync(plan.Deletions, results);

            // Output results
            OutputResults(plan, results, output);
        }
        finally
        {
            stopwatch.Stop();
            _metrics.RecordJobDuration(stopwatch.Elapsed.TotalSeconds);
            _logger.LogInformation("Rotation completed in {Duration:F2} seconds", stopwatch.Elapsed.TotalSeconds);
        }
    }

    private async Task ProcessUniqueRotationsAsync(
        IReadOnlyList<RotationAction> rotations,
        int validityDays,
        RotationResults results)
    {
        foreach (var action in rotations)
        {
            try
            {
                var kvRef = action.KeyVaultRefs[0];

                _logger.LogInformation(
                    "Rotating {AppName} ({ClientId}) - Secret {SecretId}",
                    action.AppName, action.ClientId, action.SecretId);

                // Step 1: Create new secret in Entra ID
                // Description format: {key-vault-secret-name}-{rotationdate}
                var displayName = $"{kvRef.SecretName}-{DateTime.UtcNow:yyyy-MM-dd}";
                var newSecret = await _entraIdService.CreateSecretAsync(
                    action.ObjectId,
                    displayName,
                    validityDays);

                // Step 2: Update Key Vault secret
                var newContentType = $"{action.AppName}:{action.ClientId}:{newSecret.KeyId}";

                try
                {
                    await _keyVaultService.UpdateSecretAsync(
                        kvRef.VaultName,
                        kvRef.SecretName,
                        newSecret.SecretValue,
                        newContentType,
                        newSecret.EndDateTime);

                    _logger.LogInformation(
                        "SUCCESS: Rotated {AppName} in {VaultName}/{SecretName}",
                        action.AppName, kvRef.VaultName, kvRef.SecretName);

                    _metrics.RecordRotation(action.AppName, action.ClientId, "success");
                    results.SuccessfulRotations.Add(action);
                }
                catch (Exception kvEx)
                {
                    // ROLLBACK: Delete the newly created Entra ID secret
                    _logger.LogError(kvEx,
                        "Key Vault update failed, rolling back Entra ID secret {KeyId}",
                        newSecret.KeyId);

                    try
                    {
                        await _entraIdService.DeleteSecretAsync(action.ObjectId, newSecret.KeyId);
                        _logger.LogInformation("Rollback successful: Deleted Entra ID secret {KeyId}",
                            newSecret.KeyId);
                    }
                    catch (Exception rollbackEx)
                    {
                        _logger.LogError(rollbackEx,
                            "CRITICAL: Rollback failed! Orphaned secret {KeyId} on app {ClientId}",
                            newSecret.KeyId, action.ClientId);
                    }

                    _metrics.RecordRotation(action.AppName, action.ClientId, "failed");
                    _metrics.RecordRotationFailed(action.AppName, action.ClientId, "kv_update_failed");
                    results.FailedRotations.Add((action, "kv_update_failed"));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to rotate {AppName} ({ClientId})",
                    action.AppName, action.ClientId);
                _metrics.RecordRotation(action.AppName, action.ClientId, "failed");
                _metrics.RecordRotationFailed(action.AppName, action.ClientId, "create_secret_failed");
                results.FailedRotations.Add((action, "create_secret_failed"));
            }
        }
    }

    private async Task ProcessDuplicateRotationsAsync(
        IReadOnlyList<RotationAction> rotations,
        int validityDays,
        RotationResults results)
    {
        foreach (var action in rotations)
        {
            try
            {
                _logger.LogInformation(
                    "Rotating {AppName} ({ClientId}) - {Count} Key Vaults",
                    action.AppName, action.ClientId, action.KeyVaultRefs.Count);

                // Step 1: Capture current state of ALL Key Vaults (for rollback)
                var rollbackData = new List<(KeyVaultReference Ref, string Value, string ContentType)>();
                foreach (var kvRef in action.KeyVaultRefs)
                {
                    var currentValue = await _keyVaultService.GetSecretValueAsync(
                        kvRef.VaultName, kvRef.SecretName);

                    // The current content-type contains the old mapping
                    var currentContentType = $"{action.AppName}:{action.ClientId}:{action.SecretId}";
                    rollbackData.Add((kvRef, currentValue, currentContentType));
                }

                // Step 2: Create new secret in Entra ID (once for all vaults)
                // Description format: {key-vault-secret-name}-{rotationdate} (using first KV name)
                var primaryKvRef = action.KeyVaultRefs[0];
                var displayName = $"{primaryKvRef.SecretName}-{DateTime.UtcNow:yyyy-MM-dd}";
                var newSecret = await _entraIdService.CreateSecretAsync(
                    action.ObjectId,
                    displayName,
                    validityDays);

                var newContentType = $"{action.AppName}:{action.ClientId}:{newSecret.KeyId}";

                // Step 3: Update ALL Key Vaults
                var successfulUpdates = new List<KeyVaultReference>();
                var failed = false;

                foreach (var kvRef in action.KeyVaultRefs)
                {
                    try
                    {
                        await _keyVaultService.UpdateSecretAsync(
                            kvRef.VaultName,
                            kvRef.SecretName,
                            newSecret.SecretValue,
                            newContentType,
                            newSecret.EndDateTime);

                        successfulUpdates.Add(kvRef);
                        _logger.LogInformation("Updated {VaultName}/{SecretName}",
                            kvRef.VaultName, kvRef.SecretName);
                    }
                    catch (Exception kvEx)
                    {
                        _logger.LogError(kvEx, "Failed to update {VaultName}/{SecretName}",
                            kvRef.VaultName, kvRef.SecretName);
                        failed = true;
                        break; // Stop trying more vaults
                    }
                }

                if (failed)
                {
                    // ROLLBACK: Revert successful KV updates and delete Entra ID secret
                    _logger.LogWarning("Rolling back {Count} successful Key Vault updates",
                        successfulUpdates.Count);

                    foreach (var success in successfulUpdates)
                    {
                        var original = rollbackData.First(r =>
                            r.Ref.VaultName == success.VaultName &&
                            r.Ref.SecretName == success.SecretName);

                        try
                        {
                            await _keyVaultService.UpdateSecretAsync(
                                success.VaultName,
                                success.SecretName,
                                original.Value,
                                original.ContentType);
                            _logger.LogInformation("Reverted {VaultName}/{SecretName}",
                                success.VaultName, success.SecretName);
                        }
                        catch (Exception rollbackEx)
                        {
                            _logger.LogError(rollbackEx,
                                "CRITICAL: Failed to revert {VaultName}/{SecretName}",
                                success.VaultName, success.SecretName);
                        }
                    }

                    // Delete the new Entra ID secret
                    try
                    {
                        await _entraIdService.DeleteSecretAsync(action.ObjectId, newSecret.KeyId);
                        _logger.LogInformation("Deleted orphaned Entra ID secret {KeyId}",
                            newSecret.KeyId);
                    }
                    catch (Exception deleteEx)
                    {
                        _logger.LogError(deleteEx,
                            "CRITICAL: Failed to delete orphaned secret {KeyId}",
                            newSecret.KeyId);
                    }

                    _metrics.RecordRotation(action.AppName, action.ClientId, "failed");
                    _metrics.RecordRotationFailed(action.AppName, action.ClientId, "duplicate_partial_failure");
                    results.FailedRotations.Add((action, "duplicate_partial_failure"));
                }
                else
                {
                    _logger.LogInformation(
                        "SUCCESS: Rotated {AppName} in {Count} Key Vaults",
                        action.AppName, action.KeyVaultRefs.Count);
                    _metrics.RecordRotation(action.AppName, action.ClientId, "success");
                    results.SuccessfulRotations.Add(action);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process duplicate rotation for {AppName}",
                    action.AppName);
                _metrics.RecordRotation(action.AppName, action.ClientId, "failed");
                _metrics.RecordRotationFailed(action.AppName, action.ClientId, "duplicate_rotation_failed");
                results.FailedRotations.Add((action, "duplicate_rotation_failed"));
            }
        }
    }

    private async Task ProcessDeletionsAsync(IReadOnlyList<DeletionAction> deletions, RotationResults results)
    {
        foreach (var action in deletions)
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
                // Don't emit failure metric - deletion failures are safe (secret remains)
                results.FailedDeletions.Add(action);
            }
        }
    }

    private void DisplayPlan(RotationPlan plan, OutputFormat output)
    {
        switch (output)
        {
            case OutputFormat.Json:
                OutputPlanJson(plan);
                break;
            case OutputFormat.Yaml:
                OutputPlanYaml(plan);
                break;
            default:
                OutputPlanTable(plan);
                break;
        }
    }

    private void OutputPlanTable(RotationPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("=".PadRight(65, '='));
        sb.AppendLine("                      ROTATION PLAN");
        sb.AppendLine("=".PadRight(65, '='));
        sb.AppendLine();

        // Summary
        sb.AppendLine($"Total Actions: {plan.TotalActions}");
        sb.AppendLine($"  Unique Rotations: {plan.UniqueRotations.Count}");
        sb.AppendLine($"  Duplicate Rotations: {plan.DuplicateRotations.Count}");
        sb.AppendLine($"  Deletions: {plan.Deletions.Count}");
        sb.AppendLine($"  Orphans (need attention): {plan.Orphans.Count}");
        sb.AppendLine();

        if (!plan.HasActions)
        {
            sb.AppendLine("No actions needed - all secrets are healthy!");
            _logger.LogDebug("Rotation plan output:\n{Output}", sb.ToString());
            return;
        }

        // Unique Rotations
        if (plan.UniqueRotations.Count > 0)
        {
            sb.AppendLine("-".PadRight(65, '-'));
            sb.AppendLine("UNIQUE ROTATIONS (single Key Vault mapping)");
            sb.AppendLine("-".PadRight(65, '-'));
            foreach (var action in plan.UniqueRotations)
            {
                sb.AppendLine($"  [{action.Status.ToString().ToUpperInvariant()}] {action.AppName}");
                sb.AppendLine($"      Client ID: {action.ClientId}");
                sb.AppendLine($"      Secret ID: {action.SecretId}");
                sb.AppendLine($"      Days until expiry: {action.DaysUntilExpiry}");
                sb.AppendLine($"      Key Vault: {action.KeyVaultRefs[0].VaultName}/{action.KeyVaultRefs[0].SecretName}");
                sb.AppendLine();
            }
        }

        // Duplicate Rotations
        if (plan.DuplicateRotations.Count > 0)
        {
            sb.AppendLine("-".PadRight(65, '-'));
            sb.AppendLine("DUPLICATE ROTATIONS (multiple Key Vault mappings - atomic update)");
            sb.AppendLine("-".PadRight(65, '-'));
            foreach (var action in plan.DuplicateRotations)
            {
                sb.AppendLine($"  [{action.Status.ToString().ToUpperInvariant()}] {action.AppName}");
                sb.AppendLine($"      Client ID: {action.ClientId}");
                sb.AppendLine($"      Secret ID: {action.SecretId}");
                sb.AppendLine($"      Days until expiry: {action.DaysUntilExpiry}");
                sb.AppendLine($"      Key Vaults ({action.KeyVaultRefs.Count}):");
                foreach (var kvRef in action.KeyVaultRefs)
                {
                    sb.AppendLine($"        - {kvRef.VaultName}/{kvRef.SecretName}");
                }
                sb.AppendLine();
            }
        }

        // Deletions
        if (plan.Deletions.Count > 0)
        {
            sb.AppendLine("-".PadRight(65, '-'));
            sb.AppendLine("DELETIONS (expired secrets)");
            sb.AppendLine("-".PadRight(65, '-'));
            foreach (var action in plan.Deletions)
            {
                var safetyStatus = action.SafeToDelete ? "SAFE" : "UNSAFE";
                sb.AppendLine($"  [{safetyStatus}] {action.AppName}");
                sb.AppendLine($"      Client ID: {action.ClientId}");
                sb.AppendLine($"      Secret ID: {action.SecretId}");
                if (action.SafeToDelete && action.ProofReference != null)
                {
                    sb.AppendLine($"      Proof: {action.ProofReference.VaultName}/{action.ProofReference.SecretName}");
                }
                else
                {
                    sb.AppendLine("      Warning: No proof of rotation - manual verification needed");
                }
                sb.AppendLine();
            }
        }

        // Orphans
        if (plan.Orphans.Count > 0)
        {
            sb.AppendLine("-".PadRight(65, '-'));
            sb.AppendLine("ORPHANS (no Key Vault mapping - needs human attention!)");
            sb.AppendLine("-".PadRight(65, '-'));
            foreach (var orphan in plan.Orphans)
            {
                var status = orphan.DaysUntilExpiry < 0 ? "EXPIRED" : "EXPIRING";
                sb.AppendLine($"  [{status}] {orphan.AppName}");
                sb.AppendLine($"      Client ID: {orphan.ClientId}");
                sb.AppendLine($"      Secret ID: {orphan.SecretId}");
                sb.AppendLine($"      Days until expiry: {orphan.DaysUntilExpiry}");
                sb.AppendLine($"      Pending deletion: {(orphan.PendingDeletion ? "yes (replaced by rotator, deleted at expiry)" : "no (not tracked - notify owner)")}");
                sb.AppendLine();
            }
        }

        sb.AppendLine("=".PadRight(65, '='));

        _logger.LogDebug("Rotation plan output:\n{Output}", sb.ToString());
    }

    private static object ToPlanOutputModel(RotationPlan plan)
    {
        return new
        {
            TotalActions = plan.TotalActions,
            UniqueRotationCount = plan.UniqueRotations.Count,
            DuplicateRotationCount = plan.DuplicateRotations.Count,
            DeletionCount = plan.Deletions.Count,
            OrphanCount = plan.Orphans.Count,
            UniqueRotations = plan.UniqueRotations.Select(a => new
            {
                a.AppName,
                a.ClientId,
                a.SecretId,
                a.DaysUntilExpiry,
                Status = a.Status.ToString().ToLowerInvariant(),
                KeyVault = $"{a.KeyVaultRefs[0].VaultName}/{a.KeyVaultRefs[0].SecretName}"
            }).ToList(),
            DuplicateRotations = plan.DuplicateRotations.Select(a => new
            {
                a.AppName,
                a.ClientId,
                a.SecretId,
                a.DaysUntilExpiry,
                Status = a.Status.ToString().ToLowerInvariant(),
                KeyVaults = a.KeyVaultRefs.Select(kv => $"{kv.VaultName}/{kv.SecretName}").ToList()
            }).ToList(),
            Deletions = plan.Deletions.Select(d => new
            {
                d.AppName,
                d.ClientId,
                d.SecretId,
                d.SafeToDelete,
                ProofReference = d.ProofReference != null ? $"{d.ProofReference.VaultName}/{d.ProofReference.SecretName}" : null
            }).ToList(),
            Orphans = plan.Orphans.Select(o => new
            {
                o.AppName,
                o.ClientId,
                o.SecretId,
                o.DaysUntilExpiry,
                o.PendingDeletion,
                Status = o.DaysUntilExpiry < 0 ? "expired" : "expiring"
            }).ToList()
        };
    }

    private static void OutputPlanJson(RotationPlan plan)
    {
        var model = ToPlanOutputModel(plan);
        var json = JsonSerializer.Serialize(model, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        Console.WriteLine(json);
    }

    private static void OutputPlanYaml(RotationPlan plan)
    {
        var model = ToPlanOutputModel(plan);
        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        var yaml = serializer.Serialize(model);
        Console.WriteLine(yaml);
    }

    private void OutputResults(RotationPlan plan, RotationResults results, OutputFormat output)
    {
        switch (output)
        {
            case OutputFormat.Json:
                OutputResultsJson(plan, results);
                break;
            case OutputFormat.Yaml:
                OutputResultsYaml(plan, results);
                break;
            default:
                OutputResultsTable(plan, results);
                break;
        }
    }

    private void OutputResultsTable(RotationPlan plan, RotationResults results)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("=".PadRight(65, '='));
        sb.AppendLine("                    ROTATION RESULTS");
        sb.AppendLine("=".PadRight(65, '='));
        sb.AppendLine();

        sb.AppendLine("Summary:");
        sb.AppendLine($"  Successful rotations: {results.SuccessfulRotations.Count}");
        sb.AppendLine($"  Failed rotations: {results.FailedRotations.Count}");
        sb.AppendLine($"  Successful deletions: {results.SuccessfulDeletions.Count}");
        sb.AppendLine($"  Failed deletions: {results.FailedDeletions.Count}");
        sb.AppendLine($"  Manual intervention required: {results.ManualInterventionRequired.Count}");
        sb.AppendLine($"  Orphans (unmapped): {plan.Orphans.Count}");
        sb.AppendLine();

        if (results.FailedRotations.Count > 0)
        {
            sb.AppendLine("-".PadRight(65, '-'));
            sb.AppendLine("FAILED ROTATIONS");
            sb.AppendLine("-".PadRight(65, '-'));
            foreach (var (action, reason) in results.FailedRotations)
            {
                sb.AppendLine($"  [FAILED] {action.AppName}");
                sb.AppendLine($"      Client ID: {action.ClientId}");
                sb.AppendLine($"      Reason: {reason}");
                sb.AppendLine();
            }
        }

        if (results.ManualInterventionRequired.Count > 0)
        {
            sb.AppendLine("-".PadRight(65, '-'));
            sb.AppendLine("MANUAL INTERVENTION REQUIRED");
            sb.AppendLine("-".PadRight(65, '-'));
            foreach (var action in results.ManualInterventionRequired)
            {
                sb.AppendLine($"  [UNSAFE] {action.AppName}");
                sb.AppendLine($"      Client ID: {action.ClientId}");
                sb.AppendLine($"      Secret ID: {action.SecretId}");
                sb.AppendLine("      Reason: Cannot verify this secret was previously rotated");
                sb.AppendLine();
            }
        }

        sb.AppendLine("=".PadRight(65, '='));

        _logger.LogDebug("Rotation results output:\n{Output}", sb.ToString());
    }

    private static object ToResultsOutputModel(RotationPlan plan, RotationResults results)
    {
        return new
        {
            Summary = new
            {
                SuccessfulRotations = results.SuccessfulRotations.Count,
                FailedRotations = results.FailedRotations.Count,
                SuccessfulDeletions = results.SuccessfulDeletions.Count,
                FailedDeletions = results.FailedDeletions.Count,
                ManualInterventionRequired = results.ManualInterventionRequired.Count,
                Orphans = plan.Orphans.Count
            },
            SuccessfulRotations = results.SuccessfulRotations.Select(a => new
            {
                a.AppName,
                a.ClientId,
                a.SecretId
            }).ToList(),
            FailedRotations = results.FailedRotations.Select(f => new
            {
                f.Action.AppName,
                f.Action.ClientId,
                f.Action.SecretId,
                f.Reason
            }).ToList(),
            ManualInterventionRequired = results.ManualInterventionRequired.Select(a => new
            {
                a.AppName,
                a.ClientId,
                a.SecretId
            }).ToList(),
            Orphans = plan.Orphans.Select(o => new
            {
                o.AppName,
                o.ClientId,
                o.SecretId,
                o.DaysUntilExpiry,
                o.PendingDeletion
            }).ToList()
        };
    }

    private static void OutputResultsJson(RotationPlan plan, RotationResults results)
    {
        var model = ToResultsOutputModel(plan, results);
        var json = JsonSerializer.Serialize(model, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        Console.WriteLine(json);
    }

    private static void OutputResultsYaml(RotationPlan plan, RotationResults results)
    {
        var model = ToResultsOutputModel(plan, results);
        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        var yaml = serializer.Serialize(model);
        Console.WriteLine(yaml);
    }

    /// <summary>
    /// Internal class to track rotation results during execution.
    /// </summary>
    private sealed class RotationResults
    {
        public List<RotationAction> SuccessfulRotations { get; } = new();
        public List<(RotationAction Action, string Reason)> FailedRotations { get; } = new();
        public List<DeletionAction> SuccessfulDeletions { get; } = new();
        public List<DeletionAction> FailedDeletions { get; } = new();
        public List<DeletionAction> ManualInterventionRequired { get; } = new();
    }
}
