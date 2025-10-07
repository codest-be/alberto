using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.CQRS.Registration;

/// <summary>
/// Extension methods for registering event sourcing modules.
/// </summary>
public static class EventSourcingExtensions
{
    /// <summary>
    /// Adds an event sourcing module with automatic registration of handlers and validators.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="moduleName">The unique name for this module (used for keyed services)</param>
    /// <param name="configureModule">Configuration action for the module</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddEventSourcingModule(
        this IServiceCollection services,
        string moduleName,
        Action<ModuleBuilder> configureModule)
    {
        var builder = new ModuleBuilder(services, moduleName);
        configureModule(builder);
        return builder.Build();
    }

    /// <summary>
    /// Adds an event sourcing module by scanning the assembly containing the specified type.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="moduleName">The unique name for this module</param>
    /// <param name="markerType">A type in the assembly to scan</param>
    /// <param name="eventStoreSchema">Optional EventStore schema name</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddEventSourcingModule(
        this IServiceCollection services,
        string moduleName,
        Type markerType,
        string? eventStoreSchema = null)
    {
        return services.AddEventSourcingModule(moduleName, builder =>
        {
            builder.ScanAssembly(markerType.Assembly);
            if (eventStoreSchema != null)
                builder.WithEventStoreSchema(eventStoreSchema);
        });
    }

    /// <summary>
    /// Adds an event sourcing module by scanning the specified assembly.
    /// </summary>
    public static IServiceCollection AddEventSourcingModule(
        this IServiceCollection services,
        string moduleName,
        Assembly assembly,
        string? eventStoreSchema = null)
    {
        return services.AddEventSourcingModule(moduleName, builder =>
        {
            builder.ScanAssembly(assembly);
            if (eventStoreSchema != null)
                builder.WithEventStoreSchema(eventStoreSchema);
        });
    }
}