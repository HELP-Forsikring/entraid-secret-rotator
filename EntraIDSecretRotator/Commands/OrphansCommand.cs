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
/// Command to find unmapped expiring secrets (orphans).
/// </summary>
public sealed class OrphansCommand
{
    private readonly IEntraIdService _entraIdService;
    private readonly IKeyVaultService _keyVaultService;
    private readonly Metrics _metrics;
    private readonly RotatorOptions _options;
    private readonly ILogger<OrphansCommand> _logger;

    public OrphansCommand(
        IEntraIdService entraIdService,
        IKeyVaultService keyVaultService,
        Metrics metrics,
        RotatorOptions options,
        ILogger<OrphansCommand> logger)
    {
        _entraIdService = entraIdService;
        _keyVaultService = keyVaultService;
        _metrics = metrics;
        _options = options;
        _logger = logger;
    }

    public Command BuildCommand()
    {
        var command = new Command("orphans", "Find unmapped expiring secrets");

        var filterOption = new Option<string?>("--filter", "-f") { Description = "Filter app registrations by name (case-insensitive contains)" };

        var daysOption = new Option<int?>("--days", "-d") { Description = "Consider secrets expiring within N days" };

        var notExpiredOption = new Option<bool?>("--not-expired", "-n") { Description = "Exclude already expired secrets" };

        var outputOption = new Option<OutputFormat?>("--output", "-o") { Description = "Output format: table, json, or yaml" };

        command.Options.Add(filterOption);
        command.Options.Add(daysOption);
        command.Options.Add(notExpiredOption);
        command.Options.Add(outputOption);

        command.SetAction(async (parseResult, _) =>
        {
            var filterCli = parseResult.GetValue(filterOption);
            var daysCli = parseResult.GetValue(daysOption);
            var notExpiredCli = parseResult.GetValue(notExpiredOption);
            var outputCli = parseResult.GetValue(outputOption);
            // CLI args override everything when explicitly provided
            // Otherwise: env var > appsettings > default
            var filter = filterCli ?? _options.GetAppFilter("myfilter");
            var days = daysCli ?? _options.GetDays(30);
            var notExpired = notExpiredCli ?? _options.GetNotExpired(false);
            var output = outputCli ?? _options.GetOutput(OutputFormat.Table);

            await ExecuteAsync(filter, days, notExpired, output);
        });

        return command;
    }

    private async Task ExecuteAsync(string filter, int days, bool notExpired, OutputFormat output)
    {
        _logger.LogInformation("Fetching app registrations with filter: {Filter}", filter);
        _logger.LogInformation("Looking for orphan secrets expiring within {Days} days", days);

        if (notExpired)
        {
            _logger.LogInformation("Excluding already expired secrets");
        }

        // Fetch app registrations
        var apps = await _entraIdService.ListAppRegistrationsAsync(filter);
        _logger.LogInformation("Found {Count} app registrations", apps.Count);

        // Fetch Key Vault secrets
        var kvSecrets = await _keyVaultService.ListAllSecretsAsync();
        _logger.LogInformation("Found {Count} Key Vault secrets", kvSecrets.Count);

        // Build KV mapping lookup: (clientId, secretId) -> KV secrets
        var kvMappings = kvSecrets
            .Where(kv => kv.Status == MappingStatus.Mapped)
            .GroupBy(kv => (kv.ClientId!, kv.SecretId!))
            .ToDictionary(g => g.Key, g => g.ToList());

        // Previous-version mappings: (clientId, secretId) -> KV secrets whose previous version
        // still points at the secret. This is the rotator's footprint and proves it was replaced.
        var kvPreviousMappings = kvSecrets
            .Where(kv => kv.HasPreviousMapping)
            .GroupBy(kv => (kv.PreviousMapping!.ClientId, kv.PreviousMapping!.SecretId))
            .ToDictionary(g => g.Key, g => g.ToList());

        // Find orphans
        var orphans = new List<OrphanAlert>();
        foreach (var app in apps)
        {
            // Same rule as RotationPlanBuilder: a healthy secret is one not expiring within the threshold
            var hasHealthySecret = app.Secrets.Any(s =>
                (int)(s.EndDateTime - DateTimeOffset.UtcNow).TotalDays > days);

            foreach (var secret in app.Secrets)
            {
                var daysUntilExpiry = (int)(secret.EndDateTime - DateTimeOffset.UtcNow).TotalDays;

                // Skip healthy secrets (not expiring within threshold)
                if (daysUntilExpiry > days)
                    continue;

                // Skip expired if notExpired is true
                if (daysUntilExpiry < 0 && notExpired)
                    continue;

                var key = (app.AppId, secret.KeyId);
                var hasMappings = kvMappings.ContainsKey(key);

                if (!hasMappings)
                {
                    // Same rule as RotationPlanBuilder: only the previous-version proof marks a
                    // secret as pending deletion; a healthy sibling alone does not.
                    var pendingDeletion = kvPreviousMappings.ContainsKey(key) && hasHealthySecret;

                    orphans.Add(new OrphanAlert
                    {
                        ClientId = app.AppId,
                        AppName = app.DisplayName,
                        SecretId = secret.KeyId,
                        DaysUntilExpiry = daysUntilExpiry,
                        PendingDeletion = pendingDeletion
                    });

                    // Emit metric for each orphan
                    _metrics.RecordOrphanSecret(app.DisplayName, app.AppId, daysUntilExpiry, pendingDeletion);
                }
            }
        }

        // Output results
        switch (output)
        {
            case OutputFormat.Json:
                OutputJson(orphans, days, notExpired);
                break;
            case OutputFormat.Yaml:
                OutputYaml(orphans, days, notExpired);
                break;
            default:
                OutputTable(orphans, days, notExpired);
                break;
        }
    }

