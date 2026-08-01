using Alberto.EntityFramework;
using Alberto.InMemory;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Alberto.Tests.EntityFramework;

/// <summary>
/// Verifies that both <c>AddEfProjection</c> overloads enforce the same processor-id
/// restrictions as the core <c>AddProjection</c> and <c>ReactTo</c> paths:
/// <list type="bullet">
///   <item><description>':' and '#' are rejected because they are DI-key separators.</description></item>
///   <item><description>Reserved words ("consumer", "catalog", "tenant-raw") shadow Alberto's own
///   internal service registrations and are rejected.</description></item>
///   <item><description>PascalCase, dotted, and hyphenated ids that do not contain those characters
///   are accepted — the rule is narrower than the strict module-key rule.</description></item>
/// </list>
/// Validation fires at registration time (before any service-collection entry is written), so
/// these tests need no database and carry no Testcontainers fixture.
/// </summary>
public class EfProjectionIdValidationTests
{
    // ── helpers ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers an async (declaration-only) EF projection inside a fresh Alberto module and
    /// returns an <see cref="Action"/> that can be passed to FluentAssertions.
    /// </summary>
    private static Action RegisterAsync(string processorId) =>
        () =>
        {
            var services = new ServiceCollection();
            services.AddAlberto("orders", m =>
            {
                m.WithInMemory();
                m.AddEfProjection<CounterEntity, EfTestDbContext>(
                    EfProjectionTestFixture.BuildDeclaration(processorId));
            });
        };

    /// <summary>
    /// Registers an async EF projection with an <c>afterCommit</c> callback and returns an
    /// <see cref="Action"/> that can be passed to FluentAssertions.
    /// </summary>
    private static Action RegisterWithAfterCommit(string processorId) =>
        () =>
        {
            var services = new ServiceCollection();
            services.AddAlberto("orders", m =>
            {
                m.WithInMemory();
                m.AddEfProjection<CounterEntity, EfTestDbContext>(
                    EfProjectionTestFixture.BuildDeclaration(processorId),
                    afterCommit: _ => (_, _) => Task.CompletedTask);
            });
        };

    /// <summary>
    /// Registers an inline EF projection and returns an <see cref="Action"/> that can be
    /// passed to FluentAssertions. The inline path skips <c>DeclareProcessor</c>, so validation
    /// must fire before the mode branch.
    /// </summary>
    private static Action RegisterInline(string processorId) =>
        () =>
        {
            var services = new ServiceCollection();
            services.AddAlberto("orders", m =>
            {
                m.WithInMemory();
                m.AddEfProjection<CounterEntity, EfTestDbContext>(
                    EfProjectionTestFixture.BuildDeclaration(processorId),
                    ProjectionMode.Inline);
            });
        };

    // ── separator characters ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("my:processor")]
    [InlineData("my#processor")]
    public void Async_overload_rejects_processor_id_containing_separator_chars(string processorId)
    {
        RegisterAsync(processorId).Should().Throw<ArgumentException>()
            .WithParameterName("processorId")
            .WithMessage($"*'{processorId}'*");
    }

    [Theory]
    [InlineData("my:processor")]
    [InlineData("my#processor")]
    public void AfterCommit_overload_rejects_processor_id_containing_separator_chars(string processorId)
    {
        RegisterWithAfterCommit(processorId).Should().Throw<ArgumentException>()
            .WithParameterName("processorId")
            .WithMessage($"*'{processorId}'*");
    }

    [Theory]
    [InlineData("my:processor")]
    [InlineData("my#processor")]
    public void Inline_overload_rejects_processor_id_containing_separator_chars(string processorId)
    {
        RegisterInline(processorId).Should().Throw<ArgumentException>()
            .WithParameterName("processorId")
            .WithMessage($"*'{processorId}'*");
    }

    // ── reserved words ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("consumer")]
    [InlineData("catalog")]
    [InlineData("tenant-raw")]
    public void Async_overload_rejects_reserved_processor_ids(string processorId)
    {
        RegisterAsync(processorId).Should().Throw<ArgumentException>()
            .WithParameterName("processorId")
            .WithMessage($"*'{processorId}'*reserved*");
    }

    [Theory]
    [InlineData("consumer")]
    [InlineData("catalog")]
    [InlineData("tenant-raw")]
    public void AfterCommit_overload_rejects_reserved_processor_ids(string processorId)
    {
        RegisterWithAfterCommit(processorId).Should().Throw<ArgumentException>()
            .WithParameterName("processorId")
            .WithMessage($"*'{processorId}'*reserved*");
    }

    [Theory]
    [InlineData("consumer")]
    [InlineData("catalog")]
    [InlineData("tenant-raw")]
    public void Inline_overload_rejects_reserved_processor_ids(string processorId)
    {
        RegisterInline(processorId).Should().Throw<ArgumentException>()
            .WithParameterName("processorId")
            .WithMessage($"*'{processorId}'*reserved*");
    }

    // ── valid ids are accepted ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("CounterProjection")]      // PascalCase — the common type-derived form
    [InlineData("orders.counter")]         // dotted — common with [ProcessorId] attribute
    [InlineData("counter-projection")]     // hyphenated — not ideal but structurally safe
    [InlineData("counter_projection")]     // underscored — satisfies even the strict rule
    public void Async_overload_accepts_valid_processor_ids(string processorId)
    {
        // The test is not asserting that the registration completes host startup (the full DI
        // graph is not wired), only that the validation guard does not reject a legitimate id.
        RegisterAsync(processorId).Should().NotThrow(
            $"'{processorId}' does not contain ':' or '#' and is not a reserved word");
    }

    [Theory]
    [InlineData("CounterProjection")]
    [InlineData("orders.counter")]
    [InlineData("counter-projection")]
    [InlineData("counter_projection")]
    public void AfterCommit_overload_accepts_valid_processor_ids(string processorId)
    {
        RegisterWithAfterCommit(processorId).Should().NotThrow(
            $"'{processorId}' does not contain ':' or '#' and is not a reserved word");
    }

    [Theory]
    [InlineData("CounterProjection")]
    [InlineData("orders.counter")]
    [InlineData("counter-projection")]
    [InlineData("counter_projection")]
    public void Inline_overload_accepts_valid_processor_ids(string processorId)
    {
        RegisterInline(processorId).Should().NotThrow(
            $"'{processorId}' does not contain ':' or '#' and is not a reserved word");
    }
}
