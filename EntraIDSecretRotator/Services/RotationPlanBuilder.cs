using EntraIDSecretRotator.Models;

namespace EntraIDSecretRotator.Services;

/// <summary>
/// Implementation of IRotationPlanBuilder that builds rotation plans from Entra ID and Key Vault state.
/// </summary>
public sealed class RotationPlanBuilder : IRotationPlanBuilder
{
    /// <inheritdoc/>
    public (RotationPlan Plan, int SkippedHealthyCount) BuildPlan(
        IReadOnlyList<AppRegistration> apps,
        IReadOnlyList<KeyVaultSecretInfo> kvSecrets,
        int thresholdDays,
        bool excludeExpired = false)
    {
        var uniqueRotations = new List<RotationAction>();
        var duplicateRotations = new List<RotationAction>();
        var deletions = new List<DeletionAction>();
        var orphans = new List<OrphanAlert>();
        var skippedHealthy = 0;

        // Build a lookup: (clientId, secretId) -> list of KV secrets (current mappings)
        var kvMappings = kvSecrets
            .Where(kv => kv.Status == MappingStatus.Mapped)
            .GroupBy(kv => (kv.ClientId!, kv.SecretId!))
            .ToDictionary(g => g.Key, g => g.ToList());

        // Build a lookup for previous mappings: (clientId, secretId) -> KV secret
        // This proves a secret was rotated and the old secret can safely be deleted
        var kvPreviousMappings = kvSecrets
            .Where(kv => kv.HasPreviousMapping)
            .GroupBy(kv => (kv.PreviousMapping!.ClientId, kv.PreviousMapping!.SecretId))
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var app in apps)
        {
            // Check if this app has at least one healthy secret (not expiring within threshold)
            var hasHealthySecret = app.Secrets.Any(s =>
            {
                var days = (int)(s.EndDateTime - DateTimeOffset.UtcNow).TotalDays;
                return days > thresholdDays;
            });

            foreach (var secret in app.Secrets)
            {
                var daysUntilExpiry = (int)(secret.EndDateTime - DateTimeOffset.UtcNow).TotalDays;
                var status = daysUntilExpiry < 0 ? SecretStatus.Expired
                           : daysUntilExpiry <= thresholdDays ? SecretStatus.Expiring
                           : SecretStatus.Healthy;

                // Skip healthy secrets
                if (status == SecretStatus.Healthy)
                {
                    skippedHealthy++;
                    continue;
                }

                // Skip expired secrets if excludeExpired is true
                if (status == SecretStatus.Expired && excludeExpired)
                    continue;

                var key = (app.AppId, secret.KeyId);
                var hasCurrentMappings = kvMappings.TryGetValue(key, out var mappings);
                var hasPreviousMappings = kvPreviousMappings.TryGetValue(key, out var previousMappings);

                if (hasCurrentMappings && mappings!.Count > 0)
                {
                    if (status == SecretStatus.Expired && hasHealthySecret)
                    {
                        // Expired with current mapping AND a healthy replacement exists — safe to delete
                        var firstMapping = mappings.First();
                        var safeToDelete = firstMapping.HasPreviousMapping;

                        deletions.Add(new DeletionAction
                        {
                            ObjectId = app.Id,
                            ClientId = app.AppId,
                            AppName = app.DisplayName,
                            SecretId = secret.KeyId,
                            SafeToDelete = safeToDelete,
                            ProofReference = safeToDelete ? new KeyVaultReference
                            {
                                VaultName = firstMapping.VaultName,
                                SecretName = firstMapping.SecretName
                            } : null
                        });
                    }
                    else
                    {
                        // Expiring OR expired without healthy replacement — needs rotation
                        var kvRefs = mappings.Select(m => new KeyVaultReference
                        {
                            VaultName = m.VaultName,
                            SecretName = m.SecretName
                        }).ToList();

                        var category = kvRefs.Count == 1 ? RotationCategory.Unique : RotationCategory.Duplicate;
                        var action = new RotationAction
                        {
                            ObjectId = app.Id,
                            ClientId = app.AppId,
                            AppName = app.DisplayName,
                            SecretId = secret.KeyId,
                            DaysUntilExpiry = daysUntilExpiry,
                            Status = status,
                            Category = category,
                            KeyVaultRefs = kvRefs
                        };

                        if (category == RotationCategory.Unique)
                            uniqueRotations.Add(action);
                        else
                            duplicateRotations.Add(action);
                    }
                }
                else if (status == SecretStatus.Expired && hasPreviousMappings && previousMappings!.Count > 0 && hasHealthySecret)
                {
                    // Expired secret found in a previous KV version, and the app
                    // has a healthy replacement — safe to delete the old one
                    var firstMapping = previousMappings.First();
                    deletions.Add(new DeletionAction
                    {
                        ObjectId = app.Id,
                        ClientId = app.AppId,
                        AppName = app.DisplayName,
                        SecretId = secret.KeyId,
                        SafeToDelete = true,
                        ProofReference = new KeyVaultReference
                        {
                            VaultName = firstMapping.VaultName,
                            SecretName = firstMapping.SecretName
                        }
                    });
                }
                else
                {
                    // No KV mapping at all, or expired without a healthy replacement — orphan.
                    // If a Key Vault secret's previous version still points at this secret, the
                    // rotator replaced it and will delete it once it expires (branch above).
                    // Only that proof marks it as pending deletion; a healthy sibling alone does not.
                    var pendingDeletion = hasPreviousMappings && previousMappings!.Count > 0 && hasHealthySecret;

                    orphans.Add(new OrphanAlert
                    {
                        ClientId = app.AppId,
                        AppName = app.DisplayName,
                        SecretId = secret.KeyId,
                        DaysUntilExpiry = daysUntilExpiry,
                        PendingDeletion = pendingDeletion
                    });
                }
            }
        }

        var plan = new RotationPlan
        {
            UniqueRotations = uniqueRotations,
            DuplicateRotations = duplicateRotations,
            Deletions = deletions,
            Orphans = orphans
        };

        return (plan, skippedHealthy);
    }
}