    private void OutputTable(IReadOnlyList<OrphanAlert> orphans, int daysThreshold, bool notExpired)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("ORPHAN SECRETS (no Key Vault mapping)");
        sb.AppendLine(new string('=', 65));
        sb.AppendLine();

        if (!orphans.Any())
        {
            var expiredText = notExpired ? " (excluding already expired)" : "";
            sb.AppendLine($"No orphan secrets found within {daysThreshold} days{expiredText}");
            _logger.LogDebug("Orphans command output:\n{Output}", sb.ToString());
            return;
        }

        sb.AppendLine($"Found {orphans.Count} orphan secret(s) that need attention:");
        sb.AppendLine();

        foreach (var orphan in orphans)
        {
            var status = orphan.DaysUntilExpiry < 0 ? "EXPIRED" : "EXPIRING";
            sb.AppendLine($"  [{status}] {orphan.AppName}");
            sb.AppendLine($"      Client ID: {orphan.ClientId}");
            sb.AppendLine($"      Secret ID: {orphan.SecretId}");
            sb.AppendLine($"      Days until expiry: {orphan.DaysUntilExpiry}");
            sb.AppendLine($"      Pending deletion: {(orphan.PendingDeletion ? "yes (replaced by rotator, deleted at expiry)" : "no (not tracked - notify owner)")}");
            sb.AppendLine();
        }

        // Summary
        var expired = orphans.Count(o => o.DaysUntilExpiry < 0);
        var expiring = orphans.Count(o => o.DaysUntilExpiry >= 0);

        sb.AppendLine($"Summary: {orphans.Count} orphans ({expired} expired, {expiring} expiring)");

        _logger.LogDebug("Orphans command output:\n{Output}", sb.ToString());
    }

    private static object ToOutputModel(IReadOnlyList<OrphanAlert> orphans, int daysThreshold, bool notExpired)
    {
        return new
        {
            DaysThreshold = daysThreshold,
            ExcludeExpired = notExpired,
            OrphanCount = orphans.Count,
            ExpiredCount = orphans.Count(o => o.DaysUntilExpiry < 0),
            ExpiringCount = orphans.Count(o => o.DaysUntilExpiry >= 0),
            Orphans = orphans.Select(o => new
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

    private static void OutputJson(IReadOnlyList<OrphanAlert> orphans, int daysThreshold, bool notExpired)
    {
        var model = ToOutputModel(orphans, daysThreshold, notExpired);
        var json = JsonSerializer.Serialize(model, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        Console.WriteLine(json);
    }

    private static void OutputYaml(IReadOnlyList<OrphanAlert> orphans, int daysThreshold, bool notExpired)
    {
        var model = ToOutputModel(orphans, daysThreshold, notExpired);
        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        var yaml = serializer.Serialize(model);
        Console.WriteLine(yaml);
    }
}
