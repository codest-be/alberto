using Alberto.InMemory;
using Alberto.Tenancy;
using Alberto.Testing.Xunit;
using FluentAssertions;
using Xunit;

namespace Alberto.Tests;

/// <summary>
/// Specification tests for InMemoryEventStoreBackend.
/// Test isolation is achieved through unique tag values per test.
/// </summary>
public class InMemoryEventStoreBackendTests : EventStoreBackendSpecification
{
    private readonly InMemoryEventStoreBackend _backend;

    public InMemoryEventStoreBackendTests()
    {
        _backend = new InMemoryEventStoreBackend(TimeProvider);
    }

    protected override Task<IEventStoreBackend> CreateBackend()
    {
        return Task.FromResult<IEventStoreBackend>(_backend);
    }
}

/// <summary>
/// Runs the <see cref="EventStoreBackendSpecification"/> conformance suite against
/// <see cref="InMemoryTenantEventStoreDecorator"/>. This wires the decorator into the same
/// contract that the raw <see cref="InMemoryEventStoreBackend"/> must satisfy, so a change
/// to the decorator's delegation logic that breaks any of those guarantees fails here rather
/// than silently.
///
/// <para>
/// The decorator is exercised in its real-world configuration: <c>HasTenant = true</c> (a
/// request-scoped context with an active tenant). Every <c>AppendAsync</c> call routes through
/// <c>AppendForTenant("spec-tenant", …)</c> and every <c>StreamAsync</c> call routes through
/// <c>StreamForTenant("spec-tenant", …)</c>. Because each fact gets a fresh backend with no
/// prior events, the tenant-scoped stream sees exactly the events that fact wrote — the
/// same isolation property unique test tags provide for the shared-backend tests.
/// </para>
///
/// <para>
/// <see cref="EventStoreBackendSpecification.SupportsStreamAll"/> is <c>false</c>: the
/// decorator's <c>StreamAllAsync</c> intentionally throws <see cref="InvalidOperationException"/>
/// when <c>HasTenant = true</c> to prevent a request-scoped caller from reading all tenants'
/// events. Both <c>StreamAllAsync</c> facts assert the throw — the isolation guard is part
/// of the contract and is verified here. <see cref="InMemoryTenantBackendTests.Decorator_StreamAllAsync_ThrowsWhenHasTenant"/>
/// provides an additional assertion on the exception message shape.
/// </para>
/// </summary>
public sealed class InMemoryTenantEventStoreDecoratorSpecificationTests : EventStoreBackendSpecification
{
    // StreamAllAsync throws when HasTenant = true — this is the isolation guard. Both StreamAll
    // facts assert the throw so the guard is under specification, not skipped.
    protected override bool SupportsStreamAll => false;

    protected override Task<IEventStoreBackend> CreateBackend()
    {
        // A fresh inner backend per fact so no events leak across tests even though the
        // decorator routes everything through the "spec-tenant" tenant path.
        var inner = new InMemoryEventStoreBackend(TimeProvider);
        var accessor = new SpecTenantAccessor();
        return Task.FromResult<IEventStoreBackend>(new InMemoryTenantEventStoreDecorator(inner, accessor));
    }

    /// <summary>
    /// A fixed-tenant accessor with <c>HasTenant = true</c>. This mirrors the real DI
    /// registration: the request-scoped decorator always has an active tenant, and
    /// <c>TenantId</c> is always non-null while in that context.
    /// </summary>
    private sealed class SpecTenantAccessor : ITenantAccessor
    {
        public string TenantId => "spec-tenant";
        public string? TenantIdOrDefault => "spec-tenant";
        public bool HasTenant => true;
    }
}

/// <summary>
/// Tests that verify the in-memory backend's multi-tenant path stamps the correct tenant ID
/// and isolates events by tenant — matching the behaviour of the Postgres tenant backend.
/// These tests call <c>AppendForTenant</c> and <c>StreamForTenant</c> directly on
/// <see cref="InMemoryEventStoreBackend"/> to validate the internal path used by
/// <see cref="InMemoryTenantEventStoreDecorator"/>.
/// </summary>
public class InMemoryTenantBackendTests
{
    private readonly InMemoryEventStoreBackend _backend = new();

    private string TestId { get; } = Guid.NewGuid().ToString("N")[..8];

    private static IEventToPersist Event(string type, params string[] tags) =>
        new EventToPersist
        {
            EventType = new EventType(type),
            EventData = """{"test":true}""",
            Tags = tags.Select(EventTag.Parse).ToArray()
        };

    // ── Bug (a) regression: AppendForTenant stamps TenantId ─────────────────────────

