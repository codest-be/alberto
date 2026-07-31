using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Alberto.Dcb.Analyzers;

/// <summary>
/// Reports a command pipeline whose value is thrown away, which appends nothing.
/// </summary>
/// <remarks>
/// <para>
/// Every stage of the pipeline is deferred — <c>Handle</c>, <c>Validate</c>, <c>Enrich</c>,
/// <c>Load</c> and <c>Decide</c> build a description of work and perform none of it. Only
/// <c>Commit</c>, <c>TryCommit</c> and <c>CommitUnconditionally</c> run it. A statement that ends
/// at any earlier stage is therefore a no-op that the C# compiler is perfectly happy with, because
/// an invocation is a legal expression statement whatever it returns.
/// </para>
/// <para>
/// The usual idiom is protected by accident — <c>await store.Handle(c).Decide(f);</c> does not
/// compile, since the pipeline structs are not awaitable — so what this catches is the version
/// with the <c>await</c> missing too, where the handler simply returns success having written
/// nothing.
/// </para>
/// <para>
/// The BCL mechanisms for this do not reach a package consumer.
/// <c>System.Diagnostics.Contracts.PureAttribute</c>, which CA1806 keys on, is
/// <c>[Conditional("CONTRACTS_FULL")]</c> and is stripped from the compiled assembly;
/// JetBrains' <c>[MustUseReturnValue]</c> is honoured by ReSharper and Rider but not by the
/// compiler, so it never appears in a CI build. A shipped analyzer is what is left.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DiscardedPipelineAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic id reported by this analyzer.</summary>
    public const string DiagnosticId = "ALB2001";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Command pipeline result is discarded",
        messageFormat:
            "'{0}' returns {1}, and discarding it appends nothing — the pipeline is deferred until " +
            "Commit, TryCommit or CommitUnconditionally",
        category: "Alberto.Dcb.Correctness",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "Building a command pipeline performs no IO. A statement that stops before a terminal " +
            "operation compiles cleanly, writes no events, and reports no error. Await a terminal " +
            "operation, or assign the pipeline to a variable if it is being composed in steps.");

    /// <summary>
    /// The pipeline types whose values are meaningless to discard, by metadata name. Terminal
    /// operations are absent by construction: they return <c>Task&lt;Result&gt;</c>, which is not
    /// one of these, and dropping a <c>Task</c> is already the compiler's CS4014 to report.
    /// </summary>
    private static readonly ImmutableArray<string> PipelineTypeNames = ImmutableArray.Create(
        "Alberto.Commands.CommandPipeline`1",
        "Alberto.Commands.BoundPipeline`2",
        "Alberto.Commands.UnboundPipeline`2",
        "Alberto.Commands.BoundDecision",
        "Alberto.Commands.BoundDecision`1",
        "Alberto.Commands.UnboundDecision",
        "Alberto.Commands.UnboundDecision`1");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // The type symbols are resolved once per compilation rather than per statement: a name
        // lookup for every expression statement in a solution is the difference between an
        // analyzer nobody notices and one people disable.
        context.RegisterCompilationStartAction(compilationStart =>
        {
            var pipelineTypes = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(
                SymbolEqualityComparer.Default);

            foreach (var name in PipelineTypeNames)
            {
                var symbol = compilationStart.Compilation.GetTypeByMetadataName(name);
                if (symbol is not null)
                    pipelineTypes.Add(symbol);
            }

            // Alberto.Dcb.Commands is not referenced here — nothing to say.
            if (pipelineTypes.Count == 0)
                return;

            var types = pipelineTypes.ToImmutable();

            compilationStart.RegisterOperationAction(
                operationContext => Analyze(operationContext, types),
                OperationKind.ExpressionStatement);
        });
    }

    private static void Analyze(OperationAnalysisContext context, ImmutableHashSet<INamedTypeSymbol> pipelineTypes)
    {
        var statement = (IExpressionStatementOperation)context.Operation;

        // Only a bare invocation is reported. This is deliberate rather than incidental: it leaves
        // `_ = store.Handle(command);` as the escape hatch for anyone who genuinely means to
        // discard a pipeline, matching how every other discard in C# is spelled.
        if (statement.Operation is not IInvocationOperation invocation)
            return;

        // RS1024 cannot see that the set was built with SymbolEqualityComparer.Default, so it
        // reads any Contains on a symbol collection as a reference comparison. It is not one.
#pragma warning disable RS1024
        if (invocation.Type is not { } type
            || !pipelineTypes.Contains(type.OriginalDefinition))
            return;
#pragma warning restore RS1024

        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            invocation.Syntax.GetLocation(),
            invocation.TargetMethod.Name,
            type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
    }
}
