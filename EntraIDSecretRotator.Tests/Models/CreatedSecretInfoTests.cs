using EntraIDSecretRotator.Models;
using AwesomeAssertions;
using Xunit;

namespace EntraIDSecretRotator.Tests.Models;

public class CreatedSecretInfoTests
{
    [Fact]
    public void CreatedSecretInfo_WithAllProperties_CreatesCorrectly()
    {
        // Arrange
        var keyId = "12345678-aa11-bb22-cc33-a1b2c3d4e5f6";
        var secretValue = "super-secret-value-123";
        var startDateTime = DateTimeOffset.UtcNow;
        var endDateTime = DateTimeOffset.UtcNow.AddDays(365);
        var displayName = "Rotated by EntraIDSecretRotator, 2026-01-06";

        // Act
        var info = new CreatedSecretInfo
        {
            KeyId = keyId,
            SecretValue = secretValue,
            StartDateTime = startDateTime,
            EndDateTime = endDateTime,
            DisplayName = displayName
        };

        // Assert
        info.KeyId.Should().Be(keyId);
        info.SecretValue.Should().Be(secretValue);
        info.StartDateTime.Should().Be(startDateTime);
        info.EndDateTime.Should().Be(endDateTime);
        info.DisplayName.Should().Be(displayName);
    }

    [Fact]
    public void CreatedSecretInfo_WithNullDisplayName_IsValid()
    {
        // Arrange & Act
        var info = new CreatedSecretInfo
        {
            KeyId = "key-id",
            SecretValue = "secret-value",
            StartDateTime = DateTimeOffset.UtcNow,
            EndDateTime = DateTimeOffset.UtcNow.AddDays(90),
            DisplayName = null
        };

        // Assert
        info.DisplayName.Should().BeNull();
    }

    [Fact]
    public void CreatedSecretInfo_RecordEquality_Works()
    {
        // Arrange
        var startDateTime = DateTimeOffset.UtcNow;
        var endDateTime = DateTimeOffset.UtcNow.AddDays(90);

        var info1 = new CreatedSecretInfo
        {
            KeyId = "key-id",
            SecretValue = "secret-value",
            StartDateTime = startDateTime,
            EndDateTime = endDateTime,
            DisplayName = "Test"
        };

        var info2 = new CreatedSecretInfo
        {
            KeyId = "key-id",
            SecretValue = "secret-value",
            StartDateTime = startDateTime,
            EndDateTime = endDateTime,
            DisplayName = "Test"
        };

        var info3 = new CreatedSecretInfo
        {
            KeyId = "different-key",
            SecretValue = "secret-value",
            StartDateTime = startDateTime,
            EndDateTime = endDateTime,
            DisplayName = "Test"
        };

        // Assert
        info1.Should().Be(info2);
        info1.Should().NotBe(info3);
    }

    [Fact]
    public void CreatedSecretInfo_EndDateTimeIsInFuture_WhenCreatedWithValidityDays()
    {
        // Arrange
        var validityDays = 365;
        var startDateTime = DateTimeOffset.UtcNow;
        var endDateTime = startDateTime.AddDays(validityDays);

        // Act
        var info = new CreatedSecretInfo
        {
            KeyId = "key-id",
            SecretValue = "secret-value",
            StartDateTime = startDateTime,
            EndDateTime = endDateTime,
            DisplayName = null
        };

        // Assert
        info.EndDateTime.Should().BeAfter(info.StartDateTime);
        (info.EndDateTime - info.StartDateTime).TotalDays.Should().BeApproximately(validityDays, 0.001);
    }
}
