using System.Text.RegularExpressions;
using Alberto;
using Alberto.InMemory;
using Alberto.Tenancy;
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
/// word that came from <c>ProcessorId.For&lt;T&gt;()</c>, so the error message directs the user to
/// add <c>[ProcessorId(...)]</c> to the type rather than change a string they explicitly typed.
/// </summary>
// CS8981 fires because the name is all-lowercase ascii, which is exactly the property under
// test: the id has to collide with a reserved word, and it is derived from this type's name.
// Renaming the type to satisfy the warning would delete the test.
#pragma warning disable CS8981
internal sealed class catalog
#pragma warning restore CS8981
{
    public Task HandleAsync(IdentifierProbeEvent e, CancellationToken cancellationToken) => Task.CompletedTask;
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
        Assert.True(IdentifierRules.IsValidIdentifier(value), $"'{value}' satisfies ^[a-z][a-z0-9_]{{0,62}}$");
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
        Assert.False(IdentifierRules.IsValidIdentifier(value), $"'{value}' does not satisfy ^[a-z][a-z0-9_]{{0,62}}$");
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
        Assert.True(IdentifierRules.IsValidProcessorId(processorId), $"'{processorId}' does not contain ':' or '#' and is not a reserved word");
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
        Assert.False(IdentifierRules.IsValidProcessorId(processorId), $"'{processorId}' is null/whitespace, contains ':' or '#', or is a reserved word");
    }

    [Fact]
    public void Reserved_processor_ids_are_consumer_catalog_and_tenant_raw()
    {
        // The reserved set is the source of truth; this test pins it so a refactor that
        // accidentally adds or removes an entry fails immediately rather than silently.
        // these are the three suffixes Alberto composes as '{moduleKey}:{suffix}' for its own DI
        // registrations
        Assert.Equivalent(new[] { "consumer", "catalog", "tenant-raw" }, IdentifierRules.ReservedProcessorIds, strict: true);
    }

    // ── Module key validation at AddAlberto time ──────────────────────────────────────────

    [Fact]
    public void A_valid_module_key_is_accepted_by_AddAlberto()
    {
        var services = new ServiceCollection();
        var act = () => services.AddAlberto("orders", m => m.WithInMemory());

        // 'orders' satisfies the strict identifier rule
        act();
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

        var exception = Assert.ThrowsAny<ArgumentException>(act);
        Assert.Equal("moduleKey", exception.ParamName);
        Assert.Contains($"'{moduleKey}'", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_module_key_error_message_names_the_rejected_key_and_cites_the_rule()
    {
        var services = new ServiceCollection();
        var act = () => services.AddAlberto("My#Module", m => m.WithInMemory());

        // The error must (a) name the rejected key so the user can spot the typo, and
        // (b) cite the rule so the user knows what to change it to.
        var exception = Assert.ThrowsAny<ArgumentException>(act);
        // the error must name the rejected key verbatim
        Assert.Contains("'My#Module'", exception.Message);
        // the error must state the rule so the user knows what to change the key to
        Assert.Contains(IdentifierRules.Rule, exception.Message);
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

        // 'order_auditor' does not contain ':' or '#' and is not reserved
        act();
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

        var exception = Assert.ThrowsAny<ArgumentException>(act);
        Assert.Equal("processorId", exception.ParamName);
        Assert.Matches($"(?is)'{Regex.Escape(processorId)}'.*reserved", exception.Message);
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

        var exception = Assert.ThrowsAny<ArgumentException>(act);
        Assert.Equal("processorId", exception.ParamName);
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

        var exception = Assert.ThrowsAny<ArgumentException>(act);
        Assert.Equal("processorId", exception.ParamName);
        Assert.Contains("catalog", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(new[] { "ProcessorId", "[ProcessorId" }, fragment => exception.Message.Contains(fragment, StringComparison.Ordinal));
    }

    // ── TenantContext.IsValidTenantId delegates to IdentifierRules ────────────────────────

    [Theory]
    [InlineData("acme")]
    [InlineData("tenant_one")]
    [InlineData("t2")]
    public void Valid_tenant_ids_are_accepted(string tenantId)
    {
        Assert.True(TenantContext.IsValidTenantId(tenantId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ACME")]           // uppercase
    [InlineData("acme-corp")]      // hyphen
    [InlineData("tenant:one")]     // colon
    public void Invalid_tenant_ids_are_rejected(string? tenantId)
    {
        Assert.False(TenantContext.IsValidTenantId(tenantId));
    }

    [Fact]
    public void TenantContext_TenantIdRule_matches_IdentifierRules_Rule()
    {
        // TenantContext.TenantIdRule is the operator-facing wording for invalid tenant IDs
        // It must stay aligned with IdentifierRules.Rule so both
        // code paths tell the operator the same thing.
        // TenantIdRule is a const alias for IdentifierRules.Rule and must never diverge
        Assert.Equal(IdentifierRules.Rule, TenantContext.TenantIdRule);
    }
}
