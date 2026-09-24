using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using EntraIDSecretRotator.Infrastructure;
using EntraIDSecretRotator.Models;
using Microsoft.Extensions.Logging;

namespace EntraIDSecretRotator.Services;

/// <summary>
/// Implementation of IKeyVaultService using Azure Key Vault SDK.
/// </summary>
public sealed class KeyVaultService : IKeyVaultService
{
    private readonly TokenCredential _credential;
    private readonly ILogger<KeyVaultService> _logger;
    private readonly IReadOnlyList<string> _vaultNames;

    public KeyVaultService(TokenCredential credential, IReadOnlyList<string> vaultNames, ILogger<KeyVaultService> logger)
    {
        Assert.NotNull(vaultNames, nameof(vaultNames));
        Assert.That(vaultNames.Count > 0, "At least one Key Vault must be configured");

        _credential = credential;
        _vaultNames = vaultNames;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> VaultNames => _vaultNames;

    /// <inheritdoc />
    public async Task<IReadOnlyList<KeyVaultSecretInfo>> ListAllSecretsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Listing secrets from all {Count} Key Vaults", _vaultNames.Count);

        var allSecrets = new List<KeyVaultSecretInfo>();

        foreach (var vaultName in _vaultNames)
        {
            try
            {
                var secrets = await ListSecretsAsync(vaultName, cancellationToken);
                allSecrets.AddRange(secrets);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list secrets from vault {VaultName}", vaultName);
                // Continue with other vaults instead of failing completely
            }
        }

        _logger.LogInformation("Retrieved {Count} total secrets from all vaults", allSecrets.Count);

        return allSecrets
            .OrderBy(s => s.VaultName)
            .ThenBy(s => s.SecretName)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<KeyVaultSecretInfo>> ListSecretsAsync(string vaultName, CancellationToken cancellationToken = default)
    {
        Assert.NotNullOrEmpty(vaultName, nameof(vaultName));

        _logger.LogInformation("Listing secrets from vault: {VaultName}", vaultName);

        var client = CreateSecretClient(vaultName);
        var secrets = new List<KeyVaultSecretInfo>();

        try
        {
            await foreach (var secretProperties in client.GetPropertiesOfSecretsAsync(cancellationToken))
            {
                Assert.NotNull(secretProperties, "secretProperties");

                var secretInfo = KeyVaultSecretInfo.Create(
                    vaultName,
                    secretProperties.Name,
                    secretProperties.ContentType);

                secrets.Add(secretInfo);
            }

            _logger.LogInformation("Retrieved {Count} secrets from vault {VaultName}", secrets.Count, vaultName);

            // For mapped secrets, fetch previous version info
            var enrichedSecrets = new List<KeyVaultSecretInfo>();
            foreach (var secret in secrets)
            {
                if (secret.Status == MappingStatus.Mapped)
                {
                    var previousMapping = await GetPreviousMappingAsync(client, secret.SecretName, cancellationToken);
                    enrichedSecrets.Add(secret.WithPreviousMapping(previousMapping));
                }
                else
                {
                    enrichedSecrets.Add(secret);
                }
            }

            return enrichedSecrets;
        }
        catch (Azure.RequestFailedException ex)
        {
            _logger.LogError(ex, "Azure request failed for vault {VaultName}: {Message}", vaultName, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Gets the previous mapping info for a secret by examining its version history.
    /// Returns null if there's no previous version or if the previous version isn't mapped.
    /// </summary>
    private async Task<PreviousMappingInfo?> GetPreviousMappingAsync(
        SecretClient client,
        string secretName,
        CancellationToken cancellationToken)
    {
        try
        {
            // Get all versions, sorted by creation date descending (newest first)
            var versions = new List<SecretProperties>();
            await foreach (var version in client.GetPropertiesOfSecretVersionsAsync(secretName, cancellationToken))
            {
                versions.Add(version);
            }

            // Sort by CreatedOn descending - current version is first, previous is second
            var sortedVersions = versions
                .Where(v => v.CreatedOn.HasValue)
                .OrderByDescending(v => v.CreatedOn!.Value)
                .ToList();

            // Need at least 2 versions (current + previous)
            if (sortedVersions.Count < 2)
            {
                return null;
            }

            // The second item is the previous version
            var previousVersion = sortedVersions[1];
            Assert.NotNull(previousVersion, "previousVersion");

            // Try to parse the previous version's content-type
            var previousMapping = PreviousMappingInfo.CreateFromContentType(previousVersion.ContentType);

            if (previousMapping != null)
            {
                _logger.LogDebug(
                    "Secret {SecretName} has previous mapping: ClientId={ClientId}, SecretId={SecretId}",
                    secretName,
                    previousMapping.ClientId,
                    previousMapping.SecretId);
            }

            return previousMapping;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get version history for secret {SecretName}", secretName);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string> GetSecretValueAsync(
        string vaultName,
        string secretName,
        CancellationToken cancellationToken = default)
    {
        Assert.NotNullOrEmpty(vaultName, nameof(vaultName));
        Assert.NotNullOrEmpty(secretName, nameof(secretName));

        _logger.LogInformation("Getting secret value from vault {VaultName}, secret {SecretName}", vaultName, secretName);

        try
        {
            var client = CreateSecretClient(vaultName);
            var response = await client.GetSecretAsync(secretName, cancellationToken: cancellationToken);

            Assert.NotNull(response, "response");
            Assert.NotNull(response.Value, "response.Value");
            Assert.NotNullOrEmpty(response.Value.Value, "response.Value.Value");

            _logger.LogInformation("Successfully retrieved secret {SecretName} from vault {VaultName}", secretName, vaultName);

            return response.Value.Value;
        }
        catch (Azure.RequestFailedException ex)
        {
            _logger.LogError(ex, "Failed to get secret {SecretName} from vault {VaultName}: {Message}", secretName, vaultName, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task UpdateSecretAsync(
        string vaultName,
        string secretName,
        string value,
        string contentType,
        DateTimeOffset? expiresOn = null,
        CancellationToken cancellationToken = default)
    {
        Assert.NotNullOrEmpty(vaultName, nameof(vaultName));
        Assert.NotNullOrEmpty(secretName, nameof(secretName));
        Assert.NotNullOrEmpty(value, nameof(value));
        Assert.NotNullOrEmpty(contentType, nameof(contentType));

        var expiryInfo = expiresOn.HasValue ? $", expires {expiresOn.Value:yyyy-MM-dd}" : "";
        _logger.LogInformation(
            "Updating secret {SecretName} in vault {VaultName} with content-type '{ContentType}'{ExpiryInfo}",
            secretName, vaultName, contentType, expiryInfo);

        try
        {
            var client = CreateSecretClient(vaultName);

            // Create the secret with the new value - this creates a new version
            var secret = new KeyVaultSecret(secretName, value)
            {
                Properties =
                {
                    ContentType = contentType
                }
            };

            // Only set expiry if provided (null for rollback scenarios)
            if (expiresOn.HasValue)
            {
                secret.Properties.ExpiresOn = expiresOn.Value;
            }

            var response = await client.SetSecretAsync(secret, cancellationToken);

            Assert.NotNull(response, "response");
            Assert.NotNull(response.Value, "response.Value");

            _logger.LogInformation(
                "Successfully updated secret {SecretName} in vault {VaultName}, new version: {Version}{ExpiryInfo}",
                secretName, vaultName, response.Value.Properties.Version, expiryInfo);
        }
        catch (Azure.RequestFailedException ex)
        {
            _logger.LogError(ex, "Failed to update secret {SecretName} in vault {VaultName}: {Message}", secretName, vaultName, ex.Message);
            throw;
        }
    }

    private SecretClient CreateSecretClient(string vaultName)
    {
        Assert.NotNullOrEmpty(vaultName, nameof(vaultName));

        var vaultUri = new Uri($"https://{vaultName}.vault.azure.net");
        return new SecretClient(vaultUri, _credential);
    }
}
