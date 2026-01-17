using System.Data.Common;
using System.Net;
using System.Net.Sockets;

namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Default implementation of <see cref="IErrorClassifier"/> that classifies common exceptions.
/// </summary>
internal sealed class DefaultErrorClassifier : IErrorClassifier
{
    /// <summary>
    /// Singleton instance of the default error classifier.
    /// </summary>
    public static DefaultErrorClassifier Instance { get; } = new();

    /// <inheritdoc/>
    public ErrorClassification Classify(Exception exception)
    {
        // Unwrap AggregateException to get the inner exception
        var ex = exception is AggregateException agg && agg.InnerExceptions.Count == 1
            ? agg.InnerException!
            : exception;

        return ClassifyCore(ex);
    }

    private static ErrorClassification ClassifyCore(Exception ex)
    {
        // Transient: Timeouts
        if (ex is TimeoutException or TaskCanceledException { CancellationToken.IsCancellationRequested: false })
            return ErrorClassification.Transient;

        // Transient: Network/socket errors
        if (ex is SocketException)
            return ErrorClassification.Transient;

        // Transient: Temporary database issues
        if (ex is DbException dbEx)
        {
            // Check for common transient SQL states
            // 40001 = serialization failure
            // 40P01 = deadlock detected
            // 57P03 = cannot connect now (server starting)
            // 08006 = connection failure
            // 08001 = unable to establish connection
            if (dbEx.SqlState is "40001" or "40P01" or "57P03" or "08006" or "08001")
                return ErrorClassification.Transient;
        }

        // Transient: HTTP status codes that indicate temporary issues
        if (ex is HttpRequestException httpEx && httpEx.StatusCode.HasValue)
        {
            var status = httpEx.StatusCode.Value;
            if (status is HttpStatusCode.TooManyRequests or
                HttpStatusCode.ServiceUnavailable or
                HttpStatusCode.GatewayTimeout or
                HttpStatusCode.BadGateway or
                HttpStatusCode.RequestTimeout)
            {
                return ErrorClassification.Transient;
            }
        }

        // Permanent: Argument/validation errors
        if (ex is ArgumentException or ArgumentNullException or ArgumentOutOfRangeException)
            return ErrorClassification.Permanent;

        // Permanent: Format/serialization errors
        if (ex is FormatException or InvalidCastException)
            return ErrorClassification.Permanent;

        // Transient: Unique constraint violations (concurrent INSERTs - retry will find existing row)
        if (ex is DbException dbUniqueEx && dbUniqueEx.SqlState is "23505")
            return ErrorClassification.Transient;

        // Permanent: Foreign key and check constraint violations (these won't succeed on retry)
        if (ex is DbException dbConstraintEx)
        {
            // 23503 = foreign key violation
            // 23514 = check constraint violation
            if (dbConstraintEx.SqlState is "23503" or "23514")
                return ErrorClassification.Permanent;
        }

        // Permanent: Not supported operations
        if (ex is NotSupportedException or NotImplementedException)
            return ErrorClassification.Permanent;

        // Permanent: Invalid operation (usually programmer error)
        if (ex is InvalidOperationException)
            return ErrorClassification.Permanent;

        // Permanent: File not found (resource doesn't exist)
        if (ex is FileNotFoundException or DirectoryNotFoundException)
            return ErrorClassification.Permanent;

        // Permanent: Access denied (permissions won't change between retries)
        if (ex is UnauthorizedAccessException)
            return ErrorClassification.Permanent;

        // Check inner exception if we haven't classified yet
        if (ex.InnerException is not null)
        {
            var innerClassification = ClassifyCore(ex.InnerException);
            if (innerClassification != ErrorClassification.Unknown)
                return innerClassification;
        }

        // Unknown: Let default retry logic handle it
        return ErrorClassification.Unknown;
    }
}
