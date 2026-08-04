using Alberto.Subscriptions;
using Alberto.Testing;
using Xunit;

namespace Alberto.Tests.Testing;

public class ProjectionSpecTests
{
    // ── A miniature projection, in the shape a real one has ───────────────────────

    [EventType("proj-tab-opened")]
    private sealed record TabOpened(string TabId, string Name) : IEvent;

    [EventType("proj-round-ordered")]
    private sealed record RoundOrdered(string TabId, decimal Amount) : IEvent;

    [EventType("proj-tab-closed")]
    private sealed record TabClosed(string TabId) : IEvent;

    [EventType("proj-tab-voided")]
    private sealed record TabVoided(string TabId) : IEvent;

    /// <summary>Handled, but routed to no document — the "not my business" selector.</summary>
    [EventType("proj-staff-clocked-in")]
    private sealed record StaffClockedIn(string StaffId) : IEvent;

    /// <summary>An event the projection below declares no handler for.</summary>
    [EventType("proj-lights-dimmed")]
    private sealed record LightsDimmed : IEvent;

    /// <summary>An event that could never have been appended, so could never have arrived.</summary>
    private sealed record Unappendable : IEvent;

    private sealed record TabDocument
    {
        public string TabId { get; init; } = "";
        public string Name { get; init; } = "";
        public string? TenantId { get; init; }
        public decimal Total { get; init; }
        public bool IsClosed { get; init; }
        public long Position { get; init; }
        public DateTimeOffset OpenedAt { get; init; }
        public string Server { get; init; } = "";
    }

    private static ProjectionDeclaration<TabDocument> Tabs() =>
        DeclareProjection.For<TabDocument>("tabs")
            .On<TabOpened>(
                id: e => e.TabId,
                apply: (s, e, ctx) => s with
                {
                    TabId = e.TabId,
                    Name = e.Name,
                    TenantId = ctx.TenantId,
                    Position = ctx.Position,
                    OpenedAt = ctx.Timestamp,
                    Server = ctx.Metadata.GetValueOrDefault("server", "")
                })
            .On<RoundOrdered>(
                id: e => e.TabId,
                apply: (s, e, ctx) => s with { Total = s.Total + e.Amount, Position = ctx.Position })
            .On<TabClosed>(
                id: e => e.TabId,
                apply: (s, _, _) => s.IsClosed
                    ? ProjectionResults.Unchanged<TabDocument>()
                    : ProjectionResults.Set(s with { IsClosed = true }))
            .On<TabVoided>(
                id: e => e.TabId,
                apply: (_, _, _) => ProjectionResults.Delete<TabDocument>())
            .On<StaffClockedIn>(
                id: _ => null,
                apply: (s, _, _) => s)
            .Build();

    // ── An entity-backed projection, for the redelivery guard ─────────────────────

    private sealed class TicketEntity : IProjectionEntity
    {
        public string DocumentId { get; set; } = "";
        public DateTimeOffset UpdatedAt { get; set; }
        public long LastProcessedPosition { get; set; }
        public uint Version { get; set; }
        public int RebuildVersion { get; set; }
        public int Rounds { get; set; }
    }

    private static ProjectionDeclaration<TicketEntity> Tickets() =>
        DeclareProjection.For<TicketEntity>("tickets")
            .On<RoundOrdered>(
                id: e => e.TabId,
                apply: (s, e, _) =>
                {
                    s.DocumentId = e.TabId;
                    s.Rounds++;
                    return s;
                })
            .Build();

    // ── Given ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Given_folds_the_events_in_order()
    {
        ProjectionSpec.For(Tabs())
            .Given(new TabOpened("t-1", "Corner"), new RoundOrdered("t-1", 10m), new RoundOrdered("t-1", 5m))
            .ThenDocument("t-1", d => Assert.Equal(15m, d.Total));
    }

    [Fact]
    public void Given_ignores_events_the_projection_declares_no_handler_for()
    {
        ProjectionSpec.For(Tabs())
            .Given(new TabOpened("t-1", "Corner"), new LightsDimmed())
            .ThenDocument(d => Assert.Equal("Corner", d.Name));
    }

