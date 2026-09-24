namespace EntraIDSecretRotator.Models;

/// <summary>
/// Represents an Entra ID application registration with its secrets.
/// </summary>
public sealed record AppRegistration
{
    /// <summary>
    /// The application (client) ID.
    /// </summary>
    public required string AppId { get; init; }

    /// <summary>
    /// The internal object ID.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Display name of the application.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// List of secrets (password credentials) for this application.
    /// </summary>
    public required IReadOnlyList<SecretInfo> Secrets { get; init; }

    /// <summary>
    /// Count of total secrets.
    /// </summary>
    public int SecretCount => Secrets.Count;

    /// <summary>
    /// Count of expired secrets.
    /// </summary>
    public int ExpiredSecretCount => Secrets.Count(s => s.IsExpired);

    /// <summary>
    /// Count of secrets expiring within a threshold.
    /// </summary>
    public int ExpiringSecretCount(int daysThreshold) =>
        Secrets.Count(s => s.IsExpiringSoon(daysThreshold));

    /// <summary>
    /// Whether any secrets are expired.
    /// </summary>
    public bool HasExpiredSecrets => ExpiredSecretCount > 0;

    /// <summary>
    /// Whether any secrets are expiring soon.
    /// </summary>
    public bool HasExpiringSecrets(int daysThreshold) =>
        ExpiringSecretCount(daysThreshold) > 0;
}
