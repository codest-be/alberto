using Alberto;
using Alberto.InMemory;
using Alberto.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Alberto.Tests.Configuration;

/// <summary>
/// Specifies the argument-validation rules for <see cref="ServiceCollectionExtensions.AddAlberto"/>
/// and the auto-control-loop registration behaviour when <c>WithControlLoop</c> is omitted.
/// </summary>
public class AddAlbertoArgumentTests
{
    #region Null guard — services

    /// <summary>
    /// <c>AddAlberto</c> must throw <see cref="ArgumentNullException"/> with parameter name
    /// <c>services</c> when <c>services</c> is null, before any other validation.
    ///
    /// <para>
    /// Without the null guard, the method proceeds to call <c>IsModuleKeyTaken(null, moduleKey)</c>,
    /// which iterates a null collection and throws <see cref="NullReferenceException"/> instead —
    /// a different exception type from a different call site, which does not satisfy a caller
    /// that is catching <see cref="ArgumentNullException"/>.
    /// </para>
    /// </summary>
    [Fact]
    public void AddAlberto_NullServices_ThrowsArgumentNullException()
    {
        var act = () => ((IServiceCollection)null!).AddAlberto("orders", _ => { });

        var ex = Assert.Throws<ArgumentNullException>(act);
        Assert.Equal("services", ex.ParamName);
    }

    #endregion

    #region Null guard — moduleKey

    /// <summary>
    /// <c>AddAlberto</c> must throw <see cref="ArgumentNullException"/> with parameter name
    /// <c>moduleKey</c> when <c>moduleKey</c> is null.
    ///
    /// <para>
    /// Without the null guard, null falls through to <c>IdentifierRules.IsValidIdentifier(null)</c>,
    /// which returns false, and the subsequent throw produces a plain <see cref="ArgumentException"/>
    /// about an invalid identifier — correct in spirit, but the wrong exception type and the
    /// wrong parameter name, so a caller catching <see cref="ArgumentNullException"/> does not
    /// see it.
    /// </para>
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

    #region Null guard — configure

    /// <summary>
    /// <c>AddAlberto</c> must throw <see cref="ArgumentNullException"/> with parameter name
    /// <c>configure</c> when the <c>configure</c> action is null.
    ///
    /// <para>
    /// Without the null guard, <c>configure(builder)</c> calls a null delegate and throws
    /// <see cref="NullReferenceException"/> — the wrong exception type — from inside the
    /// builder rather than at the call site, obscuring both the cause and the culprit
    /// parameter.
    /// </para>
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

    #region Auto-ControlLoop registration

    /// <summary>
    /// When <c>WithControlLoop</c> is NOT called in the configure action,
    /// <c>AddAlberto</c> must auto-register the control loop components
    /// (including <see cref="EventStoreHead"/>) by default.
    ///
    /// <para>
    /// Without the auto-registration block, a module that omits <c>WithControlLoop</c>
    /// silently starts with no control loop. Projections and reactors never receive events,
    /// and there is no startup error to signal the misconfiguration — the application just
    /// does nothing, indefinitely.
    /// </para>
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

    /// <summary>
    /// After <c>AddAlberto</c> auto-registers the control loop, it must set
    /// <c>ControlLoopConfigured</c> to <c>true</c> on the builder. The flag is the latch
    /// that prevents a subsequent <c>WithControlLoop</c> call on the same builder — such as
    /// an internal call made by <c>WithRebuilds</c> — from registering the control loop a
    /// second time. Without the assignment, the flag stays <c>false</c> after
    /// auto-registration, leaving the door open for any builder extension that calls
    /// <c>WithControlLoop</c> internally to add a duplicate registration and start two
    /// control loops that race on the same checkpoint.
    /// </summary>
    [Fact]
    public void AddAlberto_AutoRegistration_SetsControlLoopConfiguredLatch()
    {
        var services = new ServiceCollection();
        DcbModuleBuilder? captured = null;

        // No explicit WithControlLoop — the auto-registration block must fire.
        // DcbModuleBuilder is a class; captured holds the reference that AddAlberto
        // continues to operate on after configure() returns, so it reflects the post-block state.
        services.AddAlberto("orders", b =>
        {
            captured = b;
            b.WithInMemory();
        });

        Assert.NotNull(captured);
        Assert.True(captured.ControlLoopConfigured,
            "ControlLoopConfigured must be true after the auto-registration block runs. " +
            "Without the assignment, any subsequent WithControlLoop call on the same builder " +
            "sees the flag clear and registers a second control loop.");
    }

    #endregion
}
