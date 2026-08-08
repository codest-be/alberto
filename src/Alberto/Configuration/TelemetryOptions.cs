namespace Alberto.Configuration;

/// <summary>
/// Controls Alberto's OpenTelemetry instrumentation for one module.
/// </summary>
public sealed record TelemetryOptions
{
    /// <summary>Whether tracing and metrics instrumentation is active. Default true.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Whether append spans carry event tag <em>values</em> as well as their keys. Default false.
    /// </summary>
    /// <remarks>
    /// A DCB tag value is a domain identifier — an order id, a customer id, whatever the decision
    /// function scoped its consistency boundary to. Emitting those puts business identifiers into
    /// every trace an exporter forwards, so the default records only the tag keys, which is enough
    /// to see which boundary an append was checked against. Turn this on for a development
    /// environment, or where the collector is known to be inside the same trust boundary as the
    /// database.
    /// </remarks>
    public bool RecordEventTagValues { get; init; }
}

/// <summary>Configuration mirror for <see cref="TelemetryOptions"/>.</summary>
public sealed class TelemetryOverrides : IAlbertoOverrides<TelemetryOptions>
{
    /// <summary>Mirror of <see cref="TelemetryOptions.Enabled"/>.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Mirror of <see cref="TelemetryOptions.RecordEventTagValues"/>.</summary>
    public bool? RecordEventTagValues { get; set; }

    /// <inheritdoc />
    public TelemetryOptions ApplyTo(TelemetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options with
        {
            Enabled = Enabled ?? options.Enabled,
            RecordEventTagValues = RecordEventTagValues ?? options.RecordEventTagValues,
        };
    }
}
