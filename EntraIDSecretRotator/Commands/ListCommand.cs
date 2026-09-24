using System.CommandLine;
using System.Text;
using System.Text.Json;
using EntraIDSecretRotator.Infrastructure;
using EntraIDSecretRotator.Services;
using EntraIDSecretRotator.Telemetry;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace EntraIDSecretRotator.Commands;

/// <summary>
/// Command to list all app registration secrets with expiry information.
/// </summary>
public sealed class ListCommand
{
    private readonly IEntraIdService _entraIdService;
    private readonly Metrics _metrics;
    private readonly RotatorOptions _options;
    private readonly ILogger<ListCommand> _logger;

    public ListCommand(
        IEntraIdService entraIdService,
        Metrics metrics,
        RotatorOptions options,
        ILogger<ListCommand> logger)
    {
        _entraIdService = entraIdService;
        _metrics = metrics;
        _options = options;
        _logger = logger;
    }

    public Command BuildCommand()
    {
        var command = new Command("list", "List all app registration secrets with expiry information");

        var appFilterOption = new Option<string?>("--app-filter", "-f") { Description = "Filter app registrations by name (case-insensitive contains)" };

        var outputOption = new Option<OutputFormat?>("--output", "-o") { Description = "Output format: table, json, or yaml" };

        var dryRunOption = new Option<bool?>("--dry-run", "-n") { Description = "Dry run mode (no changes made, but useful for testing filters)" };

        command.Options.Add(appFilterOption);
        command.Options.Add(outputOption);
        command.Options.Add(dryRunOption);

        command.SetAction(async (parseResult, _) =>
        {
            var appFilterCli = parseResult.GetValue(appFilterOption);
            var outputCli = parseResult.GetValue(outputOption);
            var dryRunCli = parseResult.GetValue(dryRunOption);
            // CLI args override everything when explicitly provided
            // Otherwise: env var > appsettings > default
            var appFilter = appFilterCli ?? _options.GetAppFilter("myfilter");
            var output = outputCli ?? _options.GetOutput(OutputFormat.Table);
            var dryRun = dryRunCli ?? _options.GetDryRun(false);

            await ExecuteAsync(appFilter, output, dryRun);
        });

        return command;
    }

    private async Task ExecuteAsync(string appFilter, OutputFormat output, bool dryRun)
    {
        if (dryRun)
        {
            _logger.LogInformation("DRY RUN MODE - no changes will be made");
        }

        _logger.LogInformation("Fetching app registrations with filter: {Filter}", appFilter);

        var apps = await _entraIdService.ListAppRegistrationsAsync(appFilter);

        // Update metrics
        _metrics.RecordAppRegistrationCount(apps.Count);

        var expiryBuckets = new Dictionary<string, int>
        {
            ["expired"] = 0,
            ["30d"] = 0,
            ["60d"] = 0,
            ["90d"] = 0,
            ["90d+"] = 0
        };

        _metrics.ClearSecretExpiryInfo();
        foreach (var app in apps)
        {
            foreach (var secret in app.Secrets)
            {
                expiryBuckets[secret.ExpiryBucket]++;
                _metrics.RecordSecretExpiryInfo(app.DisplayName, app.AppId, secret.ExpiryBucket, secret.DaysUntilExpiry);
            }
        }

        _metrics.RecordSecretsExpiringByBucket(expiryBuckets);

        switch (output)
        {
            case OutputFormat.Json:
                OutputJson(apps);
                break;
            case OutputFormat.Yaml:
                OutputYaml(apps);
                break;
            default:
                OutputTable(apps);
                break;
        }
    }

    private void OutputTable(IReadOnlyList<Models.AppRegistration> apps)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"\nFound {apps.Count} app registration(s)\n");

        foreach (var app in apps)
        {
            sb.AppendLine($"App: {app.DisplayName}");
            sb.AppendLine($"  App ID: {app.AppId}");
            sb.AppendLine($"  Secrets: {app.SecretCount}");

            if (app.Secrets.Any())
            {
                sb.AppendLine();
                foreach (var secret in app.Secrets)
                {
                    var status = secret.IsExpired ? "EXPIRED" : $"{secret.DaysUntilExpiry} days";
                    var description = string.IsNullOrEmpty(secret.DisplayName) ? "**MISSING**" : secret.DisplayName;
                    sb.AppendLine($"    - Description: {description}");
                    sb.AppendLine($"      Key ID: {secret.KeyId}");
                    sb.AppendLine($"      Expires: {secret.EndDateTime:yyyy-MM-dd} ({status})");
                    sb.AppendLine($"      Bucket: {secret.ExpiryBucket}");
                }
            }
            else
            {
                sb.AppendLine("  (No secrets found)");
            }

            sb.AppendLine();
        }

        // Summary
        var totalSecrets = apps.Sum(a => a.SecretCount);
        var expiredSecrets = apps.Sum(a => a.ExpiredSecretCount);
        var expiring30d = apps.Sum(a => a.Secrets.Count(s => s.ExpiryBucket == "30d"));
        var expiring60d = apps.Sum(a => a.Secrets.Count(s => s.ExpiryBucket == "60d"));

        sb.AppendLine("Summary:");
        sb.AppendLine($"  Total secrets: {totalSecrets}");
        sb.AppendLine($"  Expired: {expiredSecrets}");
        sb.AppendLine($"  Expiring in 30 days: {expiring30d}");
        sb.AppendLine($"  Expiring in 60 days: {expiring60d}");

        _logger.LogDebug("List command output:\n{Output}", sb.ToString());
    }

    private static object ToOutputModel(IReadOnlyList<Models.AppRegistration> apps)
    {
        return apps.Select(app => new
        {
            app.DisplayName,
            app.AppId,
            app.Id,
            app.SecretCount,
            app.ExpiredSecretCount,
            Secrets = app.Secrets.Select(s => new
            {
                s.KeyId,
                Description = string.IsNullOrEmpty(s.DisplayName) ? "**MISSING**" : s.DisplayName,
                s.EndDateTime,
                s.DaysUntilExpiry,
                s.IsExpired,
                s.ExpiryBucket
            }).ToList()
        }).ToList();
    }

    private static void OutputJson(IReadOnlyList<Models.AppRegistration> apps)
    {
        var model = ToOutputModel(apps);
        var json = JsonSerializer.Serialize(model, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        Console.WriteLine(json);
    }

    private static void OutputYaml(IReadOnlyList<Models.AppRegistration> apps)
    {
        var model = ToOutputModel(apps);
        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new DateTimeOffsetConverter())
            .Build();
        var yaml = serializer.Serialize(model);
        Console.WriteLine(yaml);
    }
}
