using System.Reflection;
using System.Text.RegularExpressions;
using Alberto.Postgres;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace Alberto.Tests.Postgres;

/// <summary>
/// Pins the one coupling between the append functions' <c>RAISE EXCEPTION</c> wording and the
/// C# that reads it.
/// </summary>
/// <remarks>
/// <para>
/// A DCB conflict is raised, not returned: <c>RAISE EXCEPTION</c> aborts the statement, so the
/// position the boundary check found has nowhere to go but the message. That makes
/// <c>PostgresBackendHelpers.ConflictMessage</c> a parser of a string another file writes, and
/// the failure mode of such a pair is silence — reword the SQL and every conflict quietly
/// reports position <c>-1</c> again, with no test failing and no exception thrown, because the
/// parser is written to degrade rather than blow up on a message it does not recognise.
/// </para>
/// <para>
/// So the wording is asserted here, on the scripts as they ship, rather than left to the one
/// integration test that happens to provoke a conflict. There are 40-odd raises across two
/// tenancy modes and six live append functions; a test that covered one of them would not be
/// covering this.
/// </para>
/// </remarks>
public sealed class DcbConflictDetailTests
{
    /// <summary>
    /// A <c>RAISE EXCEPTION</c> whose literal mentions a DCB conflict, plus whatever follows it
    /// on the line — which is where the format argument for <c>%</c> lives.
    /// </summary>
    private static readonly Regex ConflictRaise = new(
        @"RAISE\s+EXCEPTION\s+'(?<message>DCB conflict:[^']*)'(?<tail>[^\r\n]*)",
        RegexOptions.Multiline);

    /// <summary>
    /// A <c>RAISE EXCEPTION</c> whose literal mentions a DCB conflict, together with its
    /// <c>USING</c> clause (which may be on the following line).  Used to assert that every
    /// raise site in migration 004 and later sets the <c>HINT</c> field.
    /// </summary>
    private static readonly Regex ConflictRaiseWithUsing = new(
        @"RAISE\s+EXCEPTION\s+'(?<message>DCB conflict:[^']*)'[^;]*USING\s+(?<using>[^;]+);",
        RegexOptions.Singleline);

    /// <summary>
    /// Below this, assume the scan is broken rather than that the raises went away. The current
    /// count is ~20 (10 per tenancy mode in 002_QueryFunctions.sql); the floor only has to be
    /// high enough that a regex which stopped matching, a missing file, or a resource lookup
    /// that returned nothing cannot pass.
    /// </summary>
    private const int MinimumExpectedRaises = 15;

    [Fact]
    public void Every_conflict_raise_in_the_migrations_reports_a_parseable_position()
    {
        var raises = ConflictRaises().ToList();

        raises.Should().HaveCountGreaterThanOrEqualTo(MinimumExpectedRaises,
            because: "the scan must be finding the append functions' raises; a lower count means " +
                     "the regex or the resource lookup is broken, not that the raises are gone");

        using var scope = new AssertionScope();

        foreach (var (script, message, tail) in raises)
        {
            // `%` is substituted by the format argument, so a raise that forgot it would produce
            // a literal '%' where the position should be and parse as no position at all.
            tail.Should().Contain("v_conflict_position",
                because: $"{script} raises '{message}' — the % must be filled with the position " +
                         "the boundary check found, or the message names no position to parse");

            var rendered = message.Replace("%", "4711", StringComparison.Ordinal);

            PostgresBackendHelpers.ConflictMessage.TryParsePosition(rendered, out var position)
                .Should().BeTrue(
                    because: $"{script} raises '{message}', which must stay in the " +
                             "'... found at position %' shape ConflictMessage reads");

            position.Should().Be(4711,
                because: $"{script}'s message must yield the position it was raised with, not " +
                         "some other number embedded in the text");
        }
    }

    /// <summary>
    /// The detail is what tells an operator which arm of the boundary matched, and it is the
    /// half of the message that is <em>not</em> reconstructible from the query.
    /// </summary>
    [Fact]
    public void Every_conflict_raise_carries_a_detail_beyond_the_prefix()
    {
        using var scope = new AssertionScope();

        foreach (var (script, message, _) in ConflictRaises())
        {
            var detail = PostgresBackendHelpers.ConflictMessage.Detail(message);

            detail.Should().NotBe(message,
                because: $"{script} raises '{message}', whose 'DCB conflict: ' prefix must be " +
                         "stripped or the composed message says it twice");

            detail.Should().NotBeNullOrWhiteSpace(
                because: $"{script} raises '{message}' — the wording after the prefix is the " +
                         "only place the matching arm of the boundary is named");
        }
    }

