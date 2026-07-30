# The admin surface

Alberto ships three ways to operate a store. They are the same operations behind three front
doors, and you can use any combination of them:

| | For | Needs |
|---|---|---|
| **`alberto` CLI** | People, runbooks, CI | Nothing. Point it at the database. |
| **Admin GraphQL API** | A console, your own tooling | A line in your host's DI |
| **Admin MCP server** | LLM agents | A line in your host's DI |

The CLI is documented in [operations.md](operations.md) and needs no changes to your
application — it talks straight to Postgres. This page is about the other two, which run
*inside* your host process and therefore need wiring.

All three read and write the same tables. Nothing here is a cache or a copy.

## What ships, and what doesn't

Packable, on NuGet:

| Package | Contains |
|---|---|
| `Alberto.Dcb.Admin` | `IAdminReader`, `IAdminOperator`, the module registry, the DTOs |
| `Alberto.Dcb.Admin.GraphQL` | HotChocolate query/mutation/subscription types |
| `Alberto.Dcb.Admin.Mcp` | The MCP server and its 25 tools |
| `Alberto.Dcb.Postgres` | `AddAlbertoPostgresAdmin` — the only implementation of the two interfaces |

Not packable — examples you copy, in `/apps`:

| Project | What it is |
|---|---|
| `Alberto.Admin` | The React operator console (Vite, urql, gql.tada) |
| `Alberto.Admin.Bff` | A YARP reverse proxy in front of it, with an auth seam |

There is no `npm install` for the console and no NuGet package for the BFF. If you want the
web console, copy those two directories and point them at your API. The server-side surface —
the part with the operations in it — is what ships.

**Postgres only.** `AddAlbertoPostgresAdmin` is the sole implementation of `IAdminReader` and
`IAdminOperator`. A host on the in-memory backend has no admin surface; the interfaces are
public, so you can implement them, but nothing in the box does.

## Wiring it up

The admin surface needs your modules registered first — it resolves each module's keyed
`NpgsqlDataSource`.

```csharp
builder.Services.AddAlberto("orders", m => m.WithTenancy().WithPostgres(/* … */));
builder.Services.AddAlberto("payments", m => m.WithTenancy().WithPostgres(/* … */));

// One call per module you want operable. The schema must be the one that module's
// Alberto tables actually live in — pointing it at the wrong schema reports an
// empty, healthy-looking system rather than failing.
builder.Services.AddAlbertoPostgresAdmin("orders", schema: "orders", isDefault: true);
builder.Services.AddAlbertoPostgresAdmin("payments", schema: "payments");

builder.Services.AddAlbertoAdminMcp();

builder.Services
    .AddGraphQLServer()
    .AddAlbertoAdminGraphQL();

var app = builder.Build();
app.UseWebSockets();      // subscriptions
app.MapGraphQL();
app.MapAlbertoAdminMcp("/mcp");
```

That is the whole integration. `apps/Alberto.Orders/Alberto.Orders.Api/Program.cs` is this
same wiring in a running application.

### One call per module

Each module is a separate event store. Its positions, checkpoints and dead letters mean
nothing next to another module's, so the admin surface addresses them one at a time rather
than merging them — there is no honest way to add two event logs together.

Registering only some of your modules does not make the others unsupported, it makes them
**invisible**: the console shows the registered module's numbers under no particular label,
and an unregistered module's projection can fall arbitrarily far behind without anything on
screen changing.

`isDefault: true` picks the module that answers when a caller names none. It also registers
that module as the plain, unkeyed `IAdminReader`/`IAdminOperator`. If no registration is
marked default, the first one wins.

## GraphQL

Every field takes an optional `module` argument. Passing `null` means "the default module",
which is a real answer rather than an error — a client can render before it has fetched the
module list.

```graphql
query {
  adminModules { key schema isDefault tenancyMode }

  adminSystemInfo(module: "payments") { globalPosition processorCount deadLetterCount }

  adminDeadLetters(module: "payments", tenant: "acme", limit: 50) {
    processorId eventType errorMessage failedAt
  }
}
```

**Queries** — `adminModules`, `adminSystemInfo`, `adminCheckpoints`, `adminCheckpoint`,
`adminDeadLetters`, `adminDeadLetterCount`, `adminEvents`, `adminProcessors`,
`adminProjectionTypes`, `adminProjectionStates`, `adminTenantLeases`, `adminTenants`,
`adminProcessorLeases`, `adminGlobalPosition`, `adminRebuildStates`.

**Mutations** — `adminSetCheckpoint`, `adminResetCheckpoint`, `adminClearAllDeadLetters`,
`adminClearDeadLettersForProcessor`, `adminRetryByRewind`, `adminReleaseTenantLeases`,
`adminStartRebuild`, `adminPromoteRebuild`, `adminAbortRebuild`.

