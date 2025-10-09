using Microsoft.AspNetCore.TestHost;

namespace Alberto.ComponentTests;

public interface IServiceFixture
{
    IServiceProvider Services { get; }

    TestServer Server { get; }
}