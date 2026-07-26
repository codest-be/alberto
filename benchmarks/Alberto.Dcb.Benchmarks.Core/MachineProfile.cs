using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Alberto.Dcb.Benchmarks.Core;

/// <summary>
/// The hardware and runtime identity a benchmark result is keyed by.
///
/// Results are only ever compared within one profile. A laptop run diffed against a CI
/// baseline is worse than no trend line at all, because it looks like data — so
/// <see cref="ResultComparer"/> refuses to compare across differing <see cref="ProfileId"/>s.
/// </summary>
/// <param name="Label">
/// Environment label, from ALBERTO_BENCH_PROFILE_LABEL. "ci" on the runner, "local" otherwise.
/// It only makes directory names legible; the hash is what actually distinguishes machines.
/// </param>
/// <param name="Os">Operating system description, as the runtime reports it.</param>
/// <param name="Architecture">Process architecture — x64, Arm64.</param>
/// <param name="CpuModel">Processor model, normalized to collapse whitespace.</param>
/// <param name="LogicalCores">Logical processor count.</param>
/// <param name="TotalMemoryBytes">Total managed heap budget, standing in for installed memory.</param>
/// <param name="DotnetVersion">The .NET runtime version the benchmarks ran on.</param>
/// <param name="PostgresImage">Container image tag the event store was benchmarked against.</param>
/// <param name="ExternalPostgres">
/// True when the run used a Postgres pointed at by ALBERTO_BENCH_POSTGRES rather than a
/// container the harness started. An external server is not comparable to a containerized
/// one, so it takes part in the hash.
/// </param>
public sealed record MachineProfile(
    string Label,
    string Os,
    string Architecture,
    string CpuModel,
    int LogicalCores,
    long TotalMemoryBytes,
    string DotnetVersion,
    string PostgresImage,
    bool ExternalPostgres)
{
    /// <summary>
    /// Stable directory-safe identity: the label plus a hash of every hardware field.
    /// Any field changing produces a different id, which is the point — it forces a new
    /// baseline rather than silently comparing across machines.
    /// </summary>
    public string ProfileId
    {
        get
        {
            var canonical = string.Join(
                '|',
                Os,
                Architecture,
                CpuModel,
                LogicalCores.ToString(),
                TotalMemoryBytes.ToString(),
                DotnetVersion,
                PostgresImage,
                ExternalPostgres ? "external" : "container");

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            var suffix = Convert.ToHexString(hash, 0, 4).ToLowerInvariant();

            return $"{Slug(Label)}-{suffix}";
        }
    }

    /// <summary>Reads the profile of the machine this process is running on.</summary>
    public static MachineProfile Capture(string? postgresImage = null, bool externalPostgres = false) => new(
        Label: Environment.GetEnvironmentVariable("ALBERTO_BENCH_PROFILE_LABEL") ?? "local",
        Os: OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "macos"
            : OperatingSystem.IsWindows() ? "windows"
            : "unknown",
        Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
        CpuModel: NormalizeCpu(),
        LogicalCores: Environment.ProcessorCount,
        TotalMemoryBytes: GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        DotnetVersion: Environment.Version.ToString(),
        PostgresImage: postgresImage ?? "unknown",
        ExternalPostgres: externalPostgres);

    // RuntimeInformation exposes no CPU model, so this is the closest portable stand-in.
    // It is part of the hash, not display copy, so precision matters less than stability.
    private static string NormalizeCpu()
        => RuntimeInformation.ProcessArchitecture + "/" + RuntimeInformation.OSArchitecture;

    private static string Slug(string value)
    {
        var chars = value.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
            .ToArray();

        return new string(chars).Trim('-');
    }
}
