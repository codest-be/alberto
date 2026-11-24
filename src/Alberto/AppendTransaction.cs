using System.Data.Common;

namespace Alberto;

/// <summary>
/// The database connection and transaction an append runs in, handed to an
/// <see cref="Projections.ITransactionalInlineProjection"/> so its writes commit or roll back with the events.
/// </summary>
/// <remarks>
/// Both belong to the append: the projection must not commit, roll back, close or dispose either.
/// The append's per-tenant lock is held for as long as the transaction is open.
/// <para>
/// A backend with no database transaction, such as the in-memory backend, hands over <see cref="None"/>:
/// both are <see langword="null"/>. The projection then runs its work itself. The backend still holds its
/// append lock while it runs and publishes the events only afterwards, so the projection sees events in
/// log order and a throw aborts the append. It cannot, however, make its own writes roll back with the append.
/// </para>
/// </remarks>
public sealed class AppendTransaction
{
    /// <summary>The transaction of a backend that has none.</summary>
    public static AppendTransaction None { get; } = new(null, null);

    /// <param name="connection">The open connection the events were inserted on, or <see langword="null"/> when there is no database transaction.</param>
    /// <param name="transaction">The uncommitted transaction the events were inserted in, or <see langword="null"/> when there is none.</param>
    public AppendTransaction(DbConnection? connection, DbTransaction? transaction)
    {
        if (connection is null != transaction is null)
            throw new ArgumentException("A connection and its transaction are given together or not at all.");

        Connection = connection;
        Transaction = transaction;
    }

    /// <summary>The open connection the events were inserted on, or <see langword="null"/> when the backend has no database transaction.</summary>
    public DbConnection? Connection { get; }

    /// <summary>The transaction the events were inserted in, still uncommitted, or <see langword="null"/> when the backend has none.</summary>
    public DbTransaction? Transaction { get; }
}
