using Alberto.EntityFramework;
using Alberto.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Alberto.Tests.EntityFramework;

/// <summary>
/// EF-4: an EF projection on a module that declared <c>.WithTenancy()</c> is refused at
/// registration unless the caller states that its document ids are unique across tenants.
///
/// <para>
/// The reason it has to be refused rather than fixed: <see cref="Alberto.Projections.IProjectionEntity"/>
/// carries no tenant column, so both <c>EfStateStore</c> and the inline projection load and write
/// by <c>(DocumentId, RebuildVersion)</c> with no tenant predicate available to them. Two tenants
/// producing the same document id therefore share one row — last write wins, and every read after
/// it returns the other tenant's data. Whether that can happen is a property of the declaration's
/// id selectors, which nothing here can inspect.
/// </para>
///
/// Registration-time only: the guard runs before any service is resolved, so these tests need no
/// database and carry no Testcontainers fixture.
/// </summary>
public class EfProjectionTenancyGuardTests
{
    // ── helpers ────────────────────────────────────────────────────────────────────────────

    private const string ProcessorId = "counter-projection";

    /// <summary>
    /// Builds a module with the given tenancy and uniqueness declaration and returns an
    /// <see cref="Action"/> that can be passed to an exception assertion. <paramref name="tenancyFirst"/>
    /// controls whether <c>.WithTenancy()</c> is chained before or after <c>AddEfProjection</c> —
    /// the guard runs from a deferred registration callback precisely so the answer does not
    /// depend on that.
    /// </summary>
    private static Action Register(
        bool tenancy,
        EfDocumentIdUniqueness documentIds,
        ProjectionMode mode = ProjectionMode.Async,
        bool tenancyFirst = true) =>
        () =>
        {
            var services = new ServiceCollection();
            services.AddAlberto("orders", m =>
            {
                m.WithInMemory();
                if (tenancy && tenancyFirst)
                    m.WithTenancy();

                m.AddEfProjection<CounterEntity, EfTestDbContext>(
                    EfProjectionTestFixture.BuildDeclaration(ProcessorId),
                    mode,
                    documentIds);

                if (tenancy && !tenancyFirst)
                    m.WithTenancy();
            });
        };

    /// <summary>
    /// The <c>afterCommit</c> overload, which registers through a third code path of its own.
    /// </summary>
    private static Action RegisterWithAfterCommit(bool tenancy, EfDocumentIdUniqueness documentIds) =>
        () =>
        {
            var services = new ServiceCollection();
            services.AddAlberto("orders", m =>
            {
                m.WithInMemory();
                if (tenancy)
                    m.WithTenancy();

                m.AddEfProjection<CounterEntity, EfTestDbContext>(
                    EfProjectionTestFixture.BuildDeclaration(ProcessorId),
                    afterCommit: _ => (_, _) => Task.CompletedTask,
                    documentIds: documentIds);
            });
        };

    // ── the combination that is refused ────────────────────────────────────────────────────

    [Theory]
    [InlineData(ProjectionMode.Async)]
    [InlineData(ProjectionMode.Inline)]
    public void Tenant_enabled_module_without_a_uniqueness_declaration_is_refused(ProjectionMode mode)
    {
        var exception = Assert.ThrowsAny<InvalidOperationException>(Register(tenancy: true, EfDocumentIdUniqueness.NotDeclared, mode));
        Assert.StartsWith("ALB0027:", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"'{ProcessorId}'", exception.Message, StringComparison.OrdinalIgnoreCase);
        // the message has to name the opt-in that unblocks the registration
        Assert.Contains("EfDocumentIdUniqueness.AcrossTenants", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AfterCommit_overload_is_refused_on_the_same_terms()
    {
        var exception = Assert.ThrowsAny<InvalidOperationException>(RegisterWithAfterCommit(tenancy: true, EfDocumentIdUniqueness.NotDeclared));
        Assert.StartsWith("ALB0027:", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tenancy_declared_after_the_projection_is_still_refused()
    {
        // The whole reason the guard lives in a deferred registration callback rather than at the
        // AddEfProjection call site: the callbacks do not run until the module lambda has
        // completed, so chain order cannot decide whether the module is tenant-enabled.
        var exception = Assert.ThrowsAny<InvalidOperationException>(Register(tenancy: true, EfDocumentIdUniqueness.NotDeclared, tenancyFirst: false));
        Assert.StartsWith("ALB0027:", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── the combinations that are allowed ──────────────────────────────────────────────────

    [Theory]
    [InlineData(ProjectionMode.Async)]
    [InlineData(ProjectionMode.Inline)]
    public void Tenant_enabled_module_with_the_uniqueness_declaration_is_accepted(ProjectionMode mode)
    {
        // the caller has stated that no two tenants can share a document id
        Register(tenancy: true, EfDocumentIdUniqueness.AcrossTenants, mode)();
    }

    [Fact]
    public void AfterCommit_overload_with_the_uniqueness_declaration_is_accepted()
    {
        RegisterWithAfterCommit(tenancy: true, EfDocumentIdUniqueness.AcrossTenants)();
    }

    [Theory]
    [InlineData(ProjectionMode.Async)]
    [InlineData(ProjectionMode.Inline)]
    public void Single_tenant_module_is_accepted_without_any_declaration(ProjectionMode mode)
    {
        // There is no second tenant to collide with, so NotDeclared — the default — is the
        // correct value here and must stay silent. Nobody should have to answer a tenancy
        // question on a module that has no tenancy.
        Register(tenancy: false, EfDocumentIdUniqueness.NotDeclared, mode)();
    }

    [Fact]
    public void Single_tenant_afterCommit_registration_is_accepted_without_any_declaration()
    {
        RegisterWithAfterCommit(tenancy: false, EfDocumentIdUniqueness.NotDeclared)();
    }
}
