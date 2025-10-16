# Alberto Example Load Tests

Load tests for the Alberto Example application using k6 and TypeScript.

## Prerequisites

- Node.js and npm installed
- **Either** [k6](https://k6.io/docs/get-started/installation/) installed **OR** Docker (script auto-detects and uses Docker as fallback)

## Test Scenario

The load test executes a complete order lifecycle:

1. **Create Order** - Creates a new order with random amount and customer ID
2. **Place Order** - Places the order
3. **Create Payment** - Creates a payment for the order
4. **Process Payment** - Processes the payment
5. **Ship Order** - Ships the order with a tracking number

## Load Profiles

| Profile | Duration | Max VUs | Purpose | Pass Criteria |
|---------|----------|---------|---------|---------------|
| **smoke** | 1 min | 10 | Quick validation | 95% success, p95<1s |
| **load** | 10 min | 100 | Production peak traffic | 95% success, p95<1s |
| **stress** | 12 min | 150 | Push to 70-80% capacity | 90% success, p95<2s |
| **breakpoint** | 14 min | 300 | **Find breaking point** | 30% success, p95<5s |

### Smoke Test (Default)
- Duration: 1 minute
- Virtual Users: 10
- Purpose: Verify basic functionality under minimal load

```bash
./run-test.sh
# or
TEST_PROFILE=smoke ./run-test.sh
```

### Load Test
- Duration: 10 minutes
- Virtual Users: Ramps from 50 → 100 → 50
- Purpose: Evaluate performance under expected load

```bash
TEST_PROFILE=load ./run-test.sh
```

### Stress Test
- Duration: 12 minutes
- Virtual Users: Ramps from 50 → 150
- Purpose: Push system to 70-80% capacity without exhausting resources
- Configured for PostgreSQL connection pool limits (max 200 connections)

```bash
TEST_PROFILE=stress ./run-test.sh
```

### Breakpoint Test (Stress-to-Failure)
- Duration: 14 minutes
- Virtual Users: Ramps 50 → 100 → 150 → 200 → 250 → 300
- Purpose: **Intentionally break the system** to discover limits
- Expects failures (50% error rate acceptable)
- Identifies bottlenecks: DB connections, CPU, memory, network
- **Note**: Previous tests without pooling failed at ~200 VUs. This test pushes beyond that.

```bash
TEST_PROFILE=breakpoint ./run-test.sh
```

**What to look for**:
- At what VU count do errors start appearing? (Expect smooth until 200+)
- What fails first? (DB connections, timeouts, memory?)
- Which operations fail first? (Create, Place, Payment?)
- Does the system recover when load decreases?
- How much did connection pooling help? (Compare to old 200 VU limit)

## Custom Target URL

By default, tests target `http://localhost:5000`. Override with:

```bash
BASE_URL=http://localhost:8080 ./run-test.sh
```

## Metrics

The test tracks:

- **Individual operation times**: Create order, place order, create payment, process payment, ship order
- **Full lifecycle time**: End-to-end transaction time
- **Success rates**: Percentage of successful lifecycle completions
- **HTTP metrics**: Request duration (p95, p99), failure rates

## Thresholds

Tests fail if thresholds are exceeded:

- **Smoke/Load**: p95 < 1s, p99 < 2s, error rate < 1%
- **Stress**: p95 < 2s, p99 < 5s, error rate < 5%

## Running from Aspire

The load test is integrated into the Aspire AppHost and available in the dashboard:

1. Start the Aspire application: `dotnet run --project src/Example/Alberto.AppHost`
2. Open the Aspire dashboard
3. Locate the "load-tests" resource
4. Click "Start" to begin load testing
5. Monitor results in real-time

The test automatically targets the correct API URL through Aspire service discovery.

## Development

### Manual Build and Run

```bash
# Install dependencies
npm install

# Build TypeScript
npm run build

# Run with k6
k6 run --env TEST_PROFILE=smoke --env BASE_URL=http://localhost:5000 dist/order-lifecycle.test.js
```

### Modifying Tests

Edit `src/order-lifecycle.test.ts` and rebuild:

```bash
npm run build
```
