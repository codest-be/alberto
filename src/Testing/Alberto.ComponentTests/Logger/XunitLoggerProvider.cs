using Microsoft.Extensions.Logging;
using Xunit;

namespace Alberto.ComponentTests.Logger;

internal sealed class XunitLoggerProvider : ILoggerProvider
{
    private readonly ITestOutputHelper _testOutputHelper;

    public XunitLoggerProvider(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    public void Dispose()
    {
    }

    public ILogger CreateLogger(string categoryName)
        => new XunitLogger(_testOutputHelper, categoryName);
}