    [Fact]
    public void Given_accumulates_across_calls_so_an_arrange_helper_can_be_extended()
    {
        ProjectionSpec.For(Tabs())
            .Given(new TabOpened("t-1", "Corner"))
            .Given(new RoundOrdered("t-1", 4m))
            .ThenDocument(d => Assert.Equal(4m, d.Total));
    }

    [Fact]
    public void GivenNoEvents_leaves_the_projection_empty()
    {
        ProjectionSpec.For(Tabs())
            .GivenNoEvents()
            .ThenNoDocuments();
    }

    [Fact]
    public void Given_after_When_says_arrange_comes_before_act()
    {
        var spec = ProjectionSpec.For(Tabs()).When(new TabOpened("t-1", "Corner"));

        var ex = Assert.Throws<SpecificationException>(() => spec.Given(new RoundOrdered("t-1", 1m)));
        Assert.Contains("before When", ex.Message);
    }

    [Fact]
    public void An_event_with_no_EventType_attribute_is_refused_rather_than_ignored()
    {
        var spec = ProjectionSpec.For(Tabs());

        var ex = Assert.Throws<SpecificationException>(() => spec.When(new Unappendable()));
        Assert.Contains("Unappendable", ex.Message);
        Assert.Contains("[EventType]", ex.Message);
    }

    // ── Document routing ──────────────────────────────────────────────────────────

    [Fact]
    public void Each_document_id_gets_its_own_document()
    {
        ProjectionSpec.For(Tabs())
            .Given(new TabOpened("t-1", "Corner"), new TabOpened("t-2", "Window"))
            .When(new RoundOrdered("t-1", 7m))
            .ThenDocumentCount(2)
            .ThenDocument("t-1", d => Assert.Equal(7m, d.Total))
            .ThenDocument("t-2", d => Assert.Equal(0m, d.Total));
    }

    [Fact]
    public void An_event_routed_to_no_document_writes_nothing()
    {
        ProjectionSpec.For(Tabs())
            .GivenNoEvents()
            .When(new StaffClockedIn("s-1"))
            .ThenUnchanged()
            .ThenNoDocuments();
    }

    [Fact]
    public void Delete_removes_the_document()
    {
        ProjectionSpec.For(Tabs())
            .Given(new TabOpened("t-1", "Corner"), new RoundOrdered("t-1", 10m))
            .When(new TabVoided("t-1"))
            .ThenDeleted("t-1")
            .ThenNoDocument("t-1");
    }

    [Fact]
    public void A_document_deleted_and_written_again_in_one_batch_starts_from_the_initial_state()
    {
        ProjectionSpec.For(Tabs())
            .Given(new TabOpened("t-1", "Corner"), new RoundOrdered("t-1", 10m))
            .When(new TabVoided("t-1"), new TabOpened("t-1", "Corner"))
            .ThenDocument("t-1", d => Assert.Equal(0m, d.Total));
    }

    [Fact]
    public void Unchanged_leaves_the_document_exactly_as_it_was()
    {
        ProjectionSpec.For(Tabs())
            .Given(new TabOpened("t-1", "Corner"), new TabClosed("t-1"))
            .When(new TabClosed("t-1"))
            .ThenUnchanged()
            .ThenDocument(d => Assert.True(d.IsClosed));
    }

    // ── Context ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Positions_start_at_one_and_increment_across_Given_and_When()
    {
        ProjectionSpec.For(Tabs())
            .Given(new TabOpened("t-1", "Corner"))
            .When(new RoundOrdered("t-1", 1m))
            .ThenDocument(d => Assert.Equal(2, d.Position));
    }

    [Fact]
    public void AtPosition_places_the_next_event_and_the_rest_follow_from_there()
    {
        ProjectionSpec.For(Tabs())
            .AtPosition(50)
            .Given(new TabOpened("t-1", "Corner"))
            .When(new RoundOrdered("t-1", 1m))
            .ThenDocument(d => Assert.Equal(51, d.Position));
    }