    [Theory]
    // The shapes the six live append functions actually raise.
    [InlineData("DCB conflict: event type found at position 0", 0L)]
    [InlineData("DCB conflict: event tag found at position 42", 42L)]
    [InlineData("DCB conflict: event tag matching prefix found at position 7", 7L)]
    [InlineData("DCB conflict: all event tags found at position 9007199254740993", 9007199254740993L)]
    [InlineData("DCB conflict: event matching types AND tags found at position 3", 3L)]
    [InlineData("DCB conflict: event matching types AND tag patterns found at position 3", 3L)]
    [InlineData("DCB conflict: event matching types AND all tags found at position 3", 3L)]
    public void Parses_the_position_the_append_functions_report(string message, long expected)
    {
        PostgresBackendHelpers.ConflictMessage.TryParsePosition(message, out var position)
            .Should().BeTrue();

        position.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    // A reworded message: this is the regression the source scan above is guarding against, and
    // the point of asserting it here is that the parser degrades instead of throwing.
    [InlineData("DCB conflict: event type conflicts at 42")]
    // The marker present but nothing numeric behind it.
    [InlineData("DCB conflict: event type found at position unknown")]
    [InlineData("DCB conflict: event type found at position -1")]
    public void Reports_no_position_rather_than_throwing_on_an_unrecognised_message(string? message)
    {
        PostgresBackendHelpers.ConflictMessage.TryParsePosition(message, out var position)
            .Should().BeFalse();

        position.Should().Be(-1,
            because: "a caller that ignores the return value must still see the same 'unknown' " +
                     "sentinel the lossy DcbConflictException constructor uses");
    }

    [Fact]
    public void Falls_back_to_a_usable_detail_when_the_server_gave_no_message()
    {
        PostgresBackendHelpers.ConflictMessage.Detail(null)
            .Should().NotBeNullOrWhiteSpace();
    }

    // ── structured HINT field (migration 004+) ───────────────────────────────

    [Theory]
    // The values the append functions write via v_conflict_position::TEXT.
    [InlineData("0", 0L)]
    [InlineData("42", 42L)]
    [InlineData("9007199254740993", 9007199254740993L)]
    public void TryParseStructuredHint_reads_bare_integer_from_hint_field(string hint, long expected)
    {
        PostgresBackendHelpers.ConflictMessage.TryParseStructuredHint(hint, out var position)
            .Should().BeTrue();

        position.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    // A pre-migration-004 exception has no HINT field.
    [InlineData("not a number")]
    // Negative or non-integer values never come from the append functions.
    [InlineData("42.5")]
    [InlineData("-1")]
    public void TryParseStructuredHint_degrades_on_absent_or_invalid_hint(string? hint)
    {
        PostgresBackendHelpers.ConflictMessage.TryParseStructuredHint(hint, out var position)
            .Should().BeFalse();

        position.Should().Be(-1,
            because: "the same sentinel the lossy DcbConflictException constructor uses must be " +
                     "returned so a caller that ignores the bool still sees 'unknown'");
    }

    /// <summary>
    /// Demonstrates that the structured HINT path and the prose fallback path are
    /// independent: a bare integer (what migration 004 sets via HINT) is parseable only by
    /// <see cref="PostgresBackendHelpers.ConflictMessage.TryParseStructuredHint"/>, not
    /// by the prose parser.  On any database that has run migration 004 the structured
    /// path is therefore the only viable reader; the fallback cannot silently substitute.
    /// </summary>
    [Fact]
    public void Structured_hint_and_prose_parser_are_independent_read_paths()
    {
        // v_conflict_position::TEXT produces a bare decimal integer in PostgresException.Hint.
        const string hintFromPostgres = "4711";

        // The structured reader understands a bare integer directly.
        PostgresBackendHelpers.ConflictMessage.TryParseStructuredHint(hintFromPostgres, out var fromHint)
            .Should().BeTrue("the structured path reads a bare integer from PostgresException.Hint");
        fromHint.Should().Be(4711);

        // The prose parser requires the 'found at position ' marker in MessageText; it cannot
        // read a bare integer.  If C# ever called only TryParsePosition on a post-004 database,
        // the conflict position would silently degrade to -1.
        PostgresBackendHelpers.ConflictMessage.TryParsePosition(hintFromPostgres, out _)
            .Should().BeFalse(
                "the prose parser cannot read a bare integer — the two read paths are distinct, " +
                "so passing integration tests with correct conflict positions proves the structured " +
                "path is in use, not the prose fallback");
    }

    /// <summary>
    /// Every <c>RAISE EXCEPTION</c> site in migration 004 and later must carry
    /// <c>HINT = v_conflict_position::TEXT</c> in its <c>USING</c> clause so that
    /// <see cref="PostgresBackendHelpers.ConflictMessage.TryParseStructuredHint"/> can read
    /// the conflict position from <c>PostgresException.Hint</c> without parsing prose.
    /// </summary>
    /// <remarks>
    /// A floor of 20 mirrors the 10 raise sites per tenancy mode that migration 004 introduces.
    /// If this count drops, the scan is broken, not the raises.
    /// </remarks>
    [Fact]
    public void Every_conflict_raise_in_004_and_later_carries_structured_hint_position()
    {
        const int MinimumExpectedRaisesFrom004 = 20;

        var assembly = typeof(PostgresEventStoreBackend).Assembly;
        var raises = new List<(string Script, string Using)>();

        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.EndsWith(".sql", StringComparison.Ordinal) && Is004OrLater(n)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();

            foreach (Match match in ConflictRaiseWithUsing.Matches(sql))
                raises.Add((name, match.Groups["using"].Value));
        }

        raises.Should().HaveCountGreaterThanOrEqualTo(MinimumExpectedRaisesFrom004,
            because: "the scan must be finding all raise sites in migration 004+; a lower count " +
                     "means the regex or the resource lookup is broken, not that the raises are gone");

        using var scope = new AssertionScope();

        foreach (var (script, usingClause) in raises)
        {
            // The USING clause must contain HINT so Npgsql surfaces it on
            // PostgresException.Hint, making the position readable without prose parsing.
            // HINT is used rather than DETAIL because Npgsql redacts Detail by default.
            usingClause.Should().ContainEquivalentOf("HINT",
                because: $"{script} raises a DCB conflict — migration 004+ must set HINT in " +
                         "the USING clause so the position is readable from a structured field " +
                         "(DETAIL is not used: Npgsql redacts it by default)");

            // The HINT value must be the position variable itself, not a literal or other
            // expression, so a future reword of the prose message cannot break the C# reader.
            usingClause.Should().Contain("v_conflict_position",
                because: $"{script} must set HINT = v_conflict_position::TEXT so the value " +
                         "matches what ConflictMessage.TryParseStructuredHint expects");
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the migration scripts out of the assembly rather than off disk, so the scan covers
    /// exactly the scripts that ship — a raise in a .sql file that was never added as an
    /// <c>EmbeddedResource</c> never runs and is not this test's business.
    /// </summary>
    private static IEnumerable<(string Script, string Message, string Tail)> ConflictRaises()
    {
        var assembly = typeof(PostgresEventStoreBackend).Assembly;

        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();

            foreach (Match match in ConflictRaise.Matches(sql))
            {
                yield return (
                    name,
                    match.Groups["message"].Value,
                    match.Groups["tail"].Value);
            }
        }
    }

    /// <summary>
    /// Returns <c>true</c> when the embedded resource name corresponds to migration 004 or later.
    /// The numeric prefix before the first underscore in the filename determines the ordering.
    /// </summary>
    private static bool Is004OrLater(string resourceName)
    {
        // Resource names look like "Alberto.Postgres.Migrations.004_StructuredConflictPosition.sql"
        // or "Alberto.Postgres.Migrations.SingleTenant.004_StructuredConflictPosition.sql".
        // Strip the trailing ".sql" and take the last dot-delimited segment as the filename.
        const string extension = ".sql";
        var withoutExtension = resourceName[..^extension.Length];
        var lastDot = withoutExtension.LastIndexOf('.');
        var filename = lastDot >= 0 ? withoutExtension[(lastDot + 1)..] : withoutExtension;
        var underscore = filename.IndexOf('_');
        return underscore > 0 &&
               int.TryParse(filename[..underscore], out var num) &&
               num >= 4;
    }
}
