-- ════════════════════════════════════════════════════════════════════════════
-- 03_create_subscription.sql
-- Run against: excel_reporting database (as superuser / postgres)
-- Purpose   : Create the logical replication subscription.
--             PostgreSQL will immediately copy all existing rows from
--             excel_provider and then stream ongoing changes.
--
-- Prerequisites:
--   ✓ 01_source_setup.sql has been run and PostgreSQL restarted
--   ✓ 02_reporting_schema.sql has been run (tables must exist)
--   ✓ pg_hba.conf allows replication connections from localhost
--
-- IMPORTANT (same cluster): publisher (excel_provider) and subscriber
-- (excel_reporting) live in the SAME PostgreSQL cluster. Letting
-- CREATE SUBSCRIPTION create the slot itself would HANG. So we create the
-- slot manually on the publisher first, then subscribe with create_slot=false.
-- ════════════════════════════════════════════════════════════════════════════

-- Step A — on the PUBLISHER (excel_provider): create the slot manually.
--   psql -d excel_provider -c \
--     "SELECT pg_create_logical_replication_slot('excel_reporting_sub','pgoutput');"

-- Step B — on the SUBSCRIBER (excel_reporting): subscribe, reusing that slot.
CREATE SUBSCRIPTION excel_reporting_sub
    CONNECTION 'host=localhost port=5434 dbname=excel_provider user=excel password=excel'
    PUBLICATION excel_reporting_pub
    WITH (create_slot = false, slot_name = 'excel_reporting_sub');

-- Subscription options explained:
--   copy_data = true  (default) — initial full table copy happens automatically
--   enabled   = true  (default) — starts streaming immediately after copy
--   synchronous_commit = off    — replica can lag slightly; safe for reporting

-- Verify after a few seconds:
-- SELECT subname, subenabled, subslotname FROM pg_subscription;
-- SELECT * FROM pg_stat_subscription;
--
-- Check replication lag on source (excel_provider):
-- SELECT slot_name, active, confirmed_flush_lsn FROM pg_replication_slots;