    [Fact]
    public void AtPosition_refuses_a_position_the_log_could_never_have_handed_out()
    {
        var spec = ProjectionSpec.For(Tabs());

        var ex = Assert.Throws<SpecificationException>(() => spec.AtPosition(0));
        Assert.Contains("start at 1", ex.Message);
    }

    [Fact]
    public void A_redelivery_at_a_position_already_processed_is_ignored()
    {
        ProjectionSpec.For(Tickets())
            .Given(new RoundOrdered("t-1", 1m), new RoundOrdered("t-1", 1m))
            .AtPosition(2)
            .When(new RoundOrdered("t-1", 1m))
            .ThenUnchanged()
            .ThenDocument(d => Assert.Equal(2, d.Rounds));
    }

    [Fact]
    public void An_entity_carries_the_position_it_last_processed()
    {
        ProjectionSpec.For(Tickets())
            .When(new RoundOrdered("t-1", 1m))
            .ThenDocument(d => Assert.Equal(1, d.LastProcessedPosition));
    }

    [Fact]
    public void ForTenant_is_the_tenant_the_projection_sees()
    {
        ProjectionSpec.For(Tabs())
            .ForTenant("acme")
            .When(new TabOpened("t-1", "Corner"))
            .ThenDocument(d => Assert.Equal("acme", d.TenantId));
    }

    [Fact]
    public void The_tenant_is_null_by_default_which_is_what_a_module_without_tenancy_passes()
    {
        ProjectionSpec.For(Tabs())
            .When(new TabOpened("t-1", "Corner"))
            .ThenDocument(d => Assert.Null(d.TenantId));
    }

    [Fact]
    public void Events_carry_the_Epoch_until_At_says_otherwise()
    {
        var later = ProjectionSpec.Epoch.AddDays(3);

        ProjectionSpec.For(Tabs())
            .Given(new TabOpened("t-1", "Corner"))
            .At(later)
            .When(new TabOpened("t-2", "Window"))
            .ThenDocument("t-1", d => Assert.Equal(ProjectionSpec.Epoch, d.OpenedAt))
            .ThenDocument("t-2", d => Assert.Equal(later, d.OpenedAt));
    }

    [Fact]
    public void WithMetadata_reaches_the_handler()
    {
        ProjectionSpec.For(Tabs())
            .WithMetadata(new Dictionary<string, string> { ["server"] = "ada" })
            .When(new TabOpened("t-1", "Corner"))
            .ThenDocument(d => Assert.Equal("ada", d.Server));
    }

    // ── Then verbs, and what they say when they fail ──────────────────────────────

    [Fact]
    public void ThenDocument_without_an_id_needs_there_to_be_exactly_one()
    {
        var spec = ProjectionSpec.For(Tabs())
            .Given(new TabOpened("t-1", "Corner"), new TabOpened("t-2", "Window"));

        var ex = Assert.Throws<SpecificationException>(() => spec.ThenDocument(_ => { }));
        Assert.Contains("'t-1', 't-2'", ex.Message);
        Assert.Contains("ThenDocument(id", ex.Message);
    }

    [Fact]
    public void ThenDocument_names_what_the_projection_does_hold_when_the_one_asked_for_is_missing()
    {
        var spec = ProjectionSpec.For(Tabs()).Given(new TabOpened("t-1", "Corner"));

        var ex = Assert.Throws<SpecificationException>(() => spec.ThenDocument("t-9", _ => { }));
        Assert.Contains("'t-9'", ex.Message);
        Assert.Contains("'t-1'", ex.Message);
    }

    [Fact]
    public void ThenNoDocument_fails_when_the_document_is_there()
    {
        var spec = ProjectionSpec.For(Tabs()).Given(new TabOpened("t-1", "Corner"));

        Assert.Throws<SpecificationException>(() => spec.ThenNoDocument("t-1"));
    }

    [Fact]
    public void ThenNoDocuments_fails_naming_what_was_written()
    {
        var spec = ProjectionSpec.For(Tabs()).Given(new TabOpened("t-1", "Corner"));

        var ex = Assert.Throws<SpecificationException>(spec.ThenNoDocuments);
        Assert.Contains("'t-1'", ex.Message);
    }

