namespace EntraIDSecretRotator.Models;

/// <summary>
/// Represents a secret (password credential) for an Entra ID application registration.
/// </summary>
public sealed record SecretInfo
{
    /// <summary>
    /// The unique identifier of the secret.
    /// </summary>
    public required string KeyId { get; init; }

    /// <summary>
    /// Display name/hint for the secret.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// When the secret was created.
    /// </summary>
    public DateTimeOffset? StartDateTime { get; init; }

    /// <summary>
    /// When the secret expires.
    /// </summary>
    public required DateTimeOffset EndDateTime { get; init; }

    /// <summary>
    /// Calculated days until expiry (negative if expired).
    /// </summary>
    public int DaysUntilExpiry => (int)(EndDateTime - DateTimeOffset.UtcNow).TotalDays;

    /// <summary>
    /// Whether this secret has already expired.
    /// </summary>
    public bool IsExpired => EndDateTime <= DateTimeOffset.UtcNow;

    /// <summary>
    /// Whether this secret is expiring within the given threshold.
    /// </summary>
    public bool IsExpiringSoon(int daysThreshold) =>
        !IsExpired && DaysUntilExpiry <= daysThreshold;

    /// <summary>
    /// Bucket label for metrics (30d, 60d, 90d, 90d+).
    /// </summary>
    public string ExpiryBucket
    {
        get
        {
            if (IsExpired) return "expired";
            if (DaysUntilExpiry <= 30) return "30d";
            if (DaysUntilExpiry <= 60) return "60d";
            if (DaysUntilExpiry <= 90) return "90d";
            return "90d+";
        }
    }
}
