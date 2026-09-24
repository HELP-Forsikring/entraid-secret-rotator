using EntraIDSecretRotator.Models;

namespace EntraIDSecretRotator.Services;

/// <summary>
/// Service for building rotation plans from Entra ID and Key Vault state.
/// </summary>
public interface IRotationPlanBuilder
{
    /// <summary>
    /// Build a rotation plan from current Entra ID and Key Vault state.
    /// </summary>
    /// <param name="apps">App registrations with their secrets.</param>
    /// <param name="kvSecrets">Key Vault secrets with mapping info.</param>
    /// <param name="thresholdDays">Secrets expiring within this many days need rotation.</param>
    /// <param name="excludeExpired">If true, don't include already-expired secrets.</param>
    /// <returns>Rotation plan and count of skipped healthy secrets.</returns>
    (RotationPlan Plan, int SkippedHealthyCount) BuildPlan(
        IReadOnlyList<AppRegistration> apps,
        IReadOnlyList<KeyVaultSecretInfo> kvSecrets,
        int thresholdDays,
        bool excludeExpired = false);
}
