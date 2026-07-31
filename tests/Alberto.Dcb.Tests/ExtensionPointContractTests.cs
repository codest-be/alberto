using System.Reflection;
using Alberto.Dcb.Subscriptions;
using FluentAssertions;
using Xunit;

namespace Alberto.Dcb.Tests;

/// <summary>
/// Freezes the abstract member set of every interface Alberto expects to be implemented
/// <em>outside</em> this repository.
/// </summary>
/// <remarks>
/// <para>
/// The public-API analyzer catches a widened surface, but it cannot tell the difference
/// between the two ways of widening an interface. Adding a member with a default body is
/// invisible to existing implementors. Adding an <em>abstract</em> member breaks every one
/// of them at compile time and, for anything already deployed, at load time — and there is
/// no way to take it back short of a major version.
/// </para>
/// <para>
/// So the rule for these interfaces is: after 1.0, new members ship with a default
/// implementation. This test enforces it by pinning the abstract members that exist today.
/// A member added with a default body does not appear in the reflected abstract set and the
/// test stays green; an abstract one fails here, next to an explanation, rather than in
/// someone else's build.
/// </para>
/// <para>
/// <strong>If this test fails</strong> you have added, removed, or renamed an abstract
/// member on one of the interfaces below. Three ways forward, in order of preference:
/// </para>
/// <list type="number">
///   <item>Give the new member a default implementation, in terms of the members that
///   already exist. It then disappears from this test's view.</item>
///   <item>If no correct default exists, the capability is optional — put it on its own
///   interface and have consumers type-test for it. <see cref="IClaimableDeadLetterStore"/>
///   and <see cref="IFencedCheckpointStore"/> are the worked examples.</item>
///   <item>If it genuinely has to be a required member, update the baseline here <em>and</em>
///   add an <c>UPGRADING.md</c> section. That is a major-version change; the point of this
///   test is that you cannot make it by accident.</item>
/// </list>
/// </remarks>
public sealed class ExtensionPointContractTests
{
    /// <summary>
    /// The frozen abstract surface, keyed by interface. Signatures are rendered by
    /// <see cref="Describe"/> — return type, name, and parameter types — so a change to any
    /// of those registers as a break, not just an added or removed method.
    /// </summary>
    private static readonly IReadOnlyDictionary<Type, string[]> FrozenAbstractMembers =
        new Dictionary<Type, string[]>
        {
            [typeof(IEventStoreBackend)] =
            [
                "Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(IEnumerable<IEventToPersist>, DcbQuery, Nullable<Int64>, CancellationToken)",
                "Task<Int64> GetLastPositionAsync(CancellationToken)",
                "Task<IReadOnlyCollection<IEventEnvelope>> StreamAllAsync(Int64, Nullable<Int32>, CancellationToken)",
                "Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(DcbQuery, Int64, Nullable<Int32>, CancellationToken)",
            ],

            [typeof(IDeadLetterStore)] =
            [
                "Task ClearAsync(String, CancellationToken)",
                "Task<Int32> CountAsync(String, CancellationToken)",
                "Task<IReadOnlyList<DeadLetterEntry>> GetAsync(String, String, Int32, CancellationToken)",
                "Task MarkForRetryAsync(String, CancellationToken)",
                "Task StoreAsync(DeadLetterEntry, CancellationToken)",
            ],

            [typeof(IClaimableDeadLetterStore)] =
            [
                "Task<Boolean> AbandonRetryAsync(DeadLetterClaim, CancellationToken)",
                "Task<IReadOnlyList<DeadLetterClaim>> ClaimRetryRequestedAsync(String, Int32, TimeSpan, String, CancellationToken)",
                "Task<Boolean> CompleteRetryAsync(DeadLetterClaim, CancellationToken)",
            ],

            [typeof(IProjectionRebuildStore)] =
            [
                "Task<ProjectionRebuildState> GetAsync(String, String, CancellationToken)",
                "Task<IReadOnlyList<ProjectionRebuildState>> ListAsync(CancellationToken)",
                "Task<ProjectionRebuildState> RequestAbortAsync(String, CancellationToken)",
                "Task<ProjectionRebuildState> RequestPromotionAsync(String, Boolean, CancellationToken)",
                "Task<ProjectionRebuildState> StartAsync(String, String, Int64, CancellationToken)",
            ],
        };

    /// <summary>
    /// Every interface above must still declare exactly the abstract members it declared at
    /// the freeze — no additions, no removals, no signature changes.
    /// </summary>
    [Theory]
    [MemberData(nameof(FrozenInterfaces))]
    public void AbstractSurface_MatchesTheFrozenBaseline(Type interfaceType)
    {
        var expected = FrozenAbstractMembers[interfaceType];

        // DeclaredOnly: an interface that extends another (IClaimableDeadLetterStore) is
        // responsible only for what it adds — the base is pinned by its own entry.
        var actual = interfaceType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.IsAbstract)
            .Select(Describe)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        actual.Should().BeEquivalentTo(
            expected,
            because:
                $"\n\nThe abstract surface of {interfaceType.Name} is frozen: it is implemented " +
                "outside this repository, so adding a member without a default body breaks every " +
                "implementation at compile time and every deployed one at load time.\n\n" +
                "WHAT TO DO:\n" +
                "  Option A — Give the new member a default implementation in terms of the\n" +
                "             existing ones. It stops being abstract and this test passes.\n\n" +
                "  Option B — There is no correct default, so the capability is optional.\n" +
                "             Put it on its own interface and type-test for it at the call site.\n" +
                "             See IClaimableDeadLetterStore and IFencedCheckpointStore.\n\n" +
                "  Option C — It really must be required. Update the baseline in this file AND\n" +
                "             add an UPGRADING.md section. That is a major-version change.\n\n" +
                "See CONTRIBUTING.md §\"Extension points\".\n");
    }

    /// <summary>
    /// Guards the baseline itself: an interface dropped from
    /// <see cref="FrozenAbstractMembers"/> would silently stop being checked.
    /// </summary>
    [Fact]
    public void Baseline_CoversEveryInterfaceItClaimsTo()
    {
        FrozenAbstractMembers.Keys.Should().OnlyContain(
            t => t.IsInterface && t.IsPublic,
            "every pinned extension point must still be a public interface");

        FrozenAbstractMembers.Should().HaveCount(
            4, "removing an interface from the baseline removes it from the freeze without a diff that says so");
    }

    /// <summary>Theory data: one case per pinned interface.</summary>
    public static TheoryData<Type> FrozenInterfaces()
    {
        var data = new TheoryData<Type>();
        foreach (var t in FrozenAbstractMembers.Keys)
            data.Add(t);
        return data;
    }

    /// <summary>
    /// Renders a member as "ReturnType Name(ParamType, ...)" using short type names, with
    /// generic arguments spelled out so a change from <c>List</c> to <c>IReadOnlyList</c>
    /// registers as a break.
    /// </summary>
    private static string Describe(MethodInfo m)
        => $"{ShortName(m.ReturnType)} {m.Name}({string.Join(", ", m.GetParameters().Select(p => ShortName(p.ParameterType)))})";

    private static string ShortName(Type t)
    {
        if (!t.IsGenericType)
            return t.Name;

        var name = t.Name[..t.Name.IndexOf('`', StringComparison.Ordinal)];
        var args = string.Join(", ", t.GetGenericArguments().Select(ShortName));
        return $"{name}<{args}>";
    }
}
