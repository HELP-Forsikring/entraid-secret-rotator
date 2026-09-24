using System.Diagnostics.Metrics;

namespace EntraIDSecretRotator.Telemetry;

/// <summary>
/// OpenTelemetry metrics for the EntraID Secret Rotator
/// </summary>
public sealed class Metrics : IDisposable
{
    private readonly Meter _meter;
    private readonly ObservableGauge<int> _appRegistrationsTotal;
    private readonly ObservableGauge<int> _secretsExpiring;
    private readonly ObservableGauge<int> _orphanSecrets;

    // Rotation metrics - counters
    private readonly Counter<long> _secretsRotatedTotal;
    private readonly Counter<long> _secretsDeletedTotal;

    // Rotation metrics - histogram
    private readonly Histogram<double> _jobDurationSeconds;

    private int _currentAppRegistrationsCount;
    private readonly Dictionary<string, int> _secretsByExpiryBucket = new();
    private readonly List<SecretExpiryEntry> _secretExpiryEntries = new();
    private readonly List<OrphanMetricEntry> _orphanEntries = new();

    // Rotation state for observable gauges
    private readonly List<RotationFailedEntry> _rotationFailedEntries = new();
    private readonly List<ManualInterventionEntry> _manualInterventionEntries = new();
    private readonly List<DuplicateMappingEntry> _duplicateMappingEntries = new();

    public const string MeterName = "EntraIDSecretRotator";
    public const string MeterVersion = "1.0.0";

    public Metrics()
    {
        _meter = new Meter(MeterName, MeterVersion);

        _appRegistrationsTotal = _meter.CreateObservableGauge(
            "entraid_rotator_app_registrations_total",
            () => _currentAppRegistrationsCount,
            description: "Total number of app registrations discovered");

        _secretsExpiring = _meter.CreateObservableGauge(
            "entraid_rotator_secrets_expiring",
            ObserveSecretsExpiring,
            description: "Number of secrets expiring, bucketed by time range");

        _meter.CreateObservableGauge(
            "entraid_rotator_secret_expiry_info",
            ObserveSecretExpiryInfo,
            description: "Per-secret expiry information with app details");

        _orphanSecrets = _meter.CreateObservableGauge(
            "entraid_rotator_orphan_secrets",
            ObserveOrphanSecrets,
            description: "Unmapped secrets needing attention. pending_deletion=true means the rotator replaced the secret and deletes it at expiry");

        // Rotation counters
        _secretsRotatedTotal = _meter.CreateCounter<long>(
            "entraid_rotator_secrets_rotated_total",
            description: "Total rotation attempts");

        _secretsDeletedTotal = _meter.CreateCounter<long>(
            "entraid_rotator_secrets_deleted_total",
            description: "Expired secrets deleted");

        // Rotation histogram
        _jobDurationSeconds = _meter.CreateHistogram<double>(
            "entraid_rotator_job_duration_seconds",
            unit: "s",
            description: "Job execution time in seconds");

        // Observable gauges for failure/alert tracking
        _meter.CreateObservableGauge(
            "entraid_rotator_rotation_failed",
            ObserveRotationFailed,
            description: "Failed rotations");

        _meter.CreateObservableGauge(
            "entraid_rotator_manual_intervention_required",
            ObserveManualIntervention,
            description: "Secrets needing manual attention");

        _meter.CreateObservableGauge(
            "entraid_rotator_duplicate_mappings",
            ObserveDuplicateMappings,
            description: "Detected duplicate mappings");
    }

    private IEnumerable<Measurement<int>> ObserveSecretsExpiring()
    {
        foreach (var (bucket, count) in _secretsByExpiryBucket)
        {
            yield return new Measurement<int>(
                count,
                new KeyValuePair<string, object?>("expiry_bucket", bucket));
        }
    }

    private IEnumerable<Measurement<int>> ObserveSecretExpiryInfo()
    {
        foreach (var entry in _secretExpiryEntries)
        {
            yield return new Measurement<int>(
                1,
                new KeyValuePair<string, object?>("app_name", entry.AppName),
                new KeyValuePair<string, object?>("client_id", entry.ClientId),
                new KeyValuePair<string, object?>("expiry_bucket", entry.ExpiryBucket),
                new KeyValuePair<string, object?>("days_until_expiry", entry.DaysUntilExpiry));
        }
    }

    private IEnumerable<Measurement<int>> ObserveOrphanSecrets()
    {
        foreach (var entry in _orphanEntries)
        {
            yield return new Measurement<int>(
                1,
                new KeyValuePair<string, object?>("app_name", entry.AppName),
                new KeyValuePair<string, object?>("client_id", entry.ClientId),
                new KeyValuePair<string, object?>("days_until_expiry", entry.DaysUntilExpiry),
                new KeyValuePair<string, object?>("pending_deletion", entry.PendingDeletion ? "true" : "false"));
        }
    }

    /// <summary>
    /// Update the app registrations count metric
    /// </summary>
    public void SetAppRegistrationsCount(int count)
    {
        _currentAppRegistrationsCount = count;
    }

    /// <summary>
    /// Update the secrets expiring metrics by bucket
    /// </summary>
    public void SetSecretsExpiringByBucket(Dictionary<string, int> buckets)
    {
        _secretsByExpiryBucket.Clear();
        foreach (var (bucket, count) in buckets)
        {
            _secretsByExpiryBucket[bucket] = count;
        }
    }

    /// <summary>
    /// Compatibility method for Agent 2 command pattern
    /// </summary>
    public void RecordAppRegistrationCount(int count) => SetAppRegistrationsCount(count);

