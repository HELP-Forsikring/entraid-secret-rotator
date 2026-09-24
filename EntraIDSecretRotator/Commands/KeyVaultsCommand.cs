using System.CommandLine;
using System.Text;
using System.Text.Json;
using EntraIDSecretRotator.Infrastructure;
using EntraIDSecretRotator.Models;
using EntraIDSecretRotator.Services;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace EntraIDSecretRotator.Commands;

/// <summary>
/// Command to list all secrets from Key Vaults with their mapping information.
/// </summary>
public sealed class KeyVaultsCommand
{
    private readonly IKeyVaultService _keyVaultService;
    private readonly RotatorOptions _options;
    private readonly ILogger<KeyVaultsCommand> _logger;

    public KeyVaultsCommand(
        IKeyVaultService keyVaultService,
        RotatorOptions options,
        ILogger<KeyVaultsCommand> logger)
    {
        _keyVaultService = keyVaultService;
        _options = options;
        _logger = logger;
    }

    public Command BuildCommand()
    {
        var command = new Command("keyvaults", "List all secrets from Key Vaults with mapping information");

        var outputOption = new Option<OutputFormat?>("--output", "-o") { Description = "Output format: table, json, or yaml" };

        var vaultOption = new Option<string?>("--vault", "-v") { Description = "Filter to a specific Key Vault name" };

        var statusOption = new Option<MappingStatus?>("--status", "-s") { Description = "Filter by mapping status: mapped, unmapped, or invalid" };

        command.Options.Add(outputOption);
        command.Options.Add(vaultOption);
        command.Options.Add(statusOption);

        command.SetAction(async (parseResult, _) =>
        {
            var outputCli = parseResult.GetValue(outputOption);
            var vaultCli = parseResult.GetValue(vaultOption);
            var statusCli = parseResult.GetValue(statusOption);
            // CLI args override everything when explicitly provided
            // Otherwise: env var > appsettings > default
            var output = outputCli ?? _options.GetOutput(OutputFormat.Table);
            await ExecuteAsync(output, vaultCli, statusCli);
        });

        return command;
    }

    private async Task ExecuteAsync(OutputFormat output, string? vaultFilter, MappingStatus? statusFilter)
    {
        _logger.LogInformation("Listing secrets from Key Vaults");

        if (vaultFilter != null)
        {
            _logger.LogInformation("Filtering to vault: {VaultName}", vaultFilter);
        }

        if (statusFilter != null)
        {
            _logger.LogInformation("Filtering by status: {Status}", statusFilter);
        }

        IReadOnlyList<KeyVaultSecretInfo> secrets;

        if (vaultFilter != null)
        {
            secrets = await _keyVaultService.ListSecretsAsync(vaultFilter);
        }
        else
        {
            secrets = await _keyVaultService.ListAllSecretsAsync();
        }

        // Apply status filter if specified
        if (statusFilter != null)
        {
            secrets = secrets.Where(s => s.Status == statusFilter.Value).ToList();
        }

        switch (output)
        {
            case OutputFormat.Json:
                OutputJson(secrets);
                break;
            case OutputFormat.Yaml:
                OutputYaml(secrets);
                break;
            default:
                OutputTable(secrets);
                break;
        }
    }

