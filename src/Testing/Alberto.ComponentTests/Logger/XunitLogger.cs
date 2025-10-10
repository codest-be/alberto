using Microsoft.Extensions.Logging;
using Xunit;

namespace Alberto.ComponentTests.Logger;

internal sealed class XunitLogger(ITestOutputHelper testOutputHelper, string categoryName) : ILogger
{
    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null!;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = string.IsNullOrEmpty(categoryName)
            ? $"{formatter(state, exception)}"
            : $"{categoryName} {logLevel.ToString()} : {formatter(state, exception)}";

        try
        {
            testOutputHelper.WriteLine(message);
        }
        catch (InvalidOperationException)
        {
            Console.Error.WriteLine(message);
        }
    }
}