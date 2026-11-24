using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Alberto.Tests.SampleEvents;
using Alberto.Upcasting;
using Xunit;

namespace Alberto.Tests;

/// <summary>What <see cref="DeclareUpcaster"/> refuses, before and at <c>Build()</c>.</summary>
public sealed class DeclareUpcasterValidationTests
{
    private const string Id = "e2e-versioned-order";

    private static readonly JsonTypeInfo<VersionedOrderV1> V1Shape = (JsonTypeInfo<VersionedOrderV1>)new JsonSerializerOptions
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    }.GetTypeInfo(typeof(VersionedOrderV1));

    private static VersionedOrder Upcast(VersionedOrderV1 v1) => new(v1.OrderId, v1.Amount, "BE");

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void An_event_type_id_is_required(string id)
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() => DeclareUpcaster.For<VersionedOrder>(id));
        Assert.Equal("eventTypeId", exception.ParamName);
    }

    public static TheoryData<string> Overloads => ["final", "intermediate", "final+shape", "intermediate+shape"];

    [Theory]
    [MemberData(nameof(Overloads))]
    public void Every_overload_refuses_a_source_version_below_one(string overload)
    {
        var builder = DeclareUpcaster.For<VersionedOrder>(Id);
        Action from = overload switch
        {
            "final" => () => builder.From<VersionedOrderV1>(0, Upcast),
            "intermediate" => () => builder.From<VersionedOrderV1, VersionedOrder>(0, Upcast),
            "final+shape" => () => builder.From<VersionedOrderV1>(0, V1Shape, Upcast),
            _ => () => builder.From<VersionedOrderV1, VersionedOrder>(0, V1Shape, Upcast),
        };

        var exception = Assert.ThrowsAny<ArgumentOutOfRangeException>(from);
        Assert.Equal("fromVersion", exception.ParamName);
    }

    [Fact]
    public void Build_names_only_the_duplicated_versions()
    {
        var builder = DeclareUpcaster.For<VersionedOrder>(Id)
            .From<VersionedOrderV1, VersionedOrderV1>(1, v => v)
            .From<VersionedOrderV1, VersionedOrderV1>(2, v => v)
            .From<VersionedOrderV1>(3, Upcast)
            .From<VersionedOrderV1>(3, Upcast);

        var exception = Assert.ThrowsAny<InvalidOperationException>(() => builder.Build());
        Assert.Contains("duplicate steps for version(s) 3. ", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_names_the_missing_version_of_a_gap()
    {
        var builder = DeclareUpcaster.For<VersionedOrder>(Id)
            .From<VersionedOrderV1, VersionedOrderV1>(1, v => v)
            .From<VersionedOrderV1>(3, Upcast);

        var exception = Assert.ThrowsAny<InvalidOperationException>(() => builder.Build());
        Assert.EndsWith("between versions 1 and 3. Add a step for source version 2.", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_current_version_follows_the_last_step()
    {
        Assert.Equal(3, DeclareUpcaster.For<VersionedOrder>(Id)
            .From<VersionedOrderV1, VersionedOrderV1>(1, v => v)
            .From<VersionedOrderV1>(2, Upcast)
            .Build().CurrentVersion);
    }
}
