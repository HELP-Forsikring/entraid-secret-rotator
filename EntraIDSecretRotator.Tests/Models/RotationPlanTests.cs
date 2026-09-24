using EntraIDSecretRotator.Models;
using AwesomeAssertions;
using Xunit;

namespace EntraIDSecretRotator.Tests.Models;

public class RotationPlanTests
{
    #region RotationPlan.Empty Tests

    [Fact]
    public void RotationPlan_Empty_HasNoActions()
    {
        // Act
        var plan = RotationPlan.Empty;

        // Assert
        plan.UniqueRotations.Should().BeEmpty();
        plan.DuplicateRotations.Should().BeEmpty();
        plan.Deletions.Should().BeEmpty();
        plan.Orphans.Should().BeEmpty();
        plan.TotalActions.Should().Be(0);
        plan.HasActions.Should().BeFalse();
    }

    #endregion

    #region TotalActions Tests

    [Fact]
    public void RotationPlan_TotalActions_SumsCorrectly()
    {
        // Arrange
        var plan = new RotationPlan
        {
            UniqueRotations = new[]
            {
                CreateRotationAction(RotationCategory.Unique, 1),
                CreateRotationAction(RotationCategory.Unique, 1)
            },
            DuplicateRotations = new[]
            {
                CreateRotationAction(RotationCategory.Duplicate, 2)
            },
            Deletions = new[]
            {
                CreateDeletionAction(safeToDelete: true),
                CreateDeletionAction(safeToDelete: false),
                CreateDeletionAction(safeToDelete: true)
            },
            Orphans = new[]
            {
                CreateOrphanAlert()
            }
        };

        // Act
        var totalActions = plan.TotalActions;

        // Assert
        // UniqueRotations: 2 + DuplicateRotations: 1 + Deletions: 3 = 6
        // Note: Orphans are NOT counted in TotalActions
        totalActions.Should().Be(6);
    }

    [Fact]
    public void RotationPlan_TotalActions_ExcludesOrphans()
    {
        // Arrange
        var plan = new RotationPlan
        {
            UniqueRotations = Array.Empty<RotationAction>(),
            DuplicateRotations = Array.Empty<RotationAction>(),
            Deletions = Array.Empty<DeletionAction>(),
            Orphans = new[]
            {
                CreateOrphanAlert(),
                CreateOrphanAlert(),
                CreateOrphanAlert()
            }
        };

        // Act
        var totalActions = plan.TotalActions;

        // Assert
        totalActions.Should().Be(0);
    }

    #endregion

    #region HasActions Tests

    [Fact]
    public void RotationPlan_HasActions_ReturnsTrueWhenActionsExist()
    {
        // Arrange
        var planWithUniqueRotations = new RotationPlan
        {
            UniqueRotations = new[] { CreateRotationAction(RotationCategory.Unique, 1) },
            DuplicateRotations = Array.Empty<RotationAction>(),
            Deletions = Array.Empty<DeletionAction>(),
            Orphans = Array.Empty<OrphanAlert>()
        };

        var planWithDuplicateRotations = new RotationPlan
        {
            UniqueRotations = Array.Empty<RotationAction>(),
            DuplicateRotations = new[] { CreateRotationAction(RotationCategory.Duplicate, 2) },
            Deletions = Array.Empty<DeletionAction>(),
            Orphans = Array.Empty<OrphanAlert>()
        };

        var planWithDeletions = new RotationPlan
        {
            UniqueRotations = Array.Empty<RotationAction>(),
            DuplicateRotations = Array.Empty<RotationAction>(),
            Deletions = new[] { CreateDeletionAction(safeToDelete: true) },
            Orphans = Array.Empty<OrphanAlert>()
        };

        // Assert
        planWithUniqueRotations.HasActions.Should().BeTrue();
        planWithDuplicateRotations.HasActions.Should().BeTrue();
        planWithDeletions.HasActions.Should().BeTrue();
    }

    [Fact]
    public void RotationPlan_HasActions_ReturnsTrueWhenOnlyOrphansExist()
    {
        // Arrange
        var plan = new RotationPlan
        {
            UniqueRotations = Array.Empty<RotationAction>(),
            DuplicateRotations = Array.Empty<RotationAction>(),
            Deletions = Array.Empty<DeletionAction>(),
            Orphans = new[] { CreateOrphanAlert() }
        };

        // Assert
        plan.HasActions.Should().BeTrue();
    }

    [Fact]
    public void RotationPlan_HasActions_ReturnsFalseWhenNoActionsOrOrphans()
    {
        // Arrange
        var plan = new RotationPlan
        {
            UniqueRotations = Array.Empty<RotationAction>(),
            DuplicateRotations = Array.Empty<RotationAction>(),
            Deletions = Array.Empty<DeletionAction>(),
            Orphans = Array.Empty<OrphanAlert>()
        };

        // Assert
        plan.HasActions.Should().BeFalse();
    }

