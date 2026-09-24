using EntraIDSecretRotator.Infrastructure;

namespace EntraIDSecretRotator.Models;

/// <summary>
/// Represents a secret from Azure Key Vault with parsed content-type mapping information.
/// Content-type format: {app-reg-name}:{clientId}:{secretId}
/// Example: myfilter-app-reg:12345678-abcd-1234-abcd-123456789abc:12345678-aa11-bb22-cc33-a1b2c3d4e5f6
/// </summary>
public sealed record KeyVaultSecretInfo
{
    /// <summary>
    /// The name of the Key Vault containing this secret.
    /// </summary>
    public required string VaultName { get; init; }

    /// <summary>
    /// The name of the secret.
    /// </summary>
    public required string SecretName { get; init; }

    /// <summary>
    /// The raw content-type value from Key Vault. May be null or empty.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// The parsed app registration name from content-type. Null if not parseable.
    /// </summary>
    public string? AppRegName { get; init; }

    /// <summary>
    /// The parsed client ID (app ID) from content-type. Null if not parseable.
    /// </summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// The parsed secret ID (key ID) from content-type. Null if not parseable.
    /// </summary>
    public string? SecretId { get; init; }

    /// <summary>
    /// Whether the content-type was successfully parsed and contains valid mapping information.
    /// </summary>
    public bool IsValidMapping => !string.IsNullOrEmpty(AppRegName)
                                  && !string.IsNullOrEmpty(ClientId)
                                  && !string.IsNullOrEmpty(SecretId);

    /// <summary>
    /// Gets the mapping status for display purposes.
    /// </summary>
    public MappingStatus Status
    {
        get
        {
            if (IsValidMapping) return MappingStatus.Mapped;
            if (string.IsNullOrEmpty(ContentType)) return MappingStatus.Unmapped;
            return MappingStatus.Invalid;
        }
    }

    /// <summary>
    /// Previous version mapping info, if this secret has been rotated and the previous version was also mapped.
    /// Only populated for secrets where current Status is Mapped.
    /// </summary>
    public PreviousMappingInfo? PreviousMapping { get; init; }

    /// <summary>
    /// Whether this secret has a previous version that was also properly mapped.
    /// Indicates the secret has been rotated at least once.
    /// </summary>
    public bool HasPreviousMapping => PreviousMapping != null;

    /// <summary>
    /// Creates a new instance with the previous mapping information attached.
    /// </summary>
    public KeyVaultSecretInfo WithPreviousMapping(PreviousMappingInfo? previousMapping)
    {
        return this with { PreviousMapping = previousMapping };
    }

    /// <summary>
    /// Creates a new KeyVaultSecretInfo by parsing the content-type.
    /// Invalid or missing content-type is handled gracefully.
    /// </summary>
    public static KeyVaultSecretInfo Create(string vaultName, string secretName, string? contentType)
    {
        Assert.NotNullOrEmpty(vaultName, nameof(vaultName));
        Assert.NotNullOrEmpty(secretName, nameof(secretName));

        var parsed = ParseContentType(contentType);

        var info = new KeyVaultSecretInfo
        {
            VaultName = vaultName,
            SecretName = secretName,
            ContentType = contentType,
            AppRegName = parsed.AppRegName,
            ClientId = parsed.ClientId,
            SecretId = parsed.SecretId
        };

        // Internal invariant: if we claim IsValidMapping is true, all parts must be non-empty
        if (info.IsValidMapping)
        {
            Assert.NotNullOrEmpty(info.AppRegName, nameof(AppRegName));
            Assert.NotNullOrEmpty(info.ClientId, nameof(ClientId));
            Assert.NotNullOrEmpty(info.SecretId, nameof(SecretId));
        }

        return info;
    }

    /// <summary>
    /// Parses the content-type format: {app-reg-name}:{clientId}:{secretId}
    /// Returns null values for parts that cannot be parsed.
    /// </summary>
    internal static (string? AppRegName, string? ClientId, string? SecretId) ParseContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return (null, null, null);
        }

        var parts = contentType.Split(':');

        if (parts.Length != 3)
        {
            return (null, null, null);
        }

        var appRegName = parts[0].Trim();
        var clientId = parts[1].Trim();
        var secretId = parts[2].Trim();

        // Validate that all parts are non-empty after trimming
        if (string.IsNullOrEmpty(appRegName) ||
            string.IsNullOrEmpty(clientId) ||
            string.IsNullOrEmpty(secretId))
        {
            return (null, null, null);
        }

        // Internal invariant: after successful parse, all extracted parts must be non-empty
        Assert.That(parts.Length == 3, "parts.Length must be 3 after successful format validation");
        Assert.NotNullOrEmpty(appRegName, "appRegName after parse");
        Assert.NotNullOrEmpty(clientId, "clientId after parse");
        Assert.NotNullOrEmpty(secretId, "secretId after parse");

        return (appRegName, clientId, secretId);
    }
}

/// <summary>
/// Represents the mapping status of a Key Vault secret to an Entra ID app registration.
/// </summary>
public enum MappingStatus
{
    /// <summary>
    /// Secret has a valid content-type with all three parts: app name, client ID, and secret ID.
    /// </summary>
    Mapped,

    /// <summary>
    /// Secret has no content-type set (null or empty).
    /// </summary>
    Unmapped,

    /// <summary>
    /// Secret has a content-type but it doesn't match the expected format.
    /// </summary>
    Invalid
}

/// <summary>
/// Contains mapping information from a previous version of a Key Vault secret.
/// Used to track rotation history and identify old Entra ID secrets that can be cleaned up.
/// </summary>
public sealed record PreviousMappingInfo
{
    /// <summary>
    /// The app registration name from the previous version's content-type.
    /// </summary>
    public required string AppRegName { get; init; }

    /// <summary>
    /// The client ID from the previous version's content-type.
    /// Should match the current version's ClientId for a valid rotation.
    /// </summary>
    public required string ClientId { get; init; }

    /// <summary>
    /// The secret ID from the previous version's content-type.
    /// This identifies the old Entra ID secret that can be deleted after rotation.
    /// </summary>
    public required string SecretId { get; init; }

    /// <summary>
    /// Creates a PreviousMappingInfo from a parsed content-type, if valid.
    /// Returns null if the content-type doesn't contain a valid mapping.
    /// </summary>
    public static PreviousMappingInfo? CreateFromContentType(string? contentType)
    {
        var parsed = KeyVaultSecretInfo.ParseContentType(contentType);

        if (string.IsNullOrEmpty(parsed.AppRegName) ||
            string.IsNullOrEmpty(parsed.ClientId) ||
            string.IsNullOrEmpty(parsed.SecretId))
        {
            return null;
        }

        return new PreviousMappingInfo
        {
            AppRegName = parsed.AppRegName,
            ClientId = parsed.ClientId,
            SecretId = parsed.SecretId
        };
    }
}
