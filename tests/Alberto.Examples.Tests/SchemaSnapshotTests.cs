using System.Runtime.CompilerServices;
using FluentAssertions;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Examples.Tests;

/// <summary>
/// Pins the GraphQL schema the examples serve.
/// </summary>
/// <remarks>
/// The K6 load tests in <c>tests/Alberto.Orders.LoadTests</c> send operations against a running
/// server by name — <c>mutation CreateOrder($input: CreateOrderInput!)</c> and the rest — so a
/// renamed field, a reordered argument or a dropped default breaks them at run time, in a
/// container, minutes after the change. This test turns that into a diff.
/// <para>
/// It exists for the vertical-slice refactor: every slice that moves is meant to be invisible
/// from outside, and this is what says so. Take the snapshot before the first slice moves.
/// </para>
/// <para>
/// To accept an intended schema change, run the suite with <c>REWRITE_SNAPSHOT=1</c> and commit
/// the rewritten file — the diff in review is then the schema change itself.
/// </para>
/// </remarks>
public sealed class SchemaSnapshotTests
{
    /// <summary>
    /// The one place that says which types make up the schema.
    /// </summary>
    /// <remarks>
    /// Slices move between assemblies during the refactor, so the registration call changes.
    /// Keeping it to a single line here means each move edits one place and the snapshot
    /// immediately says whether the move was faithful.
    /// </remarks>
    private static async Task<string> PrintSchemaAsync()
    {
        var schema = await new ServiceCollection()
            .AddGraphQLServer()
            .AddTypes()
            .BuildSchemaAsync();

        return schema.ToString();
    }

    [Fact]
    public async Task Schema_matches_snapshot()
    {
        var schema = await PrintSchemaAsync();
        var path = SnapshotPath();

        var ct = TestContext.Current.CancellationToken;

        if (Environment.GetEnvironmentVariable("REWRITE_SNAPSHOT") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, schema, ct);
            return;
        }

        File.Exists(path).Should().BeTrue(
            $"the snapshot at {path} should have been taken before the first slice moved — " +
            "run the suite once with REWRITE_SNAPSHOT=1 to take it");

        var expected = await File.ReadAllTextAsync(path, ct);

        Normalise(schema).Should().Be(
            Normalise(expected),
            "the vertical-slice refactor must not change what the server serves");
    }

    // Line endings differ between the machine that took the snapshot and the one checking it;
    // nothing about the schema does.
    private static string Normalise(string schema) =>
        schema.ReplaceLineEndings("\n").TrimEnd();

    // Resolves against the source tree rather than the output directory, so REWRITE_SNAPSHOT
    // writes the file a reviewer will actually see in the diff.
    private static string SnapshotPath([CallerFilePath] string sourceFile = "") =>
        Path.Combine(Path.GetDirectoryName(sourceFile)!, "Snapshots", "schema.graphql");
}
