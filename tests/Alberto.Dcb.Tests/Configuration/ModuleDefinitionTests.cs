using Alberto.Dcb;
using Alberto.Dcb.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Alberto.Dcb.Tests.Configuration;

public class ModuleDefinitionTests
{
    private sealed class FakeBackend : IAlbertoBackendDescriptor
    {
        public string Name => "Fake";
        public bool SupportsTenancy => true;
        public bool Registered { get; private set; }
        public bool TenancyAtRegistration { get; private set; }

        public IAlbertoBackendDescriptor ApplyConfiguration(IConfiguration moduleSection) => this;
        public IEnumerable<AlbertoValidationFailure> Validate(AlbertoModuleDefinition definition) => [];

        public void Register(AlbertoModuleContext context)
        {
            Registered = true;
            TenancyAtRegistration = context.TenancyEnabled;
        }
    }

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
        var services = new ServiceCollection();

        services.AddAlberto("orders", module => module
            .Register(context => seenModuleKey = context.ModuleKey)
            .UseBackend(new FakeBackend()));

        seenModuleKey.Should().Be("orders");
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
}
