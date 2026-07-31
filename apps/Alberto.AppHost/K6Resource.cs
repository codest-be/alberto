using System.Diagnostics;

namespace Alberto.AppHost;

/// <summary>
/// Represents a K6 load testing resource.
/// </summary>
public class K6Resource(string name, string workingDirectory) : Resource(name), IResourceWithWaitSupport
{
    /// <summary>
    /// Gets the working directory where K6 tests are located.
    /// </summary>
    public string WorkingDirectory { get; } = workingDirectory;
}

/// <summary>
/// Extension methods for adding K6 load testing resources to the application model.
/// </summary>
public static class K6ResourceBuilderExtensions
{
    /// <summary>
    /// Adds a K6 load testing resource to the application model.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="workingDirectory">The directory containing the K6 test project.</param>
    /// <returns>A resource builder for the K6 resource.</returns>
    public static IResourceBuilder<K6Resource> AddK6(
        this IDistributedApplicationBuilder builder,
        string name,
        string workingDirectory)
    {
        var resource = new K6Resource(name, workingDirectory);

        return builder.AddResource(resource)
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "K6 Load Tests",
                State = new ResourceStateSnapshot("Idle", KnownResourceStateStyles.Info),
                Properties = [
                    new("Working Directory", workingDirectory),
                    new("Test Profiles", "smoke, load, stress, spike, consistency, throughput, throughput-burst, throughput-extended, throughput-rerun, throughput-burst-rerun, throughput-extended-rerun")
                ]
            })
            .WithCommand(
                name: "run-smoke",
                displayName: "Smoke Test",
                executeCommand: context => RunK6TestAsync(resource, "smoke", context),
                commandOptions: new CommandOptions
                {
                    IconName = "PlayCircle",
                    IconVariant = IconVariant.Filled
                })
            .WithCommand(
                name: "run-load",
                displayName: "Load Test",
                executeCommand: context => RunK6TestAsync(resource, "load", context),
                commandOptions: new CommandOptions
                {
                    IconName = "Play",
                    IconVariant = IconVariant.Filled
                })
            .WithCommand(
                name: "run-stress",
                displayName: "Stress Test",
                executeCommand: context => RunK6TestAsync(resource, "stress", context),
                commandOptions: new CommandOptions
                {
                    IconName = "FastForward",
                    IconVariant = IconVariant.Filled
                })
            .WithCommand(
                name: "run-spike",
                displayName: "Spike Test",
                executeCommand: context => RunK6TestAsync(resource, "spike", context),
                commandOptions: new CommandOptions
                {
                    IconName = "Flash",
                    IconVariant = IconVariant.Filled
                })
            .WithCommand(
                name: "run-consistency",
                displayName: "Consistency Test",
                executeCommand: context => RunK6TestAsync(resource, "consistency", context),
                commandOptions: new CommandOptions
                {
                    IconName = "Checkmark",
                    IconVariant = IconVariant.Filled
                })
            .WithCommand(
                name: "run-throughput",
                displayName: "Throughput Test",
                executeCommand: context => RunK6TestAsync(resource, "throughput", context),
                commandOptions: new CommandOptions
                {
                    IconName = "Gauge",
                    IconVariant = IconVariant.Filled
                })
            .WithCommand(
                name: "run-throughput-burst",
                displayName: "Throughput (Burst)",
                executeCommand: context => RunK6TestAsync(resource, "throughput-burst", context),
                commandOptions: new CommandOptions
                {
                    IconName = "Rocket",
                    IconVariant = IconVariant.Filled
                })
            .WithCommand(
                name: "run-throughput-extended",
                displayName: "Throughput (Extended)",
                executeCommand: context => RunK6TestAsync(resource, "throughput-extended", context),
                commandOptions: new CommandOptions
                {
                    IconName = "DataTrending",
                    IconVariant = IconVariant.Filled
                })
            .WithCommand(
                name: "run-throughput-rerun",
                displayName: "Throughput (Skip Seeding)",
                executeCommand: context => RunK6TestAsync(resource, "throughput-rerun", context),
                commandOptions: new CommandOptions
                {
                    IconName = "ArrowRepeatAll",
                    IconVariant = IconVariant.Filled
                })
            .WithCommand(
                name: "run-throughput-burst-rerun",
                displayName: "Throughput Burst (Skip Seeding)",
                executeCommand: context => RunK6TestAsync(resource, "throughput-burst-rerun", context),
                commandOptions: new CommandOptions
                {
                    IconName = "ArrowRepeatAll",
                    IconVariant = IconVariant.Filled
                })
            .WithCommand(
                name: "run-throughput-extended-rerun",
                displayName: "Throughput Extended (Skip Seeding)",
                executeCommand: context => RunK6TestAsync(resource, "throughput-extended-rerun", context),
                commandOptions: new CommandOptions
                {
                    IconName = "ArrowRepeatAll",
                    IconVariant = IconVariant.Filled
                });
    }

    private static async Task<ExecuteCommandResult> RunK6TestAsync(
        K6Resource resource,
        string testType,
        ExecuteCommandContext context)
    {
        try
        {
            // Build the project first (quick, wait for this)
            var buildProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "npm",
                    Arguments = "run build",
                    WorkingDirectory = resource.WorkingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            buildProcess.Start();

            // Use a longer timeout for build, ignore dashboard cancellation
            using var buildCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await buildProcess.WaitForExitAsync(buildCts.Token);

            if (buildProcess.ExitCode != 0)
            {
                var buildError = await buildProcess.StandardError.ReadToEndAsync();
                throw new InvalidOperationException($"Build failed: {buildError}");
            }

            // Determine the test script and args based on type
            var (testScript, extraArgs) = testType switch
            {
                "smoke" => ("dist/smoke.test.js", ""),
                "load" => ("dist/load.test.js", ""),
                "stress" => ("dist/stress.test.js", ""),
                "spike" => ("dist/spike.test.js", ""),
                "consistency" => ("dist/consistency.test.js", ""),
                "throughput" => ("dist/throughput.test.js", "--env SEED_WAIT_TIME=5m"),
                "throughput-burst" => ("dist/throughput.test.js", "--env SCENARIO=burst --env SEED_WAIT_TIME=5m"),
                "throughput-extended" => ("dist/throughput.test.js", "--env SCENARIO=extended --env SEED_WAIT_TIME=5m"),
                "throughput-rerun" => ("dist/throughput.test.js", "--env SKIP_SEEDING=true"),
                "throughput-burst-rerun" => ("dist/throughput.test.js", "--env SCENARIO=burst --env SKIP_SEEDING=true"),
                "throughput-extended-rerun" => ("dist/throughput.test.js", "--env SCENARIO=extended --env SKIP_SEEDING=true"),
                _ => ("dist/smoke.test.js", "")
            };

            // Run K6 in background - don't wait for completion
            // Load tests can run for minutes, we don't want to block the dashboard
            var k6Args = string.IsNullOrEmpty(extraArgs)
                ? $"run {testScript}"
                : $"run {extraArgs} {testScript}";

            var k6Process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "k6",
                    Arguments = k6Args,
                    WorkingDirectory = resource.WorkingDirectory,
                    UseShellExecute = true,  // Opens in new terminal window
                    CreateNoWindow = false   // Show the K6 output window
                }
            };

            k6Process.Start();

            // Return immediately - test is running in background
            return CommandResults.Success();
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Failed to run K6 test: {ex.Message}", ex);
        }
    }
}
