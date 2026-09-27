using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Alberto.SourceGeneration;

/// <summary>
/// Implements <c>IGeneratedEvolver&lt;TState&gt;</c> on every <c>partial</c> evolver: one entry per
/// <c>IEvolve&lt;TState, TEvent&gt;</c> it implements, the same set <c>EvolverDispatcher</c> finds by
/// reflection, so the dispatcher never needs <c>GetInterfaces</c> or <c>Expression.Compile</c>.
/// </summary>
/// <remarks>
/// <c>partial</c> is the opt-in. An evolver that is not partial keeps the reflection path, and is
/// only reported (ALB3004) in a compilation that has opted into AOT with <c>[AlbertoEventRegistry]</c>.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class EvolverTableGenerator : IIncrementalGenerator
{
    private const string RegistryAttribute = "Alberto.AlbertoEventRegistryAttribute";
    private const string EvolverBase = "Alberto.Evolver<TState>";
    private const string EvolveInterface = "Alberto.IEvolve<TState, TEvent>";

    /// <summary>An evolver in an AOT compilation that the generator cannot extend.</summary>
    public static readonly DiagnosticDescriptor EvolverNotPartial = new(
        "ALB3004",
        title: "Evolver is not partial",
        messageFormat: "Evolver '{0}' is not partial, so it is dispatched by reflection and Expression.Compile, which Native AOT can only interpret; declare it (and any type containing it) partial",
        category: "Alberto.Performance",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Alberto generates the dispatch table of a partial evolver. Reported only in compilations that declare [AlbertoEventRegistry].");

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Collected and de-duplicated: a partial evolver whose parts each carry a base list is seen once per part.
        var evolvers = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                static (ctx, ct) => ReadEvolver(ctx, ct))
            .Where(static e => e is not null)
            .Collect()
            .Select(static (all, _) => new EquatableArray<EvolverModel>([.. all.GroupBy(e => e!.TypeName).Select(g => g.First()!)]));

        var hasRegistry = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
                static (ctx, ct) => ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is INamedTypeSymbol type
                                    && type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == RegistryAttribute))
            .Where(static r => r)
            .Collect()
            .Select(static (r, _) => !r.IsEmpty);

        context.RegisterSourceOutput(evolvers.Combine(hasRegistry), static (spc, input) =>
        {
            foreach (var e in input.Left)
            {
                if (e.Partial) spc.AddSource(e.HintName, Render(e));
                else if (input.Right)
                    spc.ReportDiagnostic(Diagnostic.Create(EvolverNotPartial, e.Location.ToLocation(), e.TypeName));
            }
        });
    }

    private static EvolverModel? ReadEvolver(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var syntax = (ClassDeclarationSyntax)ctx.Node;
        if (ctx.SemanticModel.GetDeclaredSymbol(syntax, ct) is not INamedTypeSymbol type
            || type.IsAbstract || type.IsGenericType)
            return null;

        INamedTypeSymbol? state = null;
        for (var current = type.BaseType; current is not null && state is null; current = current.BaseType)
            if (current.OriginalDefinition.ToDisplayString() == EvolverBase)
                state = current.TypeArguments[0] as INamedTypeSymbol;
        if (state is null) return null;

        var partial = syntax.Modifiers.Any(SyntaxKind.PartialKeyword);
        var containers = new List<string>();
        for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
        {
            if (outer.IsGenericType) return null;
            partial &= outer.DeclaringSyntaxReferences.All(r =>
                r.GetSyntax(ct) is TypeDeclarationSyntax t && t.Modifiers.Any(SyntaxKind.PartialKeyword));
            containers.Insert(0, $"partial {EventRegistryGenerator.Keyword(outer)} {outer.Name}");
        }

        // Every IEvolve<TState, E> the type implements, including through a base class or
        // explicitly: the set EvolverDispatcher reads through GetInterfaces().
        var events = type.AllInterfaces
            .Where(i => i.OriginalDefinition.ToDisplayString() == EvolveInterface
                        && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], state))
            .Select(i => i.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal);

        var typeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return new EvolverModel(
            type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(),
            new EquatableArray<string>([.. containers]),
            type.Name,
            typeName.Replace("global::", "") + ".AlbertoEvolver.g.cs",
            typeName,
            state.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            new EquatableArray<string>([.. events]),
            partial,
            LocationInfo.From(syntax.Identifier.GetLocation()));
    }

    internal static string Render(EvolverModel evolver)
    {
        var state = evolver.StateName;
        var hook = $"global::Alberto.IGeneratedEvolver<{state}>";
        var apply = $"global::System.Func<{state}, object, {state}>";

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        if (evolver.Namespace is not null) sb.AppendLine($"namespace {evolver.Namespace};").AppendLine();

        foreach (var container in evolver.Containers) sb.AppendLine(container).AppendLine("{");
        sb.AppendLine($"partial class {evolver.Name} : {hook}");
        sb.AppendLine("{");
        sb.AppendLine($"    global::System.Type {hook}.GeneratedFor => typeof({evolver.TypeName});");
        sb.AppendLine();
        sb.AppendLine($"    global::System.Collections.Generic.IReadOnlyList<(global::System.Type EventType, {apply} Apply)> {hook}.EvolveTable() =>");
        sb.AppendLine($"        new (global::System.Type, {apply})[]");
        sb.AppendLine("        {");
        foreach (var e in evolver.Events)
            sb.AppendLine($"            (typeof({e}), (state, e) => ((global::Alberto.IEvolve<{state}, {e}>)this).Apply(state, ({e})e)),");
        sb.AppendLine("        };");
        sb.AppendLine("}");
        foreach (var _ in evolver.Containers) sb.AppendLine("}");
        return sb.ToString();
    }
}

internal sealed record EvolverModel(
    string? Namespace,
    EquatableArray<string> Containers,
    string Name,
    string HintName,
    string TypeName,
    string StateName,
    EquatableArray<string> Events,
    bool Partial,
    LocationInfo Location);
