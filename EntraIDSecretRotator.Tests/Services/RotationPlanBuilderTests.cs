using EntraIDSecretRotator.Models;
using EntraIDSecretRotator.Services;
using AwesomeAssertions;
using Xunit;

namespace EntraIDSecretRotator.Tests.Services;

public class RotationPlanBuilderTests
{
    private const int Threshold = 30;
    private readonly RotationPlanBuilder _builder = new();

    private static SecretInfo Secret(string keyId, int daysUntilExpiry) => new()
    {
        KeyId = keyId,
        EndDateTime = DateTimeOffset.UtcNow.AddDays(daysUntilExpiry).AddHours(1)
    };

    private static AppRegistration App(string name, params SecretInfo[] secrets) => new()
    {
        Id = Guid.NewGuid().ToString(),
        AppId = Guid.NewGuid().ToString(),
        DisplayName = name,
        Secrets = secrets
    };

    /// <summary>
    /// A Key Vault secret as it looks right after the rotator rotated it:
    /// current version maps to the new secret, previous version still maps to the old one.
    /// </summary>
    private static KeyVaultSecretInfo RotatedKv(string vault, string name, AppRegistration app, string currentKeyId, string previousKeyId) =>
        KeyVaultSecretInfo.Create(vault, name, $"{app.DisplayName}:{app.AppId}:{currentKeyId}")
            .WithPreviousMapping(PreviousMappingInfo.CreateFromContentType($"{app.DisplayName}:{app.AppId}:{previousKeyId}"));

    // ------------------------------------------------------------------
    // No Key Vault proof: never pending deletion, whatever the siblings look like
    // ------------------------------------------------------------------

    [Fact]
    public void BuildPlan_ExpiringUnmappedSecret_WithHealthySibling_IsOrphanNotPendingDeletion()
    {
        // Arrange: someone rotated by hand; nothing in Key Vault proves it
        var app = App("myfilter-hand-rotated", Secret("old", 11), Secret("new", 170));

        // Act
        var (plan, skippedHealthy) = _builder.BuildPlan([app], [], Threshold);

        // Assert
        skippedHealthy.Should().Be(1);
        plan.UniqueRotations.Should().BeEmpty();
        plan.Deletions.Should().BeEmpty();
        plan.Orphans.Should().ContainSingle();
        plan.Orphans[0].SecretId.Should().Be("old");
        plan.Orphans[0].PendingDeletion.Should().BeFalse();
    }

    [Fact]
    public void BuildPlan_ExpiringUnmappedSecret_WithoutHealthySibling_IsOrphanNotPendingDeletion()
    {
        // Arrange: the only secret the app has is about to expire
        var app = App("myfilter-single-secret", Secret("only", 29));

        // Act
        var (plan, _) = _builder.BuildPlan([app], [], Threshold);

        // Assert
        plan.Orphans.Should().ContainSingle();
        plan.Orphans[0].PendingDeletion.Should().BeFalse();
        plan.Orphans[0].DaysUntilExpiry.Should().BePositive();
    }

    [Fact]
    public void BuildPlan_ExpiredUnmappedSecret_WithoutHealthySibling_IsOrphanNotPendingDeletion()
    {
        // Arrange
        var app = App("myfilter-dead-app", Secret("expired", -5));

        // Act
        var (plan, _) = _builder.BuildPlan([app], [], Threshold);

        // Assert
        plan.Orphans.Should().ContainSingle();
        plan.Orphans[0].PendingDeletion.Should().BeFalse();
        plan.Orphans[0].DaysUntilExpiry.Should().BeNegative();
    }

    [Fact]
    public void BuildPlan_TwoUnmappedSecretsBothInsideThreshold_NeitherIsPendingDeletion()
    {
        // Arrange
        var app = App("myfilter-two-soon", Secret("a", 5), Secret("b", 20));

        // Act
        var (plan, skippedHealthy) = _builder.BuildPlan([app], [], Threshold);

        // Assert
        skippedHealthy.Should().Be(0);
        plan.Orphans.Should().HaveCount(2);
        plan.Orphans.Should().OnlyContain(o => !o.PendingDeletion);
    }

