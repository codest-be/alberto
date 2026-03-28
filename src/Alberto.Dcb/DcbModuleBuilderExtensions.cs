using Alberto.Dcb.Subscriptions;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb;

/// <summary>
/// Extension methods for <see cref="DcbModuleBuilder"/>.
/// </summary>
public static class DcbModuleBuilderExtensions
{
    /// <summary>
    /// Registers an async projection processor using the declaration-based API.
    /// </summary>
    /// <typeparam name="TState">The projection state type.</typeparam>
    /// <param name="builder">The module builder.</param>
    /// <param name="declaration">The projection declaration.</param>
    /// <param name="stateStoreFactory">A delegate that, given the service provider, returns a state store factory.</param>
    public static DcbModuleBuilder AddProjection<TState>(
        this DcbModuleBuilder builder,
        ProjectionDeclaration<TState> declaration,
        Func<IServiceProvider, Func<IStateStore<TState>>> stateStoreFactory)
        where TState : new()
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(stateStoreFactory);
        builder.Services.AddKeyedSingleton<IEventProcessor>(builder.ModuleKey, (sp, _) =>
        {
            var factory = stateStoreFactory(sp);
            return new DeclaredAsyncProjection<TState>(declaration, factory);
        });
        return builder;
    }

    /// <summary>
    /// Configures independent per-processor control loops.
    /// Each registered <see cref="Subscriptions.IEventProcessor"/> gets its own polling loop
    /// that advances its own checkpoint independently up to <see cref="Subscriptions.EventStoreHead.Current"/>.
    /// </summary>
    /// <param name="builder">The module builder.</param>
    /// <param name="configure">Optional action to configure the control loops.</param>
    /// <returns>The module builder for chaining.</returns>
    public static DcbModuleBuilder WithControlLoop(
        this DcbModuleBuilder builder,
        Action<ControlLoopBuilder>? configure = null)
    {
        var loopBuilder = new ControlLoopBuilder(builder);
        configure?.Invoke(loopBuilder);
        loopBuilder.Build();
        return builder;
    }
}
