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
    /// Below this, assume the scan is broken rather than that the raises went away. The current
    /// count is far higher; the floor only has to be high enough that a regex which stopped
    /// matching, or a resource lookup that returned nothing, cannot pass.
    /// </summary>
    private const int MinimumExpectedRaises = 40;

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
}
