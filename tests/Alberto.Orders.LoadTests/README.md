# Alberto Orders Load Tests

K6 load tests for the Alberto Order Service GraphQL API.

## Prerequisites

- Node.js 18+
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

| Test | Purpose | Duration | Max VUs |
|------|---------|----------|---------|
| Smoke | Quick validation | 30s | 1 |
| Load | Normal load | ~14min | 20 |
| Stress | Find breaking point | ~17min | 100 |
| Spike | Sudden traffic burst | ~4min | 100 |

## Multi-Tenant Support

All tests include `X-Tenant-Id` header with round-robin distribution across predefined tenants.

Default tenants:
- `tenant-loadtest-001`
- `tenant-loadtest-002`
- `tenant-loadtest-003`

### Custom Tenants

Override via environment variable:

```bash
k6 run --env TENANT_IDS=tenant-a,tenant-b,tenant-c dist/load.test.js
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
| Full lifecycle | < 2000ms (p95) |

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
