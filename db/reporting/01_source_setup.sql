-- ════════════════════════════════════════════════════════════════════════════
-- 01_source_setup.sql
-- Run against: excel_provider database (as superuser / postgres)
-- Purpose   : Enable logical replication on the cluster and create the
--             publication that the reporting DB will subscribe to.
--
-- IMPORTANT: After the ALTER SYSTEM commands you MUST restart PostgreSQL
--            before continuing with the remaining scripts.
-- ════════════════════════════════════════════════════════════════════════════

-- ── Step 1: Enable logical replication (requires PostgreSQL restart) ──────────

ALTER SYSTEM SET wal_level            = logical;
ALTER SYSTEM SET max_replication_slots = 5;
ALTER SYSTEM SET max_wal_senders       = 5;

-- Reload config (for non-wal_level settings only).
-- wal_level itself requires a full restart — see README.
SELECT pg_reload_conf();

-- ── Step 2: Grant REPLICATION privilege to the excel user ─────────────────────
-- (Run this AFTER restarting PostgreSQL)

ALTER ROLE excel REPLICATION;

-- ── Step 3: Create publication on the source DB ───────────────────────────────
-- Publishes all INSERT / UPDATE / DELETE on the three reporting tables.

CREATE PUBLICATION excel_reporting_pub
    FOR TABLE sales, products, regions;

-- Verify:
-- SELECT * FROM pg_publication;
-- SELECT * FROM pg_publication_tables WHERE pubname = 'excel_reporting_pub';
