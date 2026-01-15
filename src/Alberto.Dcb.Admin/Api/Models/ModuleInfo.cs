namespace Alberto.Dcb.Admin.Api.Models;

/// <summary>
/// Information about a registered admin module.
/// </summary>
public sealed record ModuleInfo(
    string ModuleKey,
    string Title,
    bool ReadOnly);