    /// <summary>
    /// Compatibility method for Agent 2 command pattern
    /// </summary>
    public void RecordSecretsExpiringByBucket(Dictionary<string, int> buckets) => SetSecretsExpiringByBucket(buckets);

    /// <summary>
    /// Record a secret's expiry info for per-app metrics.
    /// </summary>
    public void RecordSecretExpiryInfo(string appName, string clientId, string expiryBucket, int daysUntilExpiry)
    {
        _secretExpiryEntries.Add(new SecretExpiryEntry(appName, clientId, expiryBucket, daysUntilExpiry));
    }

    /// <summary>
    /// Clear all secret expiry entries (call before a new scan).
    /// </summary>
    public void ClearSecretExpiryInfo()
    {
        _secretExpiryEntries.Clear();
    }

    /// <summary>
    /// Record an orphan secret for metrics.
    /// Call this for each orphan found.
    /// </summary>
    public void RecordOrphanSecret(string appName, string clientId, int daysUntilExpiry, bool pendingDeletion)
    {
        _orphanEntries.Add(new OrphanMetricEntry(appName, clientId, daysUntilExpiry, pendingDeletion));
    }

    /// <summary>
    /// Clear all orphan entries (call before a new scan).
    /// </summary>
    public void ClearOrphanSecrets()
    {
        _orphanEntries.Clear();
    }

    // =====================================================================
    // Rotation metrics methods
    // =====================================================================

    /// <summary>
    /// Record a rotation attempt with status (success/failed).
    /// </summary>
    public void RecordRotation(string appName, string clientId, string status)
    {
        _secretsRotatedTotal.Add(1,
            new KeyValuePair<string, object?>("status", status),
            new KeyValuePair<string, object?>("app_name", appName));
    }

    /// <summary>
    /// Record a successful deletion of an expired secret.
    /// </summary>
    public void RecordDeletion()
    {
        _secretsDeletedTotal.Add(1);
    }

    /// <summary>
    /// Record a failed rotation for alerting.
    /// </summary>
    public void RecordRotationFailed(string appName, string clientId, string reason)
    {
        _rotationFailedEntries.Add(new RotationFailedEntry(appName, clientId, reason));
    }

    /// <summary>
    /// Record a secret that requires manual intervention.
    /// </summary>
    public void RecordManualIntervention(string appName, string clientId, string secretId)
    {
        _manualInterventionEntries.Add(new ManualInterventionEntry(appName, clientId, secretId));
    }

    /// <summary>
    /// Record a detected duplicate mapping.
    /// </summary>
    public void RecordDuplicateMapping(string clientId, string secretId, int vaultCount)
    {
        _duplicateMappingEntries.Add(new DuplicateMappingEntry(clientId, secretId, vaultCount));
    }

    /// <summary>
    /// Record the job execution duration in seconds.
    /// </summary>
    public void RecordJobDuration(double seconds)
    {
        _jobDurationSeconds.Record(seconds);
    }

    /// <summary>
    /// Clear all rotation state (call before a new rotation run).
    /// </summary>
    public void ClearRotationState()
    {
        _rotationFailedEntries.Clear();
        _manualInterventionEntries.Clear();
        _duplicateMappingEntries.Clear();
    }

    private IEnumerable<Measurement<int>> ObserveRotationFailed()
    {
        foreach (var entry in _rotationFailedEntries)
        {
            yield return new Measurement<int>(
                1,
                new KeyValuePair<string, object?>("app_name", entry.AppName),
                new KeyValuePair<string, object?>("client_id", entry.ClientId),
                new KeyValuePair<string, object?>("reason", entry.Reason));
        }
    }

    private IEnumerable<Measurement<int>> ObserveManualIntervention()
    {
        foreach (var entry in _manualInterventionEntries)
        {
            yield return new Measurement<int>(
                1,
                new KeyValuePair<string, object?>("app_name", entry.AppName),
                new KeyValuePair<string, object?>("client_id", entry.ClientId),
                new KeyValuePair<string, object?>("secret_id", entry.SecretId));
        }
    }

    private IEnumerable<Measurement<int>> ObserveDuplicateMappings()
    {
        foreach (var entry in _duplicateMappingEntries)
        {
            yield return new Measurement<int>(
                entry.VaultCount,
                new KeyValuePair<string, object?>("client_id", entry.ClientId),
                new KeyValuePair<string, object?>("secret_id", entry.SecretId));
        }
    }

    public void Dispose()
    {
        _meter.Dispose();
    }

    // =====================================================================
    // Internal record types for tracking metrics state
    // =====================================================================

    /// <summary>
    /// Internal record for tracking orphan secrets in metrics.
    /// </summary>
    private sealed record SecretExpiryEntry(string AppName, string ClientId, string ExpiryBucket, int DaysUntilExpiry);

    private sealed record OrphanMetricEntry(string AppName, string ClientId, int DaysUntilExpiry, bool PendingDeletion);

    /// <summary>
    /// Internal record for tracking failed rotations.
    /// </summary>
    private sealed record RotationFailedEntry(string AppName, string ClientId, string Reason);

    /// <summary>
    /// Internal record for tracking manual intervention requirements.
    /// </summary>
    private sealed record ManualInterventionEntry(string AppName, string ClientId, string SecretId);

    /// <summary>
    /// Internal record for tracking duplicate mappings.
    /// </summary>
    private sealed record DuplicateMappingEntry(string ClientId, string SecretId, int VaultCount);
}
