using Alberto.Dcb.InMemory;
using Alberto.Dcb.Tenancy;
using FluentAssertions;
using Xunit;

namespace Alberto.Dcb.Tests;

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
