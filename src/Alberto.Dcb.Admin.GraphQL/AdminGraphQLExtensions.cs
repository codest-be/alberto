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
    /// Requires <see cref="IAdminReader"/> and <see cref="IAdminOperator"/> to be registered
    /// in the DI container. Call <c>AddInMemorySubscriptions()</c> and <c>UseWebSockets()</c>
    /// to enable real-time subscriptions.
    /// </remarks>
    public static IRequestExecutorBuilder AddAlbertoAdminGraphQL(
        this IRequestExecutorBuilder builder) =>
        builder.AddAdminTypes();
}
