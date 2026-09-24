using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace EntraIDSecretRotator.Telemetry;

/// <summary>
/// Setup OpenTelemetry for the application.
/// </summary>
public static class TelemetrySetup
{
    /// <summary>
    /// Configure OpenTelemetry metrics export.
    /// </summary>
    public static MeterProvider ConfigureMetrics()
    {
        var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");

        var builder = Sdk.CreateMeterProviderBuilder()
            .AddMeter("EntraIDSecretRotator");

        // Only add OTLP exporter if endpoint is configured
        if (!string.IsNullOrEmpty(otlpEndpoint))
        {
            builder.AddOtlpExporter();
        }

        return builder.Build()!;
    }
}
