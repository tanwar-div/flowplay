-- FlowPay initial schema.
--
-- The DDL is hand-written rather than generated so that the integrity constraints can be
-- stated explicitly. Several of them are the last line of defence for guarantees the
-- application also enforces: if a future code path forgets a check, these make the
-- incorrect row impossible to store rather than merely unlikely.

CREATE TABLE accounts (
    id                      uuid         NOT NULL PRIMARY KEY,
    currency                char(3)      NOT NULL,
    balance_minor_units     bigint       NOT NULL,

    -- Recorded at account creation and never updated. The reconciliation check asserts
    -- balance = opening_balance + SUM(ledger.signed_minor_units); deriving the opening
    -- balance instead would make that equation hold by construction.
    opening_balance_minor_units bigint   NOT NULL DEFAULT 0,

    reserved_minor_units    bigint       NOT NULL,
    status                  smallint     NOT NULL,
    display_name            varchar(200) NOT NULL,
    created_at              timestamptz  NOT NULL,
    updated_at              timestamptz  NOT NULL,

    -- An account can never be overdrawn, and the reserved portion can never exceed the
    -- balance backing it. Without the second constraint an authorization could reserve
    -- funds that a concurrent capture had already spent.
    CONSTRAINT ck_accounts_balance_non_negative
        CHECK (balance_minor_units >= 0),
    CONSTRAINT ck_accounts_reserved_non_negative
        CHECK (reserved_minor_units >= 0),
    CONSTRAINT ck_accounts_reserved_within_balance
        CHECK (reserved_minor_units <= balance_minor_units),
    CONSTRAINT ck_accounts_opening_balance_non_negative
        CHECK (opening_balance_minor_units >= 0)
);

CREATE INDEX ix_accounts_status ON accounts (status);

CREATE TABLE payments (
    id                          uuid         NOT NULL PRIMARY KEY,
    source_account_id           uuid         NOT NULL REFERENCES accounts (id),
    destination_account_id      uuid         NOT NULL REFERENCES accounts (id),
    amount_minor_units          bigint       NOT NULL,
    fee_minor_units             bigint       NOT NULL DEFAULT 0,
    refunded_minor_units        bigint       NOT NULL DEFAULT 0,
    currency                    char(3)      NOT NULL,
    status                      smallint     NOT NULL,
    method                      smallint     NOT NULL,
    failure_reason              smallint     NOT NULL DEFAULT 0,
    reference                   varchar(64)  NOT NULL DEFAULT '',
    description                 varchar(512) NOT NULL DEFAULT '',
    created_at                  timestamptz  NOT NULL,
    updated_at                  timestamptz  NOT NULL,
    authorized_at               timestamptz,
    captured_at                 timestamptz,
    authorization_expires_at    timestamptz,

    CONSTRAINT ck_payments_amount_positive
        CHECK (amount_minor_units > 0),
    CONSTRAINT ck_payments_fee_within_amount
        CHECK (fee_minor_units >= 0 AND fee_minor_units <= amount_minor_units),

    -- The over-refund invariant, at the storage layer. The aggregate rejects an
    -- over-refund and the row lock stops two refunds from racing, but this constraint is
    -- what makes an over-refunded payment unrepresentable: no bug anywhere above it can
    -- produce one.
    CONSTRAINT ck_payments_refund_within_amount
        CHECK (refunded_minor_units >= 0 AND refunded_minor_units <= amount_minor_units),

    CONSTRAINT ck_payments_distinct_accounts
        CHECK (source_account_id <> destination_account_id)
);

CREATE INDEX ix_payments_source_account ON payments (source_account_id);
CREATE INDEX ix_payments_destination_account ON payments (destination_account_id);
CREATE INDEX ix_payments_status_expiry ON payments (status, authorization_expires_at);

CREATE TABLE refunds (
    id                          uuid         NOT NULL PRIMARY KEY,
    payment_id                  uuid         NOT NULL REFERENCES payments (id),
    amount_minor_units          bigint       NOT NULL,
    currency                    char(3)      NOT NULL,
    status                      smallint     NOT NULL,
    reason                      varchar(256) NOT NULL DEFAULT '',
    idempotency_key             varchar(128) NOT NULL,
    resulting_payment_status    smallint     NOT NULL,
    created_at                  timestamptz  NOT NULL,

    CONSTRAINT ck_refunds_amount_positive CHECK (amount_minor_units > 0)
);

