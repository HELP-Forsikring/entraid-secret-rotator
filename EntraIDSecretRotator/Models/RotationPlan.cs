namespace EntraIDSecretRotator.Models;

/// <summary>
/// Category of a rotation action based on mapping analysis.
/// </summary>
public enum RotationCategory
{
    /// <summary>
    /// Secret is mapped in exactly one Key Vault.
    /// </summary>
    Unique,

    /// <summary>
    /// Secret is mapped in multiple Key Vaults (requires atomic update).
    /// </summary>
    Duplicate
}

/// <summary>
/// Status of a secret based on expiry analysis.
/// </summary>
public enum SecretStatus
{
    /// <summary>
    /// Secret has already expired.
    /// </summary>
    Expired,

    /// <summary>
    /// Secret expires within the rotation threshold (needs rotation).
    /// </summary>
    Expiring,

    /// <summary>
    /// Secret expires after the rotation threshold (healthy).
    /// </summary>
    Healthy
}

/// <summary>
/// Reference to a specific secret in a Key Vault.
/// </summary>
public sealed record KeyVaultReference
{
    /// <summary>
    /// The Key Vault name.
    /// </summary>
    public required string VaultName { get; init; }

    /// <summary>
    /// The secret name in the Key Vault.
    /// </summary>
    public required string SecretName { get; init; }
}

/// <summary>
/// Represents a rotation action to be performed.
/// </summary>
public sealed record RotationAction
{
    /// <summary>
    /// The Entra ID object ID (used for Graph API calls).
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// The app registration client ID.
    /// </summary>
    public required string ClientId { get; init; }

    /// <summary>
    /// The app registration display name (for logging).
    /// </summary>
    public required string AppName { get; init; }

    /// <summary>
    /// The current secret ID that needs rotation.
    /// </summary>
    public required string SecretId { get; init; }

    /// <summary>
    /// Days until the secret expires.
    /// </summary>
    public required int DaysUntilExpiry { get; init; }

    /// <summary>
    /// The status of the secret.
    /// </summary>
    public required SecretStatus Status { get; init; }

    /// <summary>
    /// The category of rotation (unique or duplicate).
    /// </summary>
    public required RotationCategory Category { get; init; }

    /// <summary>
    /// Key Vault references where this secret is mapped.
    /// For Unique: exactly 1 item. For Duplicate: 2+ items.
    /// </summary>
    public required IReadOnlyList<KeyVaultReference> KeyVaultRefs { get; init; }
}

/// <summary>
/// Represents a deletion action for an expired secret.
/// </summary>
public sealed record DeletionAction
{
    /// <summary>
    /// The Entra ID object ID (used for Graph API calls).
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// The app registration client ID.
    /// </summary>
    public required string ClientId { get; init; }

    /// <summary>
    /// The app registration display name (for logging).
    /// </summary>
    public required string AppName { get; init; }

    /// <summary>
    /// The secret ID to delete.
    /// </summary>
    public required string SecretId { get; init; }

    /// <summary>
    /// Whether it's safe to delete this secret.
    /// True only if Key Vault version history proves we rotated it.
    /// </summary>
    public required bool SafeToDelete { get; init; }

    /// <summary>
    /// The Key Vault reference that proves this secret was rotated.
    /// Null if SafeToDelete is false.
    /// </summary>
    public KeyVaultReference? ProofReference { get; init; }
}

/// <summary>
/// Represents an expiring secret that has no Key Vault mapping.
/// </summary>
public sealed record OrphanAlert
{
    /// <summary>
    /// The app registration client ID.
    /// </summary>
    public required string ClientId { get; init; }

    /// <summary>
    /// The app registration display name.
    /// </summary>
    public required string AppName { get; init; }

    /// <summary>
    /// The secret ID.
    /// </summary>
    public required string SecretId { get; init; }

    /// <summary>
    /// Days until the secret expires (negative if already expired).
    /// </summary>
    public required int DaysUntilExpiry { get; init; }

    /// <summary>
    /// Whether this secret was replaced by the rotator and is only waiting to expire.
    /// True when a Key Vault secret's previous version still maps to this secret (the
    /// rotator's own footprint) and the app has a healthy secret. Such a secret is
    /// deleted automatically once it expires. False means nothing proves the secret
    /// was replaced: the app may be manual, only partially onboarded, or the secret
    /// may belong to a developer. Those are notified on, never touched.
    /// </summary>
    public required bool PendingDeletion { get; init; }
}

/// <summary>
/// The complete action plan for a rotation run.
/// Built in Phase 2 from in-memory data, executed in Phase 3.
/// </summary>
public sealed record RotationPlan
{
    /// <summary>
    /// Secrets needing rotation that are mapped in exactly one Key Vault.
    /// Process these first.
    /// </summary>
    public required IReadOnlyList<RotationAction> UniqueRotations { get; init; }

    /// <summary>
    /// Secrets needing rotation that are mapped in multiple Key Vaults.
    /// Process these second, with atomic update semantics.
    /// </summary>
    public required IReadOnlyList<RotationAction> DuplicateRotations { get; init; }

    /// <summary>
    /// Expired secrets that may be eligible for deletion.
    /// </summary>
    public required IReadOnlyList<DeletionAction> Deletions { get; init; }

    /// <summary>
    /// Expiring secrets with no Key Vault mapping (needs human attention).
    /// </summary>
    public required IReadOnlyList<OrphanAlert> Orphans { get; init; }

    /// <summary>
    /// Total count of all actions.
    /// </summary>
    public int TotalActions => UniqueRotations.Count + DuplicateRotations.Count + Deletions.Count;

    /// <summary>
    /// Whether there are any actions to perform.
    /// </summary>
    public bool HasActions => TotalActions > 0 || Orphans.Count > 0;

    /// <summary>
    /// Creates an empty plan (no actions needed).
    /// </summary>
    public static RotationPlan Empty => new()
    {
        UniqueRotations = Array.Empty<RotationAction>(),
        DuplicateRotations = Array.Empty<RotationAction>(),
        Deletions = Array.Empty<DeletionAction>(),
        Orphans = Array.Empty<OrphanAlert>()
    };
}
