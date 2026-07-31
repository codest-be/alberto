using Alberto.Configuration;
using Alberto.Postgres;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Alberto.Tests.Postgres;

public sealed class SchemaQualifierTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Default_schema_uses_unqualified_sql_and_public_catalog_name(string? schema)
    {
        var qualifier = new SchemaQualifier(schema);

        qualifier.Name.Should().Be("public");
        qualifier.HasSchema.Should().BeFalse();
        qualifier.Table("alberto_events").Should().Be("alberto_events");
        qualifier.Function("alberto_append_events").Should().Be("alberto_append_events");
    }

    [Fact]
    public void Custom_schema_is_validated_once_and_quoted_for_every_identifier()
    {
        var qualifier = new SchemaQualifier("orders_v2");

        qualifier.Name.Should().Be("orders_v2");
        qualifier.HasSchema.Should().BeTrue();
        qualifier.Prefix.Should().Be("\"orders_v2\".");
        qualifier.Table("alberto_events").Should().Be("\"orders_v2\".alberto_events");
        qualifier.Function("alberto_append_events").Should().Be("\"orders_v2\".alberto_append_events");
    }

    [Fact]
    public void PostgreSql_maximum_identifier_length_is_accepted()
    {
        var schema = "a" + new string('0', 62);

        var qualifier = new SchemaQualifier(schema);

        qualifier.Name.Should().Be(schema);
    }

    [Theory]
    [InlineData("Orders")]
    [InlineData("orders-v2")]
    [InlineData("orders.v2")]
    [InlineData("orders\"v2")]
    [InlineData("public;drop table alberto_events;--")]
    [InlineData("1orders")]
    public void Unsafe_schema_is_rejected_before_an_adapter_can_open_a_connection(string schema)
    {
        using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused");

        var act = () => new PostgresAdminDataAccess(dataSource, schema);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("schema");
    }

    [Fact]
    public void Schema_longer_than_PostgreSql_limit_is_rejected()
    {
        var act = () => new SchemaQualifier("a" + new string('0', 63));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Invalid_schema_is_a_startup_validation_failure_even_when_auto_migrate_is_disabled()
    {
        var descriptor = new PostgresBackendDescriptor(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=alberto",
            AutoMigrate = false,
            Schema = "public;drop schema public;--",
        });
        var definition = new AlbertoModuleDefinition
        {
            ModuleKey = "orders",
            Backend = descriptor,
        };

        new AlbertoModuleValidator()
            .Collect(definition)
            .Should().ContainSingle(failure => failure.Code == "ALB1005");
    }
}
