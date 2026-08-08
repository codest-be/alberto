# Rootless Docker and host port collisions

A CI failure that is not a test failure:

```
Alberto.Tests.Postgres.MigrationUpgradeAndParityTests.MultiTenant_DeadLetterStore_AcceptsEventTypeOver200Characters [FAIL]
Docker.DotNet.DockerApiException : Docker API responded with status code=InternalServerError,
response={"message":"failed to set up container networking: driver failed programming external
connectivity on endpoint priceless_blackwell (...): error while calling RootlessKit
PortManager.AddPort(): listen tcp4 0.0.0.0:51204: bind: address already in use"}
```

The stack ends in `DockerContainer.StartAsync`. The container never came up, so no Alberto code
ran and no assertion was reached — the test named in the failure had nothing to do with it, and
any container in the suite could have drawn the same port. Re-running passes.

## What actually collides

Two independent allocators hand out host ports on the runner, and neither can see the other's
book.

The **kernel** hands out ephemeral source ports for outbound connections from
`net.ipv4.ip_local_port_range`, which defaults to `32768 60999`.

The **daemon** hands out host ports for published container ports. Testcontainers asks for a
random one, which reaches the daemon as host port `0`. libnetwork's port allocator reads
`/proc/sys/net/ipv4/ip_local_port_range` for its dynamic range and clamps the low end up to
`49153`, so on a default host it allocates from **49153–60999** — a subset of the range the
kernel is simultaneously handing out to `connect()`.

Rootless is what turns that overlap from theoretical into routine. The daemon runs inside
RootlessKit's child network namespace, allocates a port number *there*, and then asks
RootlessKit's port driver to bind the same number in the **host** namespace. The allocator's
bookkeeping lives in a namespace whose ephemeral usage is not the host's, so it can pick a port
the host kernel already gave to some outbound socket. `PortManager.AddPort()` then does the
only thing it can: `listen tcp4 0.0.0.0:51204` → `EADDRINUSE`.

`51204` sits in the overlap. So does most of the daemon's range.

On `alberto-public-1` two things load the dice. `mutation.yml` runs on the same runner and
starts a container per Stryker round; and every job burns ephemeral ports on outbound traffic —
NuGet restore, the GitHub API, image pulls — precisely while the next job is asking the daemon
for a published port.

## The fix: make the two pools disjoint

A host sysctl, so the kernel stops handing out the range the daemon publishes into.

```bash
echo 'net.ipv4.ip_local_reserved_ports = 49153-60999' | sudo tee /etc/sysctl.d/60-docker-ports.conf
sudo sysctl --system
```

`ip_local_reserved_ports` excludes those ports from *automatic* assignment — `connect()`, or
`bind()` with port `0` — while leaving an **explicit** bind to a reserved port working exactly
as before. That is the asymmetry the fix turns on: the kernel stops competing, and RootlessKit,
which always binds an explicit port number, is unaffected. It is a host-namespace setting, so it
does not touch the daemon's own range inside the child namespace.

The cost is the host's ephemeral pool dropping from about 28k ports to about 17k. For a CI
runner that is not a number anyone will notice.

Verify:

```bash
sysctl net.ipv4.ip_local_reserved_ports
```

and then, with a job running, confirm nothing local is sourcing from the reserved range:

```bash
ss -tan | awk '{split($4,a,":"); p=a[length(a)]; if (p+0 >= 49153 && p+0 <= 60999) print}'
```

Established connections published *by* containers legitimately appear there; outbound
connections whose local port is in the range do not, and are the thing this setting removes.

### The other sysctl, and why it is second choice

```
net.ipv4.ip_local_port_range = 32768 49152
```

also separates the pools, because the daemon's clamp floors its dynamic range at `49153`
whenever the range it reads starts lower. That works, but it works by relying on a libnetwork
implementation detail — the clamp — to keep the daemon *out* of the range this line just
narrowed. `ip_local_reserved_ports` states the intent directly and does not care what the
daemon's allocator does. Prefer it.

### What not to do

Pinning containers to fixed host ports (`WithPortBinding`) trades a rare cross-allocator
collision for a guaranteed collision the moment two jobs, or two test classes, want the same
service. No test in this repository binds a fixed host port, and none should.

## The repository-side backstop

The sysctl is the fix, and the repository cannot apply it. Nothing here can assert that a given
host has it set, contributors run their own daemons, and a rebuilt runner starts out without it.
So container startup also retries.

[`tests/Shared/ContainerStartup.cs`](../../tests/Shared/ContainerStartup.cs) —
`ContainerStartup.StartNewAsync` — builds and starts a container, and on a `DockerApiException`
whose message carries `address already in use` (rootless) or `port is already allocated`
(rootful) discards that container and builds another. A fresh container is what draws a fresh
port, which is why the helper takes a factory rather than a container. Four attempts, a short
increasing pause between them, and any other failure surfaces on the first attempt undelayed.

It is compiled as a linked item into the three projects that start containers — `Alberto.Tests`,
`Alberto.Tests.Messaging.Rebus`, `Alberto.Benchmarks` — none of which references the others.
**Every container in the repository starts through it.** A bare `container.StartAsync()` is the
bug, and `grep -rn "PostgreSqlBuilder" tests/ benchmarks/` is how to check that claim still holds.

The policy is specified without a daemon, in
[`ContainerStartupTests`](../../tests/Alberto.Tests/Infrastructure/ContainerStartupTests.cs):
which failures buy another attempt, which do not, that the attempt budget is finite, and that a
discarded container is removed. A backstop nobody can exercise is a backstop nobody knows has
stopped working.