    private void OutputTable(IReadOnlyList<KeyVaultSecretInfo> secrets)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"\nFound {secrets.Count} secret(s)\n");

        if (!secrets.Any())
        {
            sb.AppendLine("No secrets found!");
            _logger.LogDebug("KeyVaults command output:\n{Output}", sb.ToString());
            return;
        }

        // Group by vault for better readability
        var grouped = secrets.GroupBy(s => s.VaultName);

        foreach (var group in grouped)
        {
            sb.AppendLine($"Vault: {group.Key}");
            sb.AppendLine(new string('-', 80));

            foreach (var secret in group)
            {
                var statusText = secret.Status switch
                {
                    MappingStatus.Mapped => "MAPPED",
                    MappingStatus.Unmapped => "UNMAPPED",
                    MappingStatus.Invalid => "INVALID",
                    _ => "UNKNOWN"
                };

                sb.AppendLine($"  Secret: {secret.SecretName}");
                sb.AppendLine($"    Status: {statusText}");

                if (secret.IsValidMapping)
                {
                    sb.AppendLine($"    App Name: {secret.AppRegName}");
                    sb.AppendLine($"    Client ID: {secret.ClientId}");
                    sb.AppendLine($"    Secret ID: {secret.SecretId}");

                    if (secret.HasPreviousMapping)
                    {
                        sb.AppendLine($"    Previous Secret ID: {secret.PreviousMapping!.SecretId} (can be deleted)");
                    }
                }
                else if (!string.IsNullOrEmpty(secret.ContentType))
                {
                    sb.AppendLine($"    Content-Type: {secret.ContentType} (invalid format)");
                }
                else
                {
                    sb.AppendLine("    Content-Type: (not set)");
                }

                sb.AppendLine();
            }
        }

        // Summary
        var mapped = secrets.Count(s => s.Status == MappingStatus.Mapped);
        var unmapped = secrets.Count(s => s.Status == MappingStatus.Unmapped);
        var invalid = secrets.Count(s => s.Status == MappingStatus.Invalid);
        var withPreviousMapping = secrets.Count(s => s.HasPreviousMapping);

        sb.AppendLine("Summary:");
        sb.AppendLine($"  Total secrets: {secrets.Count}");
        sb.AppendLine($"  Mapped: {mapped}");
        sb.AppendLine($"  Unmapped: {unmapped}");
        sb.AppendLine($"  Invalid format: {invalid}");
        sb.AppendLine($"  With previous version (rotated): {withPreviousMapping}");

        // List vaults
        sb.AppendLine("\nVaults:");
        foreach (var vault in secrets.Select(s => s.VaultName).Distinct().OrderBy(v => v))
        {
            var vaultSecrets = secrets.Where(s => s.VaultName == vault).ToList();
            var vaultMapped = vaultSecrets.Count(s => s.Status == MappingStatus.Mapped);
            sb.AppendLine($"  {vault}: {vaultSecrets.Count} secrets ({vaultMapped} mapped)");
        }

        _logger.LogDebug("KeyVaults command output:\n{Output}", sb.ToString());
    }

    private static object ToOutputModel(IReadOnlyList<KeyVaultSecretInfo> secrets)
    {
        return new
        {
            TotalCount = secrets.Count,
            MappedCount = secrets.Count(s => s.Status == MappingStatus.Mapped),
            UnmappedCount = secrets.Count(s => s.Status == MappingStatus.Unmapped),
            InvalidCount = secrets.Count(s => s.Status == MappingStatus.Invalid),
            WithPreviousMappingCount = secrets.Count(s => s.HasPreviousMapping),
            Secrets = secrets.Select(s => new
            {
                s.VaultName,
                s.SecretName,
                s.ContentType,
                Status = s.Status.ToString(),
                s.AppRegName,
                s.ClientId,
                s.SecretId,
                s.IsValidMapping,
                s.HasPreviousMapping,
                PreviousSecretId = s.PreviousMapping?.SecretId,
                PreviousClientId = s.PreviousMapping?.ClientId,
                PreviousAppRegName = s.PreviousMapping?.AppRegName
            }).ToList(),
            ByVault = secrets
                .GroupBy(s => s.VaultName)
                .Select(g => new
                {
                    VaultName = g.Key,
                    SecretCount = g.Count(),
                    MappedCount = g.Count(s => s.Status == MappingStatus.Mapped),
                    UnmappedCount = g.Count(s => s.Status == MappingStatus.Unmapped),
                    InvalidCount = g.Count(s => s.Status == MappingStatus.Invalid),
                    WithPreviousMappingCount = g.Count(s => s.HasPreviousMapping)
                })
                .ToList()
        };
    }

    private static void OutputJson(IReadOnlyList<KeyVaultSecretInfo> secrets)
    {
        var model = ToOutputModel(secrets);
        var json = JsonSerializer.Serialize(model, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        Console.WriteLine(json);
    }

    private static void OutputYaml(IReadOnlyList<KeyVaultSecretInfo> secrets)
    {
        var model = ToOutputModel(secrets);
        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        var yaml = serializer.Serialize(model);
        Console.WriteLine(yaml);
    }
}
