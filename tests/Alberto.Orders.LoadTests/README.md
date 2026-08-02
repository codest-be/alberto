# Alberto Orders Load Tests

K6 load tests for the Alberto Order Service GraphQL API.

## Prerequisites

- Node.js 20+
- K6 installed (`brew install k6` on macOS)
- Alberto Orders API running

## Quick Start

```bash
# Install dependencies
npm install

# Build TypeScript
npm run build

# Run smoke test (quick validation)
npm run test:smoke

# Run full load test
npm run test:load
```

## Test Types

| Test | Script | Purpose | Duration | Max VUs |
|------|--------|---------|----------|---------|
| Smoke | `test:smoke` | Quick validation | 30s | 1 |
| Load | `test:load` | Normal load | ~14min | 20 |
| Stress | `test:stress` | Find breaking point | ~17min | 100 |
| Spike | `test:spike` | Sudden traffic burst | ~3min 20s | 100 |
| Consistency | `test:consistency` | Read-your-writes under concurrency | n/a | n/a |
| Throughput | `test:throughput` | Sustained append/read ceiling | n/a | n/a |

`test:throughput` has three variants: `test:throughput:burst`, `:extended`, and the
seeding controls `:skip-seed` / `:quick-seed`. See `package.json` for the full list.

## Multi-Tenant Support

All tests include `X-Tenant-Id` header with round-robin distribution across predefined tenants.

**Tenant ids must match `^[a-z][a-z0-9_]{0,62}$`**: a lowercase letter followed by lowercase
alphanumerics or underscores. Hyphens, uppercase and UUIDs are rejected by the API with
`INVALID_TENANT` before the request reaches a resolver, because the same allowlist governs
schema names.

Ten default tenants are defined, `tenant_loadtest_001` through `tenant_loadtest_010`, enough
to exercise tenant cache eviction. See [src/config/tenants.ts](src/config/tenants.ts).

### Custom Tenants

Override via environment variable:

```bash
k6 run --env TENANT_IDS=tenant_a,tenant_b,tenant_c dist/load.test.js
```

## Environment Configuration

```bash
# Local (default)
npm run test:load

# Docker
npm run test:docker

# Custom environment
k6 run --env ENV=staging dist/load.test.js

# Custom base URL
k6 run --env BASE_URL=http://custom:5000 dist/load.test.js
```

## Workload Distribution

The mixed workload scenario simulates realistic production traffic:

- **40%** Read operations (queries)
- **35%** Complete order lifecycles (create → confirm → ship → deliver)
- **15%** Partial lifecycles (create → confirm)
- **10%** Cancellations

## Order Lifecycle

Tests cover the complete order state machine:

```
Create Order → Draft
    ↓
Confirm Order → Confirmed
    ↓
Ship Order → Shipped
    ↓
Deliver Order → Delivered

Cancel Order (from Draft or Confirmed) → Cancelled
```

## SLA Thresholds

| Metric | Target |
|--------|--------|
| p50 latency | < 200ms |
| p95 latency | < 500ms |
| p99 latency | < 1000ms |
| Error rate | < 1% |
| Full lifecycle | < 3000ms (p95), includes ~2s of intentional sleeps |

## Results

Test results are output to stdout. For detailed analysis:

```bash
# Run with JSON output
k6 run --out json=results/load-results.json dist/load.test.js

# Run with CSV output
k6 run --out csv=results/load-results.csv dist/load.test.js
```

## Custom Metrics

The tests track these custom metrics:

- `orders_created` - Total orders created
- `orders_confirmed` - Orders confirmed
- `orders_shipped` - Orders shipped
- `orders_delivered` - Orders delivered (successful lifecycle)
- `orders_cancelled` - Orders cancelled
- `order_lifecycle_duration` - Time from creation to delivery

## Development

```bash
# Watch mode for development
npm run build:watch

# Run specific test
k6 run dist/smoke.test.js
```

## Project Structure

```
src/
├── config/           # Environment, tenant, threshold configs
├── graphql/          # GraphQL mutations and queries
├── lib/              # Helpers (client, data generators, metrics)
├── scenarios/        # Test scenarios
└── tests/            # Test entry points
```
