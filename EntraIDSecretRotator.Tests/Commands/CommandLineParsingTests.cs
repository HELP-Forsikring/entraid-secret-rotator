using System.CommandLine;
using EntraIDSecretRotator.Infrastructure;
using AwesomeAssertions;
using Xunit;

namespace EntraIDSecretRotator.Tests.Commands;

/// <summary>
/// Guards the System.CommandLine 2.0 GA migration: the commands rely on nullable option types
/// so that an omitted option yields null and falls back to configuration.
/// </summary>
public class CommandLineParsingTests
{
    private static (Command Command, Option<bool?> Flag, Option<int?> Days, Option<OutputFormat?> Output) BuildCommand()
    {
        var flag = new Option<bool?>("--dry-run", "-n") { Description = "flag" };
        var days = new Option<int?>("--days", "-d") { Description = "days" };
        var output = new Option<OutputFormat?>("--output", "-o") { Description = "output" };
        var command = new Command("test");
        command.Options.Add(flag);
        command.Options.Add(days);
        command.Options.Add(output);
        return (command, flag, days, output);
    }

    [Fact]
    public void NullableBoolFlag_WhenOmitted_IsNull()
    {
        var (command, flag, _, _) = BuildCommand();

        var result = command.Parse([]);

        result.Errors.Should().BeEmpty();
        result.GetValue(flag).Should().BeNull();
    }

    [Theory]
    [InlineData("--dry-run")]
    [InlineData("-n")]
    public void NullableBoolFlag_WhenPresentWithoutValue_IsTrue(string arg)
    {
        var (command, flag, _, _) = BuildCommand();

        var result = command.Parse([arg]);

        result.Errors.Should().BeEmpty();
        result.GetValue(flag).Should().BeTrue();
    }

    [Fact]
    public void NullableInt_WhenOmitted_IsNull_AndWhenGiven_IsParsed()
    {
        var (command, _, days, _) = BuildCommand();

        command.Parse([]).GetValue(days).Should().BeNull();
        command.Parse(["--days", "45"]).GetValue(days).Should().Be(45);
        command.Parse(["-d", "7"]).GetValue(days).Should().Be(7);
    }

    [Fact]
    public void NullableEnum_IsParsedCaseInsensitively()
    {
        var (command, _, _, output) = BuildCommand();

        command.Parse([]).GetValue(output).Should().BeNull();
        command.Parse(["--output", "json"]).GetValue(output).Should().Be(OutputFormat.Json);
        command.Parse(["-o", "Yaml"]).GetValue(output).Should().Be(OutputFormat.Yaml);
    }
}