    [Fact]
    public async Task AppendForTenant_StampsTenantId_OnReturnedEnvelope()
    {
        var result = await _backend.AppendForTenant(
            "acme",
            [Event("order-placed", $"order:{TestId}")],
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().ContainSingle()
            .Which.TenantId.Should().Be("acme");
    }

    [Fact]
    public async Task AppendAsync_SingleTenantPath_LeavesNullTenantId()
    {
        // Validates that the original single-tenant path is unchanged — events appended
        // via the interface method (not the tenant-aware method) carry TenantId = null,
        // matching the Postgres single-tenant schema that has no tenant_id column.
        var result = await _backend.AppendAsync(
            [Event("order-placed", $"order:{TestId}")],
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().ContainSingle()
            .Which.TenantId.Should().BeNull();
    }

    // ── Tenant isolation: StreamForTenant filters to the given tenant ────────────────

    [Fact]
    public async Task StreamForTenant_ReturnsOnlyThatTenantsEvents()
    {
        await _backend.AppendForTenant("acme", [Event("evt-a", $"tag:{TestId}")],
            cancellationToken: TestContext.Current.CancellationToken);
        await _backend.AppendForTenant("globex", [Event("evt-b", $"tag:{TestId}")],
            cancellationToken: TestContext.Current.CancellationToken);
        await _backend.AppendForTenant("acme", [Event("evt-c", $"tag:{TestId}")],
            cancellationToken: TestContext.Current.CancellationToken);

        var acmeEvents = await _backend.StreamForTenant(
            "acme",
            DcbQuery.ByTags($"tag:{TestId}"),
            cancellationToken: TestContext.Current.CancellationToken);

        acmeEvents.Should().HaveCount(2).And
            .AllSatisfy(e => e.TenantId.Should().Be("acme"));
    }

    [Fact]
    public async Task StreamForTenant_DoesNotSeeOtherTenantEvents()
    {
        await _backend.AppendForTenant("globex", [Event("evt", $"exclusive:{TestId}")],
            cancellationToken: TestContext.Current.CancellationToken);

        var result = await _backend.StreamForTenant(
            "acme",
            DcbQuery.ByTags($"exclusive:{TestId}"),
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().BeEmpty();
    }

    // ── DCB conflict check is tenant-scoped ─────────────────────────────────────────

    [Fact]
    public async Task AppendForTenant_DcbConflict_IsScoped_ToTenant()
    {
        // An event from tenant 'acme' must NOT trigger a conflict for tenant 'globex'
        // even if the tags match, because each tenant's DCB boundary is independent.
        var tag = $"order:{TestId}";
        var dcbQuery = DcbQuery.ByTags(tag);

        await _backend.AppendForTenant("acme", [Event("order-placed", tag)],
            cancellationToken: TestContext.Current.CancellationToken);

        // 'globex' sees an empty boundary — should succeed with expectedPosition = 0.
        var result = await _backend.AppendForTenant(
            "globex",
            [Event("order-placed", tag)],
            dcbQuery,
            expectedPosition: 0,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().ContainSingle();
    }

    [Fact]
    public async Task AppendForTenant_DcbConflict_IsDetected_WithinSameTenant()
    {
        var tag = $"order:{TestId}";
        var dcbQuery = DcbQuery.ByTags(tag);

        var first = await _backend.AppendForTenant("acme", [Event("order-placed", tag)],
            cancellationToken: TestContext.Current.CancellationToken);

        var seenPosition = first.Single().GlobalPosition;

        // A second append for the same tenant at the same position must conflict.
        await _backend.AppendForTenant("acme", [Event("order-updated", tag)],
            cancellationToken: TestContext.Current.CancellationToken);

        var act = async () => await _backend.AppendForTenant(
            "acme",
            [Event("order-cancelled", tag)],
            dcbQuery,
            expectedPosition: seenPosition,
            cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DcbConflictException>();
    }

    // ── Decorator wiring: InMemoryTenantEventStoreDecorator round-trip ───────────────

    [Fact]
    public async Task Decorator_StampsTenantId_FromAccessor()
    {
        // Verify the decorator correctly threads ITenantAccessor.TenantId to AppendForTenant.
        var fakeAccessor = new FakeTenantAccessor("umbrella");
        var decorator = new InMemoryTenantEventStoreDecorator(_backend, fakeAccessor);

        var result = await decorator.AppendAsync(
            [Event("order-placed", $"d-order:{TestId}")],
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().ContainSingle()
            .Which.TenantId.Should().Be("umbrella");
    }

    [Fact]
    public async Task Decorator_StreamAllAsync_ThrowsWhenHasTenant()
    {
        // Mirrors TenantEventStoreDecorator's guard: a request-scoped caller with an active
        // tenant must not stream all tenants' events, bypassing isolation.
        var fakeAccessor = new FakeTenantAccessor("acme");
        var decorator = new InMemoryTenantEventStoreDecorator(_backend, fakeAccessor);

        var act = async () => await decorator.StreamAllAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*StreamAllAsync*");
    }

    private sealed class FakeTenantAccessor(string tenantId) : ITenantAccessor
    {
        public string TenantId { get; } = tenantId;
        public string? TenantIdOrDefault => TenantId;
        public bool HasTenant => true;
    }
}
