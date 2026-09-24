namespace EntraIDSecretRotator.Infrastructure;

/// <summary>
/// Configuration options for the secret rotator.
/// Binds to the "Rotator" section in appsettings.json.
/// Environment variables use the Rotator__ prefix (e.g., Rotator__AppFilter).
/// </summary>
public sealed class RotatorOptions
{
    public const string SectionName = "Rotator";
    private const string EnvPrefix = "Rotator__";

    public string? AppFilter { get; set; }
    public string? Output { get; set; }
    public int? Days { get; set; }
    public bool? NotExpired { get; set; }
    public bool? DryRun { get; set; }
    public int? ValidityDays { get; set; }
    public string[]? KeyVaults { get; set; }

    /// <summary>
    /// Gets the effective app filter with priority: env var > config > default.
    /// </summary>
    public string GetAppFilter(string defaultValue)
    {
        var envValue = GetEnvVar("AppFilter");
        if (!string.IsNullOrEmpty(envValue))
            return envValue;

        return AppFilter ?? defaultValue;
    }

    /// <summary>
    /// Gets the effective output format with priority: env var > config > default.
    /// </summary>
    public OutputFormat GetOutput(OutputFormat defaultValue)
    {
        var envValue = GetEnvVar("Output");
        if (!string.IsNullOrEmpty(envValue) && Enum.TryParse<OutputFormat>(envValue, ignoreCase: true, out var envFormat))
            return envFormat;

        if (!string.IsNullOrEmpty(Output) && Enum.TryParse<OutputFormat>(Output, ignoreCase: true, out var configFormat))
            return configFormat;

        return defaultValue;
    }

    /// <summary>
    /// Gets the effective days threshold with priority: env var > config > default.
    /// </summary>
    public int GetDays(int defaultValue)
    {
        var envValue = GetEnvVar("Days");
        if (!string.IsNullOrEmpty(envValue) && int.TryParse(envValue, out var envDays))
            return envDays;

        return Days ?? defaultValue;
    }

    /// <summary>
    /// Gets the effective not-expired flag with priority: env var > config > default.
    /// </summary>
    public bool GetNotExpired(bool defaultValue)
    {
        var envValue = GetEnvVar("NotExpired");
        if (!string.IsNullOrEmpty(envValue) && bool.TryParse(envValue, out var envFlag))
            return envFlag;

        return NotExpired ?? defaultValue;
    }

    /// <summary>
    /// Gets the effective dry-run flag with priority: env var > config > default.
    /// </summary>
    public bool GetDryRun(bool defaultValue)
    {
        var envValue = GetEnvVar("DryRun");
        if (!string.IsNullOrEmpty(envValue) && bool.TryParse(envValue, out var envFlag))
            return envFlag;

        return DryRun ?? defaultValue;
    }

    /// <summary>
    /// Gets the effective validity days with priority: env var > config > default.
    /// </summary>
    public int GetValidityDays(int defaultValue)
    {
        var envValue = GetEnvVar("ValidityDays");
        if (!string.IsNullOrEmpty(envValue) && int.TryParse(envValue, out var envDays))
            return envDays;

        return ValidityDays ?? defaultValue;
    }

    /// <summary>
    /// Gets the effective Key Vault names with priority: env var > config.
    /// Environment variable can be comma-separated: "vault1,vault2,vault3"
    /// </summary>
    public IReadOnlyList<string> GetKeyVaults()
    {
        var envValue = GetEnvVar("KeyVaults");
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            // Support comma-separated values in env var
            if (envValue.Contains(','))
            {
                return envValue
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToArray();
            }
            // Single vault
            return new[] { envValue };
        }

        return KeyVaults ?? Array.Empty<string>();
    }

    private static string? GetEnvVar(string name) =>
        Environment.GetEnvironmentVariable($"{EnvPrefix}{name}");
}
