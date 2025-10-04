namespace Alberto.EventStore.Subscriptions.Subscriptions;

/// <summary>
/// Identifies a handler as a subscription with a specific ID for checkpoint management
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SubscriptionAttribute : Attribute
{
    public string SubscriptionId { get; }

    public SubscriptionAttribute(string subscriptionId)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
            throw new ArgumentException("Subscription ID cannot be empty", nameof(subscriptionId));

        SubscriptionId = subscriptionId;
    }
}