    #endregion

    #region RotationAction Tests

    [Fact]
    public void RotationAction_UniqueCategory_HasSingleKeyVaultRef()
    {
        // Arrange
        var keyVaultRef = new KeyVaultReference
        {
            VaultName = "key-vault-3",
            SecretName = "app-secret"
        };

        var action = new RotationAction
        {
            ObjectId = "obj-12345678",
            ClientId = "12345678-abcd-1234-abcd-123456789abc",
            AppName = "myfilter-api-prod",
            SecretId = "12345678-aa11-bb22-cc33-a1b2c3d4e5f6",
            DaysUntilExpiry = 15,
            Status = SecretStatus.Expiring,
            Category = RotationCategory.Unique,
            KeyVaultRefs = new[] { keyVaultRef }
        };

        // Assert
        action.Category.Should().Be(RotationCategory.Unique);
        action.KeyVaultRefs.Should().HaveCount(1);
        action.KeyVaultRefs[0].VaultName.Should().Be("key-vault-3");
        action.KeyVaultRefs[0].SecretName.Should().Be("app-secret");
    }

    [Fact]
    public void RotationAction_DuplicateCategory_HasMultipleKeyVaultRefs()
    {
        // Arrange
        var keyVaultRefs = new[]
        {
            new KeyVaultReference { VaultName = "key-vault-1", SecretName = "app-secret" },
            new KeyVaultReference { VaultName = "key-vault-2", SecretName = "app-secret" },
            new KeyVaultReference { VaultName = "key-vault-3", SecretName = "app-secret" }
        };

        var action = new RotationAction
        {
            ObjectId = "obj-12345678",
            ClientId = "12345678-abcd-1234-abcd-123456789abc",
            AppName = "myfilter-api-shared",
            SecretId = "12345678-aa11-bb22-cc33-a1b2c3d4e5f6",
            DaysUntilExpiry = 10,
            Status = SecretStatus.Expiring,
            Category = RotationCategory.Duplicate,
            KeyVaultRefs = keyVaultRefs
        };

        // Assert
        action.Category.Should().Be(RotationCategory.Duplicate);
        action.KeyVaultRefs.Should().HaveCount(3);
        action.KeyVaultRefs.Select(r => r.VaultName).Should().Contain("key-vault-1");
        action.KeyVaultRefs.Select(r => r.VaultName).Should().Contain("key-vault-2");
        action.KeyVaultRefs.Select(r => r.VaultName).Should().Contain("key-vault-3");
    }

    [Theory]
    [InlineData(SecretStatus.Expired)]
    [InlineData(SecretStatus.Expiring)]
    [InlineData(SecretStatus.Healthy)]
    public void RotationAction_AllStatusValues_AreValid(SecretStatus status)
    {
        // Arrange
        var action = new RotationAction
        {
            ObjectId = "object-id",
            ClientId = "client-id",
            AppName = "app-name",
            SecretId = "secret-id",
            DaysUntilExpiry = status == SecretStatus.Expired ? -5 : 30,
            Status = status,
            Category = RotationCategory.Unique,
            KeyVaultRefs = new[] { new KeyVaultReference { VaultName = "vault", SecretName = "secret" } }
        };

        // Assert
        action.Status.Should().Be(status);
    }

    #endregion

    #region DeletionAction Tests

    [Fact]
    public void DeletionAction_SafeToDelete_HasProofReference()
    {
        // Arrange
        var proofRef = new KeyVaultReference
        {
            VaultName = "key-vault-3",
            SecretName = "app-secret"
        };

        var deletion = new DeletionAction
        {
            ObjectId = "obj-12345678",
            ClientId = "12345678-abcd-1234-abcd-123456789abc",
            AppName = "myfilter-api-prod",
            SecretId = "old-secret-id",
            SafeToDelete = true,
            ProofReference = proofRef
        };

        // Assert
        deletion.SafeToDelete.Should().BeTrue();
        deletion.ProofReference.Should().NotBeNull();
        deletion.ProofReference!.VaultName.Should().Be("key-vault-3");
        deletion.ProofReference.SecretName.Should().Be("app-secret");
    }

    [Fact]
    public void DeletionAction_NotSafeToDelete_HasNullProofReference()
    {
        // Arrange
        var deletion = new DeletionAction
        {
            ObjectId = "obj-12345678",
            ClientId = "12345678-abcd-1234-abcd-123456789abc",
            AppName = "myfilter-api-prod",
            SecretId = "old-secret-id",
            SafeToDelete = false,
            ProofReference = null
        };

        // Assert
        deletion.SafeToDelete.Should().BeFalse();
        deletion.ProofReference.Should().BeNull();
    }

