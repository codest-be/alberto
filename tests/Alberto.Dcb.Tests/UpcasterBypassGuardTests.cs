using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Alberto.Dcb.Tests;

/// <summary>
/// Guard tests that prevent new raw-JSON event-deserialization sites from being
/// introduced without review.
/// </summary>
/// <remarks>
/// The same defect — turning an <see cref="IEventEnvelope"/> payload directly into a
/// typed event via <c>JsonSerializer.Deserialize</c> / <c>JsonDocument.Parse</c> /
/// <c>JsonNode.Parse</c> instead of routing through <see cref="EventSerializer"/> —
/// was found three separate times.  Each time a green test sat next to it.
/// This guard makes the fourth occurrence impossible to miss.
///
/// <para>
/// <strong>If <see cref="Source_NoRawDeserializationOutsideAllowList"/> fails:</strong>
/// <list type="bullet">
///   <item>You added event deserialization that bypasses the upcaster chain.
///   Route through <see cref="EventSerializer.Deserialize"/> instead — inject it
///   where you need it.  See CONTRIBUTING.md §"Event deserialization rule".</item>
///   <item>Or you added <em>genuinely non-event</em> JSON deserialization (projection
///   state, metadata dictionaries, CLI config, outbox payloads).  In that case add the
///   file to <see cref="AllowedFiles"/> below with a one-sentence justification that
///   names the concrete type being deserialized (e.g. <c>Dictionary&lt;string,string&gt;</c>,
///   <c>TState</c>, <c>AlbertoConfig</c>) — not just "not an event".</item>
/// </list>
/// </para>
/// </remarks>
public sealed class UpcasterBypassGuardTests
{
    /// <summary>
    /// Files under <c>src/</c> that are explicitly permitted to contain any of the
    /// raw-deserialization patterns, keyed by path relative to <c>src/</c> with
    /// forward-slash separators.
    /// </summary>
    /// <remarks>
    /// HOW TO ADD AN ENTRY
    /// <list type="number">
    ///   <item>Confirm the deserialized type is NOT an <see cref="IEvent"/> payload:
    ///   it must be projection state, metadata, config, or an intra-upcaster old-version
    ///   shape.</item>
    ///   <item>Write the justification as a complete sentence naming the concrete type.</item>
    ///   <item>See CONTRIBUTING.md §"Event deserialization rule" for full guidance.</item>
    /// </list>
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> AllowedFiles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Alberto.Dcb/EventSerializer.cs"] =
                "THE canonical route: applies the registered upcaster chain and returns an IEvent; " +
                "raw JsonSerializer.Deserialize is called here deliberately as the terminal step after upcasting.",

            ["Alberto.Dcb/Upcasting/UpcasterDeclaration.cs"] =
                "Internal upcaster machinery: deserializes an old-version CLR shape (e.g. OrderPlacedV1) " +
                "as the input to a transform step — not a final event object returned to application code.",

            ["Alberto.Dcb/EventEnvelopeExtensions.cs"] =
                "Contains the internal DeserializeEvent<TEvent> null-serializer fallback used by standalone / " +
                "testing paths (e.g. ProjectionDeclaration<TState>.GetDocumentId(IEventEnvelope) and " +
                "ProjectionDeclaration<TState>.Apply(TState, IEventEnvelope, ProjectionContext)). " +
                "It is guarded by the same EV-1 stale-version check as EvolverDispatcher — it throws " +
                "InvalidOperationException rather than silently returning stale state when no serializer is provided " +
                "and the envelope's stored version is older than the handler's declared version.",

            ["Alberto.Dcb/EvolverDispatcher.cs"] =
                "Backward-compatible fallback in Handler.Evolve: deserializes an event payload with raw JSON " +
                "only when no EventSerializer is provided (standalone/non-DI direct-Reconstitute usage); " +
                "guarded by a stale-version check that throws rather than silently returning wrong state " +
                "for any schema that has upcasters registered.",

            ["Alberto.Dcb.Postgres/PostgresStateStore.cs"] =
                "Deserializes TState projection state persisted in Postgres, not an event payload.",

            ["Alberto.Dcb.Postgres/PostgresDeadLetterStore.cs"] =
                "Deserializes a Dictionary<string,string> metadata column, not an event payload.",

            ["Alberto.Dcb.Postgres/PostgresBackendHelpers.cs"] =
                "Deserializes a Dictionary<string,string> event-metadata column; the event_data column " +
                "is passed through verbatim without deserialization.",

            ["Alberto.Dcb.Messaging.Postgres/PostgresOutboxStore.cs"] =
                "Deserializes a Dictionary<string,string> outbox-entry metadata column, not an event payload.",

