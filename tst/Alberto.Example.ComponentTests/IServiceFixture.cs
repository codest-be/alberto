using Microsoft.AspNetCore.TestHost;

namespace Alberto.Example.ComponentTests;

public interface IServiceFixture
{
    IServiceProvider Services { get; }

    TestServer Server { get; }
}