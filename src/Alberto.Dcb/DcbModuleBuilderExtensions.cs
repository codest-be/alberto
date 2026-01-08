namespace Alberto.Dcb;

/// <summary>
/// Extension methods for <see cref="DcbModuleBuilder"/>.
/// </summary>
public static class DcbModuleBuilderExtensions
{
    /// <summary>
    /// Configures a polling consumer with event processors.
    /// Automatically registers <see cref="Subscriptions.PollingConsumerHostedService"/>.
    /// </summary>
    /// <param name="builder">The module builder.</param>
    /// <param name="configure">Action to configure the consumer.</param>
    /// <returns>The module builder for chaining.</returns>
    public static DcbModuleBuilder WithConsumer(
        this DcbModuleBuilder builder,
        Action<ConsumerBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var consumerBuilder = new ConsumerBuilder(builder);
        configure(consumerBuilder);
        consumerBuilder.Build();

        return builder;
    }
}
