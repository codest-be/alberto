using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

public class ErrorClassifierTests
{
    private readonly IErrorClassifier _classifier = DefaultErrorClassifier.Instance;

    #region Transient Errors

    [Fact]
    public void Classify_TimeoutException_ShouldReturnTransient()
    {
        var ex = new TimeoutException("Operation timed out");

        var result = _classifier.Classify(ex);

        Assert.Equal(ErrorClassification.Transient, result);
    }

    [Fact]
    public void Classify_TaskCanceledException_NotRequested_ShouldReturnTransient()
    {
        // TaskCanceledException with non-cancelled token indicates timeout
        var ex = new TaskCanceledException("Operation cancelled", null, CancellationToken.None);

        var result = _classifier.Classify(ex);

        Assert.Equal(ErrorClassification.Transient, result);
    }

    [Fact]
    public void Classify_SocketException_ShouldReturnTransient()
    {
        var ex = new SocketException((int)SocketError.ConnectionRefused);

        var result = _classifier.Classify(ex);

        Assert.Equal(ErrorClassification.Transient, result);
    }

    [Theory]
    [InlineData("40001")] // serialization failure
    [InlineData("40P01")] // deadlock detected
    [InlineData("57P03")] // cannot connect now (server starting)
    [InlineData("08006")] // connection failure
    [InlineData("08001")] // unable to establish connection
    public void Classify_TransientDbException_ShouldReturnTransient(string sqlState)
    {
        var ex = new TestDbException(sqlState);

        var result = _classifier.Classify(ex);

        Assert.Equal(ErrorClassification.Transient, result);
    }

    [Fact]
    public void Classify_UniqueConstraintViolation_ShouldReturnTransient()
    {
        // 23505 = unique violation - transient because concurrent INSERT may have succeeded
        var ex = new TestDbException("23505");

        var result = _classifier.Classify(ex);

        Assert.Equal(ErrorClassification.Transient, result);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]       // 429
    [InlineData(HttpStatusCode.ServiceUnavailable)]   // 503
    [InlineData(HttpStatusCode.GatewayTimeout)]       // 504
    [InlineData(HttpStatusCode.BadGateway)]           // 502
    [InlineData(HttpStatusCode.RequestTimeout)]       // 408
    public void Classify_TransientHttpStatus_ShouldReturnTransient(HttpStatusCode statusCode)
    {
        var ex = new HttpRequestException("HTTP error", null, statusCode);

        var result = _classifier.Classify(ex);

        Assert.Equal(ErrorClassification.Transient, result);
    }

    #endregion

    #region Permanent Errors

