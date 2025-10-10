using Microsoft.Extensions.Logging;
using Xunit;

namespace Alberto.ComponentTests.Logger;

internal sealed class XunitLoggerProvider(ITestOutputHelper testOutputHelper) : ILoggerProvider
{
    public void Dispose()
    {
    }

    public ILogger CreateLogger(string categoryName)
        => new XunitLogger(testOutputHelper, categoryName);
}