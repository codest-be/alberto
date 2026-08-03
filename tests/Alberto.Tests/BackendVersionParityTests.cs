using Alberto.InMemory;
using Alberto.Postgres;
using FluentAssertions;
using Xunit;

namespace Alberto.Tests;

// ---------------------------------------------------------------------------
// Helpers shared by both test classes
// ---------------------------------------------------------------------------

file static class VersionParityHelpers
{
    /// <summary>
    /// Builds an event-to-persist whose input <see cref="EventType.Version"/>
    /// <em>disagrees</em> with the <c>_version:N</c> tag in its tag list.
    /// This is the critical case: storage (the tag) must win over the input object.
    /// </summary>
    /// <param name="tagVersion">Version number stamped into the <c>_version:N</c> tag.</param>
    /// <param name="inputVersion">Version number set on the <see cref="EventType"/> of the input.</param>
    public static EventToPersist MakeDisagreementEvent(
        int tagVersion,
        int inputVersion)
    {
        return new EventToPersist
        {
            // The input object claims one version…
            EventType = new EventType("parity-event", inputVersion),
            // …but the _v tag says another.
            Tags = [EventTag.ForVersion(tagVersion)],
            EventData = """{"key":"value"}"""
        };
    }

    /// <summary>
    /// Builds a normal versioned event where the input object and the <c>_version:N</c> tag agree.
    /// </summary>
    public static EventToPersist MakeVersionedEvent(int version)
    {
        return new EventToPersist
        {
            EventType = new EventType("parity-event", version),
            Tags = [EventTag.ForVersion(version)],
            EventData = """{"key":"value"}"""
        };
    }

    /// <summary>
    /// Builds an event with no <c>_version:N</c> tag at all — simulates a row written
    /// before schema versioning was introduced.
    /// </summary>
    public static EventToPersist MakeLegacyEvent()
    {
        return new EventToPersist
        {
            EventType = new EventType("parity-event"),   // no explicit version
            Tags = [],                                    // no _v tag
            EventData = """{"key":"value"}"""
        };
    }
}

// ---------------------------------------------------------------------------
// 1. InMemory — version is reconstructed from the _v tag, not the input object
//    No Docker required.
// ---------------------------------------------------------------------------

/// <summary>
/// Verifies that <see cref="InMemoryEventStoreBackend"/> reconstructs
/// <see cref="EventType.Version"/> from the stored <c>_version:N</c> tag, exactly as the
/// PostgreSQL backend does, so the two backends feed the upcaster chain the same
/// version for identical stored data.
/// </summary>
public sealed class InMemoryVersionParityTests
{
    // ── critical disagreement case ─────────────────────────────────────────

