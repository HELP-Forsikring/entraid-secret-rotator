namespace EntraIDSecretRotator.Models;

/// <summary>
/// Result of creating a new secret on an Entra ID app registration.
/// </summary>
public sealed record CreatedSecretInfo
{
    /// <summary>
    /// The unique identifier (KeyId) of the newly created secret.
    /// </summary>
    public required string KeyId { get; init; }

    /// <summary>
    /// The actual secret value (only available at creation time).
    /// </summary>
    public required string SecretValue { get; init; }

    /// <summary>
    /// When the secret was created.
    /// </summary>
    public required DateTimeOffset StartDateTime { get; init; }

    /// <summary>
    /// When the secret expires.
    /// </summary>
    public required DateTimeOffset EndDateTime { get; init; }

    /// <summary>
    /// Display name/description of the secret.
    /// </summary>
    public string? DisplayName { get; init; }
}
