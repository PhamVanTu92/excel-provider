#!/usr/bin/env bash
# ════════════════════════════════════════════════════════════════════════════
# 00_setup_reporting.sh
# Chạy tự động bởi PostgreSQL Docker entrypoint lần đầu khởi động.
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
            CREATE PUBLICATION excel_reporting_pub
                FOR TABLE sales, products, regions;
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

    CREATE TRIGGER trg_sales_notify
        AFTER INSERT OR UPDATE OR DELETE ON sales
        FOR EACH STATEMENT EXECUTE FUNCTION notify_reporting_change();

    CREATE TRIGGER trg_products_notify
        AFTER INSERT OR UPDATE OR DELETE ON products
        FOR EACH STATEMENT EXECUTE FUNCTION notify_reporting_change();

    CREATE TRIGGER trg_regions_notify
        AFTER INSERT OR UPDATE OR DELETE ON regions
        FOR EACH STATEMENT EXECUTE FUNCTION notify_reporting_change();
EOSQL

echo ">>> [reporting-init] Step 6: Tạo subscription..."
SUB_COUNT=$(psql -U "$POSTGRES_USER" -d "excel_reporting" --no-password \
    -tAc "SELECT COUNT(*) FROM pg_subscription WHERE subname = 'excel_reporting_sub'" 2>/dev/null || echo "0")

if [ "$SUB_COUNT" = "0" ]; then
    psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "excel_reporting" --no-password <<-EOSQL
        CREATE SUBSCRIPTION excel_reporting_sub
            CONNECTION 'host=127.0.0.1 port=5432 dbname=${POSTGRES_DB} user=${POSTGRES_USER} password=${POSTGRES_PASSWORD}'
            PUBLICATION excel_reporting_pub;
EOSQL
    echo ">>> [reporting-init] Subscription created."
else
    echo ">>> [reporting-init] Subscription đã tồn tại, bỏ qua."
fi

echo ">>> [reporting-init] Setup hoàn tất!"
