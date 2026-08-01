namespace Alberto.Benchmarks.Harness;

/// <summary>
/// How hard a benchmark drives its own measured path in [GlobalSetup], before BenchmarkDotNet
/// starts timing: <see cref="Invocations"/> times, or <see cref="Budget"/> of wall clock,
/// whichever comes first.
///
/// Load-bearing, not belt-and-braces. BenchmarkDotNet runs every case in its own process,
/// and every process seeds its own store, so a case parameterised at 1M events has pushed a
/// million rows through Npgsql before it measures anything while a 10k case has pushed ten
/// thousand. Without this warm-up the smallest store read as the *slowest* in the suite —
/// StreamAllFromZero measured 1687 / 962 / 780us across 10k / 100k / 1M, which is backwards,
/// because store size cannot make a LIMIT 500 read slower. It was a property of the harness,
/// not of Alberto.
///
/// BenchmarkDotNet's two warmup iterations cannot close that gap, and neither can a handful
/// of invocations here. What closes it is total round-trips before measurement, and it takes
/// far more than seems reasonable: at 30 the same case read 1334 / 1211 / 819us, and at 300
/// it still read 1290 / 738 / 685us — reduced, but with 10k stubbornly slowest. At 2000 it
/// reads 682 / 748 / 997us: 10k is now the *fastest* and latency rises with store size, which
/// is the only shape the data can justify. Allocations converge at the same point
/// (322.15 / 322.33 / 322.69KB, against a ~25KB spread when cold) and the whole thing matches
/// an independent measurement of all three sizes interleaved in one deeply warm process,
/// 672 / 680 / 770us. So 2000 is where the number stops moving, not where it looked best.
///
/// Warm the measured method and nothing else. Since each process measures exactly one method,
/// warming the class's other methods is not merely wasted time — it drives argument shapes
/// that will never be measured through the same connection, and PostgreSQL switches a
/// prepared statement to a value-agnostic generic plan after about five executions. A generic
/// plan serving three shapes is worse than the plan any one of them would have got. That is
/// not a theory: warming all three of AppendBenchmarks' methods cost the whole class ~40%
/// (SingleAppend 1010 -> 1435us, AppendWithConflictDetected 881 -> 1239us) while the
/// single-method append classes, doing the same volume of churn, were unaffected. Warming
/// only the measured method put all three back at or below their baseline. Hence one
/// [GlobalSetup(Target = ...)] per benchmark method rather than one setup warming the class.
/// </summary>
public static class Warmup
{
    public const int Invocations = 2000;

    /// <summary>
    /// Ceiling on warm-up time, because a flat invocation count is only affordable for cases
    /// that are cheap per invocation. Every read case reaches <see cref="Invocations"/> inside
    /// a couple of seconds and never sees this. BatchAppend at 1000 events costs ~1.8s per
    /// warm-up cycle — a thousand-row insert plus the cascading delete that has to undo it —
    /// so an uncapped count put a single case on course for an hour and would have made the
    /// suite unrunnable.
    ///
    /// Cutting that case short is safe, and not a compromise: the append family does not need
    /// warming at all. A control run with warm-up disabled reproduced every append case's
    /// baseline to within 3%, because appends are dominated by the round trip and fsync rather
    /// than by managed-code warmth. Reads are the only family the warm-up exists for, and the
    /// budget never binds on them.
    /// </summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);
}
