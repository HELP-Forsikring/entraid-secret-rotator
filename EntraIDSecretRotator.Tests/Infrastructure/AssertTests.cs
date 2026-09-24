using EntraIDSecretRotator.Infrastructure;
using AwesomeAssertions;
using Xunit;
using Assert = EntraIDSecretRotator.Infrastructure.Assert;

namespace EntraIDSecretRotator.Tests.Infrastructure;

public class AssertTests
{
    [Fact]
    public void That_WithTrueCondition_DoesNotThrow()
    {
        // Act
        var act = () => Assert.That(true, "test condition");

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void NotNull_WithNonNullValue_DoesNotThrow()
    {
        // Arrange
        var value = "test";

        // Act
        var act = () => Assert.NotNull(value, "value");

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void NotNullOrEmpty_WithValidString_DoesNotThrow()
    {
        // Arrange
        var value = "test";

        // Act
        var act = () => Assert.NotNullOrEmpty(value, "value");

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void NotNullOrEmpty_WithNonEmptyCollection_DoesNotThrow()
    {
        // Arrange
        var collection = new List<int> { 1, 2, 3 };

        // Act
        var act = () => Assert.NotNullOrEmpty(collection, "collection");

        // Assert
        act.Should().NotThrow();
    }

    // Note: We cannot test the failure cases directly because they call Environment.FailFast()
    // which would terminate the test process. In a real-world scenario, you might want to
    // refactor Assert to be testable (e.g., by injecting an exit handler).
}
