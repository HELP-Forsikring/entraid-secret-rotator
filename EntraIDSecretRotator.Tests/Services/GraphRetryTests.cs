using AwesomeAssertions;
using EntraIDSecretRotator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph.Models.ODataErrors;
using Xunit;

namespace EntraIDSecretRotator.Tests.Services;

public class GraphRetryTests
{
    private const string ConcurrencyMessage =
        "Error due to concurrent requests being made to the tenant. Please wait briefly and retry.";

    private static ODataError GraphError(int statusCode, string message, string? code = null) =>
        new()
        {
            ResponseStatusCode = statusCode,
            Error = new MainError { Code = code, Message = message }
        };

    private static ODataError ConcurrencyError() => GraphError(400, ConcurrencyMessage);

    private static ODataError ForbiddenError() =>
        GraphError(403, "Insufficient privileges to complete the operation.", "Authorization_RequestDenied");

    /// <summary>
    /// Records requested delays instead of sleeping, so tests run instantly.
    /// </summary>
    private sealed class RecordingDelay
    {
        public List<TimeSpan> Delays { get; } = new();

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Fails the first N calls with the given exceptions, then returns a value.
    /// </summary>
    private sealed class FlakyOperation
    {
        private readonly Queue<Exception> _failures;

        public FlakyOperation(params Exception[] failures) => _failures = new Queue<Exception>(failures);

        public int Calls { get; private set; }

        public Task<string> RunAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (_failures.Count > 0)
            {
                throw _failures.Dequeue();
            }

            return Task.FromResult("ok");
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenFirstAttemptSucceeds_CallsOnceAndNeverDelays()
    {
        // Arrange
        var operation = new FlakyOperation();
        var delay = new RecordingDelay();

        // Act
        var result = await GraphRetry.ExecuteAsync(
            operation.RunAsync, "test", NullLogger.Instance, delay: delay.DelayAsync);

        // Assert
        result.Should().Be("ok");
        operation.Calls.Should().Be(1);
        delay.Delays.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenGraphRejectsConcurrentRequest_RetriesAndSucceeds()
    {
        // Arrange
        var operation = new FlakyOperation(ConcurrencyError());
        var delay = new RecordingDelay();

        // Act
        var result = await GraphRetry.ExecuteAsync(
            operation.RunAsync, "addPassword", NullLogger.Instance, delay: delay.DelayAsync);

        // Assert
        result.Should().Be("ok");
        operation.Calls.Should().Be(2);
        delay.Delays.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ExecuteAsync_BacksOffExponentially()
    {
        // Arrange
        var operation = new FlakyOperation(ConcurrencyError(), ConcurrencyError(), ConcurrencyError());
        var delay = new RecordingDelay();

        // Act
        var result = await GraphRetry.ExecuteAsync(
            operation.RunAsync, "addPassword", NullLogger.Instance, maxAttempts: 4, delay: delay.DelayAsync);

        // Assert
        result.Should().Be("ok");
        operation.Calls.Should().Be(4);
        delay.Delays.Should().Equal(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task ExecuteAsync_WhenGraphDeniesAuthorization_ThrowsImmediatelyWithoutRetry()
    {
        // Arrange
        var forbidden = ForbiddenError();
        var operation = new FlakyOperation(forbidden);
        var delay = new RecordingDelay();

        // Act
        var act = () => GraphRetry.ExecuteAsync(
            operation.RunAsync, "addPassword", NullLogger.Instance, delay: delay.DelayAsync);

        // Assert
        (await act.Should().ThrowAsync<ODataError>()).Which.Should().BeSameAs(forbidden);
        operation.Calls.Should().Be(1);
        delay.Delays.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenAttemptsAreExhausted_ThrowsLastError()
    {
        // Arrange
        var last = ConcurrencyError();
        var operation = new FlakyOperation(ConcurrencyError(), ConcurrencyError(), last);
        var delay = new RecordingDelay();

        // Act
        var act = () => GraphRetry.ExecuteAsync(
            operation.RunAsync, "addPassword", NullLogger.Instance, maxAttempts: 3, delay: delay.DelayAsync);

        // Assert
        (await act.Should().ThrowAsync<ODataError>()).Which.Should().BeSameAs(last);
        operation.Calls.Should().Be(3);
        delay.Delays.Should().HaveCount(2);
    }

    [Fact]
    public async Task ExecuteAsync_HonoursRetryAfterHeader()
    {
        // Arrange
        var throttled = GraphError(429, "Too many requests");
        throttled.ResponseHeaders = new Dictionary<string, IEnumerable<string>>
        {
            ["Retry-After"] = new[] { "7" }
        };
        var operation = new FlakyOperation(throttled);
        var delay = new RecordingDelay();

        // Act
        await GraphRetry.ExecuteAsync(
            operation.RunAsync, "addPassword", NullLogger.Instance, delay: delay.DelayAsync);

        // Assert
        delay.Delays.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task ExecuteAsync_VoidOverload_RetriesTransientErrors()
    {
        // Arrange
        var calls = 0;
        var delay = new RecordingDelay();

        Task Operation(CancellationToken cancellationToken)
        {
            calls++;
            return calls == 1 ? throw ConcurrencyError() : Task.CompletedTask;
        }

        // Act
        await GraphRetry.ExecuteAsync(Operation, "removePassword", NullLogger.Instance, delay: delay.DelayAsync);

        // Assert
        calls.Should().Be(2);
        delay.Delays.Should().HaveCount(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCancelled_DoesNotRetry()
    {
        // Arrange
        var operation = new FlakyOperation(new OperationCanceledException());
        var delay = new RecordingDelay();

        // Act
        var act = () => GraphRetry.ExecuteAsync(
            operation.RunAsync, "addPassword", NullLogger.Instance, delay: delay.DelayAsync);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        operation.Calls.Should().Be(1);
        delay.Delays.Should().BeEmpty();
    }

    [Theory]
    [InlineData(429, "Too many requests", true)]
    [InlineData(503, "Service unavailable", true)]
    [InlineData(504, "Gateway timeout", true)]
    [InlineData(400, ConcurrencyMessage, true)]
    [InlineData(409, ConcurrencyMessage, true)]
    [InlineData(403, "Insufficient privileges to complete the operation.", false)]
    [InlineData(404, "Resource not found", false)]
    [InlineData(400, "Invalid value specified for property 'endDateTime'", false)]
    public void IsTransient_ClassifiesGraphErrors(int statusCode, string message, bool expected)
    {
        GraphRetry.IsTransient(GraphError(statusCode, message)).Should().Be(expected);
    }

    [Fact]
    public void IsTransient_NonGraphExceptions_AreNotRetried()
    {
        GraphRetry.IsTransient(new InvalidOperationException("boom")).Should().BeFalse();
        GraphRetry.IsTransient(new HttpRequestException("socket closed")).Should().BeFalse();
        GraphRetry.IsTransient(new OperationCanceledException()).Should().BeFalse();
    }

    [Fact]
    public void ComputeDelay_IsCappedAtMaxDelay()
    {
        var delay = GraphRetry.ComputeDelay(ConcurrencyError(), attempt: 12, baseDelay: TimeSpan.FromSeconds(1));

        delay.Should().Be(GraphRetry.MaxDelay);
    }
}