    [Theory]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(ArgumentNullException))]
    [InlineData(typeof(ArgumentOutOfRangeException))]
    public void Classify_ArgumentExceptions_ShouldReturnPermanent(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType, "test")!;

        var result = _classifier.Classify(ex);

        Assert.Equal(ErrorClassification.Permanent, result);
    }

    [Fact]
    public void Classify_FormatException_ShouldReturnPermanent()
    {
        var ex = new FormatException("Invalid format");

        var result = _classifier.Classify(ex);

        Assert.Equal(ErrorClassification.Permanent, result);
    }

    [Fact]
    public void Classify_InvalidCastException_ShouldReturnPermanent()
    {
        var ex = new InvalidCastException("Cannot cast");

        var result = _classifier.Classify(ex);

        Assert.Equal(ErrorClassification.Permanent, result);
    }

    [Theory]
    [InlineData("23503")] // foreign key violation
    [InlineData("23514")] // check constraint violation
    public void Classify_ConstraintViolation_ShouldReturnPermanent(string sqlState)
    {
        var ex = new TestDbException(sqlState);

        var result = _classifier.Classify(ex);

        Assert.Equal(ErrorClassification.Permanent, result);
    }

    [Fact]
    public void Classify_NotSupportedException_ShouldReturnPermanent()
    {
        var ex = new NotSupportedException("Operation not supported");

        var result = _classifier.Classify(ex);

        Assert.Equal(ErrorClassification.Permanent, result);
    }

    [Fact]
    public void Classify_NotImplementedException_ShouldReturnPermanent()
    {
        var ex = new NotImplementedException("Not implemented");

        var result = _classifier.Classify(ex);

        Assert.Equal(ErrorClassification.Permanent, result);
    }

    [Fact]
    public void Classify_InvalidOperationException_ShouldReturnUnknown()
    {
        // InvalidOperationException is NOT Permanent: EF Core raises it on connection-pool
        // exhaustion and Npgsql raises it for several transient protocol states. Treating it as
        // Permanent would silently dead-letter every event processed during a load spike.
        // Unknown lets the standard retry-then-dead-letter policy run instead.
        var ex = new InvalidOperationException("Invalid operation");

        var result = _classifier.Classify(ex);

        Assert.Equal(ErrorClassification.Unknown, result);
    }

    [Fact]
    public void Classify_ConnectionPoolExhaustionMessage_ShouldReturnUnknown()
    {
        // EF Core surfaces connection-pool exhaustion as an InvalidOperationException with the
        // message "Timeout expired. The timeout period elapsed prior to obtaining a connection
        // from the pool". This must NOT be dead-lettered immediately — it should be retried.
        var ex = new InvalidOperationException(
            "Timeout expired. The timeout period elapsed prior to obtaining a connection from the pool.");

        var result = _classifier.Classify(ex);

        Assert.Equal(ErrorClassification.Unknown, result);
    }

    [Fact]
    public void Classify_FileNotFoundException_ShouldReturnPermanent()
    {
        var ex = new FileNotFoundException("File not found");

        var result = _classifier.Classify(ex);

        Assert.Equal(ErrorClassification.Permanent, result);
    }

    [Fact]
    public void Classify_DirectoryNotFoundException_ShouldReturnPermanent()
    {
        var ex = new DirectoryNotFoundException("Directory not found");

        var result = _classifier.Classify(ex);

        Assert.Equal(ErrorClassification.Permanent, result);
    }

    [Fact]
    public void Classify_UnauthorizedAccessException_ShouldReturnPermanent()
    {
        var ex = new UnauthorizedAccessException("Access denied");

        var result = _classifier.Classify(ex);

        Assert.Equal(ErrorClassification.Permanent, result);
    }

    #endregion

    #region Unknown Errors

    [Fact]
    public void Classify_GenericException_ShouldReturnUnknown()
    {
        var ex = new Exception("Generic error");

        var result = _classifier.Classify(ex);

        Assert.Equal(ErrorClassification.Unknown, result);
    }

    [Fact]
    public void Classify_CustomException_ShouldReturnUnknown()
    {
        var ex = new CustomTestException("Custom error");

        var result = _classifier.Classify(ex);

        Assert.Equal(ErrorClassification.Unknown, result);
    }

    [Fact]
    public void Classify_HttpException_WithOtherStatusCode_ShouldReturnUnknown()
    {
        var ex = new HttpRequestException("HTTP error", null, HttpStatusCode.NotFound);

        var result = _classifier.Classify(ex);

        Assert.Equal(ErrorClassification.Unknown, result);
    }

    [Fact]
    public void Classify_DbException_WithUnknownSqlState_ShouldReturnUnknown()
    {
        var ex = new TestDbException("99999");

        var result = _classifier.Classify(ex);

        Assert.Equal(ErrorClassification.Unknown, result);
    }

    #endregion

    #region AggregateException Unwrapping

    [Fact]
    public void Classify_AggregateException_WithSingleInner_ShouldUnwrap()
    {
        var inner = new TimeoutException("Timeout");
        var ex = new AggregateException(inner);

        var result = _classifier.Classify(ex);

        Assert.Equal(ErrorClassification.Transient, result);
    }

    [Fact]
    public void Classify_AggregateException_WithMultipleInner_ShouldCheckInnerRecursively()
    {
        var ex = new AggregateException(
            new TimeoutException("Timeout 1"),
            new TimeoutException("Timeout 2"));

        var result = _classifier.Classify(ex);

        // Multiple inner exceptions - doesn't unwrap but still checks InnerException recursively
        // AggregateException.InnerException returns the first inner, which is a TimeoutException
        Assert.Equal(ErrorClassification.Transient, result);
    }

    #endregion

    #region Inner Exception Check

    [Fact]
    public void Classify_WrappedException_ShouldCheckInner()
    {
        var inner = new TimeoutException("Timeout");
        var ex = new Exception("Wrapper", inner);

        var result = _classifier.Classify(ex);

        Assert.Equal(ErrorClassification.Transient, result);
    }

    [Fact]
    public void Classify_DeepNestedTransient_ShouldReturnTransient()
    {
        var timeout = new TimeoutException("Timeout");
        var level1 = new Exception("Level 1", timeout);
        var level2 = new Exception("Level 2", level1);

        var result = _classifier.Classify(level2);

        Assert.Equal(ErrorClassification.Transient, result);
    }

    #endregion

    #region Test Helpers

    private sealed class TestDbException(string sqlState) : DbException("Test DB error")
    {
        public override string? SqlState { get; } = sqlState;
    }

    private sealed class CustomTestException(string message) : Exception(message);

    #endregion
}
