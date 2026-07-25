using System.Text;

namespace Alberto.Dcb.Configuration;

/// <summary>
/// One thing wrong with a module's configuration, stated as a problem plus the specific edit
/// that fixes it.
/// </summary>
/// <param name="Code">Stable identifier, for example <c>ALB0001</c>. Safe to grep for and to link docs from.</param>
/// <param name="Problem">What is wrong, in one sentence, naming the offending value.</param>
/// <param name="Remedy">The concrete change to make, naming the method or configuration key.</param>
public sealed record AlbertoValidationFailure(string Code, string Problem, string Remedy)
{
    /// <summary>Renders this failure as two indented lines.</summary>
    public string Format() =>
        $"[{Code}] {Problem}{Environment.NewLine}          → {Remedy}";
}

/// <summary>
/// Renders a module's validation failures into the message attached to the
/// <see cref="Microsoft.Extensions.Options.OptionsValidationException"/> thrown at startup.
/// </summary>
public static class AlbertoValidationReport
{
    /// <summary>
    /// Describes every failure for one module. The message is what a developer sees in the
    /// console when the host refuses to start, so it names the module, counts the problems,
    /// and closes with where else these settings can come from.
    /// </summary>
    public static string Describe(string moduleKey, IReadOnlyList<AlbertoValidationFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(moduleKey);
        ArgumentNullException.ThrowIfNull(failures);

        var builder = new StringBuilder();
        var noun = failures.Count == 1 ? "problem" : "problems";

        builder.Append($"Alberto module '{moduleKey}' cannot start: {failures.Count} configuration {noun}.");
        builder.AppendLine();

        foreach (var failure in failures)
        {
            builder.AppendLine();
            builder.AppendLine("  " + failure.Format());
        }

        builder.AppendLine();
        builder.Append(
            $"Settings can also be supplied under 'Alberto:Modules:{moduleKey}' in configuration.");

        return builder.ToString();
    }
}
