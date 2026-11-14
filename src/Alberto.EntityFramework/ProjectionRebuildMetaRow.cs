namespace Alberto.EntityFramework;

/// <summary>
/// Read-only EF mapping of the two columns of <c>alberto_projection_rebuild_meta</c> that
/// <see cref="ProjectionQueryFilters.ActiveVersion"/> needs. The table belongs to
/// <c>Alberto.Postgres</c>'s migrations, so the mapping is excluded from EF's.
/// </summary>
internal sealed class ProjectionRebuildMetaRow
{
    public string ProcessorId { get; set; } = string.Empty;

    public int ActiveVersion { get; set; }
}
