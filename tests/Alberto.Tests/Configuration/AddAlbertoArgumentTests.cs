using Alberto;
using Alberto.InMemory;
using Alberto.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Alberto.Tests.Configuration;

/// <summary>
/// Tests that close surviving mutants in <see cref="ServiceCollectionExtensions.AddAlberto"/>
/// for the argument-guard statements and the auto-control-loop registration.
/// </summary>
public class AddAlbertoArgumentTests
{
    #region Null guard — services (line 41)

    /// <summary>
    /// <c>AddAlberto</c> must throw <see cref="ArgumentNullException"/> when <c>services</c>
    /// is null, before any other validation.
    /// Kills the Statement mutant at line 41 (<c>ArgumentNullException.ThrowIfNull(services)</c>
    /// removed): without the guard, calling <c>IsModuleKeyTaken(null, moduleKey)</c> iterates a
    /// null collection and throws <see cref="NullReferenceException"/> instead, which does not
    /// satisfy <c>Assert.Throws&lt;ArgumentNullException&gt;</c>.
    /// </summary>
    [Fact]
    public void AddAlberto_NullServices_ThrowsArgumentNullException()
    {
        var act = () => ((IServiceCollection)null!).AddAlberto("orders", _ => { });

        var ex = Assert.Throws<ArgumentNullException>(act);
        Assert.Equal("services", ex.ParamName);
    }

    #endregion

    #region Null guard — moduleKey (line 42)

    /// <summary>
    /// <c>AddAlberto</c> must throw <see cref="ArgumentNullException"/> when <c>moduleKey</c>
    /// is null.
    /// Kills the Statement mutant at line 42 (<c>ArgumentException.ThrowIfNullOrWhiteSpace(moduleKey)</c>
    /// removed): without the guard, null falls through to <c>IdentifierRules.IsValidIdentifier(null)</c>
    /// which returns false, and the subsequent throw produces a plain <see cref="ArgumentException"/>,
    /// not an <see cref="ArgumentNullException"/> — so <c>Assert.Throws&lt;ArgumentNullException&gt;</c>
    /// fails.
    /// </summary>
    [Fact]
    public void AddAlberto_NullModuleKey_ThrowsArgumentNullException()
    {
        var services = new ServiceCollection();

        var act = () => services.AddAlberto(null!, _ => { });

        var ex = Assert.Throws<ArgumentNullException>(act);
        Assert.Equal("moduleKey", ex.ParamName);
    }

    #endregion

    #region Null guard — configure (line 61)

    /// <summary>
    /// <c>AddAlberto</c> must throw <see cref="ArgumentNullException"/> when the
    /// <c>configure</c> action is null.
    /// Kills the Statement mutant at line 61 (<c>ArgumentNullException.ThrowIfNull(configure)</c>
    /// removed): without the guard, <c>configure(builder)</c> calls a null delegate and throws
    /// <see cref="NullReferenceException"/>, not <see cref="ArgumentNullException"/>.
    /// </summary>
    [Fact]
    public void AddAlberto_NullConfigure_ThrowsArgumentNullException()
    {
        var services = new ServiceCollection();

        var act = () => services.AddAlberto("orders", null!);

        var ex = Assert.Throws<ArgumentNullException>(act);
        Assert.Equal("configure", ex.ParamName);
    }

    #endregion

    #region Auto-ControlLoop registration (line 92 / task line 94)

    /// <summary>
    /// When <c>WithControlLoop</c> is NOT called in the configure action,
    /// <c>AddAlberto</c> must auto-register the control loop components
    /// (including <see cref="EventStoreHead"/>) by default.
    /// Kills the Boolean→false mutant on <c>if (!builder.ControlLoopConfigured)</c>:
    /// with the mutation the block is never entered, so <see cref="EventStoreHead"/> is never
    /// added to the service collection and the assertion below fails.
    /// </summary>
    [Fact]
    public void AddAlberto_WithoutWithControlLoop_AutoRegistersEventStoreHead()
    {
        var services = new ServiceCollection();

        // Deliberately no WithControlLoop call — auto-registration should fire.
        services.AddAlberto("orders", m => m.WithInMemory());

        // EventStoreHead is registered as AddKeyedSingleton<EventStoreHead>("orders") by
        // ControlLoopRegistration.Register, which runs only when the auto-registration block
        // is entered.
        var hasEventStoreHead = services.Any(d => d.ServiceType == typeof(EventStoreHead));

        Assert.True(hasEventStoreHead,
            "EventStoreHead must be present in the service collection when WithControlLoop " +
            "is not called explicitly; its absence means the auto-registration block was skipped.");
    }

    #endregion
}
