namespace Alberto.Cli;

/// <summary>One target and the immutable facts captured before a mutation is confirmed.</summary>
internal sealed record PlannedTarget<TTarget, TPlan>(TTarget Target, TPlan Plan);

/// <summary>
/// Owns the ordering contract for a fail-closed fan-out mutation: probe every target, run one
/// gate over the complete plan, then attempt every planned write.
/// </summary>
internal static class PlannedMutation
{
    public static async Task<int> RunAsync<TTarget, TPlan>(
        IReadOnlyList<TTarget> targets,
        Func<TTarget, Task<TPlan>> probe,
        Action<TTarget, Exception> reportProbeFailure,
        Func<IReadOnlyList<PlannedTarget<TTarget, TPlan>>, int?> gate,
        Func<TTarget, TPlan, Task> apply,
        Action<TTarget, Exception> reportApplyFailure)
    {
        var plans = new List<PlannedTarget<TTarget, TPlan>>(targets.Count);
        var probeFailed = false;

        foreach (var target in targets)
        {
            try
            {
                plans.Add(new PlannedTarget<TTarget, TPlan>(target, await probe(target)));
            }
            catch (Exception ex)
            {
                reportProbeFailure(target, ex);
                probeFailed = true;
            }
        }

        if (probeFailed)
            return 1;

        if (gate(plans) is { } gateCode)
            return gateCode;

        var applyFailed = false;
        foreach (var plan in plans)
        {
            try
            {
                await apply(plan.Target, plan.Plan);
            }
            catch (Exception ex)
            {
                reportApplyFailure(plan.Target, ex);
                applyFailed = true;
            }
        }

        return applyFailed ? 1 : 0;
    }
}
