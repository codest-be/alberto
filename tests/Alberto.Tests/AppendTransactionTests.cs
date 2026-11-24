using Xunit;

namespace Alberto.Tests;

public sealed class AppendTransactionTests
{
    [Fact]
    public void None_HasNeitherAConnectionNorATransaction()
    {
        Assert.Null(AppendTransaction.None.Connection);
        Assert.Null(AppendTransaction.None.Transaction);
    }
}
