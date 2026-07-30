using HotChocolate.Execution.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb.Admin.GraphQL;

/// <summary>
/// Extension methods to register Alberto admin GraphQL types with a HotChocolate server.
/// </summary>
public static class AdminGraphQLExtensions
{
    /// <summary>
    /// Adds Alberto admin queries, mutations, and subscriptions to the GraphQL server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Requires <see cref="IAdminReader"/> and <see cref="IAdminOperator"/> to be registered in
    /// the DI container — <c>AddAlbertoPostgresAdmin</c> does that. Subscriptions additionally
    /// need <c>UseWebSockets()</c> on the app and a subscription provider on the executor.
    /// </para>
    /// <para>
    /// <c>AddInMemorySubscriptions()</c> is enough for a single instance and wrong for more than
    /// one: a mutation handled by one replica has to reach a subscriber connected to another,
    /// which an in-process topic cannot do, and the failure is silent — the console simply stops
    /// updating for some users. Behind a load balancer use a real backplane, such as
    /// <c>AddPostgresSubscriptions()</c> from <c>HotChocolate.Subscriptions.Postgres</c>.
    /// </para>
    /// </remarks>
    public static IRequestExecutorBuilder AddAlbertoAdminGraphQL(
        this IRequestExecutorBuilder builder) =>
        builder.AddAdminTypes();
}
