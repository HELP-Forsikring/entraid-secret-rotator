using EntraIDSecretRotator.Infrastructure;
using AwesomeAssertions;
using Xunit;

namespace EntraIDSecretRotator.Tests.Infrastructure;

public class RotatorOptionsTests : IDisposable
{
    private const string EnvPrefix = "Rotator__";
    private readonly List<string> _envVarsToClean = [];

    public void Dispose()
    {
        // Clean up any environment variables set during tests
        foreach (var envVar in _envVarsToClean)
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    private void SetEnvVar(string name, string value)
    {
        var fullName = $"{EnvPrefix}{name}";
        Environment.SetEnvironmentVariable(fullName, value);
        _envVarsToClean.Add(fullName);
    }

    #region GetAppFilter Tests

    [Fact]
    public void GetAppFilter_WhenNoConfigOrEnvVar_ReturnsCliDefault()
    {
        // Arrange
        var options = new RotatorOptions();

        // Act
        var result = options.GetAppFilter("cli-default");

        // Assert
        result.Should().Be("cli-default");
    }

    [Fact]
    public void GetAppFilter_WhenConfigSet_ReturnsConfigValue()
    {
        // Arrange
        var options = new RotatorOptions { AppFilter = "config-value" };

        // Act
        var result = options.GetAppFilter("cli-default");

        // Assert
        result.Should().Be("config-value");
    }

    [Fact]
    public void GetAppFilter_WhenEnvVarSet_ReturnsEnvVarValue()
    {
        // Arrange
        SetEnvVar("AppFilter", "env-value");
        var options = new RotatorOptions { AppFilter = "config-value" };

        // Act
        var result = options.GetAppFilter("cli-default");

        // Assert
        result.Should().Be("env-value");
    }

    [Fact]
    public void GetAppFilter_EnvVarOverridesConfig()
    {
        // Arrange
        SetEnvVar("AppFilter", "env-value");
        var options = new RotatorOptions { AppFilter = "config-value" };

        // Act
        var result = options.GetAppFilter("cli-default");

        // Assert
        result.Should().Be("env-value");
    }

    #endregion

    #region GetOutput Tests

    [Fact]
    public void GetOutput_WhenNoConfigOrEnvVar_ReturnsCliDefault()
    {
        // Arrange
        var options = new RotatorOptions();

        // Act
        var result = options.GetOutput(OutputFormat.Table);

        // Assert
        result.Should().Be(OutputFormat.Table);
    }

    [Fact]
    public void GetOutput_WhenConfigSet_ReturnsConfigValue()
    {
        // Arrange
        var options = new RotatorOptions { Output = "json" };

        // Act
        var result = options.GetOutput(OutputFormat.Table);

        // Assert
        result.Should().Be(OutputFormat.Json);
    }

    [Fact]
    public void GetOutput_WhenEnvVarSet_ReturnsEnvVarValue()
    {
        // Arrange
        SetEnvVar("Output", "yaml");
        var options = new RotatorOptions { Output = "json" };

        // Act
        var result = options.GetOutput(OutputFormat.Table);

        // Assert
        result.Should().Be(OutputFormat.Yaml);
    }

    [Fact]
    public void GetOutput_IsCaseInsensitive()
    {
        // Arrange
        SetEnvVar("Output", "YAML");
        var options = new RotatorOptions();

        // Act
        var result = options.GetOutput(OutputFormat.Table);

        // Assert
        result.Should().Be(OutputFormat.Yaml);
    }

    [Fact]
    public void GetOutput_WhenEnvVarInvalid_FallsBackToConfig()
    {
        // Arrange
        SetEnvVar("Output", "invalid");
        var options = new RotatorOptions { Output = "json" };

        // Act
        var result = options.GetOutput(OutputFormat.Table);

        // Assert
        result.Should().Be(OutputFormat.Json);
    }

    [Fact]
    public void GetOutput_WhenBothInvalid_FallsBackToCliDefault()
    {
        // Arrange
        SetEnvVar("Output", "invalid");
        var options = new RotatorOptions { Output = "also-invalid" };

        // Act
        var result = options.GetOutput(OutputFormat.Table);

        // Assert
        result.Should().Be(OutputFormat.Table);
    }

    #endregion

    #region GetDays Tests

    [Fact]
    public void GetDays_WhenNoConfigOrEnvVar_ReturnsCliDefault()
    {
        // Arrange
        var options = new RotatorOptions();

        // Act
        var result = options.GetDays(30);

        // Assert
        result.Should().Be(30);
    }

    [Fact]
    public void GetDays_WhenConfigSet_ReturnsConfigValue()
    {
        // Arrange
        var options = new RotatorOptions { Days = 60 };

        // Act
        var result = options.GetDays(30);

        // Assert
        result.Should().Be(60);
    }

    [Fact]
    public void GetDays_WhenEnvVarSet_ReturnsEnvVarValue()
    {
        // Arrange
        SetEnvVar("Days", "90");
        var options = new RotatorOptions { Days = 60 };

        // Act
        var result = options.GetDays(30);

        // Assert
        result.Should().Be(90);
    }

    [Fact]
    public void GetDays_WhenEnvVarInvalid_FallsBackToConfig()
    {
        // Arrange
        SetEnvVar("Days", "not-a-number");
        var options = new RotatorOptions { Days = 60 };

        // Act
        var result = options.GetDays(30);

        // Assert
        result.Should().Be(60);
    }

    #endregion

    #region GetNotExpired Tests

    [Fact]
    public void GetNotExpired_WhenNoConfigOrEnvVar_ReturnsCliDefault()
    {
        // Arrange
        var options = new RotatorOptions();

        // Act
        var result = options.GetNotExpired(false);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void GetNotExpired_WhenConfigSet_ReturnsConfigValue()
    {
        // Arrange
        var options = new RotatorOptions { NotExpired = true };

        // Act
        var result = options.GetNotExpired(false);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void GetNotExpired_WhenEnvVarSet_ReturnsEnvVarValue()
    {
        // Arrange
        SetEnvVar("NotExpired", "true");
        var options = new RotatorOptions { NotExpired = false };

        // Act
        var result = options.GetNotExpired(false);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void GetNotExpired_WhenEnvVarInvalid_FallsBackToConfig()
    {
        // Arrange
        SetEnvVar("NotExpired", "not-a-bool");
        var options = new RotatorOptions { NotExpired = true };

        // Act
        var result = options.GetNotExpired(false);

        // Assert
        result.Should().BeTrue();
    }

    #endregion

    #region GetDryRun Tests

    [Fact]
    public void GetDryRun_WhenNoConfigOrEnvVar_ReturnsCliDefault()
    {
        // Arrange
        var options = new RotatorOptions();

        // Act
        var result = options.GetDryRun(false);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void GetDryRun_WhenConfigSet_ReturnsConfigValue()
    {
        // Arrange
        var options = new RotatorOptions { DryRun = true };

        // Act
        var result = options.GetDryRun(false);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void GetDryRun_WhenEnvVarSet_ReturnsEnvVarValue()
    {
        // Arrange
        SetEnvVar("DryRun", "true");
        var options = new RotatorOptions { DryRun = false };

        // Act
        var result = options.GetDryRun(false);

        // Assert
        result.Should().BeTrue();
    }

    #endregion

    #region GetKeyVaults Tests

    [Fact]
    public void GetKeyVaults_WhenNoConfigOrEnvVar_ReturnsEmptyList()
    {
        // Arrange
        var options = new RotatorOptions();

        // Act
        var result = options.GetKeyVaults();

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void GetKeyVaults_WhenConfigSet_ReturnsConfigValue()
    {
        // Arrange
        var options = new RotatorOptions
        {
            KeyVaults = ["vault-1", "vault-2", "vault-3"]
        };

        // Act
        var result = options.GetKeyVaults();

        // Assert
        result.Should().BeEquivalentTo(["vault-1", "vault-2", "vault-3"]);
    }

    [Fact]
    public void GetKeyVaults_WhenEnvVarSet_ReturnsEnvVarValue()
    {
        // Arrange
        SetEnvVar("KeyVaults", "env-vault-1,env-vault-2");
        var options = new RotatorOptions
        {
            KeyVaults = ["config-vault-1"]
        };

        // Act
        var result = options.GetKeyVaults();

        // Assert
        result.Should().BeEquivalentTo(["env-vault-1", "env-vault-2"]);
    }

    [Fact]
    public void GetKeyVaults_EnvVar_TrimsWhitespace()
    {
        // Arrange
        SetEnvVar("KeyVaults", "  vault-1 , vault-2 ,  vault-3  ");
        var options = new RotatorOptions();

        // Act
        var result = options.GetKeyVaults();

        // Assert
        result.Should().BeEquivalentTo(["vault-1", "vault-2", "vault-3"]);
    }

    [Fact]
    public void GetKeyVaults_EnvVar_SkipsEmptyEntries()
    {
        // Arrange
        SetEnvVar("KeyVaults", "vault-1,,vault-2,,,vault-3");
        var options = new RotatorOptions();

        // Act
        var result = options.GetKeyVaults();

        // Assert
        result.Should().BeEquivalentTo(["vault-1", "vault-2", "vault-3"]);
    }

    [Fact]
    public void GetKeyVaults_EnvVar_OverridesConfig()
    {
        // Arrange
        SetEnvVar("KeyVaults", "env-vault");
        var options = new RotatorOptions
        {
            KeyVaults = ["config-vault-1", "config-vault-2"]
        };

        // Act
        var result = options.GetKeyVaults();

        // Assert
        result.Should().BeEquivalentTo(["env-vault"]);
    }

    [Fact]
    public void GetKeyVaults_EnvVar_SingleValue_ReturnsAsList()
    {
        // Arrange
        SetEnvVar("KeyVaults", "single-vault");
        var options = new RotatorOptions();

        // Act
        var result = options.GetKeyVaults();

        // Assert
        result.Should().BeEquivalentTo(["single-vault"]);
    }

    #endregion

    #region Priority Order Tests

    [Fact]
    public void Priority_EnvVarOverridesConfigOverridesCli()
    {
        // This test verifies the complete priority chain
        // Arrange
        SetEnvVar("AppFilter", "env-wins");
        var options = new RotatorOptions { AppFilter = "config-value" };

        // Act
        var result = options.GetAppFilter("cli-default");

        // Assert
        result.Should().Be("env-wins");
    }

    [Fact]
    public void Priority_ConfigOverridesCli_WhenNoEnvVar()
    {
        // Arrange
        var options = new RotatorOptions { AppFilter = "config-wins" };

        // Act
        var result = options.GetAppFilter("cli-default");

        // Assert
        result.Should().Be("config-wins");
    }

    #endregion
}
