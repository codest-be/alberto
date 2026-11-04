using Alberto.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Alberto.Subscriptions;

/// <summary>What the retry middleware does for one processor once the fast retries are used up.</summary>
internal sealed record ProcessorPolicy(
    bool HaltsOnFailure,
    RetryOptions Retry,
    HaltOptions Halt);

/// <summary>
/// Resolves the failure policy of a processor from the module's validated definition: the
/// processor's declared kind decides between halting and dead-lettering, and its per-processor
/// overrides are layered over the module-wide options. One shared middleware chain serves every
/// processor of a module, so the lookup is by processor id at dispatch time.
/// </summary>
internal sealed class ProcessorPolicies
{
    private const string RebuildMarker = "::rebuild::";

    private readonly ControlLoopOptions _module;
    private readonly Dictionary<string, ProcessorDeclaration> _declarations;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProcessorPolicy> _resolved =
        new(StringComparer.Ordinal);

    internal ProcessorPolicies(ControlLoopOptions module, IEnumerable<ProcessorDeclaration> declarations)
    {
        _module = module;
        _declarations = new Dictionary<string, ProcessorDeclaration>(StringComparer.Ordinal);
        foreach (var declaration in declarations)
            _declarations.TryAdd(declaration.ProcessorId, declaration);
    }

    /// <summary>The policies of a module, read from its validated definition.</summary>
    internal static ProcessorPolicies For(IServiceProvider services, string moduleKey)
    {
        var definition = services
            .GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>()
            .Get(moduleKey);

        return new ProcessorPolicies(definition.ControlLoop, definition.Processors);
    }

    internal ProcessorPolicy For(string processorId) =>
        _resolved.GetOrAdd(processorId, Resolve);

    private ProcessorPolicy Resolve(string processorId)
    {
        // A shadow rebuild replays a projection under "{id}::rebuild::{version}" and halts like it.
        var declaredId = processorId.Contains(RebuildMarker, StringComparison.Ordinal)
            ? processorId[..processorId.IndexOf(RebuildMarker, StringComparison.Ordinal)]
            : processorId;

        // A processor with no declaration is custom wiring: keep the dead-letter behaviour.
        if (!_declarations.TryGetValue(declaredId, out var declaration))
            return new ProcessorPolicy(false, _module.Retry, _module.Halt);

        var execution = declaration.Execution;
        return new ProcessorPolicy(
            declaration.Kind == ProcessorKind.Projection || declaration.HaltsOnFailure,
            execution.Retry?.ApplyTo(_module.Retry) ?? _module.Retry,
            execution.Halt?.ApplyTo(_module.Halt) ?? _module.Halt);
    }
}

/// <summary>How a halted processor tells its loop, so health and liveness can say so.</summary>
internal interface IHaltObserver
{
    void Halted(long globalPosition);

    void Resumed();
}
