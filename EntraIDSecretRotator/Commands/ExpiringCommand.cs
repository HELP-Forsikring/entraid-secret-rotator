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
/// Command to list secrets expiring within N days.
/// </summary>
public sealed class ExpiringCommand
{
    private readonly IEntraIdService _entraIdService;
    private readonly Metrics _metrics;
    private readonly RotatorOptions _options;
    private readonly ILogger<ExpiringCommand> _logger;

    public ExpiringCommand(
        IEntraIdService entraIdService,
        Metrics metrics,
        RotatorOptions options,
        ILogger<ExpiringCommand> logger)
    {
        _entraIdService = entraIdService;
        _metrics = metrics;
        _options = options;
        _logger = logger;
    }

    public Command BuildCommand()
    {
        var command = new Command("expiring", "List secrets expiring within N days");

        var daysOption = new Option<int?>("--days", "-d") { Description = "Number of days threshold for expiring secrets" };

        var appFilterOption = new Option<string?>("--app-filter", "-f") { Description = "Filter app registrations by name (case-insensitive contains)" };

        var outputOption = new Option<OutputFormat?>("--output", "-o") { Description = "Output format: table, json, or yaml" };

        var notExpiredOption = new Option<bool?>("--not-expired") { Description = "Exclude already expired secrets from the output" };

        var dryRunOption = new Option<bool?>("--dry-run", "-n") { Description = "Dry run mode (no changes made, but useful for testing filters)" };

        command.Options.Add(daysOption);
        command.Options.Add(appFilterOption);
        command.Options.Add(outputOption);
        command.Options.Add(notExpiredOption);
        command.Options.Add(dryRunOption);

        command.SetAction(async (parseResult, _) =>
        {
            var daysCli = parseResult.GetValue(daysOption);
            var appFilterCli = parseResult.GetValue(appFilterOption);
            var outputCli = parseResult.GetValue(outputOption);
            var notExpiredCli = parseResult.GetValue(notExpiredOption);
            var dryRunCli = parseResult.GetValue(dryRunOption);
            // CLI args override everything when explicitly provided
            // Otherwise: env var > appsettings > default
            var days = daysCli ?? _options.GetDays(30);
            var appFilter = appFilterCli ?? _options.GetAppFilter("myfilter");
            var output = outputCli ?? _options.GetOutput(OutputFormat.Table);
            var notExpired = notExpiredCli ?? _options.GetNotExpired(false);
            var dryRun = dryRunCli ?? _options.GetDryRun(false);

            await ExecuteAsync(days, appFilter, output, notExpired, dryRun);
        });

        return command;
    }

