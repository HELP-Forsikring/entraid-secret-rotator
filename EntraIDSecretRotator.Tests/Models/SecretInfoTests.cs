using EntraIDSecretRotator.Models;
using AwesomeAssertions;
using Xunit;

namespace EntraIDSecretRotator.Tests.Models;

public class SecretInfoTests
{
    [Fact]
    public void DaysUntilExpiry_WhenExpiresInFuture_ReturnsPositiveDays()
    {
        // Arrange
        var expiryDate = DateTimeOffset.UtcNow.AddDays(30);
        var secret = new SecretInfo
        {
            KeyId = "test-key-id",
            EndDateTime = expiryDate
        };

        // Act
        var days = secret.DaysUntilExpiry;

        // Assert
        days.Should().BePositive().And.BeLessThanOrEqualTo(30);
    }

    [Fact]
    public void DaysUntilExpiry_WhenExpired_ReturnsNegativeDays()
    {
        // Arrange
        var expiryDate = DateTimeOffset.UtcNow.AddDays(-10);
        var secret = new SecretInfo
        {
            KeyId = "test-key-id",
            EndDateTime = expiryDate
        };

        // Act
        var days = secret.DaysUntilExpiry;

        // Assert
        days.Should().BeLessThan(0);
    }

    [Fact]
    public void IsExpired_WhenExpiryDateInPast_ReturnsTrue()
    {
        // Arrange
        var secret = new SecretInfo
        {
            KeyId = "test-key-id",
            EndDateTime = DateTimeOffset.UtcNow.AddDays(-1)
        };

        // Act & Assert
        secret.IsExpired.Should().BeTrue();
    }

    [Fact]
    public void IsExpired_WhenExpiryDateInFuture_ReturnsFalse()
    {
        // Arrange
        var secret = new SecretInfo
        {
            KeyId = "test-key-id",
            EndDateTime = DateTimeOffset.UtcNow.AddDays(30)
        };

        // Act & Assert
        secret.IsExpired.Should().BeFalse();
    }

    [Theory]
    [InlineData(-1, "expired")]
    [InlineData(10, "30d")]
    [InlineData(30, "30d")]
    [InlineData(45, "60d")]
    [InlineData(60, "60d")]
    [InlineData(75, "90d")]
    [InlineData(90, "90d")]
    [InlineData(120, "90d+")]
    public void ExpiryBucket_ReturnsCorrectBucket(int daysUntilExpiry, string expectedBucket)
    {
        // Arrange
        var expiryDate = DateTimeOffset.UtcNow.AddDays(daysUntilExpiry);
        var secret = new SecretInfo
        {
            KeyId = "test-key-id",
            EndDateTime = expiryDate
        };

        // Act
        var bucket = secret.ExpiryBucket;

        // Assert
        bucket.Should().Be(expectedBucket);
    }

    [Theory]
    [InlineData(10, 30, true)]
    [InlineData(30, 30, true)]
    [InlineData(32, 30, false)]  // Use 32 instead of 31 to avoid timing edge case (microsecond drift)
    [InlineData(60, 30, false)]
    public void IsExpiringSoon_WithThreshold_ReturnsExpectedResult(
        int daysUntilExpiry,
        int threshold,
        bool expectedResult)
    {
        // Arrange
        var expiryDate = DateTimeOffset.UtcNow.AddDays(daysUntilExpiry);
        var secret = new SecretInfo
        {
            KeyId = "test-key-id",
            EndDateTime = expiryDate
        };

        // Act
        var result = secret.IsExpiringSoon(threshold);

        // Assert
        result.Should().Be(expectedResult);
    }

    [Fact]
    public void IsExpiringSoon_WhenAlreadyExpired_ReturnsFalse()
    {
        // Arrange
        var secret = new SecretInfo
        {
            KeyId = "test-key-id",
            EndDateTime = DateTimeOffset.UtcNow.AddDays(-1)
        };

        // Act
        var result = secret.IsExpiringSoon(30);

        // Assert
        result.Should().BeFalse();
    }
}
