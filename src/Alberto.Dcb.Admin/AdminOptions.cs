namespace Alberto.Dcb.Admin;

/// <summary>
/// Configuration options for the DCB Admin panel.
/// </summary>
public sealed class AdminOptions
{
    /// <summary>
    /// Base path for admin endpoints. Default: "/alberto"
    /// </summary>
    public string BasePath { get; set; } = "/alberto";

    /// <summary>
    /// Enable read-only mode (disables rebuild/clear operations).
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Enable the admin UI. Default: true
    /// </summary>
    public bool EnableUI { get; set; } = true;

    /// <summary>
    /// Title displayed in the admin UI.
    /// </summary>
    public string Title { get; set; } = "DCB Admin";

    /// <summary>
    /// Authorization policy name (null = no authorization).
    /// </summary>
    public string? AuthorizationPolicy { get; set; }
}
