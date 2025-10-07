using System.Reflection;
using Alberto.CQRS.Commands;
using Alberto.CQRS.Queries;
using Alberto.CQRS.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.CQRS.Registration;

/// <summary>
/// Builder for configuring an event sourcing module with automatic registration.
/// </summary>
public sealed class CQRSBuilder
{
    private readonly List<Assembly> _assemblies = [];
    private readonly IServiceCollection _services;

    internal CQRSBuilder(IServiceCollection services)
    {
        _services = services;
    }

    /// <summary>
    /// Scans the given assembly for command handlers, query handlers, and validators.
    /// </summary>
    public CQRSBuilder ScanAssembly(Assembly assembly)
    {
        _assemblies.Add(assembly);
        return this;
    }

    /// <summary>
    /// Builds and registers all components.
    /// </summary>
    internal IServiceCollection Build()
    {
        foreach (var assembly in _assemblies)
        {
            RegisterCommandHandlers(assembly);
            RegisterQueryHandlers(assembly);
            RegisterValidators(assembly);
        }

        _services.AddScoped<CommandExecutor>();
        _services.AddScoped<QueryExecutor>();

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
                _services.AddScoped(@interface, handlerType);
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
                _services.AddScoped(@interface, handlerType);
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
                _services.AddScoped(@interface, validatorType);
            }
        }
    }
}