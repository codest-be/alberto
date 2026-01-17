using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Alberto.Dcb.Telemetry;

/// <summary>
/// Alias for backward compatibility. Use <see cref="AlbertoMetrics"/> directly.
/// </summary>
/// <remarks>
/// This class forwards all members to <see cref="AlbertoMetrics"/> in Alberto.Dcb.
/// </remarks>
public static class AlbertoTelemetry
{
    /// <inheritdoc cref="AlbertoMetrics.Name"/>
    public const string Name = AlbertoMetrics.Name;

    /// <inheritdoc cref="AlbertoMetrics.Version"/>
    public const string Version = AlbertoMetrics.Version;

    /// <inheritdoc cref="AlbertoMetrics.Source"/>
    public static ActivitySource Source => AlbertoMetrics.Source;

    /// <inheritdoc cref="AlbertoMetrics.Meter"/>
    public static Meter Meter => AlbertoMetrics.Meter;

    #region Counters

    /// <inheritdoc cref="AlbertoMetrics.EventsAppended"/>
    public static Counter<long> EventsAppended => AlbertoMetrics.EventsAppended;

    /// <inheritdoc cref="AlbertoMetrics.EventsProcessed"/>
    public static Counter<long> EventsProcessed => AlbertoMetrics.EventsProcessed;

    /// <inheritdoc cref="AlbertoMetrics.ProcessingErrors"/>
    public static Counter<long> ProcessingErrors => AlbertoMetrics.ProcessingErrors;

    /// <inheritdoc cref="AlbertoMetrics.DeadLetters"/>
    public static Counter<long> DeadLetters => AlbertoMetrics.DeadLetters;

    /// <inheritdoc cref="AlbertoMetrics.Retries"/>
    public static Counter<long> Retries => AlbertoMetrics.Retries;

    /// <inheritdoc cref="AlbertoMetrics.ConcurrencyConflicts"/>
    public static Counter<long> ConcurrencyConflicts => AlbertoMetrics.ConcurrencyConflicts;

    #endregion

    #region Histograms

    /// <inheritdoc cref="AlbertoMetrics.AppendDuration"/>
    public static Histogram<double> AppendDuration => AlbertoMetrics.AppendDuration;

    /// <inheritdoc cref="AlbertoMetrics.ProcessingDuration"/>
    public static Histogram<double> ProcessingDuration => AlbertoMetrics.ProcessingDuration;

    #endregion

    #region Activity Names

    /// <inheritdoc cref="AlbertoMetrics.AppendActivityName"/>
    public const string AppendActivityName = AlbertoMetrics.AppendActivityName;

    /// <inheritdoc cref="AlbertoMetrics.ConsumeActivityName"/>
    public const string ConsumeActivityName = AlbertoMetrics.ConsumeActivityName;

    /// <inheritdoc cref="AlbertoMetrics.ProcessActivityName"/>
    public const string ProcessActivityName = AlbertoMetrics.ProcessActivityName;

    #endregion
}
