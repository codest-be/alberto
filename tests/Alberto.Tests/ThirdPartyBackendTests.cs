using FluentAssertions;
using Xunit;
using Alberto.Append;
using Alberto.Tests.SampleEvents;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Tests;

public class ThirdPartyBackendTests
{
    private sealed class Probe : IAppendInterceptor
    {
        public int Calls;

        public Task<IReadOnlyCollection<IEventEnvelope>> OnAppendingAsync(
            AppendContext context,
            Func<AppendContext, Task<IReadOnlyCollection<IEventEnvelope>>> next,
            CancellationToken ct = default)
        {
            Calls++;
            return next(context);
        }
    }

    [Fact]
    public async Task A_backend_declared_with_public_API_only_gets_append_interceptors()
    {
        var descriptor = new CountingBackendDescriptor();
        var probe = new Probe();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IAppendInterceptor>("third_party", probe);
        services.AddAlberto("third_party", builder => builder.UseBackend(descriptor));
        using var sp = services.BuildServiceProvider();

        var backend = sp.GetRequiredKeyedService<IEventStoreBackend>("third_party");
        await backend.AppendAsync([]);

        probe.Calls.Should().Be(1);
        descriptor.Backend.Appends.Should().Be(1);
    }
}