    [Fact]
    public void ThenDocumentCount_reports_the_count_it_found()
    {
        var spec = ProjectionSpec.For(Tabs()).Given(new TabOpened("t-1", "Corner"));

        var ex = Assert.Throws<SpecificationException>(() => spec.ThenDocumentCount(2));
        Assert.Contains("Expected 2", ex.Message);
        Assert.Contains("1: 't-1'", ex.Message);
    }

    [Fact]
    public void ThenDeleted_distinguishes_a_removed_document_from_one_never_written()
    {
        var spec = ProjectionSpec.For(Tabs())
            .GivenNoEvents()
            .When(new TabVoided("t-1"));

        var ex = Assert.Throws<SpecificationException>(() => spec.ThenDeleted("t-1"));
        Assert.Contains("no such document existed before", ex.Message);
    }

    [Fact]
    public void ThenDeleted_fails_when_the_document_survived()
    {
        var spec = ProjectionSpec.For(Tabs())
            .Given(new TabOpened("t-1", "Corner"))
            .When(new RoundOrdered("t-1", 1m));

        var ex = Assert.Throws<SpecificationException>(() => spec.ThenDeleted("t-1"));
        Assert.Contains("still holds it", ex.Message);
    }

    [Fact]
    public void ThenUnchanged_names_what_was_written_instead()
    {
        var spec = ProjectionSpec.For(Tabs()).When(new TabOpened("t-1", "Corner"));

        var ex = Assert.Throws<SpecificationException>(spec.ThenUnchanged);
        Assert.Contains("wrote 't-1'", ex.Message);
    }

    [Fact]
    public void ThenUnchanged_names_what_was_deleted_instead()
    {
        var spec = ProjectionSpec.For(Tabs())
            .Given(new TabOpened("t-1", "Corner"))
            .When(new TabVoided("t-1"));

        var ex = Assert.Throws<SpecificationException>(spec.ThenUnchanged);
        Assert.Contains("deleted 't-1'", ex.Message);
    }

    [Fact]
    public void ThenUnchanged_needs_a_When_to_compare_across()
    {
        var spec = ProjectionSpec.For(Tabs()).Given(new TabOpened("t-1", "Corner"));

        var ex = Assert.Throws<SpecificationException>(spec.ThenUnchanged);
        Assert.Contains("needs a When", ex.Message);
    }

    [Fact]
    public void ThenDeleted_needs_a_When_to_compare_across()
    {
        var spec = ProjectionSpec.For(Tabs()).Given(new TabOpened("t-1", "Corner"));

        var ex = Assert.Throws<SpecificationException>(() => spec.ThenDeleted("t-1"));
        Assert.Contains("needs a When", ex.Message);
    }

    [Fact]
    public void Each_When_re_bases_what_unchanged_and_deleted_compare_against()
    {
        ProjectionSpec.For(Tabs())
            .When(new TabOpened("t-1", "Corner"))
            .When(new LightsDimmed())
            .ThenUnchanged();
    }

    [Fact]
    public void ThenDocuments_hands_over_everything_for_the_assertions_the_verbs_do_not_cover()
    {
        ProjectionSpec.For(Tabs())
            .Given(new TabOpened("t-1", "Corner"), new TabOpened("t-2", "Window"))
            .ThenDocuments(documents =>
            {
                Assert.Equal(["t-1", "t-2"], documents.Keys.Order());
                Assert.All(documents.Values, d => Assert.Equal(0m, d.Total));
            });
    }

    [Fact]
    public void Every_verb_returns_the_specification_so_a_chain_never_narrows()
    {
        ProjectionSpec.For(Tabs())
            .Given(new TabOpened("t-1", "Corner"))
            .When(new RoundOrdered("t-1", 3m))
            .ThenDocumentCount(1)
            .ThenNoDocument("t-2")
            .ThenDocuments(d => Assert.Single(d))
            .ThenDocument("t-1", d => Assert.Equal(3m, d.Total));
    }
}
