# FlowPay

A distributed payment processing backend in .NET 10, built around three properties that
are easy to claim and hard to hold at the same time: **no duplicate financial effects**,
**a ledger that always balances**, and **predictable latency under concurrent load**.

Payments are authorized, captured, refunded and voided through an HTTP API backed by
PostgreSQL. Every state change commits together with the integration event it produces,
and a separate worker moves those events to RabbitMQ with staged retries and a
dead-letter queue. The domain core is written to run without allocating, and the
repository ships the benchmarks, stress tests and load harness that check all of it.

---

## Table of contents

- [What this is](#what-this-is)
- [Architecture](#architecture)
- [The three guarantees](#the-three-guarantees)
  - [1. Exactly one financial effect per request](#1-exactly-one-financial-effect-per-request)
  - [2. A ledger that cannot silently drift](#2-a-ledger-that-cannot-silently-drift)
  - [3. An allocation-free hot path](#3-an-allocation-free-hot-path)
- [Asynchronous workflows](#asynchronous-workflows)
- [Running it](#running-it)
- [The API](#the-api)
- [Testing and verification](#testing-and-verification)
- [Finding and fixing the throughput ceiling](#finding-and-fixing-the-throughput-ceiling)
- [Benchmarks](#benchmarks)
- [Project layout](#project-layout)
- [Design decisions and trade-offs](#design-decisions-and-trade-offs)

---

## What this is

FlowPay models the part of a payment processor that actually moves money:

- **Accounts** hold a balance in a single currency, split between settled funds and funds
  reserved by in-flight authorizations.
- **Payments** move money from one account to another through a strict lifecycle —
  `Pending → Authorized → Captured → PartiallyRefunded → Refunded`, with `Failed`,
  `Voided` and `Expired` as terminal exits.
- **Refunds** return some or all of a captured payment, bounded by what was captured.
- **Ledger entries** record every movement as balanced double-entry lines, append-only.
- **Integration events** are published for every lifecycle transition and consumed
  asynchronously to maintain read models.

It is a portfolio project, not a production payment processor: there is no authentication,
no PCI scope, no real card network, and no money. Everything else — the concurrency
control, the transactional guarantees, the failure handling — is built the way it would
need to be if there were.

---

## Architecture

```
                      ┌──────────────┐
   HTTP clients ─────▶│   gateway    │  nginx, round-robins across API replicas
                      └──────┬───────┘
                             │
          ┌──────────────────┼──────────────────┐
          ▼                  ▼                  ▼
    ┌──────────┐       ┌──────────┐       ┌──────────┐
    │  api #1  │       │  api #2  │       │  api #3  │   ASP.NET Core minimal API
    └────┬─────┘       └────┬─────┘       └────┬─────┘
         │                  │                  │
         └──────────────────┼──────────────────┘
                            ▼
                   ┌────────────────┐
                   │   PostgreSQL   │  accounts, payments, refunds, ledger_entries,
                   │                │  idempotency_keys, outbox_messages, inbox_messages
                   └───────┬────────┘
                           │  FOR UPDATE SKIP LOCKED
          ┌────────────────┴────────────────┐
          ▼                                 ▼
    ┌──────────┐                      ┌──────────┐
    │ worker#1 │                      │ worker#2 │   outbox dispatcher + consumers
    └────┬─────┘                      └────┬─────┘
         └───────────────┬─────────────────┘
                         ▼
                 ┌───────────────┐
                 │   RabbitMQ    │  flowpay.events ─┬─▶ flowpay.settlement
                 │               │                  └─▶ flowpay.audit
                 │               │  staged retries ──▶ flowpay.dlq
                 └───────────────┘
```

Both tiers scale horizontally with **no coordination service, no leader election and no
distributed lock**. API replicas are serialized against each other by a unique index;
worker replicas are kept disjoint by `FOR UPDATE SKIP LOCKED`. PostgreSQL is the only
thing that has to agree with itself.

The code is layered so the rules sit at the bottom and the machinery at the top:

| Project | Depends on | Holds |
| --- | --- | --- |
| `FlowPay.Domain` | nothing but the BCL | Value types, the state machine, aggregates, invariants |
| `FlowPay.Application` | Domain | Use cases, idempotency protocol, outbox contracts, DTOs |
| `FlowPay.Infrastructure` | Application | EF Core, PostgreSQL, RabbitMQ, dispatcher, projections |
| `FlowPay.Api` | Infrastructure | HTTP endpoints, OpenTelemetry, health checks |
| `FlowPay.Worker` | Infrastructure | Outbox dispatcher, consumers, maintenance sweeps |

---

## The three guarantees

### 1. Exactly one financial effect per request

Every mutating endpoint requires an `Idempotency-Key` header. It is not optional, because
the clients most likely to forget it are exactly the ones whose retries cause duplicate
charges.

The protocol is **one database transaction**:

```
BEGIN;
  INSERT INTO idempotency_keys (...) VALUES (...) ON CONFLICT (endpoint, key) DO NOTHING;
  -- 0 rows → someone else owns this key; read their stored response and replay it
  -- 1 row  → we own it, so do the work:
  SELECT ... FOR UPDATE   (accounts, in ascending id order)
  UPDATE accounts ...
  INSERT INTO payments ...
  INSERT INTO ledger_entries ...
  INSERT INTO outbox_messages ...
  UPDATE idempotency_keys SET status = 'completed', response_body = ...;
COMMIT;
```

Collapsing the claim, the work, the event and the stored response into one transaction is
what makes duplicate effects **impossible rather than unlikely**:

- There is no window where the key is claimed but the work is unfinished.
- There is no window where the work is committed but the key is not.
- A crash at any point rolls back everything, so the client's retry is a genuine first
  attempt rather than a request that silently half-happened.

Concurrency across replicas is handled by PostgreSQL itself. When two requests carrying
the same key arrive at different API instances, the second instance's `INSERT` **blocks on
the unique index** until the first transaction resolves. If the first commits, the second
sees no rows inserted and replays the stored response. If the first rolls back, the second
wins the claim and does the work. No distributed lock is involved.

Two further details matter:

- **The stored response is replayed verbatim**, not regenerated. A client retrying after a
  timeout needs to learn what its original request did, not what the resource looks like
  now. (This is also why `response_body` is `text` rather than `jsonb`: `jsonb` normalizes
  whitespace and reorders keys, and the replay would no longer be byte-identical.)
- **Reusing one key for a different request is rejected with 422**, not answered with the
  first response. A 128-bit fingerprint of the canonicalized request parameters is stored
  with the claim and compared on every replay. Silently returning the first response would
  leave the client believing it had moved money it had not.

Rejections deliberately do **not** consume the key: a rejected request leaves no state, so
there is nothing to make idempotent, and caching the failure would trap a client that
fixed its request and retried.

### 2. A ledger that cannot silently drift

Money is never a `decimal`. It is a `readonly struct Money` holding a signed count of
**minor units** (cents, paise, yen) plus a packed currency code, so sums are exact and the
double-entry invariant can be asserted with integer equality.

Every movement writes balanced lines. A capture of 1,000.00 with a 29.30 fee produces:

| Account | Direction | Amount |
| --- | --- | --- |
| payer | debit | 1,000.00 |
| payee | credit | 970.70 |
| platform fees | credit | 29.30 |

Fees are credited to a real per-currency account rather than subtracted into thin air,
precisely so the lines still sum to zero.

Four defences stand behind the refund invariant — total refunds may never exceed the
captured amount:

1. **The aggregate** rejects an over-refund outright.
2. **`SELECT ... FOR UPDATE`** on the payment row makes concurrent refunds serialize, so
   the second one reads a total that already includes the first.
3. **`CHECK (refunded_minor_units <= amount_minor_units)`** makes an over-refunded row
   unstorable, whatever the application does.
4. **A unique index on the refund's idempotency key**, independent of the idempotency
   table, so even a code path that bypassed the idempotency layer could not double-refund.

Account locks are always acquired **in ascending id order**. A payment A→B and a
simultaneous payment B→A that each locked their own source first would deadlock; a global
lock order makes the wait cycle impossible rather than merely recoverable by PostgreSQL's
deadlock detector.

Correctness is checked rather than assumed. `GET /v1/admin/invariants` verifies four
things over the whole database, and returns **500** if any of them fails so an automated
check can gate on the status code alone:

- the signed sum of every ledger line is zero — money is neither created nor destroyed;
- each payment's own lines balance — which localizes an imbalance to its cause;
- no payment is refunded beyond its captured amount;
- every account's balance equals its recorded opening balance plus its net ledger
  movement — which catches a balance change that happened without a matching ledger line.

The opening balance is **stored**, not derived. Reconstructing it from the current balance
and the ledger would make the equation true by construction and the check worthless.

### 3. An allocation-free hot path

The domain core is written so the operations every payment touches do not allocate:

- **`Currency`** packs its three ASCII letters into a `uint`. Equality is one integer
  compare; parsing is a span scan with no string allocated.
- **`Money`** is a `readonly struct` over `long` + `Currency`. Arithmetic is exposed as
  `TryAdd`/`TrySubtract` returning `bool`, with overflow detected by sign arithmetic —
  throwing would allocate an exception and a stack trace on the hottest path in the
  system, and failures here are routine business outcomes, not bugs.
- **`PaymentStateMachine`** encodes the lifecycle as a bitmask table. There are eight
  statuses, so the legal targets for any status fit in one byte, and a transition check is
  a table load, a shift and a mask. The table is declared as a `ReadOnlySpan<byte>` of
  constants, which the compiler emits into the assembly's read-only data section — no
  array to allocate, no type initializer to run, nothing for the GC to track.
- **`PaymentValidator`** takes a `ref struct` request and returns a flags bitmask. It
  reports *every* failure in one pass without allocating a list.
- **`FeeSchedule`** does 2.9% + 30c in 64-bit integers with banker's rounding. The
  per-operation amount cap (10¹² minor units) is what guarantees `amount × basisPoints`
  stays inside a `long`, which is what keeps fee calculation off the slower 128-bit and
  `decimal` paths. Rounding is half-to-even because always rounding half away from zero
  would bias every fee upward by a fraction of a minor unit.
- **`IdempotencyKey`** validates and fingerprints on the stack: the UTF-8 transcode
  targets a `stackalloc` buffer sized from the maximum key length.

The aggregates deliberately **do not raise domain events**. Collecting events would mean
allocating a list and an event object per transition. Instead the application layer writes
the outbox row inside the same transaction, which gives the identical all-or-nothing
guarantee without putting an allocation in the domain.

`bench/FlowPay.Benchmarks` measures all of this with `[MemoryDiagnoser]`, so "allocates
nothing" is a column in the results rather than an intention.

---

## Asynchronous workflows

### The transactional outbox

Publishing to RabbitMQ from inside a request would mean choosing which half to lose when
the broker is unreachable: either the capture rolls back because the broker is down, or
the capture commits and its event vanishes. Writing the event to the same transaction
removes the choice — the event is exactly as durable as the payment.

A separate dispatcher moves rows to the broker:

```sql
SELECT * FROM outbox_messages
WHERE status = 0 AND available_at <= now()
ORDER BY available_at, occurred_at
LIMIT 256
FOR UPDATE SKIP LOCKED;
```

`SKIP LOCKED` is what makes the dispatcher horizontally scalable: several workers poll the
same table and each claims a disjoint batch. Adding a worker just adds throughput.

Publishing uses **publisher confirmations**, so a row is only marked dispatched once the
broker has acknowledged it. A failed publish does not stall the batch: the row keeps its
pending status, its attempt count rises, and `available_at` moves into the future by an
exponentially growing, **jittered** delay. Jitter is not decoration — without it, every
row that failed during an outage is scheduled for the same instant and the recovered
broker is hit by the entire backlog at once. After the attempt budget is spent the row is
parked as `Failed`, where the health check sees it and `POST /v1/admin/outbox/requeue`
can return it.

A crash between a successful publish and the commit re-delivers the batch, so delivery is
**at-least-once**. Marking rows dispatched *before* publishing would turn a crash into
permanently lost events, which is strictly worse than a duplicate a consumer can detect.

### Consumer-side exactly-once

Each consumer claims the event id in an `inbox_messages` row **in the same transaction as
its side effects**:

```
BEGIN;
  INSERT INTO inbox_messages (message_id, consumer, ...) ON CONFLICT DO NOTHING;
  -- 0 rows → already handled, abandon and ack
  <apply the handler's effects>
COMMIT;
-- only then: ack the broker
```

Acknowledging only after the commit means a crash in between re-delivers the message with
the claim rolled back, so the retry is a genuine first attempt. The key is
`(message_id, consumer)`, so several consumers each handle the same event exactly once.

The settlement projection updates **accumulating** totals (`captured_count + 1`) rather
than idempotent assignments, deliberately: an idempotent projection would hide a
duplicate, while this one makes it visible as a wrong number.

### Staged retries and the dead-letter queue

A failed handler never requeues immediately — that produces a hot loop that saturates the
consumer and the broker while a downstream dependency is down. Instead the message is
republished to a delay queue whose only job is to hold it for a TTL before the broker
dead-letters it back:

```
flowpay.settlement ──fail──▶ flowpay.settlement.retry.5s   ──TTL──▶ flowpay.settlement
                   ──fail──▶ flowpay.settlement.retry.30s  ──TTL──▶ flowpay.settlement
                   ──fail──▶ flowpay.settlement.retry.120s ──TTL──▶ flowpay.settlement
                   ──exhausted──▶ flowpay.dlx ──▶ flowpay.dlq
```

Each delay queue dead-letters through the **default exchange** with the origin queue's
name as the routing key, so a retry returns to exactly one queue rather than fanning out
to every consumer bound to the events exchange. Messages that exhaust every attempt land
in a dead-letter queue rather than being dropped: a payment event that cannot be processed
is something a person needs to see.

---

## Running it

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (pinned in `global.json`)
- Docker (for the stack, and for the Testcontainers-based integration tests)

### The whole stack

```bash
docker compose up --build
```

Then open <http://localhost:5080> for the endpoint index and
<http://localhost:15672> for the RabbitMQ management UI (`flowpay` / `flowpay`).

Scaling is the interesting part:

```bash
docker compose up --build --scale api=3 --scale worker=2
```

Three API replicas share one idempotency table and are serialized by its unique index;
two workers poll one outbox and are kept disjoint by `SKIP LOCKED`. Nothing needs to be
reconfigured for either.

### Running the API locally

```bash
docker compose up -d postgres rabbitmq
dotnet run --project src/FlowPay.Api     # http://localhost:5080
dotnet run --project src/FlowPay.Worker
```

In `Development` the API also runs the outbox dispatcher itself, so a single process is
enough to exercise the whole flow.

### A first payment

```bash
# Create two accounts
PAYER=$(curl -sS -X POST http://localhost:5080/v1/accounts \
  -H 'content-type: application/json' \
  -d '{"currency":"USD","openingBalanceMinorUnits":500000,"displayName":"payer"}' \
  | python3 -c 'import sys,json;print(json.load(sys.stdin)["id"])')

PAYEE=$(curl -sS -X POST http://localhost:5080/v1/accounts \
  -H 'content-type: application/json' \
  -d '{"currency":"USD","openingBalanceMinorUnits":0,"displayName":"merchant"}' \
  | python3 -c 'import sys,json;print(json.load(sys.stdin)["id"])')

# Authorize and capture in one call
curl -sS -X POST http://localhost:5080/v1/payments \
  -H 'content-type: application/json' \
  -H 'Idempotency-Key: demo-0001' \
  -d "{\"sourceAccountId\":\"$PAYER\",\"destinationAccountId\":\"$PAYEE\",
       \"amountMinorUnits\":100000,\"currency\":\"USD\",\"method\":\"Card\",
       \"reference\":\"ORDER-1\",\"capture\":true}"

# Send it again with the same key: same response, no second charge
curl -isS -X POST http://localhost:5080/v1/payments \
  -H 'content-type: application/json' \
  -H 'Idempotency-Key: demo-0001' \
  -d "{\"sourceAccountId\":\"$PAYER\",\"destinationAccountId\":\"$PAYEE\",
       \"amountMinorUnits\":100000,\"currency\":\"USD\",\"method\":\"Card\",
       \"reference\":\"ORDER-1\",\"capture\":true}" | grep -i 'idempotent-replay'
```

---

## The API

| Method | Route | Notes |
| --- | --- | --- |
| `POST` | `/v1/accounts` | Idempotent on a client-supplied id |
| `GET` | `/v1/accounts/{id}` | Balance, reserved and available |
| `POST` | `/v1/payments` | Authorize, optionally capture. **Requires `Idempotency-Key`** |
| `GET` | `/v1/payments/{id}` | Includes the transitions currently available |
| `GET` | `/v1/payments/{id}/ledger` | The payment's double-entry lines |
| `POST` | `/v1/payments/{id}/capture` | **Requires `Idempotency-Key`** |
| `POST` | `/v1/payments/{id}/void` | **Requires `Idempotency-Key`** |
| `POST` | `/v1/payments/{id}/refunds` | **Requires `Idempotency-Key`** |
| `GET` | `/v1/admin/invariants` | 200 when healthy, 500 when an invariant is broken |
| `GET` | `/v1/admin/outbox/depth` | Backlog by state |
| `POST` | `/v1/admin/outbox/requeue` | Return parked messages to the pending pool |
| `GET` | `/health/live` | Liveness: checks nothing, by design |
| `GET` | `/health/ready` | Readiness: database, outbox depth, broker |
| `GET` | `/metrics` | Prometheus exposition |

**Amounts are always integer minor units.** A JSON number parsed as a double cannot
represent every decimal exactly, and the one field where that must never happen is the one
saying how much money to move.

Status codes follow a single rule: **422** means the request is wrong and resending it
unchanged will fail the same way; **409** means it conflicts with the resource's current
state, so re-reading and retrying may work; **500** means a bug in FlowPay.

One case looks surprising and is deliberate: a payment declined for insufficient funds
returns **201 with a `Failed` payment**, not a 4xx. The request succeeded in creating a
durable, addressable record of a decline, and the client needs that record's id to
reconcile against.

Replayed responses carry `Idempotent-Replay: true`, so a client can tell "my retry was
accepted" from "my retry did the work".

---

## Testing and verification

```bash
dotnet test tests/FlowPay.UnitTests          # domain rules, no I/O
dotnet test tests/FlowPay.IntegrationTests   # real PostgreSQL + RabbitMQ via Testcontainers
```

The integration tests use **real containers, not in-memory substitutes**, because the
guarantees under test do not exist outside PostgreSQL. `ON CONFLICT DO NOTHING`,
`FOR UPDATE`, `SKIP LOCKED` and the `CHECK` constraints *are* the mechanism — a fake would
either reimplement them (testing the fake) or ignore them (testing nothing).

Among the scenarios they cover:

- hundreds of replays of one key resolve to one payment and one debit;
- 32 genuinely concurrent requests with the same key produce exactly one payment;
- a key reused with different parameters is rejected rather than replayed;
- 40 concurrent refunds against a payment that can absorb 10 leave it refunded by exactly
  its captured amount;
- simultaneous payments A→B and B→A do not deadlock;
- 30 concurrent payments out of an account funded for 10 settle exactly 10;
- a rejected payment leaves no outbox row behind;
- a redelivered event is applied once, and two consumers each apply it once.

### The load and correctness harness

`tools/FlowPay.LoadTest` drives the running stack and **exits non-zero when a correctness
property is violated**, so it works as a CI gate rather than something a person has to
read and judge.

```bash
# Sustained rate with a full latency distribution
dotnet run -c Release --project tools/FlowPay.LoadTest -- \
  --scenario throughput --rps 400 --duration 120 --csv throughput.csv

# A large batch of payments, then a whole-ledger invariant check
dotnet run -c Release --project tools/FlowPay.LoadTest -- \
  --scenario soak --operations 100000 --concurrency 64

# One idempotency key, submitted 10,000 times concurrently
dotnet run -c Release --project tools/FlowPay.LoadTest -- \
  --scenario replay --operations 10000 --concurrency 32

# Concurrent refunds aimed at single payments
dotnet run -c Release --project tools/FlowPay.LoadTest -- \
  --scenario refundstorm --concurrency 64

# The invariant check on its own
dotnet run -c Release --project tools/FlowPay.LoadTest -- --scenario verify
```

Two details make the throughput numbers worth reading:

- **The run is rate-paced, not open-throttle.** Firing as fast as the client can means
  every request queues behind the harness's own backlog, and the measured latency becomes
  a property of the load generator rather than the service.
- **Percentiles come from the complete sample, by nearest rank.** An interpolated p95
  reports a latency no request actually experienced; for a latency objective, the honest
  answer is a real observation.

The replay scenario does not trust the responses it gets back. It reads the payer's
balance before and after and asserts the difference is exactly one payment.

---

## Finding and fixing the throughput ceiling

The first honest throughput run failed, and the way it failed is the most interesting
thing in the repository.

Against a 400 rps target the stack sustained **179 rps with a p95 of 564 ms** — not
slightly short, but stuck. Three symptoms narrowed it down quickly:

1. Raising the number of payer accounts from 64 to 512 changed nothing (179 → 194 rps). So
   the contention was not between payers and payees.
2. `pg_stat_activity` showed **62 of 64 connections** in `Lock / tuple`, all waiting on the
   same `SELECT ... FOR UPDATE` against `accounts`.
3. `pg_locks` confirmed **60 ungranted locks on a single tuple** in that table.

The cause was a design decision that looked harmless. Fees are credited to a real account
so that each capture's ledger lines still sum to zero — but there was **one fee account per
currency**, so every capture in the system had to lock that one row. PostgreSQL holds a row
lock until commit, so throughput was pinned to one divided by the mean transaction
duration, no matter how many API replicas or distinct accounts were involved. The fee
account's id also happened to sort first, so the lock was acquired at the start of each
transaction and held through every remaining statement.

The fix was to **shard the fee accounts**: each capture derives one of N fee accounts from
its payment id, so concurrent captures contend only when they collide on a shard.
Deriving the shard from the payment id rather than at random means a retried capture
resolves to the same account, and because every shard is a real account, the double-entry
invariant and the balance reconciliation carry over unchanged. Total fee income is a sum
across the family.

With 16 shards, on the same hardware and the same workload:

| | Before | After |
| --- | --- | --- |
| Sustained throughput | 179 rps | **399 rps** |
| p50 latency | 367 ms | **38 ms** |
| p95 latency | 564 ms | **80 ms** |
| p99 latency | 798 ms | **125 ms** |
| Failed requests | 0 | 0 |

The implementation and the reasoning live in
[`FeeAccountShards`](src/FlowPay.Domain/Payments/FeeAccountShards.cs).

These figures come from a single six-core laptop running everything at once — PostgreSQL,
RabbitMQ, three API replicas, two workers and the load generator — so they are a floor
rather than a ceiling. Reproduce them with `scripts/load-test.sh`.

## Benchmarks

```bash
dotnet run -c Release --project bench/FlowPay.Benchmarks -- --filter '*'

# Or one group
dotnet run -c Release --project bench/FlowPay.Benchmarks -- --filter '*StateTransitionBenchmarks*'
```

Release is not optional — a Debug build disables inlining and most of the JIT
optimizations these numbers depend on, and BenchmarkDotNet refuses to run against one.

The suite covers `Money` and `Currency` operations, request validation, idempotency key
parsing and fingerprinting, fee calculation, and the lifecycle transitions. Every class
carries `[MemoryDiagnoser]`, so the allocation column is part of the result.

A note on how the transition benchmarks are built, because it changes what the numbers
mean. A lifecycle transition is **destructive** — a captured payment cannot be captured
twice — so a naive benchmark measures the first call and then measures the rejection path
forever. Recreating the aggregate in an `[IterationSetup]` forces BenchmarkDotNet down to
one invocation per iteration, which for a nanosecond-scale operation produces a standard
deviation larger than the mean. Instead the destructive benchmarks work through a pool of
4,096 pre-built aggregates with `OperationsPerInvoke` dividing the cost back down, and the
**rejection** paths — which leave the aggregate unchanged by design — are measured
directly at full precision.

`FeeCalculationBenchmarks` includes the `decimal` implementation this design rejected,
computing the same fee with the same rounding mode, so the choice of integer minor units
rests on a measured gap rather than an assumption.

---

## Project layout

```
src/
  FlowPay.Domain/           Money, Currency, IdempotencyKey, FeeSchedule,
                            PaymentStateMachine, Account, PaymentIntent, double-entry
  FlowPay.Application/      IdempotentExecutor, PaymentService, SettlementEngine,
                            outbox contracts, integration events, DTOs
  FlowPay.Infrastructure/   EF Core model, repositories with FOR UPDATE, idempotency
                            store, outbox dispatcher, RabbitMQ, projections, migrations
  FlowPay.Api/              Minimal API endpoints, OpenTelemetry, health checks
  FlowPay.Worker/           Outbox dispatcher, consumers, expiry and purge sweeps
tests/
  FlowPay.UnitTests/        Domain rules, no I/O
  FlowPay.IntegrationTests/ Testcontainers: PostgreSQL + RabbitMQ
bench/
  FlowPay.Benchmarks/       BenchmarkDotNet, all classes with [MemoryDiagnoser]
tools/
  FlowPay.LoadTest/         Throughput, soak, replay, refund storm, verify
deploy/nginx/               Gateway config that re-resolves scaled API replicas
```

Schema lives in hand-written, numbered SQL under
`src/FlowPay.Infrastructure/Migrations/`, applied at startup by a small runner that takes
a PostgreSQL advisory lock so several instances starting at once cannot race. The DDL is
written by hand rather than generated because the constraints *are* the point — the
over-refund check, the non-negative balance checks, the partial index the dispatcher
depends on, the generated signed-amount column — and they deserve to be legible.

---

## Design decisions and trade-offs

**One transaction for the idempotency claim and the work.** The alternative — claim in one
transaction, work in another — needs leases, crash recovery, and a story for what a
half-finished claim means. This design needs none of that. The cost is holding the claim
row's lock for the duration of the work, which is what makes a concurrent duplicate wait
rather than fail.

**READ COMMITTED, not a stricter isolation level.** The claim has to see a competing
transaction's committed row immediately after unblocking on the unique index, which a
snapshot taken at transaction start would hide. Serialization is bought where it is needed
— with row locks — rather than globally.

**Locking accounts one row at a time in sorted order.** A single
`WHERE id = ANY(...) ORDER BY id FOR UPDATE` would be one round trip instead of two or
three, but PostgreSQL does not guarantee it takes the locks in the order the sort produced.
Sequential locking in a globally consistent order makes a deadlock impossible rather than
merely recoverable. The extra round trips are sub-millisecond and happen at most three
times per payment.

**EF Core's execution strategy is deliberately disabled.** It retries whole operations,
which is unsafe for code managing its own transaction — a retry would re-run the
idempotency claim against a transaction that no longer exists. Retries belong at the HTTP
layer, where the idempotency key makes them safe.

**Currency is a packed `uint` in memory and `char(3)` in the database.** Equality on the
hot path is one integer compare; the rows are read by people during incidents, and `'USD'`
is worth more there than `4478293`. The conversion happens once per row on
materialization, well off the hot path.

**UUIDv7 for every identifier.** Time-ordered keys land at the right-hand edge of the
primary-key B-tree instead of scattering across it. Under sustained insert load that is the
difference between appending to a hot page and dirtying a random page per row.

**Pooled `DbContext` and source-generated JSON.** At a few hundred requests a second, EF's
per-instance setup and reflection-based serialization are a measurable share of request
time. Both are on the p95 path twice for idempotent writes, since the response is
serialized again to be stored for replay.

**A degraded broker does not fail readiness.** An unreachable RabbitMQ or a deep outbox is
reported as *degraded*, not *unhealthy*: payments are still being accepted and committed
correctly, and taking the instance out of the load balancer would reduce the capacity
available to drain the backlog.

**Liveness checks nothing.** A dependency outage must not cause the orchestrator to kill a
healthy process that would recover on its own.

### Known limits

- No authentication or authorization. Every endpoint is open; `/v1/admin/*` is grouped
  under one prefix so a deployment can restrict it at the ingress, but nothing enforces it
  here.
- Single-currency accounts and no FX. A payment's two accounts must share a currency.
- The outbox dispatcher polls. At much higher volumes `LISTEN`/`NOTIFY` or logical
  decoding would cut the latency floor that the poll interval imposes.
- Authorization expiry sweeps on an interval rather than at the exact expiry instant.
- No partitioning or archival. `ledger_entries` and `outbox_messages` grow without bound;
  a real deployment would partition by time and move cold data out.
