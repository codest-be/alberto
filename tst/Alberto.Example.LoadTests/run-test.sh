#!/bin/bash

# Alberto Load Test Runner
# This script builds and runs the k6 load tests
# Tries to use local k6, falls back to Docker if not available

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# Check if node_modules exists, install if not
if [ ! -d "node_modules" ]; then
    echo "Installing dependencies..."
    npm install
fi

# Build the TypeScript tests
echo "Building tests..."
npm run build

# Get test profile and base URL from environment or use defaults
TEST_PROFILE="${TEST_PROFILE:-smoke}"
BASE_URL="${BASE_URL:-http://localhost:5000}"

echo "Running load test..."
echo "Profile: $TEST_PROFILE"
echo "Target URL: $BASE_URL"
echo ""

# Show warning for breakpoint test
if [ "$TEST_PROFILE" = "breakpoint" ]; then
    echo "⚠️  WARNING: Breakpoint test will intentionally push the system to failure!"
    echo "   This test ramps up to 300 VUs and expects up to 50% error rate."
    echo "   Previous limit was ~200 VUs. This tests if connection pooling helps."
    echo "   Use this to discover system limits and bottlenecks."
    echo ""
fi

# Check if k6 is installed locally
if command -v k6 &> /dev/null; then
    echo "Using local k6..."
    k6 run \
        --env TEST_PROFILE="$TEST_PROFILE" \
        --env BASE_URL="$BASE_URL" \
        dist/order-lifecycle.test.js
else
    echo "k6 not found, using Docker..."
    docker run --rm \
        --add-host=host.docker.internal:host-gateway \
        -v "$SCRIPT_DIR:/tests" \
        -e TEST_PROFILE="$TEST_PROFILE" \
        -e BASE_URL="$BASE_URL" \
        grafana/k6:latest run /tests/dist/order-lifecycle.test.js
fi
