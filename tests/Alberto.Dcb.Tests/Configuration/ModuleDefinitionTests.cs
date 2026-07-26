using Alberto.Dcb;
using Alberto.Dcb.Configuration;
using Alberto.Dcb.Tests.Testing;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Alberto.Dcb.Tests.Configuration;

public class ModuleDefinitionTests
{
    private static AlbertoModuleDefinition Resolve(IServiceCollection services, string moduleKey) =>
        services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>()
            .Get(moduleKey);

    [Fact]
    public void The_definition_is_resolvable_as_a_named_options_instance()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module.UseBackend(new FakeBackend()));

        Resolve(services, "orders").ModuleKey.Should().Be("orders");
    }

    [Fact]
    public void Two_modules_keep_separate_definitions()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module.UseBackend(new FakeBackend()));
        services.AddAlberto("billing", module => module
            .UseBackend(new FakeBackend())
            .Configure(d => d with { ControlLoop = d.ControlLoop with { BatchSize = 5 } }));

        var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>();

        monitor.Get("orders").ControlLoop.BatchSize.Should().Be(100);
        monitor.Get("billing").ControlLoop.BatchSize.Should().Be(5);
    }

    [Fact]
    public void Backends_are_registered_after_the_whole_lambda_has_run()
    {
        var backend = new FakeBackend();
        var services = new ServiceCollection();

        services.AddAlberto("orders", module => module
            .UseBackend(backend)
            .WithTenancy());

        backend.Registered.Should().BeTrue();
        backend.TenancyAtRegistration.Should().BeTrue(
            "deferred registration must see the final definition regardless of call order");
    }

    [Fact]
    public void Declaration_order_does_not_change_the_definition()
    {
        var tenancyFirst = new ServiceCollection();
        tenancyFirst.AddAlberto("orders", m => m.WithTenancy().UseBackend(new FakeBackend()));

        var tenancyLast = new ServiceCollection();
        tenancyLast.AddAlberto("orders", m => m.UseBackend(new FakeBackend()).WithTenancy());

        Resolve(tenancyFirst, "orders").TenancyEnabled
            .Should().Be(Resolve(tenancyLast, "orders").TenancyEnabled);
    }

    [Fact]
    public void Configuration_overrides_what_the_lambda_set()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alberto:Modules:orders:ControlLoop:BatchSize"] = "17",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAlberto("orders", module => module
            .UseBackend(new FakeBackend())
            .Configure(d => d with { ControlLoop = d.ControlLoop with { BatchSize = 500 } }));

        Resolve(services, "orders").ControlLoop.BatchSize.Should().Be(17);
    }

    [Fact]
    public void Declared_processors_appear_in_the_definition()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module
            .UseBackend(new FakeBackend())
            .DeclareProcessor(new ProcessorDeclaration
            {
                ProcessorId = "summary",
                Kind = ProcessorKind.Projection,
            }));

        Resolve(services, "orders").Processors.Should().ContainSingle()
            .Which.ProcessorId.Should().Be("summary");
    }

    [Fact]
    public void Deferred_registrations_run_against_the_final_definition()
    {
        string? seenModuleKey = null;
        bool seenTenancyEnabled = false;
        var services = new ServiceCollection();

        // .Register() is called before .WithTenancy() to prove the callback sees the
        // post-lambda definition, not a snapshot captured at the point of registration.
        services.AddAlberto("orders", module => module
            .Register(context =>
            {
                seenModuleKey = context.ModuleKey;
                seenTenancyEnabled = context.TenancyEnabled;
            })
            .WithTenancy()
            .UseBackend(new FakeBackend()));

        seenModuleKey.Should().Be("orders");
        seenTenancyEnabled.Should().BeTrue("the callback sees the definition after all builder calls complete");
    }

    [Fact]
    public void A_second_backend_declaration_is_rejected_immediately()
    {
        var services = new ServiceCollection();

        var act = () => services.AddAlberto("orders", module => module
            .UseBackend(new FakeBackend())
            .UseBackend(new FakeBackend()));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already declares*Fake*");
    }

    // ── Call-order invariant ─────────────────────────────────────────────────
    // AddAlberto runs the entire configure lambda before registering anything.
    // WithTenancy() and UseBackend() (or WithPostgres) can appear in any order;
    // no startup validator rejects or flags a particular sequence. These two tests
    // document that invariant so a reader who has seen the old (incorrect) "order
    // matters" doc can verify the claim is false.

    [Fact]
    public void WithTenancy_before_backend_and_after_produce_identical_definitions()
    {
        var before = new ServiceCollection();
        before.AddAlberto("orders", m => m.WithTenancy().UseBackend(new FakeBackend()));

        var after = new ServiceCollection();
        after.AddAlberto("orders", m => m.UseBackend(new FakeBackend()).WithTenancy());

        var resolvedBefore = Resolve(before, "orders");
        var resolvedAfter = Resolve(after, "orders");

        resolvedBefore.TenancyEnabled.Should().BeTrue();
        resolvedAfter.TenancyEnabled.Should().BeTrue("call order must not affect whether tenancy is applied");
        resolvedBefore.TenancyEnabled.Should().Be(resolvedAfter.TenancyEnabled);
    }

    [Fact]
    public void No_validation_error_is_raised_when_WithTenancy_follows_UseBackend()
    {
        // Previously, incorrect documentation claimed a startup validator would reject
        // a module that called .WithTenancy() after the backend declaration. No such
        // validator exists: the call order is irrelevant by design.
        var services = new ServiceCollection();
        var backend = new FakeBackend();
        services.AddAlberto("orders", m => m.UseBackend(backend).WithTenancy());

        // Building and resolving the definition must not throw.
        var act = () => Resolve(services, "orders");

        act.Should().NotThrow();
        Resolve(services, "orders").TenancyEnabled.Should().BeTrue();
    }
}
