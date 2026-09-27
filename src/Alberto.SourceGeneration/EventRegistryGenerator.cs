using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Alberto.SourceGeneration;

/// <summary>
/// Emits <c>Registry</c> on every class marked <c>[AlbertoEventRegistry(typeof(SomeJsonContext))]</c>:
/// the registry <c>EventTypeRegistry.FromAssembly</c> would build for this assembly, with tag
/// extractors written out as code and JSON contracts read from the named context.
/// </summary>
/// <remarks>
/// Opt-in by construction. The generator ships inside the Alberto package and so runs in every
/// consumer's compilation; without the attribute it emits nothing and reports nothing, so a
/// consumer on the reflection path sees no change.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class EventRegistryGenerator : IIncrementalGenerator
{
    private const string RegistryAttribute = "Alberto.AlbertoEventRegistryAttribute";
    private const string EventTypeAttribute = "Alberto.EventTypeAttribute";
    private const string TagAttribute = "Alberto.TagAttribute";
    private const string EventInterface = "Alberto.IEvent";
    private const string JsonSerializableAttribute = "System.Text.Json.Serialization.JsonSerializableAttribute";

    /// <summary>Two event types share an id.</summary>
    public static readonly DiagnosticDescriptor DuplicateId = new(
        "ALB3001",
        title: "Duplicate event type id",
        messageFormat: "Event type id '{0}' is declared by both '{1}' and '{2}'",
        category: "Alberto.Correctness",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "An id names exactly one event type. The registry refuses a duplicate at startup; this reports it at compile time.");

    /// <summary>An event type is not listed on the registry's JSON context.</summary>
    public static readonly DiagnosticDescriptor MissingFromJsonContext = new(
        "ALB3002",
        title: "Event type missing from the JSON context",
        messageFormat: "Event type '{0}' is not in '{1}'; add [JsonSerializable(typeof({0}))] to it",
        category: "Alberto.Correctness",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generated registry reads every event's JSON contract from the context named on [AlbertoEventRegistry]. An event the context does not list has no contract to read.");

    /// <summary>A [Tag] property's type has no meaningful string form.</summary>
    public static readonly DiagnosticDescriptor UnsupportedTagType = new(
        "ALB3003",
        title: "Tag property type has no meaningful string form",
        messageFormat: "[Tag] property '{0}.{1}' is of type '{2}', which does not override ToString(); every event would be tagged with the type name",
        category: "Alberto.Correctness",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A tag value is the property's ToString() (a Guid's \"D\" format). A type that inherits object's or ValueType's ToString() yields its type name, so every event carries the same tag.");

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var registries = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
                static (ctx, ct) => ReadRegistry(ctx, ct))
            .Where(static r => r is not null)
            .Collect();

        var events = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is TypeDeclarationSyntax { AttributeLists.Count: > 0 }
                    and not InterfaceDeclarationSyntax,
                static (ctx, ct) => ReadEvent(ctx, ct))
            .Where(static e => e is not null)
            .Collect();

        context.RegisterSourceOutput(registries.Combine(events), static (spc, input) =>
        {
            if (input.Left.IsEmpty) return;

            // A partial event declares the attribute once but can be seen through several parts.
            var all = input.Right
                .GroupBy(e => e!.TypeName)
                .Select(g => g.First()!)
                .OrderBy(e => e.TypeName, StringComparer.Ordinal)
                .ToList();

            foreach (var group in all.GroupBy(e => e.Id).Where(g => g.Count() > 1))
            {
                var first = group.First();
                foreach (var e in group.Skip(1))
                    spc.ReportDiagnostic(Diagnostic.Create(
                        DuplicateId, e.Location.ToLocation(), e.Id, first.TypeName, e.TypeName));
            }

            foreach (var e in all)
            foreach (var tag in e.Tags.Where(t => t.UnsupportedTypeName is not null))
                spc.ReportDiagnostic(Diagnostic.Create(
                    UnsupportedTagType, tag.Location.ToLocation(), e.TypeName, tag.Property, tag.UnsupportedTypeName));

            foreach (var registry in input.Left.Distinct())
            {
                var registered = new List<EventModel>();
                foreach (var e in all)
                {
                    if (registry!.ContextTypes.Contains(e.TypeName)) registered.Add(e);
                    else spc.ReportDiagnostic(Diagnostic.Create(
                        MissingFromJsonContext, e.Location.ToLocation(), e.TypeName, registry.ContextName));
                }

                spc.AddSource(registry!.HintName, Render(registry, registered));
            }
        });
    }

    private static RegistryModel? ReadRegistry(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is not INamedTypeSymbol type) return null;

        var attribute = type.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == RegistryAttribute);
        if (attribute is null
            || attribute.ConstructorArguments.Length != 1
            || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol jsonContext)
            return null;

        var contextTypes = jsonContext.GetAttributes()
            .Where(a => a.AttributeClass?.ToDisplayString() == JsonSerializableAttribute
                        && a.ConstructorArguments.Length == 1)
            .Select(a => a.ConstructorArguments[0].Value)
            .OfType<ITypeSymbol>()
            .Select(t => t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            .OrderBy(n => n, StringComparer.Ordinal);

        var containers = new List<string>();
        for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
            containers.Insert(0, $"partial {Keyword(outer)} {outer.Name}");

        return new RegistryModel(
            type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(),
            new EquatableArray<string>([.. containers]),
            $"partial {Keyword(type)} {type.Name}",
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                .Replace("global::", "").Replace('<', '[').Replace('>', ']') + ".AlbertoEventRegistry.g.cs",
            jsonContext.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            new EquatableArray<string>([.. contextTypes]));
    }

    private static EventModel? ReadEvent(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        // The same filter EventTypeRegistry.FromAssemblies applies: a concrete IEvent that carries
        // [EventType] itself (the attribute is Inherited = false, and read that way).
        if (ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is not INamedTypeSymbol type
            || type.IsAbstract
            || !type.AllInterfaces.Any(i => i.ToDisplayString() == EventInterface))
            return null;

        var attribute = type.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == EventTypeAttribute);
        if (attribute is null || attribute.ConstructorArguments.Length != 1
            || attribute.ConstructorArguments[0].Value is not string id)
            return null;

        var version = 1;
        var upcastingNotRequired = false;
        foreach (var named in attribute.NamedArguments)
        {
            if (named.Key == "Version" && named.Value.Value is int v) version = v;
            if (named.Key == "UpcastingNotRequired" && named.Value.Value is bool b) upcastingNotRequired = b;
        }

        return new EventModel(
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            id, version, upcastingNotRequired,
            new EquatableArray<TagModel>([.. ReadTags(type)]),
            LocationInfo.From(attribute.ApplicationSyntaxReference?.GetSyntax(ct).GetLocation() ?? ctx.Node.GetLocation()));
    }

    // Mirrors ReflectionTagExtractor: public instance properties carrying [Tag], the type's own
    // first and then each base's, as Type.GetProperties returns them.
    private static IEnumerable<TagModel> ReadTags(INamedTypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current is not null; current = current.BaseType)
        foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.IsStatic || property.IsIndexer || property.DeclaredAccessibility != Accessibility.Public
                || property.GetMethod is null || !seen.Add(property.Name))
                continue;

            var tag = property.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == TagAttribute);
            if (tag is null || tag.ConstructorArguments.Length != 1 || tag.ConstructorArguments[0].Value is not string concept)
                continue;

            yield return new TagModel(
                concept,
                property.Name,
                HasMeaningfulToString(property.Type) ? null : property.Type.ToDisplayString(),
                LocationInfo.From(tag.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? property.Locations.FirstOrDefault()));
        }
    }

    private static bool HasMeaningfulToString(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];

        // An interface or type parameter is only known at run time; give it the benefit of the doubt.
        if (type.TypeKind is TypeKind.Interface or TypeKind.TypeParameter or TypeKind.Error or TypeKind.Dynamic)
            return true;

        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType
                || current.TypeKind == TypeKind.Array)
                return false;

            if (current.GetMembers("ToString").OfType<IMethodSymbol>().Any(m => m.IsOverride && m.Parameters.IsEmpty))
                return true;
        }

        return false;
    }

    internal static string Keyword(INamedTypeSymbol type) => type switch
    {
        { IsRecord: true, TypeKind: TypeKind.Struct } => "record struct",
        { IsRecord: true } => "record",
        { TypeKind: TypeKind.Struct } => "struct",
        { TypeKind: TypeKind.Interface } => "interface",
        _ => "class",
    };

    internal static string Render(RegistryModel registry, IReadOnlyList<EventModel> events)
    {
        const string Pair = "global::System.Collections.Generic.KeyValuePair<string, object?>";
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        if (registry.Namespace is not null) sb.AppendLine($"namespace {registry.Namespace};").AppendLine();

        foreach (var container in registry.Containers) sb.AppendLine(container).AppendLine("{");
        sb.AppendLine(registry.Declaration);
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>Every [EventType] in this assembly, generated by Alberto from [AlbertoEventRegistry].</summary>");
        sb.AppendLine("    public static global::Alberto.IEventTypeRegistry Registry { get; } = global::Alberto.EventTypeRegistry.CreateBuilder()");
        foreach (var e in events)
        {
            sb.AppendLine("        .Add(new global::Alberto.EventTypeDescriptor(");
            sb.AppendLine($"            {Literal(e.Id)}, {e.Version}, {(e.UpcastingNotRequired ? "true" : "false")},");
            sb.AppendLine($"            {registry.ContextName}.Default.GetTypeInfo(typeof({e.TypeName}))!,");
            if (e.Tags.Count == 0)
            {
                sb.AppendLine($"            static _ => global::System.Array.Empty<{Pair}>()))");
                continue;
            }

            sb.AppendLine("            static e =>");
            sb.AppendLine("            {");
            sb.AppendLine($"                var @event = ({e.TypeName})e;");
            sb.AppendLine($"                return new {Pair}[]");
            sb.AppendLine("                {");
            foreach (var tag in e.Tags)
                sb.AppendLine($"                    new({Literal(tag.Concept)}, @event.{tag.Property}),");
            sb.AppendLine("                };");
            sb.AppendLine("            }))");
        }
        sb.AppendLine("        .Build();");
        sb.AppendLine("}");
        foreach (var _ in registry.Containers) sb.AppendLine("}");
        return sb.ToString();
    }

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);
}

