using EntraIDSecretRotator.Models;

namespace EntraIDSecretRotator.Services;

/// <summary>
/// Service for interacting with Entra ID (Azure AD) app registrations.
/// </summary>
public interface IEntraIdService
{
    /// <summary>
    /// List all app registrations matching the given filter.
    /// </summary>
    /// <param name="nameFilter">Filter by display name (case-insensitive contains).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of app registrations with their secrets.</returns>
    Task<IReadOnlyList<AppRegistration>> ListAppRegistrationsAsync(
        string nameFilter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new secret on an app registration.
    /// </summary>
    /// <param name="objectId">The Entra ID object ID of the application.</param>
    /// <param name="displayName">Display name for the secret (e.g., "Rotated by EntraIDSecretRotator, 2026-01-06").</param>
    /// <param name="validityDays">Number of days the secret should be valid.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created secret info including the secret value.</returns>
    Task<CreatedSecretInfo> CreateSecretAsync(
        string objectId,
        string displayName,
        int validityDays,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a secret from an app registration.
    /// Used for rollback when Key Vault update fails, and for cleanup of expired secrets.
    /// </summary>
    /// <param name="objectId">The Entra ID object ID of the application.</param>
    /// <param name="keyId">The KeyId of the secret to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteSecretAsync(
        string objectId,
        string keyId,
        CancellationToken cancellationToken = default);
}
