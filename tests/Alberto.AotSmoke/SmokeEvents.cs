using System.Text.Json.Serialization;
using Alberto.Messaging;

namespace Alberto.AotSmoke;

// ---- events -------------------------------------------------------------------------------

[EventType(Id)]
public sealed record AccountOpened(
    [property: Tag("account")] string AccountId,
    string Owner) : IEvent
{
    public const string Id = "smoke-account-opened";
}

/// <summary>Current shape. Version 1 had no <see cref="Currency"/>; see <see cref="FundsDepositedV1"/>.</summary>
[EventType(Id, Version = 2)]
public sealed record FundsDeposited(
    [property: Tag("account")] string AccountId,
    decimal Amount,
    string Currency) : IEvent
{
    public const string Id = "smoke-funds-deposited";
}

/// <summary>The v1 payload, only ever read as the input of the upcaster.</summary>
public sealed record FundsDepositedV1(string AccountId, decimal Amount);

[Message(Type, 1)]
public sealed record AccountOpenedMessage(string AccountId, string Owner)
{
    public const string Type = "smoke.account-opened";
}

// ---- commands and state -------------------------------------------------------------------

public sealed record OpenAccount(string AccountId, string Owner);

public sealed record Deposit(string AccountId, decimal Amount);

public sealed record AccountState
{
    public bool Opened { get; init; }
    public string? Owner { get; init; }
    public decimal Balance { get; init; }
    public string? Currency { get; init; }
}

public sealed class AccountEvolver : Evolver<AccountState>,
    IEvolve<AccountState, AccountOpened>,
    IEvolve<AccountState, FundsDeposited>
{
    public AccountState Apply(AccountState state, AccountOpened e)
        => state with { Opened = true, Owner = e.Owner };

    public AccountState Apply(AccountState state, FundsDeposited e)
        => state with { Balance = state.Balance + e.Amount, Currency = e.Currency };
}

// ---- the AOT wiring: a JSON context and a hand-written registry ----------------------------

// Case-insensitive to match the options EventSerializer uses on the reflection path.
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AccountOpened))]
[JsonSerializable(typeof(FundsDeposited))]
[JsonSerializable(typeof(FundsDepositedV1))]
[JsonSerializable(typeof(AccountOpenedMessage))]
internal sealed partial class SmokeJsonContext : JsonSerializerContext;

internal static class SmokeEvents
{
    public static readonly IEventTypeRegistry Registry = EventTypeRegistry.CreateBuilder()
        .Add(SmokeJsonContext.Default.AccountOpened,
            e => [new("account", e.AccountId)])
        .Add(SmokeJsonContext.Default.FundsDeposited,
            e => [new("account", e.AccountId)])
        .Build();
}