            ["Alberto.Dcb.InMemory/EventDataJson.cs"] =
                "Parses a JsonDocument purely to re-emit the payload text in the canonical form a jsonb " +
                "column would store it in; it never materializes an event CLR type, so there is nothing " +
                "for an upcaster to have been applied to.",

            ["Alberto.Dcb.Testing.Xunit/EventStoreBackendSpecification.cs"] =
                "Parses two JsonNode trees so the round-trip assertion can compare payloads for semantic " +
                "equality instead of byte equality; no event type is deserialized.",
        };

    // Guards against JsonSerializer.Deserialize, JsonDocument.Parse, and JsonNode.Parse —
    // all three are gateways through which raw event JSON can bypass the upcaster chain.
    // The pattern anchors on the method-name boundary (dot before method name) so
    // partial matches like "MyJsonSerializer.Deserialize" do not trigger it.
    private static readonly Regex RawDeserializePattern = new(
        @"JsonSerializer\.Deserialize[<(]" +
        @"|JsonDocument\.Parse[<(]" +
        @"|JsonNode\.Parse[<(]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Fails when a .cs file under <c>src/</c> contains a raw-deserialization call and
    /// is not in <see cref="AllowedFiles"/>.
    /// </summary>
    [Fact]
    public void Source_NoRawDeserializationOutsideAllowList()
    {
        var srcRoot = FindSrcRoot();

        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            if (!RawDeserializePattern.IsMatch(source))
                continue;

            // Relative path from src/ root, forward slashes for cross-platform key matching.
            var relativePath = Path.GetRelativePath(srcRoot, file).Replace('\\', '/');

            if (!AllowedFiles.ContainsKey(relativePath))
                violations.Add(relativePath);
        }

        violations.Should().BeEmpty(
            because:
                "\n\nRaw JSON deserialization was found in src/ files not on the allow-list.\n\n" +
                "OFFENDING FILES:\n" +
                string.Join("\n", violations.Select(v => $"  {v}")) + "\n\n" +
                "WHAT TO DO:\n" +
                "  Option A — You are deserializing an event envelope payload.\n" +
                "             Route through EventSerializer.Deserialize instead.\n" +
                "             Inject EventSerializer where you need it.\n\n" +
                "  Option B — You are deserializing genuinely non-event JSON\n" +
                "             (projection state, metadata dict, CLI config, outbox payload).\n" +
                "             Add the file to UpcasterBypassGuardTests.AllowedFiles with a\n" +
                "             one-sentence justification naming the concrete type.\n" +
                "             See CONTRIBUTING.md §\"Event deserialization rule\".\n");
    }

    /// <summary>
    /// Verifies that every entry in <see cref="AllowedFiles"/> still corresponds to
    /// a file that exists and still contains the guarded pattern.
    /// </summary>
    /// <remarks>
    /// This keeps the allow-list honest: once a file is cleaned up its entry must be
    /// removed, so the list never grows into a meaningless graveyard of stale entries
    /// that make the guard look more permissive than it is.
    /// </remarks>
    [Fact]
    public void AllowList_HasNoStaleEntries()
    {
        var srcRoot = FindSrcRoot();
        var stale = new List<string>();

        foreach (var (relativePath, _) in AllowedFiles)
        {
            var absolutePath = Path.Combine(
                srcRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(absolutePath))
            {
                stale.Add($"{relativePath} — file no longer exists");
                continue;
            }

            var source = File.ReadAllText(absolutePath);
            if (!RawDeserializePattern.IsMatch(source))
                stale.Add($"{relativePath} — file no longer contains a raw-deserialization call; remove from AllowedFiles");
        }

        stale.Should().BeEmpty(
            because:
                "\n\nStale entries found in UpcasterBypassGuardTests.AllowedFiles.\n\n" +
                string.Join("\n", stale.Select(s => $"  {s}")) + "\n\n" +
                "Remove each stale entry from the AllowedFiles dictionary.\n");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Walks up from the test-assembly location until it finds a directory that
    /// contains both <c>src/</c> and <c>tests/</c> subdirectories (the repo root),
    /// then returns the path to its <c>src/</c> child.
    /// </summary>
    private static string FindSrcRoot()
    {
        var dir = new DirectoryInfo(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);

        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src")) &&
                Directory.Exists(Path.Combine(dir.FullName, "tests")))
                return Path.Combine(dir.FullName, "src");

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Cannot locate the src/ directory: no ancestor of the test-assembly " +
            "location contains both src/ and tests/ subdirectories.");
    }
}
