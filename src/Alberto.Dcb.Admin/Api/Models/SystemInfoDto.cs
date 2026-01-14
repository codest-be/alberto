namespace Alberto.Dcb.Admin.Api.Models;

/// <summary>
/// System information for a module.
/// </summary>
public sealed record SystemInfoDto(
    string ModuleKey,
    long GlobalPosition,
    int ProcessorCount,
    int DeadLetterCount,
    bool ReadOnlyMode);