    [Fact]
    public void BuildPlan_PreviousMappingForDifferentSecret_DoesNotMarkPendingDeletion()
    {
        // Arrange: Key Vault history proves "other" was rotated, not "old"
        var app = App("myfilter-other-proof", Secret("old", 14), Secret("current", 170));
        var kv = RotatedKv("key-vault-3", "other-proof-client-secret", app, currentKeyId: "current", previousKeyId: "other");

        // Act
        var (plan, _) = _builder.BuildPlan([app], [kv], Threshold);

        // Assert
        plan.Orphans.Should().ContainSingle(o => o.SecretId == "old");
        plan.Orphans[0].PendingDeletion.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // Key Vault proof: pending deletion while expiring, deleted once expired
    // ------------------------------------------------------------------

    [Fact]
    public void BuildPlan_ExpiringSecretReplacedByRotator_IsOrphanPendingDeletion()
    {
        // Arrange: rotator rotated "old" -> "new"; Key Vault previous version still points at "old"
        var app = App("myfilter-rotated-app", Secret("old", 11), Secret("new", 170));
        var kv = RotatedKv("key-vault-3", "rotated-client-secret", app, currentKeyId: "new", previousKeyId: "old");

        // Act
        var (plan, _) = _builder.BuildPlan([app], [kv], Threshold);

        // Assert
        plan.UniqueRotations.Should().BeEmpty();
        plan.Deletions.Should().BeEmpty();
        plan.Orphans.Should().ContainSingle();
        plan.Orphans[0].SecretId.Should().Be("old");
        plan.Orphans[0].PendingDeletion.Should().BeTrue();
    }

    [Fact]
    public void BuildPlan_ExpiringSecretReplacedByRotator_PerEnvironmentVaults_IsPendingDeletionOnlyForProvenSecret()
    {
        // Arrange: one secret per environment.
        // Dev was rotated by the rotator; test was never onboarded and is expiring too.
        var app = App("myfilter-per-env-app", Secret("dev-old", 17), Secret("dev-new", 170), Secret("test-manual", 20), Secret("prod", 76));
        var devKv = RotatedKv("key-vault-1", "per-env-client-secret", app, currentKeyId: "dev-new", previousKeyId: "dev-old");
        var prodKv = KeyVaultSecretInfo.Create("key-vault-3", "per-env-client-secret", $"{app.DisplayName}:{app.AppId}:prod");

        // Act
        var (plan, _) = _builder.BuildPlan([app], [devKv, prodKv], Threshold);

        // Assert
        plan.Orphans.Should().HaveCount(2);
        plan.Orphans.Should().ContainSingle(o => o.SecretId == "dev-old" && o.PendingDeletion);
        plan.Orphans.Should().ContainSingle(o => o.SecretId == "test-manual" && !o.PendingDeletion);
    }

    [Fact]
    public void BuildPlan_ExpiredSecretReplacedByRotator_IsDeletion_NotOrphan()
    {
        // Arrange: same as above, but "old" has now expired
        var app = App("myfilter-rotated-app", Secret("old", -3), Secret("new", 160));
        var kv = RotatedKv("key-vault-3", "rotated-client-secret", app, currentKeyId: "new", previousKeyId: "old");

        // Act
        var (plan, _) = _builder.BuildPlan([app], [kv], Threshold);

        // Assert
        plan.Orphans.Should().BeEmpty();
        plan.Deletions.Should().ContainSingle();
        plan.Deletions[0].SecretId.Should().Be("old");
        plan.Deletions[0].SafeToDelete.Should().BeTrue();
    }

    [Fact]
    public void BuildPlan_ExpiringSecretWithProof_ButNoHealthySibling_IsNotPendingDeletion()
    {
        // Arrange: proof exists, but the replacement is itself inside the threshold,
        // so the deletion branch would not fire either. Label must match what will happen.
        var app = App("myfilter-odd-app", Secret("old", 11), Secret("new", 20));
        var kv = RotatedKv("key-vault-3", "odd-client-secret", app, currentKeyId: "new", previousKeyId: "old");

        // Act
        var (plan, _) = _builder.BuildPlan([app], [kv], Threshold);

        // Assert
        plan.Orphans.Should().ContainSingle(o => o.SecretId == "old");
        plan.Orphans[0].PendingDeletion.Should().BeFalse();
        plan.UniqueRotations.Should().ContainSingle(r => r.SecretId == "new");
    }

    // ------------------------------------------------------------------
    // Mapped secrets are rotations, never orphans
    // ------------------------------------------------------------------

    [Fact]
    public void BuildPlan_ExpiringMappedSecret_IsRotation_NotOrphan()
    {
        // Arrange
        var app = App("myfilter-api-prod", Secret("mapped", 10));
        var kv = KeyVaultSecretInfo.Create("key-vault-3", "api-prod-secret", $"myfilter-api-prod:{app.AppId}:mapped");

        // Act
        var (plan, _) = _builder.BuildPlan([app], [kv], Threshold);

        // Assert
        plan.Orphans.Should().BeEmpty();
        plan.UniqueRotations.Should().ContainSingle();
    }
}
