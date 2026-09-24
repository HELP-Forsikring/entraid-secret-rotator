using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace EntraIDSecretRotator.Infrastructure;

/// <summary>
/// Negative Space assertions - crashes immediately on invariant violation.
/// These are NOT for user input validation - they verify internal program invariants.
/// Violations indicate bugs in the code, not invalid user input.
/// </summary>
public static class Assert
{
    /// <summary>
    /// Asserts that a condition is true. Crashes immediately if false.
    /// </summary>
    [DebuggerHidden]
    [StackTraceHidden]
    public static void That(
        bool condition,
        string message,
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        if (!condition)
        {
            Crash($"Assertion failed: {message}", filePath, lineNumber);
        }
    }

    /// <summary>
    /// Asserts that a reference is not null. Crashes immediately if null.
    /// </summary>
    [DebuggerHidden]
    [StackTraceHidden]
    public static T NotNull<T>(
        [NotNull] T? value,
        string parameterName,
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0) where T : class
    {
        if (value is null)
        {
            Crash($"Assertion failed: {parameterName} must not be null", filePath, lineNumber);
        }
        return value;
    }

    /// <summary>
    /// Asserts that a string is not null or empty. Crashes immediately if null or empty.
    /// </summary>
    [DebuggerHidden]
    [StackTraceHidden]
    public static string NotNullOrEmpty(
        [NotNull] string? value,
        string parameterName,
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        if (string.IsNullOrEmpty(value))
        {
            Crash($"Assertion failed: {parameterName} must not be null or empty", filePath, lineNumber);
        }
        return value;
    }

    /// <summary>
    /// Asserts that a collection is not null or empty. Crashes immediately if null or empty.
    /// </summary>
    [DebuggerHidden]
    [StackTraceHidden]
    public static IReadOnlyList<T> NotNullOrEmpty<T>(
        [NotNull] IReadOnlyList<T>? value,
        string parameterName,
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        if (value is null || value.Count == 0)
        {
            Crash($"Assertion failed: {parameterName} must not be null or empty", filePath, lineNumber);
        }
        return value;
    }

    /// <summary>
    /// Marks code paths that should never be reached. Crashes immediately if executed.
    /// </summary>
    [DebuggerHidden]
    [StackTraceHidden]
    public static void Unreachable(
        string message = "Unreachable code executed",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        Crash($"Assertion failed: {message}", filePath, lineNumber);
    }

    [DoesNotReturn]
    [DebuggerHidden]
    [StackTraceHidden]
    private static void Crash(string message, string filePath, int lineNumber)
    {
        var fileName = Path.GetFileName(filePath);
        var fullMessage = $"{message} at {fileName}:{lineNumber}";

        // Log to stderr before crashing
        Console.Error.WriteLine($"FATAL: {fullMessage}");
        Console.Error.WriteLine("This is a bug in the program, not invalid input.");

        // Crash the application immediately
        Environment.FailFast(fullMessage);
    }
}
