using EntraIDSecretRotator.Models;
using AwesomeAssertions;
using Xunit;

namespace EntraIDSecretRotator.Tests.Models;

public class KeyVaultSecretInfoTests
{
    private const string ValidVaultName = "key-vault-3";
    private const string ValidSecretName = "my-app-secret";
    private const string ValidAppRegName = "myfilter-api-prod";
    private const string ValidClientId = "12345678-abcd-1234-abcd-123456789abc";
    private const string ValidSecretId = "12345678-aa11-bb22-cc33-a1b2c3d4e5f6";

    #region Create Tests

    [Fact]
    public void Create_WithValidContentType_ReturnsValidMapping()
    {
        // Arrange
        var contentType = $"{ValidAppRegName}:{ValidClientId}:{ValidSecretId}";

        // Act
        var info = KeyVaultSecretInfo.Create(ValidVaultName, ValidSecretName, contentType);

        // Assert
        info.VaultName.Should().Be(ValidVaultName);
        info.SecretName.Should().Be(ValidSecretName);
        info.ContentType.Should().Be(contentType);
        info.AppRegName.Should().Be(ValidAppRegName);
        info.ClientId.Should().Be(ValidClientId);
        info.SecretId.Should().Be(ValidSecretId);
        info.IsValidMapping.Should().BeTrue();
        info.Status.Should().Be(MappingStatus.Mapped);
    }

    [Fact]
    public void Create_WithNullContentType_ReturnsUnmapped()
    {
        // Act
        var info = KeyVaultSecretInfo.Create(ValidVaultName, ValidSecretName, null);

        // Assert
        info.VaultName.Should().Be(ValidVaultName);
        info.SecretName.Should().Be(ValidSecretName);
        info.ContentType.Should().BeNull();
        info.AppRegName.Should().BeNull();
        info.ClientId.Should().BeNull();
        info.SecretId.Should().BeNull();
        info.IsValidMapping.Should().BeFalse();
        info.Status.Should().Be(MappingStatus.Unmapped);
    }

    [Fact]
    public void Create_WithEmptyContentType_ReturnsUnmapped()
    {
        // Act
        var info = KeyVaultSecretInfo.Create(ValidVaultName, ValidSecretName, "");

        // Assert
        info.ContentType.Should().Be("");
        info.IsValidMapping.Should().BeFalse();
        info.Status.Should().Be(MappingStatus.Unmapped);
    }

    [Fact]
    public void Create_WithWhitespaceContentType_ReturnsUnmapped()
    {
        // Act
        var info = KeyVaultSecretInfo.Create(ValidVaultName, ValidSecretName, "   ");

        // Assert
        info.ContentType.Should().Be("   ");
        info.IsValidMapping.Should().BeFalse();
        // Whitespace-only is treated as invalid format, not empty
        info.Status.Should().Be(MappingStatus.Invalid);
    }

    [Fact]
    public void Create_WithInvalidContentType_ReturnsInvalid()
    {
        // Act
        var info = KeyVaultSecretInfo.Create(ValidVaultName, ValidSecretName, "invalid-format");

        // Assert
        info.ContentType.Should().Be("invalid-format");
        info.AppRegName.Should().BeNull();
        info.ClientId.Should().BeNull();
        info.SecretId.Should().BeNull();
        info.IsValidMapping.Should().BeFalse();
        info.Status.Should().Be(MappingStatus.Invalid);
    }

    #endregion

    #region ParseContentType Tests