**Subscriptions** — `onAdminSystemUpdated`, `onAdminCheckpointUpdated`,
`onAdminDeadLettersChanged`, `onAdminRebuildUpdated`, `onAdminAuditEvent`.

Only `adminDeadLetters`, `adminEvents` and `adminProjectionStates` take a `tenant` argument —
they are the three that return per-tenant rows. Filtering is server-side; a client that drops
the argument gets every tenant.

### Subscriptions and replicas

Subscription topics are module-scoped, so a subscriber watching `payments` never sees an
`orders` event.

**They fire on operator actions, not on organic progress.** Every publish comes from an admin
mutation — reset a checkpoint and every connected console learns immediately. A projection
quietly catching up, or a processor starting to dead-letter on its own, publishes nothing.
A console that wants live lag has to poll for it; the subscriptions keep operators consistent
with *each other*, not with the store.

The backplane is yours to choose. `AddInMemorySubscriptions()` is fine for a single instance.
It is **not** fine behind a load balancer: a mutation handled by one replica must reach a
subscriber connected to another, which an in-process topic cannot do. The Orders example runs
five replicas and uses Postgres `LISTEN`/`NOTIFY`:

```csharp
builder.Services
    .AddGraphQLServer()
    .AddAlbertoAdminGraphQL()
    .AddPostgresSubscriptions((sp, options) =>
    {
        var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>("orders");
        options.ConnectionFactory = ct => dataSource.OpenConnectionAsync(ct);
    });
```

That needs the `HotChocolate.Subscriptions.Postgres` package. Any HotChocolate backplane works.

## MCP

`AddAlbertoAdminMcp()` exposes the same reader and operator as 25 tools over stateless
Streamable HTTP — no per-session state, so it scales and redeploys without draining
connections. The server carries instructions telling the agent where diagnosis starts, which
tools are destructive, and the two invariants that trip agents up most (positions are
per-database; tenancy mode is fixed at migration time).

Mutating tools take an `operatorId` that is recorded in the audit trail, defaulting to `mcp`.

**The MCP server sees only the default module.** Its tools resolve the unkeyed
`IAdminReader`/`IAdminOperator`, which is the registration made by `isDefault: true`. There is
no module argument on the tools. A host with several modules can drive all of them over
GraphQL but only the default one over MCP.

## Audit trail

Every mutation appends an admin event to the module's own event log, in the same transaction
as the change:

`admin-checkpoint-reset`, `admin-checkpoint-rewound`, `admin-dead-letters-cleared`,
`admin-dead-letters-retried`, `admin-tenant-leases-released`, `admin-rebuild-started`,
`admin-rebuild-promoted`, `admin-rebuild-aborted`.

Each carries the `operatorId` the caller passed. Because it is an ordinary event, it is
readable after the fact through `adminEvents` and live through `onAdminAuditEvent`. On a
multi-tenant store these events use the reserved tenant `__admin__`.

The transaction matters: if the mutation rolls back, so does its audit record. There is no
window where a change is applied but unattributed.

## Security

**The library authenticates nobody.** `AddAlbertoAdminGraphQL` adds fields and
`MapAlbertoAdminMcp` maps an endpoint; neither adds an authorization policy. Mounted as
written above, anyone who can reach the port can reset a checkpoint. That is a deliberate
choice — the admin surface is plain ASP.NET Core wiring and does not invent an auth model —
but it is yours to close.

Standard options, in rough order of effort: put the endpoints behind
`.RequireAuthorization()`, bind them to an internal-only port, or run them behind a BFF.

`apps/Alberto.Admin.Bff` is the third of those, worked out. It is anonymous by default and
says so rather than faking a sign-in. `AdminBffAuthentication.AddAdminAuthentication` is the
seam: register an OIDC handler there and return `true`, and the default authorization policy
starts requiring a signed-in user, `/bff/login` and `/bff/logout` get mapped, and `/bff/user`
starts answering 401 — which is what makes the console render its sign-in prompt. The YARP
route table does not change. It also implements the double-submit antiforgery pattern for
mutating requests.

## Known gaps

- **The signed-in user does not reach the audit trail.** The BFF forwards an
  `X-Alberto-Operator` header, but nothing on the API side reads it: mutations are attributed
  to the `operatorId` argument, defaulting to `admin-panel`. Wire an identity provider today
  and the audit trail still says `admin-panel`.
- **MCP is single-module** — see above.
- **GraphQL has no `renameCheckpoint`.** `IAdminOperator.RenameCheckpointAsync` is exposed by
  the CLI and by the `alberto_rename_checkpoint` MCP tool, but has no mutation. A console
  cannot rename a checkpoint after a processor is renamed in code.
- **`adminProjectionStates` returns no document body**, so a console can list projection
  documents and their positions but cannot show what is in one.
