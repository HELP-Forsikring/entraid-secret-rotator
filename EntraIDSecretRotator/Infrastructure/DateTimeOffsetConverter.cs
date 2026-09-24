using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace EntraIDSecretRotator.Infrastructure;

public sealed class DateTimeOffsetConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(DateTimeOffset) || type == typeof(DateTimeOffset?);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var scalar = parser.Consume<Scalar>();
        if (string.IsNullOrEmpty(scalar.Value))
            return null;
        return DateTimeOffset.Parse(scalar.Value);
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var dateTime = value as DateTimeOffset?;
        var formatted = dateTime?.ToString("o") ?? "";
        emitter.Emit(new Scalar(formatted));
    }
}
