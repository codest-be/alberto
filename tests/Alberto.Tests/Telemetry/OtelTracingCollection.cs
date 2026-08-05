using Xunit;

namespace Alberto.Tests.Telemetry;

// OTel's ActivityListener is process-wide: a TracerProvider that subscribes to
// AlbertoMetrics.Source will export every Alberto activity started anywhere in the
// process during its lifetime, including those started by tests in other classes running
// concurrently. TelemetryRegistrationTests asserts exact activity counts on such a
// TracerProvider, so it must not run at the same time as AppendSpanPrivacyTests, which
// starts real Alberto.Append activities via TelemetryAppendInterceptor.
// Placing both classes in this named collection serialises them without affecting the
// rest of the suite.
[CollectionDefinition(Name)]
public sealed class OtelTracingCollection
{
    public const string Name = "otel-tracing";
}