    #endregion

    #region OrphanAlert Tests

    [Fact]
    public void OrphanAlert_WithPositiveDaysUntilExpiry_IndicatesExpiringSecret()
    {
        // Arrange
        var orphan = new OrphanAlert
        {
            ClientId = "12345678-abcd-1234-abcd-123456789abc",
            AppName = "myfilter-orphan-app",
            SecretId = "orphan-secret-id",
            DaysUntilExpiry = 15,
            PendingDeletion = false
        };

        // Assert
        orphan.DaysUntilExpiry.Should().BePositive();
    }

    [Fact]
    public void OrphanAlert_WithNegativeDaysUntilExpiry_IndicatesExpiredSecret()
    {
        // Arrange
        var orphan = new OrphanAlert
        {
            ClientId = "12345678-abcd-1234-abcd-123456789abc",
            AppName = "myfilter-orphan-app",
            SecretId = "orphan-secret-id",
            DaysUntilExpiry = -10,
            PendingDeletion = false
        };

        // Assert
        orphan.DaysUntilExpiry.Should().BeNegative();
    }

    #endregion

    #region KeyVaultReference Tests

    [Fact]
    public void KeyVaultReference_CreatedWithRequiredProperties()
    {
        // Arrange & Act
        var reference = new KeyVaultReference
        {
            VaultName = "key-vault-3",
            SecretName = "my-app-secret"
        };

        // Assert
        reference.VaultName.Should().Be("key-vault-3");
        reference.SecretName.Should().Be("my-app-secret");
    }

    [Fact]
    public void KeyVaultReference_RecordEquality_Works()
    {
        // Arrange
        var ref1 = new KeyVaultReference { VaultName = "vault", SecretName = "secret" };
        var ref2 = new KeyVaultReference { VaultName = "vault", SecretName = "secret" };
        var ref3 = new KeyVaultReference { VaultName = "vault", SecretName = "different" };

        // Assert
        ref1.Should().Be(ref2);
        ref1.Should().NotBe(ref3);
    }

    #endregion

    #region RotationCategory Enum Tests

    [Fact]
    public void RotationCategory_HasExpectedValues()
    {
        // Assert
        Enum.GetValues<RotationCategory>().Should().HaveCount(2);
        Enum.IsDefined(RotationCategory.Unique).Should().BeTrue();
        Enum.IsDefined(RotationCategory.Duplicate).Should().BeTrue();
    }

    #endregion

    #region SecretStatus Enum Tests

    [Fact]
    public void SecretStatus_HasExpectedValues()
    {
        // Assert
        Enum.GetValues<SecretStatus>().Should().HaveCount(3);
        Enum.IsDefined(SecretStatus.Expired).Should().BeTrue();
        Enum.IsDefined(SecretStatus.Expiring).Should().BeTrue();
        Enum.IsDefined(SecretStatus.Healthy).Should().BeTrue();
    }

    #endregion

    #region Helper Methods

    private static RotationAction CreateRotationAction(RotationCategory category, int keyVaultRefCount)
    {
        var refs = Enumerable.Range(0, keyVaultRefCount)
            .Select(i => new KeyVaultReference
            {
                VaultName = $"vault-{i}",
                SecretName = "secret"
            })
            .ToArray();

        return new RotationAction
        {
            ObjectId = Guid.NewGuid().ToString(),
            ClientId = Guid.NewGuid().ToString(),
            AppName = "test-app",
            SecretId = Guid.NewGuid().ToString(),
            DaysUntilExpiry = 15,
            Status = SecretStatus.Expiring,
            Category = category,
            KeyVaultRefs = refs
        };
    }

    private static DeletionAction CreateDeletionAction(bool safeToDelete)
    {
        return new DeletionAction
        {
            ObjectId = Guid.NewGuid().ToString(),
            ClientId = Guid.NewGuid().ToString(),
            AppName = "test-app",
            SecretId = Guid.NewGuid().ToString(),
            SafeToDelete = safeToDelete,
            ProofReference = safeToDelete
                ? new KeyVaultReference { VaultName = "vault", SecretName = "secret" }
                : null
        };
    }

    private static OrphanAlert CreateOrphanAlert()
    {
        return new OrphanAlert
        {
            ClientId = Guid.NewGuid().ToString(),
            AppName = "test-orphan-app",
            SecretId = Guid.NewGuid().ToString(),
            DaysUntilExpiry = 10,
            PendingDeletion = false
        };
    }

    #endregion
}
