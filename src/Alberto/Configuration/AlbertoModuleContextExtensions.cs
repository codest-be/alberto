using Alberto.Append;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Alberto.Configuration;

/// <summary>Registration helpers for <see cref="IAlbertoBackendDescriptor"/> implementations.</summary>
public static class AlbertoModuleContextExtensions
{
    /// <summary>
    /// Registers a keyed <see cref="IEventStoreBackend"/> for the module, wrapped so that the
    /// module's <see cref="IAppendInterceptor"/>s run around every append. This is the only way a
    /// backend should register itself: it also registers the append interceptor pipeline.
    /// </summary>
    /// <param name="context">The context passed to <see cref="IAlbertoBackendDescriptor.Register"/>.</param>
    /// <param name="lifetime">Singleton, or Scoped when the backend captures per-request state such as the tenant.</param>
    /// <param name="factory">Creates the raw backend. It must also implement <see cref="IEventStoreHeadBackend"/>.</param>
    /// <param name="keySuffix">
    /// Appended to <c>context.ModuleKey</c> to form the service key, for example <c>":consumer"</c>.
    /// Null registers under <c>context.ModuleKey</c> itself.
    /// </param>
    /// <param name="dbSystem">The <c>db.system.name</c> for read spans, or null when the backend is not a database.</param>
    public static void AddEventStoreBackend(
        this AlbertoModuleContext context,
        ServiceLifetime lifetime,
        Func<IServiceProvider, IEventStoreBackend> factory,
        string? keySuffix = null,
        string? dbSystem = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(factory);

        var moduleKey = context.ModuleKey;
        context.Services.TryAddKeyedSingleton<IAppendInterceptorPipeline>(moduleKey, (sp, _) =>
            new AppendInterceptorPipeline(sp.GetKeyedServices<IAppendInterceptor>(moduleKey)));

        context.Services.Add(new ServiceDescriptor(
            typeof(IEventStoreBackend),
            moduleKey + keySuffix,
            (sp, _) => new InterceptingEventStoreBackend(
                factory(sp),
                sp.GetRequiredKeyedService<IAppendInterceptorPipeline>(moduleKey),
                dbSystem),
            lifetime));
    }
}
