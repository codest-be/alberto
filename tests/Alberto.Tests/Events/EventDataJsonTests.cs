using Alberto.InMemory;
using Xunit;

namespace Alberto.Tests;

/// <summary>
/// The in-memory backend's payload normalization, which exists so that the double is not a more
/// permissive store than the PostgreSQL backend it stands in for. The expectations here were taken
/// from a real PostgreSQL 16 casting the same literals to <c>jsonb</c>.
/// </summary>
public class EventDataJsonTests
{
    [Fact]
    public void Insignificant_whitespace_is_dropped()
    {
        Assert.Equal("""{"a":1,"b":[1,2]}""", EventDataJson.Normalize("""{  "a" : 1 ,  "b" : [ 1, 2 ] }""", "e"));
    }

    [Fact]
    public void Object_keys_are_ordered_by_length_then_bytewise()
    {
        // jsonb: {"z": 1, "aa": 2, "amount": 100, "orderId": "abc"}
        Assert.Equal("""{"z":1,"aa":2,"amount":100,"orderId":"abc"}""", EventDataJson.Normalize("""{"orderId":"abc","amount":100,"z":1,"aa":2}""", "e"));
    }

    [Fact]
    public void Ordering_applies_at_every_level()
    {
        Assert.Equal("""{"o":{"a":{"x":2,"yy":1},"z":1},"arr":[{"a":2,"b":1}]}""", EventDataJson.Normalize("""{"o":{"z":1,"a":{"yy":1,"x":2}},"arr":[{"b":1,"a":2}]}""", "e"));
    }

    [Fact]
    public void Array_order_is_untouched()
    {
        Assert.Equal("[3,1,2]", EventDataJson.Normalize("[3, 1, 2]", "e"));
    }

    [Fact]
    public void Duplicate_keys_collapse_to_the_last_one()
    {
        Assert.Equal("""{"a":2}""", EventDataJson.Normalize("""{"a":1,"a":2}""", "e"));
    }

    [Fact]
    public void Keys_are_ordered_by_utf8_byte_length_not_by_char_count()
    {
        // "é" is one char but two UTF-8 bytes, so it sorts after the two-byte-and-shorter keys
        // and alongside "ab" — length 2 either way — where the byte comparison decides.
        Assert.Equal("""{"z":3,"ab":2,"é":1}""", EventDataJson.Normalize("""{"é":1,"ab":2,"z":3}""", "e"));
    }

    [Fact]
    public void Non_ascii_is_written_literally_rather_than_escaped()
    {
        // jsonb stores text, not escapes. The default System.Text.Json encoder would emit
        // é here, which would make the payload look different for no reason.
        Assert.Equal("""{"s":"café"}""", EventDataJson.Normalize("""{"s":"café"}""", "e"));
    }

    [Fact]
    public void Number_literals_are_left_alone()
    {
        // Deliberately NOT jsonb's numeric canonicalization, which would render this 1E+30 as
        // thirty zeros. See EventDataJson's remarks: near-miss emulation is worse than none.
        Assert.Equal("""{"a":1E+30,"b":1.500}""", EventDataJson.Normalize("""{"a":1E+30,"b":1.500}""", "e"));
    }

    [Fact]
    public void Malformed_json_is_rejected()
    {
        var act = () => EventDataJson.Normalize("""{"a": }""", "order-placed");

        var exception = Assert.ThrowsAny<ArgumentException>(act);
        Assert.Matches("(?is)order-placed.*not well-formed JSON", exception.Message);
    }

    [Fact]
    public void A_nul_escape_is_rejected()
    {
        // Valid JSON that PostgreSQL refuses at the jsonb cast, because U+0000 has no
        // representation in its text type. Accepting it here would mean an append that passes
        // every test and fails on the first real deployment.
        var act = () => EventDataJson.Normalize("{\"s\":\"a\\u0000b\"}", "order-placed");

        var exception = Assert.ThrowsAny<ArgumentException>(act);
        Assert.Matches("(?is)order-placed.*NUL", exception.Message);
    }

    [Fact]
    public void A_payload_that_is_already_canonical_is_returned_unchanged()
    {
        const string canonical = """{"z":1,"aa":2}""";

        Assert.Equal(canonical, EventDataJson.Normalize(canonical, "e"));
    }
}
