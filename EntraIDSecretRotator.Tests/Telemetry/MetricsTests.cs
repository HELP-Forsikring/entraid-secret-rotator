using System.Diagnostics.Metrics;
using EntraIDSecretRotator.Telemetry;
using AwesomeAssertions;
using Xunit;

namespace EntraIDSecretRotator.Tests.Telemetry;

public class MetricsTests
{
    private static List<Dictionary<string, object?>> Observe(Metrics metrics, string instrumentName)
    {
        var observed = new List<Dictionary<string, object?>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == Metrics.MeterName && instrument.Name == instrumentName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<int>((_, _, tags, _) =>
        {
            var dict = new Dictionary<string, object?>();
            foreach (var tag in tags) dict[tag.Key] = tag.Value;
            observed.Add(dict);
        });
        listener.Start();
        listener.RecordObservableInstruments();
        return observed;
    }

    [Fact]
    public void OrphanSecrets_Gauge_CarriesPendingDeletionLabel()
    {
        // Arrange
        using var metrics = new Metrics();
        metrics.RecordOrphanSecret("myfilter-rotated-app", "client-a", 11, pendingDeletion: true);
        metrics.RecordOrphanSecret("myfilter-hand-rotated", "client-b", 29, pendingDeletion: false);

        // Act
        var observed = Observe(metrics, "entraid_rotator_orphan_secrets");

        // Assert
        observed.Should().HaveCount(2);
        observed.Should().ContainSingle(t =>
            (string?)t["app_name"] == "myfilter-rotated-app" && (string?)t["pending_deletion"] == "true");
        observed.Should().ContainSingle(t =>
            (string?)t["app_name"] == "myfilter-hand-rotated" && (string?)t["pending_deletion"] == "false");
        observed.Should().OnlyContain(t => !t.ContainsKey("has_healthy_replacement"));
    }

    [Fact]
    public void ClearOrphanSecrets_RemovesAllEntries()
    {
        // Arrange
        using var metrics = new Metrics();
        metrics.RecordOrphanSecret("myfilter-x", "client", 5, pendingDeletion: false);

        // Act
        metrics.ClearOrphanSecrets();

        // Assert
        Observe(metrics, "entraid_rotator_orphan_secrets").Should().BeEmpty();
    }
}
