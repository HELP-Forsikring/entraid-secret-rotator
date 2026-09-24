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
/// Command to list only already expired secrets.
/// </summary>
public sealed class ExpiredCommand
{
    private readonly IEntraIdService _entraIdService;
    private readonly Metrics _metrics;
    private readonly RotatorOptions _options;
    private readonly ILogger<ExpiredCommand> _logger;

    public ExpiredCommand(
        IEntraIdService entraIdService,
        Metrics metrics,
        RotatorOptions options,
        ILogger<ExpiredCommand> logger)
    {
        _entraIdService = entraIdService;
        _metrics = metrics;
        _options = options;
        _logger = logger;
    }

    public Command BuildCommand()
    {
        var command = new Command("expired", "List only already expired secrets");

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
        _logger.LogInformation("Looking for already expired secrets");

        var apps = await _entraIdService.ListAppRegistrationsAsync(appFilter);

        // Filter to only apps with expired secrets
        var appsWithExpiredSecrets = apps
            .Select(app => new
            {
                App = app,
                ExpiredSecrets = app.Secrets
                    .Where(s => s.IsExpired)
                    .ToList()
            })
            .Where(x => x.ExpiredSecrets.Any())
            .Select(x => new Models.AppRegistration
            {
                Id = x.App.Id,
                AppId = x.App.AppId,
                DisplayName = x.App.DisplayName,
                Secrets = x.ExpiredSecrets
            })
            .ToList();

        // Update metrics
        _metrics.RecordAppRegistrationCount(apps.Count);

        switch (output)
        {
            case OutputFormat.Json:
                OutputJson(appsWithExpiredSecrets);
                break;
            case OutputFormat.Yaml:
                OutputYaml(appsWithExpiredSecrets);
                break;
            default:
                OutputTable(appsWithExpiredSecrets);
                break;
        }
    }

    private void OutputTable(IReadOnlyList<Models.AppRegistration> apps)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"\nFound {apps.Count} app registration(s) with expired secrets\n");

        if (!apps.Any())
        {
            sb.AppendLine("No expired secrets found!");
            _logger.LogDebug("Expired command output:\n{Output}", sb.ToString());
            return;
        }

        foreach (var app in apps)
        {
            sb.AppendLine($"App: {app.DisplayName}");
            sb.AppendLine($"  App ID: {app.AppId}");
            sb.AppendLine($"  Expired secrets: {app.SecretCount}");
            sb.AppendLine();

            foreach (var secret in app.Secrets)
            {
                var daysAgo = Math.Abs(secret.DaysUntilExpiry);
                var description = string.IsNullOrEmpty(secret.DisplayName) ? "**MISSING**" : secret.DisplayName;

                sb.AppendLine($"    - Description: {description}");
                sb.AppendLine($"      Key ID: {secret.KeyId}");
                sb.AppendLine($"      Expired: {secret.EndDateTime:yyyy-MM-dd} ({daysAgo} days ago)");
                sb.AppendLine();
            }
        }

        // Summary
        var totalExpired = apps.Sum(a => a.SecretCount);
        sb.AppendLine("Summary:");
        sb.AppendLine($"  Total expired: {totalExpired}");

        _logger.LogDebug("Expired command output:\n{Output}", sb.ToString());
    }

    private static object ToOutputModel(IReadOnlyList<Models.AppRegistration> apps)
    {
        return new
        {
            AppCount = apps.Count,
            TotalExpired = apps.Sum(a => a.SecretCount),
            Apps = apps.Select(app => new
            {
                app.DisplayName,
                app.AppId,
                app.Id,
                app.SecretCount,
                Secrets = app.Secrets.Select(s => new
                {
                    s.KeyId,
                    Description = string.IsNullOrEmpty(s.DisplayName) ? "**MISSING**" : s.DisplayName,
                    s.EndDateTime,
                    s.DaysUntilExpiry,
                    DaysExpired = Math.Abs(s.DaysUntilExpiry)
                }).ToList()
            }).ToList()
        };
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
