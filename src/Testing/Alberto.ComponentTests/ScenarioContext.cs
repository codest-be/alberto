using Alberto.ComponentTests.Logger;
using Alberto.ComponentTests.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Alberto.ComponentTests;

public sealed class ScenarioContext(ITestOutputHelper testOutputHelper, IServiceFixture serviceFixture)
    : IServiceProvider
{
    private readonly Dictionary<string, object> _data = new();
    private readonly ILoggerProvider _loggerProvider = new XunitLoggerProvider(testOutputHelper);

    internal readonly List<IStep> BackgroundSteps = [];
    private IServiceProvider? _currentServiceProvider = null;

    public static bool LogStepDurationDefault { get; set; } = false;
    internal bool LogStepDuration { get; set; } = LogStepDurationDefault;

    public object? GetService(Type serviceType)
        => _currentServiceProvider?.GetService(serviceType);


    internal IServiceScope CreateAndUseNewServiceScope()
    {
        var serviceScope = serviceFixture.Services.CreateScope();
        _currentServiceProvider = serviceScope.ServiceProvider;
        return serviceScope;
    }

    public void Set<T>(T value, string key)
        where T : notnull
        => _data[key] = value;

    public T? TryGet<T>(string key)
        where T : notnull
        => _data.TryGetValue(key, out var value) ? (T)value : default;

    public T Get<T>(string key)
        where T : notnull
        => TryGet<T>(key) ??
           throw new InvalidOperationException($"No {typeof(T).Name} value found with key '{key}' on scenario context");

    public ILogger CreateLogger()
        => _loggerProvider.CreateLogger(string.Empty);

    public ILogger CreateLogger<T>()
        => _loggerProvider.CreateLogger(typeof(T).Name);

    public HttpClient HttpClient()
        => serviceFixture.Server.CreateClient();
}