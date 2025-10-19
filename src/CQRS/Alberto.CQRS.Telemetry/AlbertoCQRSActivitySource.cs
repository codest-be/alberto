using System.Diagnostics;
using Alberto.CQRS.Diagnostics;

namespace Alberto.CQRS.Telemetry;

/// <summary>
/// Provides the ActivitySource for Alberto CQRS operations.
/// </summary>
public static class AlbertoCQRSActivitySource
{
    /// <summary>
    /// Gets the name of the activity source for this library.
    /// </summary>
    public static string Name => "Alberto.CQRS";

    private static string Version { get; } =
        typeof(IDiagnosticsEventListener).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>
    /// Gets the activity source for this library.
    /// </summary>
    public static ActivitySource Source { get; } = new(Name, Version);
}