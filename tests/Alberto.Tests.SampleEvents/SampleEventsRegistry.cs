using System.Text.Json.Serialization;

namespace Alberto.Tests.SampleEvents;

// The generated counterpart of FromAssembly over this assembly, which the registry parity test
// compares the two of. Case-insensitive to match EventSerializer's default options.
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(VersionedOrder))]
[JsonSerializable(typeof(PlainNote))]
internal sealed partial class SampleEventsJsonContext : JsonSerializerContext;

[AlbertoEventRegistry(typeof(SampleEventsJsonContext))]
public static partial class SampleEventsRegistry;

/// <summary>What <see cref="SampleEvolver"/> folds.</summary>
public sealed record SampleState
{
    public int Notes { get; init; }
    public decimal Total { get; init; }
    public string? LastCountry { get; init; }
}

/// <summary>
/// Partial, so its dispatch table is generated; the evolver parity test folds it next to a
/// reflection-dispatched twin. One handler is explicit, which the table has to reach too.
/// </summary>
public partial class SampleEvolver : Evolver<SampleState>,
    IEvolve<SampleState, PlainNote>,
    IEvolve<SampleState, VersionedOrder>
{
    public SampleState Apply(SampleState state, PlainNote e) => state with { Notes = state.Notes + 1 };

    SampleState IEvolve<SampleState, VersionedOrder>.Apply(SampleState state, VersionedOrder e)
        => state with { Total = state.Total + e.Amount, LastCountry = e.Country };
}
