using Alberto.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Alberto.Subscriptions;

/// <summary>What the retry middleware does for one processor once the fast retries are used up.</summary>
internal sealed record ProcessorPolicy(
    bool HaltsOnFailure,
    RetryOptions Retry,
    HaltOptions Halt,
    RedeliveryOptions Redelivery);

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

    private readonly bool _retryLoop;

    /// <param name="module">Module-wide options.</param>
    /// <param name="declarations">The module's declared processors.</param>
    /// <param name="retryLoop">
    /// True for the dead-letter retry loop: a row there is already a dead letter (a reactor's,
    /// or a projection's from before halting), so a failure is rescheduled, never halted on.
    /// </param>
    internal ProcessorPolicies(ControlLoopOptions module, IEnumerable<ProcessorDeclaration> declarations, bool retryLoop = false)
    {
        _retryLoop = retryLoop;
        _module = module;
        _declarations = new Dictionary<string, ProcessorDeclaration>(StringComparer.Ordinal);
        foreach (var declaration in declarations)
            _declarations.TryAdd(declaration.ProcessorId, declaration);
    }

    /// <summary>The policies of a module, read from its validated definition.</summary>
    internal static ProcessorPolicies For(IServiceProvider services, string moduleKey, bool retryLoop = false)
    {
        var definition = services
            .GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>()
            .Get(moduleKey);

        return new ProcessorPolicies(definition.ControlLoop, definition.Processors, retryLoop);
    }

    internal ProcessorPolicy For(string processorId) =>
        _resolved.GetOrAdd(processorId, Resolve);

    /// <summary>
    /// How a processor's loop dispatches: its declared options with configuration applied, or
    /// the default for custom wiring that declared nothing.
    /// </summary>
    internal ProcessorExecutionOptions ExecutionFor(string processorId) =>
        _declarations.TryGetValue(DeclaredId(processorId), out var declaration)
            ? declaration.Execution
            : ProcessorExecutionOptions.Default;

    // A shadow rebuild replays a projection under "{id}::rebuild::{version}", and runs like it.
    private static string DeclaredId(string processorId) =>
        processorId.Contains(RebuildMarker, StringComparison.Ordinal)
            ? processorId[..processorId.IndexOf(RebuildMarker, StringComparison.Ordinal)]
            : processorId;

    private ProcessorPolicy Resolve(string processorId)
    {
        // A processor with no declaration is custom wiring: keep the dead-letter behaviour.
        if (!_declarations.TryGetValue(DeclaredId(processorId), out var declaration))
            return new ProcessorPolicy(false, _module.Retry, _module.Halt, _module.Redelivery);

        var execution = declaration.Execution;
        return new ProcessorPolicy(
            !_retryLoop && (declaration.Kind == ProcessorKind.Projection || declaration.HaltsOnFailure),
            execution.Retry?.ApplyTo(_module.Retry) ?? _module.Retry,
            execution.Halt?.ApplyTo(_module.Halt) ?? _module.Halt,
            // A halting processor never dead-letters, so it never redelivers.
            execution.Redelivery?.ApplyTo(_module.Redelivery) ?? _module.Redelivery);
    }
}

/// <summary>How a halted processor tells its loop, so health and liveness can say so.</summary>
internal interface IHaltObserver
{
    void Halted(long globalPosition);

    void Resumed();
}
