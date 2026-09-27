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