    [Fact]
    public async Task TagVersion_WinsOver_InputObjectVersion()
    {
        // Arrange: input claims version 5, but the _v tag says 3.
        var backend = new InMemoryEventStoreBackend();
        var evt = VersionParityHelpers.MakeDisagreementEvent(tagVersion: 3, inputVersion: 5);

        // Act
        var result = await backend.AppendAsync(
            [evt],
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert: storage (_version:3) wins.
        result.Single().EventType.Version.Should().Be(3,
            "the _v tag is the source of truth on read, not EventType.Version on the input object");
    }

    [Fact]
    public async Task StreamedEnvelope_ReflectsTagVersion_NotInputVersion()
    {
        // Same disagreement scenario verified on the stream path, not just the append result.
        var backend = new InMemoryEventStoreBackend();
        var evt = VersionParityHelpers.MakeDisagreementEvent(tagVersion: 2, inputVersion: 7);

        await backend.AppendAsync([evt], cancellationToken: TestContext.Current.CancellationToken);

        var streamed = await backend.StreamAllAsync(cancellationToken: TestContext.Current.CancellationToken);

        streamed.Single().EventType.Version.Should().Be(2,
            "the stream path must return the same version as the append result");
    }

    // ── agreement cases ────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(10)]
    public async Task AgreedVersion_IsPreservedFaithfully(int version)
    {
        var backend = new InMemoryEventStoreBackend();
        var evt = VersionParityHelpers.MakeVersionedEvent(version);

        var result = await backend.AppendAsync(
            [evt],
            cancellationToken: TestContext.Current.CancellationToken);

        result.Single().EventType.Version.Should().Be(version);
    }

    // ── legacy (no _v tag) ─────────────────────────────────────────────────

    [Fact]
    public async Task LegacyEvent_WithNoVersionTag_DefaultsToVersion1()
    {
        var backend = new InMemoryEventStoreBackend();
        var evt = VersionParityHelpers.MakeLegacyEvent();

        var result = await backend.AppendAsync(
            [evt],
            cancellationToken: TestContext.Current.CancellationToken);

        result.Single().EventType.Version.Should().Be(1,
            "events written before schema versioning was introduced must be treated as v1");
    }

    [Fact]
    public async Task LegacyEvent_StreamPath_DefaultsToVersion1()
    {
        var backend = new InMemoryEventStoreBackend();
        await backend.AppendAsync(
            [VersionParityHelpers.MakeLegacyEvent()],
            cancellationToken: TestContext.Current.CancellationToken);

        var streamed = await backend.StreamAllAsync(cancellationToken: TestContext.Current.CancellationToken);

        streamed.Single().EventType.Version.Should().Be(1);
    }
}

// ---------------------------------------------------------------------------
// 2. Postgres — same assertions over the real backend (Testcontainers / Docker)
//    If Docker is unavailable the fixture fails to start and xUnit skips the class.
// ---------------------------------------------------------------------------

/// <summary>
/// Verifies that <see cref="PostgresEventStoreBackend"/> reconstructs
/// <see cref="EventType.Version"/> from the stored <c>_version:N</c> tag.
/// These tests require Docker (Testcontainers); the class is skipped automatically
/// when Docker is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresVersionParityTests(SingleTenantPostgresFixture fixture)
    : IClassFixture<SingleTenantPostgresFixture>
{
    private IEventStoreBackend CreateBackend() =>
        new PostgresEventStoreBackend(fixture.DataSource);

    // ── critical disagreement case ─────────────────────────────────────────

    [Fact]
    public async Task TagVersion_WinsOver_InputObjectVersion()
    {
        var backend = CreateBackend();
        var evt = VersionParityHelpers.MakeDisagreementEvent(tagVersion: 3, inputVersion: 5);

        var result = await backend.AppendAsync(
            [evt],
            cancellationToken: TestContext.Current.CancellationToken);

        result.Single().EventType.Version.Should().Be(3,
            "Postgres reconstructs version from the _v tag; the input EventType.Version is ignored on read");
    }

    [Fact]
    public async Task StreamedEnvelope_ReflectsTagVersion_NotInputVersion()
    {
        var backend = CreateBackend();
        var evt = VersionParityHelpers.MakeDisagreementEvent(tagVersion: 2, inputVersion: 7);

        await backend.AppendAsync([evt], cancellationToken: TestContext.Current.CancellationToken);

        var streamed = await backend.StreamAllAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Use the last appended event (other tests may have populated the store).
        streamed.Should().Contain(e =>
            e.EventType.Id == "parity-event" && e.EventType.Version == 2,
            "the stream path must reflect the tag version, not the input object version");
    }

    // ── agreement cases ────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AgreedVersion_IsPreservedFaithfully(int version)
    {
        var backend = CreateBackend();
        var evt = VersionParityHelpers.MakeVersionedEvent(version);

        var result = await backend.AppendAsync(
            [evt],
            cancellationToken: TestContext.Current.CancellationToken);

        result.Single().EventType.Version.Should().Be(version);
    }

    // ── legacy (no _v tag) ─────────────────────────────────────────────────

    [Fact]
    public async Task LegacyEvent_WithNoVersionTag_DefaultsToVersion1()
    {
        var backend = CreateBackend();
        var evt = VersionParityHelpers.MakeLegacyEvent();

        var result = await backend.AppendAsync(
            [evt],
            cancellationToken: TestContext.Current.CancellationToken);

        result.Single().EventType.Version.Should().Be(1,
            "events written before schema versioning was introduced must be treated as v1");
    }
}

// ---------------------------------------------------------------------------
// 3. Cross-backend parity assertion (InMemory only — no Postgres process needed)
//
//    Directly tests the symmetry property: for the same stored input, both the
//    InMemory helper (ParseFromTags) and the Postgres helper (ParseFromRawTags)
//    return the same version.  This runs without Docker and catches any divergence
//    between the two overloads in EventVersionTag.
// ---------------------------------------------------------------------------

/// <summary>
/// Exercises both <c>EventVersionTag</c> overloads with the same logical input and
/// asserts they agree, so drift between the two helpers is caught immediately.
/// </summary>
public sealed class EventVersionTagParityTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(10)]
    public void ParseFromTags_And_ParseFromRawTags_AgreeOnVersion(int version)
    {
        var tag = EventTag.ForVersion(version);
        IReadOnlyCollection<EventTag> tagCollection = [tag];
        string[] rawTags = [tag.Value];

        var fromTags = EventVersionTag.ParseFromTags(tagCollection);
        var fromRawTags = EventVersionTag.ParseFromRawTags(rawTags);

        fromTags.Should().Be(version);
        fromRawTags.Should().Be(version);
        fromTags.Should().Be(fromRawTags,
            "both overloads must extract the same version from equivalent inputs");
    }

    [Fact]
    public void ParseFromTags_And_ParseFromRawTags_AgreeOnDefaultVersion1_WhenNoTag()
    {
        IReadOnlyCollection<EventTag> emptyTagCollection = [];
        string[] emptyRawTags = [];

        var fromTags = EventVersionTag.ParseFromTags(emptyTagCollection);
        var fromRawTags = EventVersionTag.ParseFromRawTags(emptyRawTags);

        fromTags.Should().Be(1);
        fromRawTags.Should().Be(1);
        fromTags.Should().Be(fromRawTags);
    }

    [Fact]
    public void ParseFromTags_And_ParseFromRawTags_AgreeWithOtherTagsPresent()
    {
        // The _v tag is not required to be first or last — the helper must scan all tags.
        var orderTag = EventTag.FromStorage("order", "abc123");
        var versionTag = EventTag.ForVersion(3);
        var customerTag = EventTag.FromStorage("customer", "456");

        // _v tag in the middle
        IReadOnlyCollection<EventTag> tags = [orderTag, versionTag, customerTag];
        string[] rawTags = [orderTag.Value, versionTag.Value, customerTag.Value];

        EventVersionTag.ParseFromTags(tags).Should().Be(3);
        EventVersionTag.ParseFromRawTags(rawTags).Should().Be(3);
    }
}
