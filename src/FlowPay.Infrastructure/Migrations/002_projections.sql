-- Read models maintained by the asynchronous consumers.
--
-- These exist to make the delivery guarantees observable. Both tables accumulate — the
-- summary adds to running totals, the audit log appends a row — so a message applied
-- twice produces a visibly wrong result. That is what makes them a real test of the
-- inbox deduplication rather than a decorative projection.

CREATE TABLE settlement_summaries (
    account_id                  uuid        NOT NULL PRIMARY KEY REFERENCES accounts (id),
    currency                    char(3)     NOT NULL,
    captured_count              bigint      NOT NULL DEFAULT 0,
    captured_gross_minor_units  bigint      NOT NULL DEFAULT 0,
    captured_net_minor_units    bigint      NOT NULL DEFAULT 0,
    fee_minor_units             bigint      NOT NULL DEFAULT 0,
    refunded_count              bigint      NOT NULL DEFAULT 0,
    refunded_minor_units        bigint      NOT NULL DEFAULT 0,
    updated_at                  timestamptz NOT NULL,

    CONSTRAINT ck_settlement_counts_non_negative
        CHECK (captured_count >= 0 AND refunded_count >= 0)
);

CREATE TABLE payment_audit_log (
    id          bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    event_id    uuid         NOT NULL,
    payment_id  uuid         NOT NULL,
    event_type  varchar(100) NOT NULL,
    occurred_at timestamptz  NOT NULL,
    recorded_at timestamptz  NOT NULL,
    payload     jsonb        NOT NULL
);

CREATE INDEX ix_payment_audit_payment ON payment_audit_log (payment_id);
CREATE INDEX ix_payment_audit_occurred ON payment_audit_log (occurred_at);

-- A second, independent guard on duplicate application. The inbox table is what the
-- consumer checks; this index means that even a consumer that skipped the check could not
-- write the same event twice.
CREATE UNIQUE INDEX ux_payment_audit_event ON payment_audit_log (event_id);
