using EntraIDSecretRotator.Models;

namespace EntraIDSecretRotator.Services;

/// <summary>
/// Service for interacting with Azure Key Vaults to list and manage secrets.
/// </summary>
public interface IKeyVaultService
{
    /// <summary>
    /// Lists all secrets from all configured Key Vaults.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of secrets with their mapping information.</returns>
    Task<IReadOnlyList<KeyVaultSecretInfo>> ListAllSecretsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists secrets from a specific Key Vault.
    /// </summary>
    /// <param name="vaultName">Name of the Key Vault.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of secrets with their mapping information.</returns>
    Task<IReadOnlyList<KeyVaultSecretInfo>> ListSecretsAsync(string vaultName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the names of all configured Key Vaults.
    /// </summary>
    IReadOnlyList<string> VaultNames { get; }

    /// <summary>
    /// Gets the current value of a secret.
    /// Used to capture the previous value for potential rollback.
    /// </summary>
    /// <param name="vaultName">Name of the Key Vault.</param>
    /// <param name="secretName">Name of the secret.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The secret value.</returns>
    Task<string> GetSecretValueAsync(
        string vaultName,
        string secretName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a secret with a new value and content-type.
    /// This creates a new version of the secret in Key Vault.
    /// </summary>
    /// <param name="vaultName">Name of the Key Vault.</param>
    /// <param name="secretName">Name of the secret.</param>
    /// <param name="value">The new secret value.</param>
    /// <param name="contentType">The new content-type (format: app-name:clientId:secretId).</param>
    /// <param name="expiresOn">When the secret expires (should match Entra ID secret expiry). Null for rollback scenarios.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpdateSecretAsync(
        string vaultName,
        string secretName,
        string value,
        string contentType,
        DateTimeOffset? expiresOn = null,
        CancellationToken cancellationToken = default);
}
