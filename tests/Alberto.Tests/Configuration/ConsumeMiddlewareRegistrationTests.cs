using Alberto;
using Alberto.InMemory;
using Alberto.Subscriptions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Alberto.Tests.Configuration;

/// <summary>
/// Tests that AddConsumeMiddleware, AddBatchConsumeMiddleware, and UseErrorClassifier
/// register the expected keyed services.
/// </summary>
public class ConsumeMiddlewareRegistrationTests
{
    private sealed class StubErrorClassifier : IErrorClassifier
    {
        public ErrorClassification Classify(Exception exception) => ErrorClassification.Unknown;
    }

    [Fact]
    public void AddConsumeMiddleware_registers_a_keyed_ConsumeMiddleware_singleton()
    {
        ConsumeMiddleware stub = (_, next) => next();
        var services = new ServiceCollection();
        services.AddAlberto("orders", m => m
            .WithInMemory()
            .AddConsumeMiddleware(_ => stub));

        var provider = services.BuildServiceProvider();

        provider.GetKeyedServices<ConsumeMiddleware>("orders")
            .Should().ContainSingle()
            .Which.Should().BeSameAs(stub);
    }

    [Fact]
    public void AddBatchConsumeMiddleware_registers_a_keyed_BatchConsumeMiddleware_singleton()
    {
        BatchConsumeMiddleware stub = (_, next) => next();
        var services = new ServiceCollection();
        services.AddAlberto("orders", m => m
            .WithInMemory()
            .AddBatchConsumeMiddleware(_ => stub));

        var provider = services.BuildServiceProvider();

        provider.GetKeyedServices<BatchConsumeMiddleware>("orders")
            .Should().ContainSingle()
            .Which.Should().BeSameAs(stub);
    }

    [Fact]
    public void UseErrorClassifier_instance_overload_registers_a_keyed_IErrorClassifier()
    {
        var classifier = new StubErrorClassifier();
        var services = new ServiceCollection();
        services.AddAlberto("orders", m => m
            .WithInMemory()
            .UseErrorClassifier(classifier));

        var provider = services.BuildServiceProvider();

        provider.GetKeyedService<IErrorClassifier>("orders")
            .Should().BeSameAs(classifier);
    }

    [Fact]
    public void UseErrorClassifier_generic_overload_registers_a_keyed_IErrorClassifier()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", m => m
            .WithInMemory()
            .UseErrorClassifier<StubErrorClassifier>());

        var provider = services.BuildServiceProvider();

        provider.GetKeyedService<IErrorClassifier>("orders")
            .Should().BeOfType<StubErrorClassifier>();
    }
}
