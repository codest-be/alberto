# Message transports

Alberto ships no broker bindings. There is no RabbitMQ package, no Kafka package, no Azure Service
Bus package, and there is no PostgreSQL transport either. This document explains why that is a
decision rather than a backlog item.

## The seam

`IMessageTransport` is where Alberto stops and the wire begins.

```csharp
public interface IMessageTransport
{
    Task PublishAsync(ExternalMessage message, CancellationToken ct = default);
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}
```

Three methods, one of which does the work. Everything on Alberto's side of that boundary is about
durability: an `OutboxHandler` stages an entry only for an event that is already committed, and an
`OutboxRelay` claims it under a lease and hands it over at least once. Everything on the far side
is the application's. Alberto never learns whether the broker acknowledged, how it routed, or what
the wire format was.

`ExternalMessage` carries the two fields that exist purely to feed somebody else's client.
`Destination` is a transport address, an exchange or topic or queue. `RoutingHint` is a partition
key, a routing key or a message group id, and its interpretation is transport-specific by
definition. Alberto sets them and forgets them.

## Why no bindings ship

A broker binding in the box means that broker's client version in Alberto's release matrix, and a
standing commitment to track its breaking changes. That is a real cost, and what it buys is smaller
than the cost, because the seam is three methods wide. The worked adapter in
`tests/Alberto.Tests.Messaging.Rebus` is twenty lines: an envelope record, one method that picks
between two client calls, and two lifecycle methods that do nothing on purpose. Owning twenty lines
you can read is cheaper than depending on a package that has to be republished every time the
broker client moves.

The second reason matters more. Shipping a binding picks a winner. Rebus, MassTransit, Wolverine
and a client you wrote yourself are indistinguishable from where Alberto sits, and Alberto has no
information that would let it prefer one. Publishing `Alberto.Messaging.Rebus` would imply a
recommendation Alberto is not entitled to make, and would leave every other bus looking like the
unsupported path.

So the only implementation in the repository is `InMemoryTransport`, and that is a test double.
Whatever you already run is what you plug in.

## Postgres as the broker

This decision was reached by asking whether `Alberto.Messaging.Postgres` should ship a PostgreSQL
transport, modelled on the one in [Rebus.PostgreSql](https://github.com/rebus-org/Rebus.PostgreSql),
so that Postgres itself becomes the queue.

It should not, and the reason generalizes. If you want Postgres as your broker, configure your bus
with a Postgres transport and put an adapter in front of it. You get the queue table, the dequeue
logic, the deferred delivery and the expiry sweep from a project that maintains them, and Alberto
stays out of it. An Alberto-native Postgres transport would be a second `FOR UPDATE SKIP LOCKED`
claim loop, over a second table, in the same database that `PostgresOutboxStore` is already
claiming from. Two hops through one database, and Alberto maintaining machinery it can get for
free.

`Alberto.Messaging.Postgres` therefore contains `PostgresOutboxStore` and nothing else.

## There is no inbound path

Alberto cannot receive a message. `ControlLoop` polls `alberto_events` and dispatches to
`IEventProcessor` through the middleware chain, and that is the only way anything enters the
system. `Alberto.Subscriptions` subscribes to the event log, not to a broker.

**This is a scope boundary, not a missing feature.** Alberto is an event store with an outbox.
Receiving belongs to a bus, and you already have one, or you would not be publishing anywhere.
A handler on your bus turns an inbound message into a command, and the command appends events.
Alberto sees the events.

The alternative was considered and rejected. Receiving would require a public message-handler
abstraction, a polling dispatcher, retry and dead-lettering shaped around messages rather than
around `ConsumeEventContext`, and a dedup inbox. That is a message-consumption subsystem, and
publishing its abstraction at 1.0 would freeze it under semver before a single consumer existed.
It is the same bet that got the admin surface parked, and it has the same downside.

## Four traps

Every one of these applies whichever bus you choose.

**Do not run two outboxes.** Rebus, MassTransit and Wolverine all ship an outbox of their own, and
Alberto's outbox already provides the guarantee they are offering. Turning both on gives you two
staging tables and two relays for one message, with the second one adding latency and failure modes
in exchange for nothing. Use Alberto's outbox and your bus's transport, and leave your bus's outbox
off.

**The bus client's lifecycle belongs to the host.** Alberto calls `StartAsync` before the relay's
first claim and exactly one `StopAsync` after the last relay exits, but it never disposes a
transport it did not construct. Bus clients are disposable and are normally owned by the DI
container, so an adapter should wrap a resolved client, make both lifecycle methods no-ops, and
leave disposal where it already is. See
[Implementing a transport](../reactors-and-outbox.md#implementing-a-transport) for the contract in
full, including what `StopAsync` must tolerate after a failed `StartAsync`.

**Idempotency on the way back in is yours.** Every bus here delivers at least once, so the handler
that turns a message into an Alberto command will eventually see a duplicate. Appending the same
events a second time is not a retry, it is corruption, and no amount of care in the transport
prevents it. DCB consistency boundaries help, because a boundary that already contains the effect
of the first delivery will reject the second, but they only help if you write the boundary with
that in mind. Carrying the message id into the decision is the usual way.

**Delivery is still unordered.** The outbox has never promised ordering, for
[three separate reasons](../reactors-and-outbox.md#ordering-there-isnt-any). A SQL-backed transport
that orders by priority and insertion within a recipient does not change that, because concurrent
workers erase the ordering on the way out. Nothing regresses by choosing one, but nothing improves
either.

## Writing an adapter

[Implementing a transport](../reactors-and-outbox.md#implementing-a-transport) has the full
contract, and [A worked adapter](../reactors-and-outbox.md#a-worked-adapter) has the code. Two
things are worth knowing before you write one:

- `StartAsync` and `StopAsync` should do nothing. The host constructed the bus client and the
  container disposes it, so an adapter must not start or dispose one it does not own.
- A null `Destination` means default routing, and on a bus that routes by static type the default
  can only ever reach one queue, because every Alberto message shares one envelope type. Set
  `Destination` when different messages need different queues.

`ExternalMessage.Payload` is already-serialized JSON, so by default it rides as a string field
inside that envelope and the consumer parses it. That is an ordinary arrangement, not a problem. If
the wire format is a contract with someone outside your codebase, a serializer that writes the
payload through as the message body costs about forty more lines and takes the .NET type off the
wire entirely; [Optional: raw JSON on the
wire](../reactors-and-outbox.md#optional-raw-json-on-the-wire) has it.

The complete code, with an integration test that drives it through a real relay against PostgreSQL,
is in
[tests/Alberto.Tests.Messaging.Rebus](https://github.com/codest-be/alberto/tree/main/tests/Alberto.Tests.Messaging.Rebus).
It is written against Rebus because an adapter has to be written against something. That is not a
recommendation, and nothing in it is specific to Rebus except the client calls.
