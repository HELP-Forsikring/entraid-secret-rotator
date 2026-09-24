using EntraIDSecretRotator.Models;
using AwesomeAssertions;
using Xunit;

namespace EntraIDSecretRotator.Tests.Models;

public class AppRegistrationTests
{
    [Fact]
    public void SecretCount_WithMultipleSecrets_ReturnsCorrectCount()
    {
        // Arrange
        var app = new AppRegistration
        {
            Id = "id",
            AppId = "app-id",
            DisplayName = "Test App",
            Secrets = new List<SecretInfo>
            {
                new() { KeyId = "1", EndDateTime = DateTimeOffset.UtcNow.AddDays(30) },
                new() { KeyId = "2", EndDateTime = DateTimeOffset.UtcNow.AddDays(60) }
            }
        };

        // Act & Assert
        app.SecretCount.Should().Be(2);
    }

    [Fact]
    public void ExpiredSecretCount_WithMixedSecrets_ReturnsCorrectCount()
    {
        // Arrange
        var app = new AppRegistration
        {
            Id = "id",
            AppId = "app-id",
            DisplayName = "Test App",
            Secrets = new List<SecretInfo>
            {
                new() { KeyId = "1", EndDateTime = DateTimeOffset.UtcNow.AddDays(-10) }, // Expired
                new() { KeyId = "2", EndDateTime = DateTimeOffset.UtcNow.AddDays(30) },  // Valid
                new() { KeyId = "3", EndDateTime = DateTimeOffset.UtcNow.AddDays(-5) }   // Expired
            }
        };

        // Act & Assert
        app.ExpiredSecretCount.Should().Be(2);
    }

    [Fact]
    public void ExpiringSecretCount_WithThreshold_ReturnsCorrectCount()
    {
        // Arrange
        var app = new AppRegistration
        {
            Id = "id",
            AppId = "app-id",
            DisplayName = "Test App",
            Secrets = new List<SecretInfo>
            {
                new() { KeyId = "1", EndDateTime = DateTimeOffset.UtcNow.AddDays(10) },  // Expiring soon
                new() { KeyId = "2", EndDateTime = DateTimeOffset.UtcNow.AddDays(25) },  // Expiring soon
                new() { KeyId = "3", EndDateTime = DateTimeOffset.UtcNow.AddDays(60) },  // Not expiring
                new() { KeyId = "4", EndDateTime = DateTimeOffset.UtcNow.AddDays(-5) }   // Already expired
            }
        };

        // Act
        var count = app.ExpiringSecretCount(30);

        // Assert
        count.Should().Be(2);
    }

    [Fact]
    public void HasExpiredSecrets_WithExpiredSecret_ReturnsTrue()
    {
        // Arrange
        var app = new AppRegistration
        {
            Id = "id",
            AppId = "app-id",
            DisplayName = "Test App",
            Secrets = new List<SecretInfo>
            {
                new() { KeyId = "1", EndDateTime = DateTimeOffset.UtcNow.AddDays(-10) }
            }
        };

        // Act & Assert
        app.HasExpiredSecrets.Should().BeTrue();
    }

    [Fact]
    public void HasExpiredSecrets_WithNoExpiredSecrets_ReturnsFalse()
    {
        // Arrange
        var app = new AppRegistration
        {
            Id = "id",
            AppId = "app-id",
            DisplayName = "Test App",
            Secrets = new List<SecretInfo>
            {
                new() { KeyId = "1", EndDateTime = DateTimeOffset.UtcNow.AddDays(30) }
            }
        };

        // Act & Assert
        app.HasExpiredSecrets.Should().BeFalse();
    }

    [Fact]
    public void HasExpiringSecrets_WithExpiringSecret_ReturnsTrue()
    {
        // Arrange
        var app = new AppRegistration
        {
            Id = "id",
            AppId = "app-id",
            DisplayName = "Test App",
            Secrets = new List<SecretInfo>
            {
                new() { KeyId = "1", EndDateTime = DateTimeOffset.UtcNow.AddDays(15) }
            }
        };

        // Act
        var result = app.HasExpiringSecrets(30);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void HasExpiringSecrets_WithNoExpiringSecrets_ReturnsFalse()
    {
        // Arrange
        var app = new AppRegistration
        {
            Id = "id",
            AppId = "app-id",
            DisplayName = "Test App",
            Secrets = new List<SecretInfo>
            {
                new() { KeyId = "1", EndDateTime = DateTimeOffset.UtcNow.AddDays(60) }
            }
        };

        // Act
        var result = app.HasExpiringSecrets(30);

        // Assert
        result.Should().BeFalse();
    }
}
