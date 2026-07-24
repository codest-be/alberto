namespace Alberto.Dcb.Configuration;

/// <summary>
/// Controls Alberto's OpenTelemetry instrumentation for one module.
/// </summary>
public sealed record TelemetryOptions
{
    /// <summary>Whether tracing and metrics instrumentation is active. Default true.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Whether append spans carry the serialized payload size. Default true.</summary>
    public bool RecordEventPayloadSize { get; init; } = true;
}

/// <summary>Configuration mirror for <see cref="TelemetryOptions"/>.</summary>
public sealed class TelemetryOverrides : IAlbertoOverrides<TelemetryOptions>
{
    /// <summary>Mirror of <see cref="TelemetryOptions.Enabled"/>.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Mirror of <see cref="TelemetryOptions.RecordEventPayloadSize"/>.</summary>
    public bool? RecordEventPayloadSize { get; set; }

    /// <inheritdoc />
    public TelemetryOptions ApplyTo(TelemetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options with
        {
            Enabled = Enabled ?? options.Enabled,
            RecordEventPayloadSize = RecordEventPayloadSize ?? options.RecordEventPayloadSize,
        };
    }
}
