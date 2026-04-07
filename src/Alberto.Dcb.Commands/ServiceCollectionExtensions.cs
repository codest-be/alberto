using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb;

public static class AlbertoStoreServiceCollectionExtensions
{
    public static IServiceCollection AddAlbertoStore(
        this IServiceCollection services,
        object moduleKey,
        Assembly eventsAssembly) =>
        services.AddSingleton(sp => new AlbertoStore(
            sp.GetRequiredKeyedService<IEventStore>(moduleKey),
            EventSerializer.FromAssembly(eventsAssembly)));
}
