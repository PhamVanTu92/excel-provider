#!/usr/bin/env bash
# ════════════════════════════════════════════════════════════════════════════
# 00_setup_reporting.sh — chạy tự động khi PostgreSQL init lần đầu
# Chỉ làm steps 1-5 (không tạo subscription ở đây vì TCP chưa sẵn sàng).
# Subscription được tạo bởi service excel-reporting-setup sau khi postgres healthy.
# ════════════════════════════════════════════════════════════════════════════
set -e

echo ">>> [reporting-init] Step 1: Tạo database excel_reporting..."
psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" --no-password <<-EOSQL
    CREATE DATABASE excel_reporting OWNER "$POSTGRES_USER";
EOSQL

echo ">>> [reporting-init] Step 2: Cấp quyền REPLICATION..."
psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" --no-password <<-EOSQL
    ALTER ROLE "$POSTGRES_USER" REPLICATION;
EOSQL

echo ">>> [reporting-init] Step 3: Tạo tables trên excel_provider..."
psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" --no-password <<-EOSQL
    CREATE TABLE IF NOT EXISTS sales (
        id          BIGSERIAL      PRIMARY KEY,
        sale_date   DATE           NOT NULL,
        region      TEXT           NOT NULL,
        product     TEXT           NOT NULL,
        category    TEXT           NOT NULL,
        revenue     DECIMAL(12,2)  NOT NULL CHECK (revenue > 0),
        units       INT            NOT NULL CHECK (units >= 0),
        channel     TEXT           NOT NULL CHECK (channel IN ('Online','Store')),
        created_at  TIMESTAMPTZ    DEFAULT NOW(),
        updated_at  TIMESTAMPTZ    DEFAULT NOW()
    );
    CREATE INDEX IF NOT EXISTS idx_sales_date   ON sales(sale_date);
    CREATE INDEX IF NOT EXISTS idx_sales_region ON sales(region);

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
EOSQL

echo ">>> [reporting-init] Step 4: Tạo publication trên excel_provider..."
psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" --no-password <<-EOSQL
    DO \$\$
    BEGIN
        IF NOT EXISTS (
            SELECT 1 FROM pg_publication WHERE pubname = 'excel_reporting_pub'
        ) THEN
            CREATE PUBLICATION excel_reporting_pub FOR TABLE sales, products, regions;
            RAISE NOTICE 'Publication excel_reporting_pub created.';
        ELSE
            RAISE NOTICE 'Publication excel_reporting_pub already exists, skipping.';
        END IF;
    END;
    \$\$;
EOSQL

echo ">>> [reporting-init] Step 5: Tạo schema và triggers trên excel_reporting..."
psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "excel_reporting" --no-password <<-EOSQL
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
    CREATE INDEX IF NOT EXISTS idx_rep_sales_date    ON sales(sale_date);
    CREATE INDEX IF NOT EXISTS idx_rep_sales_region  ON sales(region);
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

    CREATE OR REPLACE FUNCTION notify_reporting_change()
    RETURNS TRIGGER LANGUAGE plpgsql AS \$\$
    BEGIN
        PERFORM pg_notify('reporting_data_changed', TG_TABLE_NAME);
        RETURN NULL;
    END;
    \$\$;

    DROP TRIGGER IF EXISTS trg_sales_notify    ON sales;
    DROP TRIGGER IF EXISTS trg_products_notify ON products;
    DROP TRIGGER IF EXISTS trg_regions_notify  ON regions;

    -- FOR EACH ROW (không phải STATEMENT): logical replication apply chạy theo
    -- từng dòng, KHÔNG bao giờ kích hoạt trigger mức STATEMENT.
    -- ENABLE ALWAYS: apply worker chạy với session_replication_role = 'replica',
    -- nên trigger mặc định (ORIGIN) bị bỏ qua; phải ALWAYS mới fire khi dữ liệu
    -- đến qua replication. (Listener .NET đã debounce nên không lo spam notify.)
    CREATE TRIGGER trg_sales_notify
        AFTER INSERT OR UPDATE OR DELETE ON sales
        FOR EACH ROW EXECUTE FUNCTION notify_reporting_change();
    ALTER TABLE sales ENABLE ALWAYS TRIGGER trg_sales_notify;

    CREATE TRIGGER trg_products_notify
        AFTER INSERT OR UPDATE OR DELETE ON products
        FOR EACH ROW EXECUTE FUNCTION notify_reporting_change();
    ALTER TABLE products ENABLE ALWAYS TRIGGER trg_products_notify;

    CREATE TRIGGER trg_regions_notify
        AFTER INSERT OR UPDATE OR DELETE ON regions
        FOR EACH ROW EXECUTE FUNCTION notify_reporting_change();
    ALTER TABLE regions ENABLE ALWAYS TRIGGER trg_regions_notify;
EOSQL

echo ">>> [reporting-init] Steps 1-5 hoàn tất! Subscription sẽ được tạo bởi excel-reporting-setup service."
