using System.Text.Json.Serialization;

namespace Alberto.Postgres;

/// <summary>
/// Source-generated JSON contracts for the shapes the Postgres packages write themselves — today
/// only the string-to-string metadata map on events, dead letters and outbox entries — so none of
/// them goes through reflection-based System.Text.Json.
/// </summary>
/// <remarks>
/// Internal, and shared with <c>Alberto.Messaging.Postgres</c> through <c>InternalsVisibleTo</c>.
/// Default options on purpose: the maps were written with <c>JsonSerializer</c>'s defaults, and a
/// string dictionary has no member names for any option to change.
/// </remarks>
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class AlbertoPostgresJsonContext : JsonSerializerContext;
