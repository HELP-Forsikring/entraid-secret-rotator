using EntraIDSecretRotator.Services;
using AwesomeAssertions;
using Microsoft.Graph.Models;
using Xunit;

namespace EntraIDSecretRotator.Tests.Services;

public class EntraIdServiceTests
{
    [Theory]
    [InlineData("myfilter-api-prod", "myfilter", true)]
    [InlineData("MYFILTER-API-PROD", "myfilter", true)]
    [InlineData("my-MyFilter-app", "myfilter", true)]
    [InlineData("myfilter", "myfilter", true)]
    [InlineData("other-api", "myfilter", false)]
    [InlineData("", "myfilter", false)]
    public void MatchesFilter_IsCaseInsensitiveContains(string displayName, string filter, bool expected)
    {
        // Arrange
        var app = new Application { DisplayName = displayName };

        // Act
        var result = EntraIdService.MatchesFilter(app, filter);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void MatchesFilter_WithNullDisplayName_ReturnsFalse()
    {
        // Arrange
        var app = new Application { DisplayName = null };

        // Act
        var result = EntraIdService.MatchesFilter(app, "myfilter");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void GraphMaxPageSize_IsGraphMaximumForApplications()
    {
        // Graph rejects $top above 999 on /applications
        EntraIdService.GraphMaxPageSize.Should().Be(999);
    }
}