    [Fact]
    public void ParseContentType_WithValidFormat_ReturnsAllParts()
    {
        // Arrange
        var contentType = $"{ValidAppRegName}:{ValidClientId}:{ValidSecretId}";

        // Act
        var result = KeyVaultSecretInfo.ParseContentType(contentType);

        // Assert
        result.AppRegName.Should().Be(ValidAppRegName);
        result.ClientId.Should().Be(ValidClientId);
        result.SecretId.Should().Be(ValidSecretId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseContentType_WithNullOrEmptyOrWhitespace_ReturnsNulls(string? contentType)
    {
        // Act
        var result = KeyVaultSecretInfo.ParseContentType(contentType);

        // Assert
        result.AppRegName.Should().BeNull();
        result.ClientId.Should().BeNull();
        result.SecretId.Should().BeNull();
    }

    [Theory]
    [InlineData("only-one-part")]
    [InlineData("two:parts")]
    [InlineData("four:parts:here:present")]
    [InlineData("five:parts:here:present:now")]
    public void ParseContentType_WithWrongNumberOfParts_ReturnsNulls(string contentType)
    {
        // Act
        var result = KeyVaultSecretInfo.ParseContentType(contentType);

        // Assert
        result.AppRegName.Should().BeNull();
        result.ClientId.Should().BeNull();
        result.SecretId.Should().BeNull();
    }

    [Theory]
    [InlineData(":clientId:secretId")]
    [InlineData("appName::secretId")]
    [InlineData("appName:clientId:")]
    [InlineData("::")]
    public void ParseContentType_WithEmptyParts_ReturnsNulls(string contentType)
    {
        // Act
        var result = KeyVaultSecretInfo.ParseContentType(contentType);

        // Assert
        result.AppRegName.Should().BeNull();
        result.ClientId.Should().BeNull();
        result.SecretId.Should().BeNull();
    }

    [Fact]
    public void ParseContentType_WithWhitespaceParts_TrimsAndReturnsNullsForEmpty()
    {
        // Act - all parts are just whitespace
        var result = KeyVaultSecretInfo.ParseContentType("   :   :   ");

        // Assert - whitespace-only parts become empty after trim, so returns nulls
        result.AppRegName.Should().BeNull();
        result.ClientId.Should().BeNull();
        result.SecretId.Should().BeNull();
    }

    [Fact]
    public void ParseContentType_WithLeadingTrailingWhitespace_TrimsCorrectly()
    {
        // Arrange
        var contentType = $"  {ValidAppRegName}  :  {ValidClientId}  :  {ValidSecretId}  ";

        // Act
        var result = KeyVaultSecretInfo.ParseContentType(contentType);

        // Assert - whitespace is trimmed
        result.AppRegName.Should().Be(ValidAppRegName);
        result.ClientId.Should().Be(ValidClientId);
        result.SecretId.Should().Be(ValidSecretId);
    }

    #endregion

    #region Status Tests

    [Fact]
    public void Status_WhenAllPartsValid_ReturnsMapped()
    {
        // Arrange
        var contentType = $"{ValidAppRegName}:{ValidClientId}:{ValidSecretId}";
        var info = KeyVaultSecretInfo.Create(ValidVaultName, ValidSecretName, contentType);

        // Assert
        info.Status.Should().Be(MappingStatus.Mapped);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Status_WhenContentTypeNullOrEmpty_ReturnsUnmapped(string? contentType)
    {
        // Arrange
        var info = KeyVaultSecretInfo.Create(ValidVaultName, ValidSecretName, contentType);

        // Assert
        info.Status.Should().Be(MappingStatus.Unmapped);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("missing:parts")]
    [InlineData("too:many:parts:here")]
    [InlineData("application/json")] // Common content-type that doesn't match our format
    [InlineData("text/plain")]
    public void Status_WhenContentTypeInvalidFormat_ReturnsInvalid(string contentType)
    {
        // Arrange
        var info = KeyVaultSecretInfo.Create(ValidVaultName, ValidSecretName, contentType);

        // Assert
        info.Status.Should().Be(MappingStatus.Invalid);
    }

    #endregion

    #region Real-World Scenarios

    [Fact]
    public void Create_WithRealWorldValidExample_ParsesCorrectly()
    {
        // Arrange - documented content-type format
        var contentType = "myfilter-api-prod:12345678-abcd-1234-abcd-123456789abc:12345678-aa11-bb22-cc33-a1b2c3d4e5f6";

        // Act
        var info = KeyVaultSecretInfo.Create("key-vault-3", "api-secret", contentType);

        // Assert
        info.IsValidMapping.Should().BeTrue();
        info.AppRegName.Should().Be("myfilter-api-prod");
        info.ClientId.Should().Be("12345678-abcd-1234-abcd-123456789abc");
        info.SecretId.Should().Be("12345678-aa11-bb22-cc33-a1b2c3d4e5f6");
    }

    [Theory]
    [InlineData("key-vault-0")]
    [InlineData("key-vault-1")]
    [InlineData("key-vault-2")]
    [InlineData("key-vault-3")]
    [InlineData("key-vault-4")]
    public void Create_WithAllConfiguredVaultNames_Works(string vaultName)
    {
        // Act
        var info = KeyVaultSecretInfo.Create(vaultName, "test-secret", null);

        // Assert
        info.VaultName.Should().Be(vaultName);
    }

    [Fact]
    public void IsValidMapping_RequiresAllThreeParts()
    {
        // This test ensures the IsValidMapping property correctly checks all three parts

        // Arrange - create info with all parts null
        var info = KeyVaultSecretInfo.Create(ValidVaultName, ValidSecretName, null);

        // Assert
        info.IsValidMapping.Should().BeFalse();
        info.AppRegName.Should().BeNull();
        info.ClientId.Should().BeNull();
        info.SecretId.Should().BeNull();
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void ParseContentType_WithColonsInValues_SplitsOnFirstThreeParts()
    {
        // Arrange - content-type with extra colons (4 parts, should fail)
        var contentType = "app:client:secret:extra";

        // Act
        var result = KeyVaultSecretInfo.ParseContentType(contentType);

        // Assert - returns null because there are 4 parts, not 3
        result.AppRegName.Should().BeNull();
        result.ClientId.Should().BeNull();
        result.SecretId.Should().BeNull();
    }

    [Fact]
    public void Create_PreservesOriginalContentType()
    {
        // Arrange
        var originalContentType = "  app  :  client  :  secret  ";

        // Act
        var info = KeyVaultSecretInfo.Create(ValidVaultName, ValidSecretName, originalContentType);

        // Assert - original content-type is preserved, but parsed values are trimmed
        info.ContentType.Should().Be(originalContentType);
        info.AppRegName.Should().Be("app");
        info.ClientId.Should().Be("client");
        info.SecretId.Should().Be("secret");
    }

    [Fact]
    public void Create_WithSpecialCharactersInValues_ParsesCorrectly()
    {
        // Arrange - GUIDs contain hyphens, app names might have hyphens and underscores
        var contentType = "myfilter-api_service-v2:a1b2c3d4-e5f6-7890-abcd-ef1234567890:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

        // Act
        var info = KeyVaultSecretInfo.Create(ValidVaultName, ValidSecretName, contentType);

        // Assert
        info.IsValidMapping.Should().BeTrue();
        info.AppRegName.Should().Be("myfilter-api_service-v2");
        info.ClientId.Should().Be("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        info.SecretId.Should().Be("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    }

    #endregion

    #region PreviousMapping Tests

    [Fact]
    public void WithPreviousMapping_AddsPreviousMappingInfo()
    {
        // Arrange
        var contentType = $"{ValidAppRegName}:{ValidClientId}:{ValidSecretId}";
        var info = KeyVaultSecretInfo.Create(ValidVaultName, ValidSecretName, contentType);

        var previousSecretId = "87654321-4321-4321-4321-cba987654321";
        var previousMapping = new PreviousMappingInfo
        {
            AppRegName = ValidAppRegName,
            ClientId = ValidClientId,
            SecretId = previousSecretId
        };

        // Act
        var infoWithPrevious = info.WithPreviousMapping(previousMapping);

        // Assert
        infoWithPrevious.HasPreviousMapping.Should().BeTrue();
        infoWithPrevious.PreviousMapping.Should().NotBeNull();
        infoWithPrevious.PreviousMapping!.SecretId.Should().Be(previousSecretId);
        infoWithPrevious.PreviousMapping.ClientId.Should().Be(ValidClientId);
        infoWithPrevious.PreviousMapping.AppRegName.Should().Be(ValidAppRegName);
    }

    [Fact]
    public void WithPreviousMapping_WithNull_ReturnsNoPreviousMapping()
    {
        // Arrange
        var contentType = $"{ValidAppRegName}:{ValidClientId}:{ValidSecretId}";
        var info = KeyVaultSecretInfo.Create(ValidVaultName, ValidSecretName, contentType);

        // Act
        var infoWithNull = info.WithPreviousMapping(null);

        // Assert
        infoWithNull.HasPreviousMapping.Should().BeFalse();
        infoWithNull.PreviousMapping.Should().BeNull();
    }

    [Fact]
    public void HasPreviousMapping_WhenNoPreviousMapping_ReturnsFalse()
    {
        // Arrange
        var info = KeyVaultSecretInfo.Create(ValidVaultName, ValidSecretName, null);

        // Assert
        info.HasPreviousMapping.Should().BeFalse();
    }

    [Fact]
    public void Create_InitiallyHasNoPreviousMapping()
    {
        // Arrange
        var contentType = $"{ValidAppRegName}:{ValidClientId}:{ValidSecretId}";

        // Act
        var info = KeyVaultSecretInfo.Create(ValidVaultName, ValidSecretName, contentType);

        // Assert
        info.HasPreviousMapping.Should().BeFalse();
        info.PreviousMapping.Should().BeNull();
    }

    #endregion

    #region PreviousMappingInfo Tests

    [Fact]
    public void PreviousMappingInfo_CreateFromContentType_WithValidFormat_ReturnsInfo()
    {
        // Arrange
        var contentType = $"{ValidAppRegName}:{ValidClientId}:{ValidSecretId}";

        // Act
        var result = PreviousMappingInfo.CreateFromContentType(contentType);

        // Assert
        result.Should().NotBeNull();
        result!.AppRegName.Should().Be(ValidAppRegName);
        result.ClientId.Should().Be(ValidClientId);
        result.SecretId.Should().Be(ValidSecretId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid")]
    [InlineData("two:parts")]
    [InlineData(":missing:first")]
    public void PreviousMappingInfo_CreateFromContentType_WithInvalidFormat_ReturnsNull(string? contentType)
    {
        // Act
        var result = PreviousMappingInfo.CreateFromContentType(contentType);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void PreviousMappingInfo_CreateFromContentType_WithDifferentSecretId_ReturnsCorrectValues()
    {
        // This simulates a previous version pointing to a different secret
        var previousSecretId = "87654321-4321-4321-4321-cba987654321";
        var contentType = $"{ValidAppRegName}:{ValidClientId}:{previousSecretId}";

        // Act
        var result = PreviousMappingInfo.CreateFromContentType(contentType);

        // Assert
        result.Should().NotBeNull();
        result!.SecretId.Should().Be(previousSecretId);
        result.SecretId.Should().NotBe(ValidSecretId);
    }

    #endregion
}