    private async Task ExecuteAsync(int days, string appFilter, OutputFormat output, bool notExpired, bool dryRun)
    {
        if (dryRun)
        {
            _logger.LogInformation("DRY RUN MODE - no changes will be made");
        }

        _logger.LogInformation("Fetching app registrations with filter: {Filter}", appFilter);
        _logger.LogInformation("Looking for secrets expiring within {Days} days", days);

        if (notExpired)
        {
            _logger.LogInformation("Excluding already expired secrets");
        }

        var apps = await _entraIdService.ListAppRegistrationsAsync(appFilter);

        // Filter to only apps with expiring (or expired, unless --not-expired is set) secrets
        var appsWithExpiringSecrets = apps
            .Select(app => new
            {
                App = app,
                ExpiringSecrets = app.Secrets
                    .Where(s => notExpired
                        ? s.IsExpiringSoon(days)
                        : s.IsExpired || s.IsExpiringSoon(days))
                    .ToList()
            })
            .Where(x => x.ExpiringSecrets.Any())
            .Select(x => new Models.AppRegistration
            {
                Id = x.App.Id,
                AppId = x.App.AppId,
                DisplayName = x.App.DisplayName,
                Secrets = x.ExpiringSecrets
            })
            .ToList();

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
                OutputJson(appsWithExpiringSecrets, days, notExpired);
                break;
            case OutputFormat.Yaml:
                OutputYaml(appsWithExpiringSecrets, days, notExpired);
                break;
            default:
                OutputTable(appsWithExpiringSecrets, days, notExpired);
                break;
        }
    }

    private void OutputTable(IReadOnlyList<Models.AppRegistration> apps, int daysThreshold, bool notExpired)
    {
        var sb = new StringBuilder();
        var expiredText = notExpired ? " (excluding already expired)" : "";
        sb.AppendLine($"\nFound {apps.Count} app registration(s) with secrets expiring within {daysThreshold} days{expiredText}\n");

        if (!apps.Any())
        {
            sb.AppendLine("No expiring secrets found!");
            _logger.LogDebug("Expiring command output:\n{Output}", sb.ToString());
            return;
        }

        foreach (var app in apps)
        {
            sb.AppendLine($"App: {app.DisplayName}");
            sb.AppendLine($"  App ID: {app.AppId}");
            sb.AppendLine($"  Expiring secrets: {app.SecretCount}");
            sb.AppendLine();

            foreach (var secret in app.Secrets)
            {
                var status = secret.IsExpired
                    ? $"EXPIRED ({Math.Abs(secret.DaysUntilExpiry)} days ago)"
                    : $"{secret.DaysUntilExpiry} days remaining";

                var description = string.IsNullOrEmpty(secret.DisplayName) ? "**MISSING**" : secret.DisplayName;

                sb.AppendLine($"    - Description: {description}");
                sb.AppendLine($"      Key ID: {secret.KeyId}");
                sb.AppendLine($"      Expires: {secret.EndDateTime:yyyy-MM-dd} ({status})");

                if (secret.IsExpired)
                {
                    sb.AppendLine("      STATUS: *** EXPIRED ***");
                }
                else if (secret.DaysUntilExpiry <= 7)
                {
                    sb.AppendLine("      STATUS: *** CRITICAL - Expires in 7 days or less ***");
                }
                else if (secret.DaysUntilExpiry <= 30)
                {
                    sb.AppendLine("      STATUS: WARNING - Expires soon");
                }

                sb.AppendLine();
            }
        }

        // Summary
        var totalExpiring = apps.Sum(a => a.SecretCount);
        var expired = apps.Sum(a => a.Secrets.Count(s => s.IsExpired));
        var critical = apps.Sum(a => a.Secrets.Count(s => !s.IsExpired && s.DaysUntilExpiry <= 7));
        var warning = apps.Sum(a => a.Secrets.Count(s => !s.IsExpired && s.DaysUntilExpiry > 7 && s.DaysUntilExpiry <= 30));

        sb.AppendLine("Summary:");
        sb.AppendLine($"  Total expiring: {totalExpiring}");
        if (!notExpired)
        {
            sb.AppendLine($"  Already expired: {expired}");
        }
        sb.AppendLine($"  Critical (≤7 days): {critical}");
        sb.AppendLine($"  Warning (≤30 days): {warning}");

        _logger.LogDebug("Expiring command output:\n{Output}", sb.ToString());
    }

    private static object ToOutputModel(IReadOnlyList<Models.AppRegistration> apps, int daysThreshold, bool notExpired)
    {
        return new
        {
            DaysThreshold = daysThreshold,
            ExcludeExpired = notExpired,
            AppCount = apps.Count,
            Apps = apps.Select(app => new
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
            }).ToList()
        };
    }

    private static void OutputJson(IReadOnlyList<Models.AppRegistration> apps, int daysThreshold, bool notExpired)
    {
        var model = ToOutputModel(apps, daysThreshold, notExpired);
        var json = JsonSerializer.Serialize(model, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        Console.WriteLine(json);
    }

    private static void OutputYaml(IReadOnlyList<Models.AppRegistration> apps, int daysThreshold, bool notExpired)
    {
        var model = ToOutputModel(apps, daysThreshold, notExpired);
        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new DateTimeOffsetConverter())
            .Build();
        var yaml = serializer.Serialize(model);
        Console.WriteLine(yaml);
    }
}