internal sealed record RegistryModel(
    string? Namespace,
    EquatableArray<string> Containers,
    string Declaration,
    string HintName,
    string ContextName,
    EquatableArray<string> ContextTypes);

internal sealed record EventModel(
    string TypeName,
    string Id,
    int Version,
    bool UpcastingNotRequired,
    EquatableArray<TagModel> Tags,
    LocationInfo Location);

/// <summary>A tag property; <see cref="UnsupportedTypeName"/> is set when its type has no useful ToString().</summary>
internal sealed record TagModel(string Concept, string Property, string? UnsupportedTypeName, LocationInfo Location);

/// <summary>A <see cref="Location"/> reduced to values, so the pipeline's outputs stay comparable.</summary>
internal sealed record LocationInfo(string Path, TextSpan Span, LinePositionSpan LineSpan)
{
    public static LocationInfo From(Location? location)
    {
        var line = location?.GetLineSpan() ?? default;
        return new LocationInfo(line.Path ?? "", location?.SourceSpan ?? default, line.Span);
    }

    public Location ToLocation() => Location.Create(Path, Span, LineSpan);
}

/// <summary>An immutable array compared by its items, which is what incremental caching needs.</summary>
internal readonly struct EquatableArray<T>(T[] items) : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly T[] _items = items ?? [];

    public int Count => (_items ?? []).Length;

    public bool Contains(T item) => (_items ?? []).Contains(item);

    public bool Equals(EquatableArray<T> other) => (_items ?? []).AsSpan().SequenceEqual(other._items ?? []);

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = 17;
        foreach (var item in _items ?? []) hash = hash * 31 + item.GetHashCode();
        return hash;
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items ?? [])).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
