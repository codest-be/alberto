using System.Net;
using Alberto.TestInfrastructure;
using Docker.DotNet;
using FluentAssertions;
using Xunit;

namespace Alberto.Tests.Infrastructure;

/// <summary>
/// Specifies the retry policy in <see cref="ContainerStartup"/>: which startup failures buy
/// another attempt, and which do not.
/// </summary>
/// <remarks>
/// No Docker here, deliberately. The policy is driven through
/// <see cref="ContainerStartup.StartNewCoreAsync{T}"/>, which takes the build/start/dispose
/// steps as delegates — the point of these tests is that a repository-wide backstop against a
/// host-level flake is itself specified, rather than being code nobody can prove still works.
/// </remarks>
public sealed class ContainerStartupTests
{
    /// <summary>The rootless failure from CI run 31059592626, trimmed to its shape.</summary>
    private static DockerApiException PortCollision() =>
        new(HttpStatusCode.InternalServerError,
            """{"message":"failed to set up container networking: driver failed programming """ +
            """external connectivity on endpoint priceless_blackwell: error while calling """ +
            """RootlessKit PortManager.AddPort(): listen tcp4 0.0.0.0:51204: bind: address """ +
            """already in use"}""");

    /// <summary>The same condition as a rootful daemon words it.</summary>
    private static DockerApiException PortAlreadyAllocated() =>
        new(HttpStatusCode.InternalServerError,
            """{"message":"driver failed programming external connectivity on endpoint: """ +
            """Bind for 0.0.0.0:51204 failed: port is already allocated"}""");

    [Fact]
    public async Task Start_ThatSucceedsFirstTime_BuildsExactlyOneSubject()
    {
        var built = 0;

        var result = await ContainerStartup.StartNewCoreAsync(
            build: () => ++built,
            start: (_, _) => Task.CompletedTask,
            dispose: _ => Task.CompletedTask,
            retryDelay: TimeSpan.Zero,
            TestContext.Current.CancellationToken);

        result.Should().Be(1);
        built.Should().Be(1, because: "a start that works must not build a second container");
    }

    [Fact]
    public async Task PortCollision_IsRetried_WithAFreshSubjectEachTime()
    {
        var built = 0;
        var attempts = 0;

        var result = await ContainerStartup.StartNewCoreAsync(
            build: () => ++built,
            start: (_, _) => ++attempts < 3 ? throw PortCollision() : Task.CompletedTask,
            dispose: _ => Task.CompletedTask,
            retryDelay: TimeSpan.Zero,
            TestContext.Current.CancellationToken);

        attempts.Should().Be(3);
        result.Should().Be(3, because:
            "each attempt must start a newly built container — the host port is assigned at " +
            "create time, so reusing the instance would ask for the same allocation again");
    }

    [Fact]
    public async Task RootfulWording_IsRetriedToo()
    {
        var attempts = 0;

        await ContainerStartup.StartNewCoreAsync(
            build: () => 0,
            start: (_, _) => ++attempts < 2 ? throw PortAlreadyAllocated() : Task.CompletedTask,
            dispose: _ => Task.CompletedTask,
            retryDelay: TimeSpan.Zero,
            TestContext.Current.CancellationToken);

        attempts.Should().Be(2);
    }

    [Fact]
    public async Task PortCollision_NestedInsideAnotherException_IsStillRecognised()
    {
        var attempts = 0;

        await ContainerStartup.StartNewCoreAsync(
            build: () => 0,
            start: (_, _) => ++attempts < 2
                ? throw new InvalidOperationException("wait strategy failed", PortCollision())
                : Task.CompletedTask,
            dispose: _ => Task.CompletedTask,
            retryDelay: TimeSpan.Zero,
            TestContext.Current.CancellationToken);

        attempts.Should().Be(2);
    }

    [Fact]
    public async Task EveryAttemptCollides_RethrowsRatherThanLoopingForever()
    {
        var attempts = 0;

        var start = () => ContainerStartup.StartNewCoreAsync(
            build: () => 0,
            start: (_, _) => { attempts++; throw PortCollision(); },
            dispose: _ => Task.CompletedTask,
            retryDelay: TimeSpan.Zero,
            TestContext.Current.CancellationToken);

        await start.Should().ThrowAsync<DockerApiException>(because:
            "a host that is genuinely out of ports must fail the run, not retry indefinitely");

        attempts.Should().Be(4, because: "the attempt budget is four, and the last one is not retried");
    }

    /// <summary>
    /// The point of matching on the message rather than on <see cref="DockerApiException"/>
    /// alone: a real daemon error must surface on the first attempt, undelayed and undisguised.
    /// </summary>
    [Fact]
    public async Task UnrelatedDaemonError_IsNotRetried()
    {
        var attempts = 0;

        var start = () => ContainerStartup.StartNewCoreAsync(
            build: () => 0,
            start: (_, _) =>
            {
                attempts++;
                throw new DockerApiException(
                    HttpStatusCode.NotFound, """{"message":"No such image: postgres:16-alpine"}""");
            },
            dispose: _ => Task.CompletedTask,
            retryDelay: TimeSpan.Zero,
            TestContext.Current.CancellationToken);

        await start.Should().ThrowAsync<DockerApiException>();
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task DiscardedSubject_IsDisposed_SoTheCreatedContainerIsNotLeaked()
    {
        var attempts = 0;
        var disposed = new List<int>();

        await ContainerStartup.StartNewCoreAsync(
            build: () => attempts,
            start: (_, _) => ++attempts < 3 ? throw PortCollision() : Task.CompletedTask,
            dispose: subject => { disposed.Add(subject); return Task.CompletedTask; },
            retryDelay: TimeSpan.Zero,
            TestContext.Current.CancellationToken);

        disposed.Should().HaveCount(2, because: "the two containers that failed to bind must be removed");
    }

    /// <summary>
    /// Cleanup runs on a best-effort basis: a dispose that throws must not replace the
    /// diagnosis of a retryable collision with a teardown error.
    /// </summary>
    [Fact]
    public async Task DisposeThatThrows_DoesNotAbortTheRetry()
    {
        var attempts = 0;

        var result = await ContainerStartup.StartNewCoreAsync(
            build: () => attempts,
            start: (_, _) => ++attempts < 2 ? throw PortCollision() : Task.CompletedTask,
            dispose: _ => throw new InvalidOperationException("container removal failed"),
            retryDelay: TimeSpan.Zero,
            TestContext.Current.CancellationToken);

        attempts.Should().Be(2);
        result.Should().Be(1);
    }
}
