using Alberto.Commands;
using System.Collections.Immutable;
using System.Reflection;
using Alberto.Dcb.Analyzers;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Alberto.Dcb.Tests.Analyzers;

/// <summary>
/// Runs <see cref="DiscardedPipelineAnalyzer"/> (ALB2001) against real compilations that reference
/// the real <c>Alberto.Dcb.Commands</c>, so the type matching is exercised against the shipped
/// pipeline types rather than against stand-ins named to look like them.
/// </summary>
public class DiscardedPipelineAnalyzerTests
{
    [Fact]
    public async Task A_pipeline_that_stops_before_Commit_is_reported()
    {
        var diagnostics = await AnalyzeBody(
            "store.Handle(command).Load(Boundary, new OrderState(), Apply).Decide(Decide);");

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be(DiscardedPipelineAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task A_pipeline_that_stops_at_Handle_is_reported()
    {
        // The earliest possible stopping point. Nothing has been declared about the command yet,
        // let alone written, which is exactly why the compiler has nothing to object to.
        var diagnostics = await AnalyzeBody("store.Handle(command);");

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be(DiscardedPipelineAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task An_awaited_Commit_is_not_reported()
    {
        var diagnostics = await AnalyzeBody(
            "await store.Handle(command).Load(Boundary, new OrderState(), Apply)"
            + ".Decide(Decide).Commit(default);");

        diagnostics.Should().BeEmpty("the pipeline reached a terminal operation and ran");
    }

    [Fact]
    public async Task A_pipeline_assigned_to_a_variable_is_not_reported()
    {
        // Composing a pipeline in steps is legitimate — the value is still live, and whether it is
        // eventually committed is beyond what a single-statement rule can know.
        var diagnostics = await AnalyzeBody(
            "var pipeline = store.Handle(command).Load(Boundary, new OrderState(), Apply)"
            + ".Decide(Decide);"
            + "await pipeline.Commit(default);");

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task An_explicit_discard_is_not_reported()
    {
        // `_ =` is how C# spells a deliberate discard everywhere else, so it is the suppression
        // mechanism here too rather than a pragma.
        var diagnostics = await AnalyzeBody("_ = store.Handle(command);");

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unrelated_discarded_call_is_not_reported()
    {
        var diagnostics = await AnalyzeBody("command.ToString();");

        diagnostics.Should().BeEmpty("the rule is about pipeline types, not about ignored results");
    }

    [Fact]
    public async Task The_message_names_the_stage_that_was_dropped()
    {
        var diagnostics = await AnalyzeBody(
            "store.Handle(command).Load(Boundary, new OrderState(), Apply).Decide(Decide);");

        var message = diagnostics.Single().GetMessage();
        message.Should().Contain("Decide", "the developer needs to know which stage they stopped at");
        message.Should().Contain("Commit", "and what the statement was missing");
    }

    // ── harness ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Compiles <paramref name="body"/> inside a handler that has a store, a command, a boundary
    /// and an evolver in scope, then returns only this analyzer's diagnostics.
    /// </summary>
    /// <remarks>
    /// The compilation is asserted to be free of C# errors first. Without that, a typo in a snippet
    /// would silently produce no operations to analyze and the test would pass by reporting nothing.
    /// </remarks>
    private static async Task<ImmutableArray<Diagnostic>> AnalyzeBody(string body)
    {
        var source = $$"""
            using System.Threading;
            using System.Threading.Tasks;
            using Alberto.Commands;
            using Alberto.Dcb;

            public sealed record PlaceOrder(string OrderId);

            public sealed record OrderState
            {
                public bool Exists { get; init; }
            }

            public static class Handler
            {
                public static DcbQuery Boundary => DcbQuery.For("order", "1");

                public static OrderState Apply(OrderState state, IEvent @event) => state;

                public static Decision Decide(PlaceOrder command, OrderState state) =>
                    Decision.Succeed();

                public static async Task Run(AlbertoStore store, PlaceOrder command)
                {
                    {{body}}
                    await Task.CompletedTask;
                }
            }
            """;

        var compilation = CSharpCompilation.Create(
            assemblyName: "AnalyzerProbe",
            syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
            references: ReferenceSet,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        errors.Should().BeEmpty(
            "the probe must compile, or there would be no operations for the analyzer to see: {0}",
            string.Join("; ", errors.Select(e => e.ToString())));

        var withAnalyzer = compilation.WithAnalyzers(
            [new DiscardedPipelineAnalyzer()]);

        var diagnostics = await withAnalyzer.GetAnalyzerDiagnosticsAsync(
            TestContext.Current.CancellationToken);

        return diagnostics
            .Where(d => d.Id == DiscardedPipelineAnalyzer.DiagnosticId)
            .ToImmutableArray();
    }

    /// <summary>
    /// Every non-dynamic assembly loaded into the test process. Enumerating the loaded set rather
    /// than naming references keeps the probe compiling as Alberto's own dependencies move, which
    /// matters because a missing reference surfaces as a compile error in the probe rather than as
    /// anything obviously to do with references.
    /// </summary>
    private static readonly IReadOnlyList<MetadataReference> ReferenceSet = BuildReferenceSet();

    private static IReadOnlyList<MetadataReference> BuildReferenceSet()
    {
        // The set is built from what is *already* loaded, and the CLR loads an assembly lazily, on
        // first use of a type from it. Touching one type per assembly the probe needs — here, in
        // the initializer rather than in a static constructor, which runs after field initializers
        // — is what guarantees they are in the enumeration below. AlbertoStore alone is not
        // enough: it pulls in Alberto.Dcb.Commands, but the probe also names DcbQuery, IEvent and
        // Decision, which live in Alberto.Dcb.
        _ = typeof(AlbertoStore).Assembly;
        _ = typeof(DcbQuery).Assembly;

        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToList();
    }
}
