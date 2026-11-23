using System.Data.Common;

namespace Alberto;

/// <summary>
/// The database connection and transaction an append runs in, handed to an
/// <see cref="Projections.ITransactionalInlineProjection"/> so its writes commit or roll back with the events.
/// </summary>
/// <remarks>
/// Both belong to the append: the projection must not commit, roll back, close or dispose either.
/// The append's per-tenant lock is held for as long as the transaction is open.
/// </remarks>
public sealed class AppendTransaction(DbConnection connection, DbTransaction transaction)
{
    /// <summary>The open connection the events were inserted on.</summary>
    public DbConnection Connection { get; } = connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>The transaction the events were inserted in, still uncommitted.</summary>
    public DbTransaction Transaction { get; } = transaction ?? throw new ArgumentNullException(nameof(transaction));
}
