#!/usr/bin/env bash
# Brings up the stack and runs the correctness and throughput scenarios against it.
#
# The replay and refund-storm scenarios exit non-zero if a duplicate financial effect or
# an over-refund is detected, so this script fails loudly rather than printing a report
# someone has to interpret.
set -euo pipefail

cd "$(dirname "$0")/.."

API_REPLICAS="${API_REPLICAS:-3}"
WORKER_REPLICAS="${WORKER_REPLICAS:-2}"
DURATION="${DURATION:-120}"
RPS="${RPS:-400}"

echo "==> Starting the stack (api=${API_REPLICAS}, worker=${WORKER_REPLICAS})"
docker compose up --build -d \
    --scale "api=${API_REPLICAS}" \
    --scale "worker=${WORKER_REPLICAS}"

cleanup() {
    echo "==> Tearing down"
    docker compose down -v
}
trap cleanup EXIT

echo "==> Waiting for readiness"
for _ in $(seq 1 60); do
    if curl -fsS http://localhost:5080/health/ready > /dev/null 2>&1; then
        break
    fi
    sleep 5
done

echo "==> Idempotent replay: 10,000 attempts on one key"
dotnet run -c Release --project tools/FlowPay.LoadTest -- \
    --scenario replay --operations 10000 --concurrency 32

echo "==> Concurrent refund stress"
dotnet run -c Release --project tools/FlowPay.LoadTest -- \
    --scenario refundstorm --concurrency 64 --operations 4000

echo "==> Soak: 100,000 payments, then a full invariant check"
dotnet run -c Release --project tools/FlowPay.LoadTest -- \
    --scenario soak --operations 100000 --concurrency 64 --csv loadtest-soak.csv

echo "==> Throughput: ${RPS} rps for ${DURATION}s"
dotnet run -c Release --project tools/FlowPay.LoadTest -- \
    --scenario throughput --rps "${RPS}" --duration "${DURATION}" \
    --csv loadtest-throughput.csv

echo "==> Final invariant check"
dotnet run -c Release --project tools/FlowPay.LoadTest -- --scenario verify
