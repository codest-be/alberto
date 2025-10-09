using Microsoft.Extensions.Logging;
using Xunit;

namespace Alberto.ComponentTests.Logger;

internal sealed class XunitLogger : ILogger
{
    private readonly string _categoryName;
    private readonly ITestOutputHelper _testOutputHelper;

    public XunitLogger(ITestOutputHelper testOutputHelper, string categoryName)
    {
        _testOutputHelper = testOutputHelper;
        _categoryName = categoryName;
    }

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
        var message = string.IsNullOrEmpty(_categoryName)
            ? $"{formatter(state, exception)}"
            : $"{_categoryName} {logLevel.ToString()} : {formatter(state, exception)}";

        try
        {
            _testOutputHelper.WriteLine(message);
        }
        catch (InvalidOperationException)
        {
            Console.Error.WriteLine(message);
        }
    }
}