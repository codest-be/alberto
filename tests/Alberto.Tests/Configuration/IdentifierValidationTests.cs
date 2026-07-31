using Alberto;
using Alberto.InMemory;
using Alberto.Tenancy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

// Auxiliary types live at namespace level so ProcessorId.For<T>() sees the simple type name,
// matching how the rest of the Configuration test suite defines its handler/event types.
namespace Alberto.Tests.Configuration;

[EventType("identifier-probe-event")]
internal sealed record IdentifierProbeEvent(string Id) : IEvent;

/// <summary>
/// A handler whose type-derived processor id ("catalog") is a reserved word.
/// Naming it "catalog" exercises the code path where ValidateProcessorIdArg receives a reserved
/// word that came from ProcessorId.For<T>(), so the error message directs the user to add
/// [ProcessorId(...)] to the type rather than change a string they explicitly typed.
/// </summary>
internal sealed class catalog
{
    public Task HandleAsync(IdentifierProbeEvent e, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Covers identifier validation for module keys (strict: lowercase-only) and processor ids
/// (permissive: only ':' and '#' and reserved words are rejected). Both are validated at
/// registration time — AddAlberto for module keys, ReactTo/AddProjection for processor ids —
/// so a bad key surfaces immediately rather than at host startup or first request.
/// </summary>
public class IdentifierValidationTests
{
    // ── IdentifierRules.IsValidIdentifier ────────────────────────────────────────────────

    [Theory]
    [InlineData("orders")]
    [InlineData("payments")]
    [InlineData("orders2")]
    [InlineData("a")]
    [InlineData("a_b_c")]
    // 63-character string (the maximum allowed)
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Valid_identifiers_are_accepted(string value)
    {
        IdentifierRules.IsValidIdentifier(value).Should().BeTrue(
            "'{0}' satisfies ^[a-z][a-z0-9_]{{0,62}}$", value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Orders")]          // uppercase
    [InlineData("2orders")]         // digit start
    [InlineData("orders#shard")]    // '#' — shard-key separator
    [InlineData("orders:processor")] // ':' — reader-store-factory key separator
    [InlineData("orders-v1")]       // hyphen
    [InlineData("orders.v1")]       // dot
    // 64-character string (one over the limit)
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Invalid_identifiers_are_rejected(string? value)
    {
        IdentifierRules.IsValidIdentifier(value).Should().BeFalse(
            "'{0}' does not satisfy ^[a-z][a-z0-9_]{{0,62}}$", value);
    }

    // ── IdentifierRules.IsValidProcessorId ────────────────────────────────────────────────

    [Theory]
    // Lowercase (also passes the strict rule)
    [InlineData("my_reactor")]
    [InlineData("order_summary")]
    // PascalCase — the common type-derived form
    [InlineData("OrderSummaryProjection")]
    [InlineData("ShipmentNotifier")]
    // Dotted names — common with [ProcessorId] attribute
    [InlineData("orders.summary")]
    [InlineData("payments.legacy")]
    // Hyphenated — not great, but not structurally dangerous
    [InlineData("order-summary")]
    public void Valid_processor_ids_are_accepted(string processorId)
    {
        IdentifierRules.IsValidProcessorId(processorId).Should().BeTrue(
            "'{0}' does not contain ':' or '#' and is not a reserved word", processorId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("orders:processor")] // ':' creates ambiguity in "{moduleKey}:{processorId}"
    [InlineData("orders#shard")]     // '#' creates ambiguity with "{moduleKey}#{shardId}"
    [InlineData("consumer")]         // shadows {moduleKey}:consumer — the backend DI key
    [InlineData("catalog")]          // shadows {moduleKey}:catalog — the catalog data source key
    [InlineData("tenant-raw")]       // shadows {moduleKey}:tenant-raw — the raw tenant backend key
    public void Invalid_processor_ids_are_rejected(string? processorId)
    {
        IdentifierRules.IsValidProcessorId(processorId).Should().BeFalse(
            "'{0}' is null/whitespace, contains ':' or '#', or is a reserved word", processorId);
    }

    [Fact]
    public void Reserved_processor_ids_are_consumer_catalog_and_tenant_raw()
    {
        // The reserved set is the source of truth; this test pins it so a refactor that
        // accidentally adds or removes an entry fails immediately rather than silently.
        IdentifierRules.ReservedProcessorIds.Should().BeEquivalentTo(
            new[] { "consumer", "catalog", "tenant-raw" },
            "these are the three suffixes Alberto composes as '{moduleKey}:{suffix}' for its own DI registrations");
    }

    // ── Module key validation at AddAlberto time ──────────────────────────────────────────

    [Fact]
    public void A_valid_module_key_is_accepted_by_AddAlberto()
    {
        var services = new ServiceCollection();
        var act = () => services.AddAlberto("orders", m => m.WithInMemory());

        act.Should().NotThrow("'orders' satisfies the strict identifier rule");
    }

    [Theory]
    [InlineData("Orders")]           // uppercase start
    [InlineData("orders#main")]      // '#' — breaks ShardKey.TryParse
    [InlineData("orders:events")]    // ':' — breaks the reader factory key
    [InlineData("2fast")]            // digit start
    [InlineData("orders-v1")]        // hyphen
    public void An_invalid_module_key_throws_ArgumentException_at_AddAlberto(string moduleKey)
    {
        var services = new ServiceCollection();
        var act = () => services.AddAlberto(moduleKey, m => m.WithInMemory());

        act.Should().Throw<ArgumentException>()
            .WithParameterName("moduleKey")
            .WithMessage($"*'{moduleKey}'*");
    }

    [Fact]
    public void The_module_key_error_message_names_the_rejected_key_and_cites_the_rule()
    {
        var services = new ServiceCollection();
        var act = () => services.AddAlberto("My#Module", m => m.WithInMemory());

        // The error must (a) name the rejected key so the user can spot the typo, and
        // (b) cite the rule so the user knows what to change it to.
        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.Message.Should().Contain("'My#Module'",
            "the error must name the rejected key verbatim");
        exception.Message.Should().Contain(IdentifierRules.Rule,
            "the error must state the rule so the user knows what to change the key to");
    }

    // ── Processor id validation at ReactTo time ───────────────────────────────────────────

    [Fact]
    public void A_valid_processor_id_is_accepted_by_ReactTo()
    {
        var services = new ServiceCollection();
        var act = () => services.AddAlberto("orders", m =>
        {
            m.WithInMemory();
            m.ReactTo<IdentifierProbeEvent>(
                _ => (_, _) => Task.CompletedTask,
                "order_auditor");
        });

        act.Should().NotThrow("'order_auditor' does not contain ':' or '#' and is not reserved");
    }

    [Theory]
    [InlineData("consumer")]  // shadows {moduleKey}:consumer
    [InlineData("catalog")]   // shadows {moduleKey}:catalog
    public void A_reserved_processor_id_throws_ArgumentException_at_ReactTo(string processorId)
    {
        var services = new ServiceCollection();
        var act = () => services.AddAlberto("orders", m =>
        {
            m.WithInMemory();
            m.ReactTo<IdentifierProbeEvent>(
                _ => (_, _) => Task.CompletedTask,
                processorId);
        });

        act.Should().Throw<ArgumentException>()
            .WithParameterName("processorId")
            .WithMessage($"*'{processorId}'*reserved*");
    }

    [Theory]
    [InlineData("my:processor")]  // ':' creates ambiguity in "{moduleKey}:{processorId}"
    [InlineData("my#sub")]        // '#' creates ambiguity with "{moduleKey}#{shardId}"
    public void A_processor_id_containing_separator_chars_throws_ArgumentException_at_ReactTo(
        string processorId)
    {
        var services = new ServiceCollection();
        var act = () => services.AddAlberto("orders", m =>
        {
            m.WithInMemory();
            m.ReactTo<IdentifierProbeEvent>(
                _ => (_, _) => Task.CompletedTask,
                processorId);
        });

        act.Should().Throw<ArgumentException>()
            .WithParameterName("processorId");
    }

    [Fact]
    public void A_type_derived_processor_id_that_is_reserved_names_the_handler_type_in_the_error()
    {
        // The handler is named "catalog", so ProcessorId.For<catalog>() returns "catalog",
        // which is a reserved word. The error message should direct the user to add
        // [ProcessorId("valid_id")] to the handler type rather than giving a generic message.
        var services = new ServiceCollection();
        var act = () => services.AddAlberto("orders", m =>
        {
            m.WithInMemory();
            m.ReactTo<IdentifierProbeEvent, catalog>(h => h.HandleAsync);
        });

        act.Should().Throw<ArgumentException>()
            .WithParameterName("processorId")
            .WithMessage("*catalog*")
            .And.Message.Should().ContainAny(
                "ProcessorId",
                "[ProcessorId");
    }

    // ── TenantContext.IsValidTenantId delegates to IdentifierRules ────────────────────────

    [Theory]
    [InlineData("acme")]
    [InlineData("tenant_one")]
    [InlineData("t2")]
    public void Valid_tenant_ids_are_accepted(string tenantId)
    {
        TenantContext.IsValidTenantId(tenantId).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ACME")]           // uppercase
    [InlineData("acme-corp")]      // hyphen
    [InlineData("tenant:one")]     // colon
    public void Invalid_tenant_ids_are_rejected(string? tenantId)
    {
        TenantContext.IsValidTenantId(tenantId).Should().BeFalse();
    }

    [Fact]
    public void TenantContext_TenantIdRule_matches_IdentifierRules_Rule()
    {
        // TenantContext.TenantIdRule is used in the CLI's error message for invalid tenant IDs
        // in 'alberto shards assign'. It must stay aligned with IdentifierRules.Rule so both
        // code paths tell the operator the same thing.
        TenantContext.TenantIdRule.Should().Be(IdentifierRules.Rule,
            "TenantIdRule is a const alias for IdentifierRules.Rule and must never diverge");
    }
}
