using System.Reflection;
using Alberto.CQRS.Commands;
using Alberto.CQRS.Queries;
using Alberto.CQRS.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.CQRS.Registration;

/// <summary>
/// Builder for configuring an event sourcing module with automatic registration.
/// </summary>
public sealed class ModuleBuilder
{
    private readonly List<Assembly> _assemblies = [];
    private readonly string _moduleName;
    private readonly IServiceCollection _services;
    private string? _eventStoreSchema;

    internal ModuleBuilder(IServiceCollection services, string moduleName)
    {
        _services = services;
        _moduleName = moduleName;
    }

    /// <summary>
    /// Scans the given assembly for command handlers, query handlers, and validators.
    /// </summary>
    public ModuleBuilder ScanAssembly(Assembly assembly)
    {
        _assemblies.Add(assembly);
        return this;
    }

    /// <summary>
    /// Specifies the EventStore schema this module should use.
    /// </summary>
    public ModuleBuilder WithEventStoreSchema(string schemaName)
    {
        _eventStoreSchema = schemaName;
        return this;
    }

    /// <summary>
    /// Builds and registers all components.
    /// </summary>
    public IServiceCollection Build()
    {
        foreach (var assembly in _assemblies)
        {
            RegisterCommandHandlers(assembly);
            RegisterQueryHandlers(assembly);
            RegisterValidators(assembly);
        }

        // Register executors
        _services.AddKeyedScoped<CommandExecutor>(_moduleName, (sp, _) =>
            new CommandExecutor(sp, _moduleName));

        _services.AddKeyedScoped<QueryExecutor>(_moduleName, (sp, _) =>
            new QueryExecutor(sp, _moduleName));

        return _services;
    }

    private void RegisterCommandHandlers(Assembly assembly)
    {
        var handlerTypes = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => t.GetInterfaces().Any(i =>
                i.IsGenericType && (
                    i.GetGenericTypeDefinition() == typeof(ICommandHandler<>) ||
                    i.GetGenericTypeDefinition() == typeof(ICommandHandler<,>))))
            .ToList();

        foreach (var handlerType in handlerTypes)
        {
            var interfaces = handlerType.GetInterfaces()
                .Where(i => i.IsGenericType && (
                    i.GetGenericTypeDefinition() == typeof(ICommandHandler<>) ||
                    i.GetGenericTypeDefinition() == typeof(ICommandHandler<,>)))
                .ToList();

            foreach (var @interface in interfaces)
            {
                _services.AddKeyedScoped(@interface, _moduleName, handlerType);
            }
        }
    }

    private void RegisterQueryHandlers(Assembly assembly)
    {
        var handlerTypes = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => t.GetInterfaces().Any(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IQueryHandler<,>)))
            .ToList();

        foreach (var handlerType in handlerTypes)
        {
            var interfaces = handlerType.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IQueryHandler<,>))
                .ToList();

            foreach (var @interface in interfaces)
            {
                _services.AddKeyedScoped(@interface, _moduleName, handlerType);
            }
        }
    }

    private void RegisterValidators(Assembly assembly)
    {
        var validatorTypes = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => t.GetInterfaces().Any(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IValidator<>)))
            .ToList();

        foreach (var validatorType in validatorTypes)
        {
            var interfaces = validatorType.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IValidator<>))
                .ToList();

            foreach (var @interface in interfaces)
            {
                _services.AddKeyedScoped(@interface, _moduleName, validatorType);
            }
        }
    }
}