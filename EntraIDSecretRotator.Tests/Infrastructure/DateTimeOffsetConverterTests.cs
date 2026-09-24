using EntraIDSecretRotator.Infrastructure;
using AwesomeAssertions;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace EntraIDSecretRotator.Tests.Infrastructure;

public class DateTimeOffsetConverterTests
{
    private readonly ISerializer _serializer;
    private readonly IDeserializer _deserializer;

    public DateTimeOffsetConverterTests()
    {
        _serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new DateTimeOffsetConverter())
            .Build();

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new DateTimeOffsetConverter())
            .Build();
    }

    [Fact]
    public void Accepts_DateTimeOffset_ReturnsTrue()
    {
        // Arrange
        var converter = new DateTimeOffsetConverter();

        // Act & Assert
        converter.Accepts(typeof(DateTimeOffset)).Should().BeTrue();
    }

    [Fact]
    public void Accepts_NullableDateTimeOffset_ReturnsTrue()
    {
        // Arrange
        var converter = new DateTimeOffsetConverter();

        // Act & Assert
        converter.Accepts(typeof(DateTimeOffset?)).Should().BeTrue();
    }

    [Fact]
    public void Accepts_OtherTypes_ReturnsFalse()
    {
        // Arrange
        var converter = new DateTimeOffsetConverter();

        // Act & Assert
        converter.Accepts(typeof(DateTime)).Should().BeFalse();
        converter.Accepts(typeof(string)).Should().BeFalse();
        converter.Accepts(typeof(int)).Should().BeFalse();
    }

    [Fact]
    public void Serialize_DateTimeOffset_ReturnsIso8601String()
    {
        // Arrange
        var dateTime = new DateTimeOffset(2025, 11, 17, 11, 28, 35, 655, TimeSpan.Zero);
        var obj = new { EndDateTime = dateTime };

        // Act
        var yaml = _serializer.Serialize(obj);

        // Assert
        yaml.Should().Contain("endDateTime: 2025-11-17T11:28:35.6550000+00:00");
    }

    [Fact]
    public void Serialize_DateTimeOffsetWithOffset_PreservesOffset()
    {
        // Arrange
        var dateTime = new DateTimeOffset(2025, 11, 17, 12, 28, 35, 0, TimeSpan.FromHours(1));
        var obj = new { EndDateTime = dateTime };

        // Act
        var yaml = _serializer.Serialize(obj);

        // Assert
        yaml.Should().Contain("endDateTime: 2025-11-17T12:28:35.0000000+01:00");
    }

    [Fact]
    public void Serialize_NullableDateTimeOffset_WhenNull_ReturnsEmptyString()
    {
        // Arrange
        DateTimeOffset? dateTime = null;
        var obj = new TestClassWithNullable { EndDateTime = dateTime };

        // Act
        var yaml = _serializer.Serialize(obj);

        // Assert
        yaml.Should().Contain("endDateTime: ''");
    }

    [Fact]
    public void Serialize_NullableDateTimeOffset_WhenHasValue_ReturnsIso8601String()
    {
        // Arrange
        DateTimeOffset? dateTime = new DateTimeOffset(2025, 11, 17, 11, 28, 35, 0, TimeSpan.Zero);
        var obj = new TestClassWithNullable { EndDateTime = dateTime };

        // Act
        var yaml = _serializer.Serialize(obj);

        // Assert
        yaml.Should().Contain("endDateTime: 2025-11-17T11:28:35.0000000+00:00");
    }

    [Fact]
    public void Deserialize_Iso8601String_ReturnsDateTimeOffset()
    {
        // Arrange
        var yaml = "endDateTime: 2025-11-17T11:28:35.6550000+00:00";

        // Act
        var result = _deserializer.Deserialize<TestClassWithNonNullable>(yaml);

        // Assert
        result.EndDateTime.Year.Should().Be(2025);
        result.EndDateTime.Month.Should().Be(11);
        result.EndDateTime.Day.Should().Be(17);
        result.EndDateTime.Hour.Should().Be(11);
        result.EndDateTime.Minute.Should().Be(28);
    }

    [Fact]
    public void Deserialize_Iso8601StringWithOffset_PreservesOffset()
    {
        // Arrange
        var yaml = "endDateTime: 2025-11-17T12:28:35.0000000+01:00";

        // Act
        var result = _deserializer.Deserialize<TestClassWithNonNullable>(yaml);

        // Assert
        result.EndDateTime.Offset.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void RoundTrip_PreservesValue()
    {
        // Arrange
        var original = new DateTimeOffset(2025, 12, 31, 23, 59, 59, 999, TimeSpan.FromHours(-5));
        var obj = new TestClassWithNonNullable { EndDateTime = original };

        // Act
        var yaml = _serializer.Serialize(obj);
        var result = _deserializer.Deserialize<TestClassWithNonNullable>(yaml);

        // Assert
        result.EndDateTime.Should().Be(original);
    }

    private class TestClassWithNullable
    {
        public DateTimeOffset? EndDateTime { get; set; }
    }

    private class TestClassWithNonNullable
    {
        public DateTimeOffset EndDateTime { get; set; }
    }
}
