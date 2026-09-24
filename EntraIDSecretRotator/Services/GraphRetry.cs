using EntraIDSecretRotator.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Graph.Models.ODataErrors;

namespace EntraIDSecretRotator.Services;

/// <summary>
/// Retries Microsoft Graph write operations that Graph rejected for transient reasons,
/// using exponential backoff.
///
/// The Graph SDK's own retry handler only covers HTTP 429/503/504. Graph also rejects
/// back-to-back writes against the same tenant with an HTTP 4xx whose message reads
/// "Error due to concurrent requests being made to the tenant. Please wait briefly and retry."
/// That happened when the rotator created two secrets on the same app 300 ms apart.
///
/// Only explicit Graph rejections are retried. A rejected request was never applied, so
/// retrying cannot create a secret twice. Network-level failures are deliberately not
/// retried here for that reason.
/// </summary>
public static class GraphRetry
{
    public const int DefaultMaxAttempts = 4;
    public static readonly TimeSpan DefaultBaseDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan MaxJitter = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Runs <paramref name="operation"/>, retrying transient Graph rejections with exponential backoff.
    /// </summary>
    /// <param name="operation">The Graph call to run.</param>
    /// <param name="operationName">Human-readable name used in log messages.</param>
    /// <param name="logger">Logger for retry warnings.</param>
    /// <param name="cancellationToken">Cancellation token passed to the operation and the delay.</param>
    /// <param name="maxAttempts">Total attempts including the first one.</param>
    /// <param name="baseDelay">Delay before the first retry; doubles on each further retry.</param>
    /// <param name="delay">Delay implementation. Defaults to Task.Delay with jitter. Injectable for tests.</param>
    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        string operationName,
        ILogger logger,
        CancellationToken cancellationToken = default,
        int maxAttempts = DefaultMaxAttempts,
        TimeSpan? baseDelay = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        Assert.NotNull(operation, nameof(operation));
        Assert.NotNullOrEmpty(operationName, nameof(operationName));
        Assert.NotNull(logger, nameof(logger));
        Assert.That(maxAttempts >= 1, "maxAttempts must be at least 1");

        var effectiveBaseDelay = baseDelay ?? DefaultBaseDelay;
        var effectiveDelay = delay ?? DefaultDelayAsync;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(cancellationToken);
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex))
            {
                var wait = ComputeDelay(ex, attempt, effectiveBaseDelay);

                logger.LogWarning(
                    "Graph rejected {Operation} with a transient error (attempt {Attempt} of {MaxAttempts}); retrying in {Delay}: {Error}",
                    operationName, attempt, maxAttempts, wait, ex.Message);

                await effectiveDelay(wait, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Overload for Graph calls that return no content.
    /// </summary>
    public static async Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        string operationName,
        ILogger logger,
        CancellationToken cancellationToken = default,
        int maxAttempts = DefaultMaxAttempts,
        TimeSpan? baseDelay = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        Assert.NotNull(operation, nameof(operation));

        await ExecuteAsync(
            async ct =>
            {
                await operation(ct);
                return true;
            },
            operationName,
            logger,
            cancellationToken,
            maxAttempts,
            baseDelay,
            delay);
    }

    /// <summary>
    /// True when Graph explicitly rejected the request for a reason that a later retry can succeed on:
    /// throttling status codes, or the tenant-level "concurrent requests" rejection.
    /// </summary>
    public static bool IsTransient(Exception exception)
    {
        if (exception is not ODataError error)
        {
            return false;
        }

        if (error.ResponseStatusCode is 429 or 503 or 504)
        {
            return true;
        }

        var text = $"{error.Error?.Code} {error.Error?.Message}";
        return text.Contains("concurrent request", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Request_ThrottledTemporarily", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Retry-After header if Graph sent one, otherwise baseDelay doubled per attempt, capped at MaxDelay.
    /// </summary>
    internal static TimeSpan ComputeDelay(Exception exception, int attempt, TimeSpan baseDelay)
    {
        Assert.That(attempt >= 1, "attempt must be at least 1");

        var retryAfter = RetryAfter(exception);
        if (retryAfter is { } serverDelay)
        {
            return Min(serverDelay, MaxDelay);
        }

        var factor = 1 << Math.Min(attempt - 1, 10);
        return Min(baseDelay * factor, MaxDelay);
    }

    private static TimeSpan? RetryAfter(Exception exception)
    {
        if (exception is not ODataError { ResponseHeaders: { } headers })
        {
            return null;
        }

        foreach (var (name, values) in headers)
        {
            if (!string.Equals(name, "Retry-After", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var first = values?.FirstOrDefault();
            if (int.TryParse(first, out var seconds) && seconds > 0)
            {
                return TimeSpan.FromSeconds(seconds);
            }
        }

        return null;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static Task DefaultDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)MaxJitter.TotalMilliseconds));
        return Task.Delay(delay + jitter, cancellationToken);
    }
}
