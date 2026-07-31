using Alberto.Postgres;
using FluentAssertions;
using Xunit;

namespace Alberto.Tests.Postgres;

/// <summary>
/// Covers how a migration script declares that it must run outside a transaction block.
/// </summary>
/// <remarks>
/// DbUp's transaction mode is a property of the upgrader rather than of the script, so
/// <see cref="PostgresMigrator"/> reads the declaration out of the script itself and groups
/// scripts into separate upgraders accordingly. Getting the declaration wrong is silent in
/// both directions: a script that should have opted out fails with PostgreSQL error 25001,
/// and — worse — a script that opts out by accident loses its transaction and can leave the
/// schema half-migrated after a failure.
/// </remarks>
public sealed class MigrationTransactionModeTests
{
    [Fact]
    public void MarkerOnItsOwnCommentLine_DeclaresNoTransaction()
    {
        const string script = """
            -- Alberto DCB Event Store - Migration 018
            -- alberto:no-transaction
            CREATE INDEX CONCURRENTLY ix_thing ON thing (col);
            """;

        PostgresMigrator.ScriptDeclaresNoTransaction(script).Should().BeTrue();
    }

    [Fact]
    public void IndentedMarker_DeclaresNoTransaction()
    {
        const string script = """
                -- alberto:no-transaction
            CREATE INDEX CONCURRENTLY ix_thing ON thing (col);
            """;

        PostgresMigrator.ScriptDeclaresNoTransaction(script).Should().BeTrue(
            because: "leading whitespace before a comment is not a meaningful difference");
    }

    /// <summary>
    /// The marker is a word that migration headers naturally use when they explain themselves.
    /// A substring match over the file would let any such explanation silently strip the script
    /// of its transaction — which is how this was first written, and it went unnoticed because
    /// the script being tested also genuinely carried the marker.
    /// </summary>
    [Fact]
    public void MarkerMentionedInProse_DoesNotDeclareNoTransaction()
    {
        const string script = """
            -- Alberto DCB Event Store - Migration 019
            -- Unlike 018, this script does not need the alberto:no-transaction marker,
            -- because VALIDATE CONSTRAINT is perfectly happy inside a transaction.
            ALTER TABLE thing VALIDATE CONSTRAINT thing_check;
            """;

        PostgresMigrator.ScriptDeclaresNoTransaction(script).Should().BeFalse(
            because: "a script that merely describes the marker has not applied it");
    }

    [Fact]
    public void MarkerInsideSqlRatherThanAComment_DoesNotDeclareNoTransaction()
    {
        const string script = """
            INSERT INTO alberto_notes (body) VALUES ('alberto:no-transaction');
            """;

        PostgresMigrator.ScriptDeclaresNoTransaction(script).Should().BeFalse(
            because: "the declaration is a comment directive, not a string that appears in SQL");
    }

    [Fact]
    public void ScriptWithoutTheMarker_RunsInATransaction()
    {
        const string script = """
            -- Alberto DCB Event Store - Migration 017
            ALTER TABLE thing VALIDATE CONSTRAINT thing_check;
            """;

        PostgresMigrator.ScriptDeclaresNoTransaction(script).Should().BeFalse(
            because: "transactional execution is the default and must stay the default");
    }

    [Fact]
    public void MarkerWithWindowsLineEndings_DeclaresNoTransaction()
    {
        const string script =
            "-- Alberto DCB Event Store - Migration 018\r\n" +
            "-- alberto:no-transaction\r\n" +
            "CREATE INDEX CONCURRENTLY ix_thing ON thing (col);\r\n";

        PostgresMigrator.ScriptDeclaresNoTransaction(script).Should().BeTrue(
            because: "a script checked out with CRLF endings must behave identically");
    }
}