CREATE INDEX ix_refunds_payment ON refunds (payment_id);

-- Independent of the idempotency table: even a code path that bypassed the idempotency
-- layer entirely could not write two refunds under one key.
CREATE UNIQUE INDEX ux_refunds_idempotency_key ON refunds (idempotency_key);

CREATE TABLE ledger_entries (
    id                      uuid        NOT NULL PRIMARY KEY,
    payment_id              uuid        NOT NULL REFERENCES payments (id),
    refund_id               uuid        REFERENCES refunds (id),
    account_id              uuid        NOT NULL REFERENCES accounts (id),
    direction               smallint    NOT NULL,
    entry_type              smallint    NOT NULL,
    amount_minor_units      bigint      NOT NULL,
    currency                char(3)     NOT NULL,
    created_at              timestamptz NOT NULL,

    -- Direction carries the sign, so the amount itself is always positive and a
    -- "negative credit" cannot be written.
    CONSTRAINT ck_ledger_amount_positive CHECK (amount_minor_units > 0),
    CONSTRAINT ck_ledger_direction CHECK (direction IN (0, 1)),

    -- A stored generated column, so the invariant checks can sum signed amounts inside
    -- PostgreSQL instead of streaming the whole ledger into the application to add it up.
    signed_minor_units bigint NOT NULL
        GENERATED ALWAYS AS (
            CASE WHEN direction = 1 THEN amount_minor_units ELSE -amount_minor_units END
        ) STORED
);

CREATE INDEX ix_ledger_entries_payment ON ledger_entries (payment_id);
CREATE INDEX ix_ledger_entries_account ON ledger_entries (account_id);

CREATE TABLE idempotency_keys (
    endpoint                varchar(100) NOT NULL,
    key                     varchar(128) NOT NULL,
    request_fingerprint     bytea        NOT NULL,
    status                  smallint     NOT NULL,
    response_status_code    integer,

    -- text, not jsonb. jsonb normalizes whitespace and reorders object keys, so a
    -- replayed response would not be byte-identical to the original. A client retrying a
    -- timed-out request needs to receive exactly what its first attempt produced, which
    -- is worth more here than making the stored body queryable.
    response_body           text,
    resource_id             uuid,
    created_at              timestamptz  NOT NULL,
    completed_at            timestamptz,
    expires_at              timestamptz  NOT NULL,

    -- This primary key is the deduplication mechanism. INSERT ... ON CONFLICT DO NOTHING
    -- against it is an atomic claim that works across every API instance without a
    -- distributed lock, a leader, or a coordination service.
    CONSTRAINT pk_idempotency_keys PRIMARY KEY (endpoint, key)
);

CREATE INDEX ix_idempotency_expires_at ON idempotency_keys (expires_at);

CREATE TABLE outbox_messages (
    id              uuid          NOT NULL PRIMARY KEY,
    event_type      varchar(100)  NOT NULL,
    aggregate_id    uuid          NOT NULL,
    payload         jsonb         NOT NULL,
    status          smallint      NOT NULL,
    attempts        integer       NOT NULL DEFAULT 0,
    available_at    timestamptz   NOT NULL,
    occurred_at     timestamptz   NOT NULL,
    dispatched_at   timestamptz,
    last_error      varchar(1024),
    trace_id        varchar(64),

    CONSTRAINT ck_outbox_attempts_non_negative CHECK (attempts >= 0)
);

-- The dispatcher's only query is "pending and due, oldest first". A partial index over
-- exactly that predicate stays small however large the dispatched history grows, so
-- polling cost tracks the backlog rather than the table.
CREATE INDEX ix_outbox_pending
    ON outbox_messages (available_at, occurred_at)
    WHERE status = 0;

CREATE INDEX ix_outbox_failed
    ON outbox_messages (occurred_at)
    WHERE status = 2;

CREATE TABLE inbox_messages (
    message_id          uuid         NOT NULL,
    consumer            varchar(100) NOT NULL,
    event_type          varchar(100) NOT NULL,
    processed_at        timestamptz  NOT NULL,
    delivery_attempt    integer      NOT NULL DEFAULT 1,

    -- The consumer-side half of exactly-once effects. A redelivered message collides
    -- here and its transaction is abandoned, so the side effects apply once even though
    -- delivery is at-least-once.
    CONSTRAINT pk_inbox_messages PRIMARY KEY (message_id, consumer)
);

CREATE INDEX ix_inbox_processed_at ON inbox_messages (processed_at);
