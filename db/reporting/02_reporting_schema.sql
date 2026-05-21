-- ════════════════════════════════════════════════════════════════════════════
-- 02_reporting_schema.sql
-- Run against: excel_reporting database (as excel user)
-- Purpose   : Create the replica schema (tables + indexes) and the pg_notify
--             trigger that wakes up ReplicationListenerService in .NET.
--
-- NOTE: Tables use BIGINT (not BIGSERIAL) for id because primary keys are
--       replicated from the source — the reporting DB never generates them.
-- ════════════════════════════════════════════════════════════════════════════

-- ── Tables ────────────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS sales (
    id          BIGINT         PRIMARY KEY,
    sale_date   DATE           NOT NULL,
    region      TEXT           NOT NULL,
    product     TEXT           NOT NULL,
    category    TEXT           NOT NULL,
    revenue     DECIMAL(12,2)  NOT NULL,
    units       INT            NOT NULL,
    channel     TEXT           NOT NULL,
    created_at  TIMESTAMPTZ    DEFAULT NOW(),
    updated_at  TIMESTAMPTZ    DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_rep_sales_date   ON sales(sale_date);
CREATE INDEX IF NOT EXISTS idx_rep_sales_region ON sales(region);
CREATE INDEX IF NOT EXISTS idx_rep_sales_product ON sales(product);

CREATE TABLE IF NOT EXISTS products (
    product_id    TEXT           PRIMARY KEY,
    name          TEXT           NOT NULL,
    category      TEXT           NOT NULL,
    price         DECIMAL(10,2)  NOT NULL,
    current_stock INT            NOT NULL DEFAULT 0,
    min_stock     INT            NOT NULL DEFAULT 10,
    updated_at    TIMESTAMPTZ    DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS regions (
    region_id       TEXT           PRIMARY KEY,
    name            TEXT           NOT NULL,
    manager         TEXT           NOT NULL,
    monthly_target  DECIMAL(12,2)  NOT NULL,
    yearly_target   DECIMAL(12,2)  NOT NULL
);

-- ── pg_notify trigger ─────────────────────────────────────────────────────────
-- Fires after any DML on the replicated tables.
-- Payload = table name so ReplicationListenerService can map to operations.

CREATE OR REPLACE FUNCTION notify_reporting_change()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    PERFORM pg_notify('reporting_data_changed', TG_TABLE_NAME);
    RETURN NULL;
END;
$$;

-- Drop first to allow idempotent re-runs.
DROP TRIGGER IF EXISTS trg_sales_notify    ON sales;
DROP TRIGGER IF EXISTS trg_products_notify ON products;
DROP TRIGGER IF EXISTS trg_regions_notify  ON regions;

CREATE TRIGGER trg_sales_notify
    AFTER INSERT OR UPDATE OR DELETE ON sales
    FOR EACH STATEMENT EXECUTE FUNCTION notify_reporting_change();

CREATE TRIGGER trg_products_notify
    AFTER INSERT OR UPDATE OR DELETE ON products
    FOR EACH STATEMENT EXECUTE FUNCTION notify_reporting_change();

CREATE TRIGGER trg_regions_notify
    AFTER INSERT OR UPDATE OR DELETE ON regions
    FOR EACH STATEMENT EXECUTE FUNCTION notify_reporting_change();

-- Verify:
-- SELECT tgname, tgrelid::regclass FROM pg_trigger WHERE tgname LIKE 'trg_%_notify';
