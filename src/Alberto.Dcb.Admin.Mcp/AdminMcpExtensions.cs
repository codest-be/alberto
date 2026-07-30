using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb.Admin.Mcp;

/// <summary>
/// Extension methods to register the Alberto admin MCP surface with an ASP.NET Core host.
/// </summary>
public static class AdminMcpExtensions
{
    private const string ServerInstructions = """
        This server exposes the operator surface of an Alberto DCB event store — an append-only
        event log with Dynamic Consistency Boundaries, where tags are first-class filters rather
        than a stream name.

        What you can inspect: the log itself (alberto_list_events), the processors that consume it
        and how far each has got (alberto_list_processors, alberto_list_checkpoints), the read
        models they maintain (alberto_list_projection_states), events that failed and were parked
        (alberto_list_dead_letters), and tenant lease distribution on multi-tenant stores.

        Diagnosis usually starts at alberto_system_info, then narrows to whichever processor is
        lagging or dead-lettering.

        What you can change: checkpoints, dead letters, tenant leases and projection rebuilds.
        These are real operator actions against a live store, not a sandbox. Tools whose
        description begins with DESTRUCTIVE either drop data permanently or cause a processor to
        re-run side effects; read the description before calling one, and prefer
        alberto_retry_by_rewind over clearing dead letters and alberto_start_rebuild over resetting
        a checkpoint. Always pass operatorId — mutations are attributed to it, and it defaults to a
        generic "mcp" otherwise. Attribution is durable: every mutation appends an admin-* event to
        the log in the same transaction as the change, so you can read your own actions back with
        alberto_list_events, filtering by type — admin-checkpoint-reset, admin-dead-letters-cleared,
        admin-rebuild-started, and so on.

        Two invariants worth holding onto: global positions are per-database, so a position from
        one shard means nothing against another; and a store's single-tenant or multi-tenant mode
        is fixed at migration time and cannot be changed.
        """;

    /// <summary>
    /// Registers the Alberto admin MCP server and its tools.
    /// </summary>
    /// <remarks>
    /// Requires <see cref="IAdminReader"/> and <see cref="IAdminOperator"/> to be registered in
    /// the DI container — the same two registrations the GraphQL admin surface consumes. The
    /// transport is stateless Streamable HTTP, so the server holds no per-session state and can be
    /// scaled or redeployed without draining connections.
    /// </remarks>
    public static IServiceCollection AddAlbertoAdminMcp(this IServiceCollection services)
    {
        services
            .AddMcpServer(options => options.ServerInstructions = ServerInstructions)
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools<SystemTools>()
            .WithTools<CheckpointTools>()
            .WithTools<DeadLetterTools>()
            .WithTools<EventTools>()
            .WithTools<ProjectionTools>()
            .WithTools<TenantTools>()
            .WithTools<RebuildTools>();

        return services;
    }

    /// <summary>
    /// Maps the Alberto admin MCP Streamable HTTP endpoint.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="pattern">Route prefix to mount the MCP endpoint at. Defaults to <c>/mcp</c>.</param>
    public static IEndpointConventionBuilder MapAlbertoAdminMcp(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/mcp") =>
        endpoints.MapMcp(pattern);
}
