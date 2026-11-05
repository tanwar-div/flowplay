#!/usr/bin/env bash
# Builds, runs both test suites, and smoke-runs the benchmarks.
#
# This is what CI runs, minus the end-to-end load stage, so a failure here is a failure
# there. The integration tests start real PostgreSQL and RabbitMQ containers through
# Testcontainers, so Docker has to be available.
set -euo pipefail

cd "$(dirname "$0")/.."

echo "==> Restoring and building"
dotnet build FlowPay.slnx -c Release

echo "==> Unit tests"
dotnet test tests/FlowPay.UnitTests/FlowPay.UnitTests.csproj -c Release --no-build

echo "==> Integration tests (Testcontainers: PostgreSQL + RabbitMQ)"
dotnet test tests/FlowPay.IntegrationTests/FlowPay.IntegrationTests.csproj -c Release --no-build

echo "==> Benchmark smoke run (short job)"
dotnet run -c Release --project bench/FlowPay.Benchmarks -- \
    --job short --filter '*StateTransitionBenchmarks*'

echo "==> All checks passed"
