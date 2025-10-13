using System.Diagnostics;
using Alberto.EventStore.Diagnostics;

namespace Alberto.EventStore.Telemetry;

internal static class AlbertoActivitySource
{
    /// <summary>
    ///     Gets the name of the activity source for this library.
    /// </summary>
    public static string Name => "Alberto";

    private static string Version { get; } =
        typeof(IDiagnosticsEventListener).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>
    ///     Gets the activity source for this library.
    /// </summary>
    public static ActivitySource Source { get; } = new(Name, Version);
}