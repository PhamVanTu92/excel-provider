-- V009: Seed Excel Data Provider registration
-- NOTE: This migration only runs on FIRST Postgres start (initdb).
-- For existing deployments, the Excel.Provider service auto-seeds on startup.
--
-- Provider credentials:
--   clientId:     excel-provider
--   clientSecret: excel-secret-dev-2024   ← hashed below via pgcrypto

CREATE EXTENSION IF NOT EXISTS pgcrypto;

-- ── Provider registration ─────────────────────────────────────────────────────

INSERT INTO provider_registry (
    provider_id,
    display_name,
    description,
    client_id,
    client_secret_hash,
    operations,
    timeout_ms,
    priority,
    status
)
VALUES (
    'excel-provider',
    'Excel Data Provider',
    'Serves realtime dashboard KPIs and analytical reports from Excel data source',
    'excel-provider',
    crypt('excel-secret-dev-2024', gen_salt('bf', 11)),
    ARRAY[
        'report.dashboard.summary',
        'report.sales.trend',
        'report.inventory.status',
        'report.regional.performance'
    ],
    60000,
    5,
    'active'
)
ON CONFLICT (provider_id) DO NOTHING;

-- ── Operation registry ────────────────────────────────────────────────────────

INSERT INTO operation_registry (
    operation_pattern,
    handler_type,
    provider_id,
    params_schema,
    payload_schema,
    timeout_ms,
    cacheable,
    cache_ttl_seconds,
    idempotent,
    status
)
VALUES

-- Realtime dashboard KPIs
(
    'report.dashboard.summary',
    'provider',
    'excel-provider',
    '{
        "type": "object",
        "properties": {
            "date": {"type": "string", "format": "date", "description": "ISO date, defaults to today"}
        },
        "additionalProperties": false
    }'::jsonb,
    '{
        "type": "object",
        "required": ["totalRevenue","totalUnits","topRegion","topProduct","revenueByChannel","alerts"],
        "properties": {
            "totalRevenue":      {"type": "number"},
            "totalUnits":        {"type": "integer"},
            "topRegion":         {"type": "string"},
            "topProduct":        {"type": "string"},
            "revenueByChannel":  {"type": "object", "properties": {"online":{"type":"number"},"store":{"type":"number"}}},
            "alerts":            {"type": "array", "items": {"type": "string"}}
        }
    }'::jsonb,
    30000,
    TRUE,
    60,
    TRUE,
    'active'
),

-- Sales trend analytical report
(
    'report.sales.trend',
    'provider',
    'excel-provider',
    '{
        "type": "object",
        "required": ["fromDate","toDate"],
        "properties": {
            "fromDate": {"type": "string", "format": "date"},
            "toDate":   {"type": "string", "format": "date"},
            "groupBy":  {"type": "string", "enum": ["day","week","month"], "default": "day"}
        },
        "additionalProperties": false
    }'::jsonb,
    '{
        "type": "object",
        "required": ["labels","series"],
        "properties": {
            "labels": {"type": "array", "items": {"type": "string"}},
            "series": {
                "type": "array",
                "items": {
                    "type": "object",
                    "properties": {
                        "name": {"type": "string"},
                        "data": {"type": "array", "items": {"type": "number"}}
                    }
                }
            }
        }
    }'::jsonb,
    60000,
    TRUE,
    300,
    TRUE,
    'active'
),

-- Inventory status
(
    'report.inventory.status',
    'provider',
    'excel-provider',
    '{"type": "object", "additionalProperties": false}'::jsonb,
    '{
        "type": "object",
        "required": ["products","summary"],
        "properties": {
            "products": {
                "type": "array",
                "items": {
                    "type": "object",
                    "properties": {
                        "name":     {"type": "string"},
                        "category": {"type": "string"},
                        "stock":    {"type": "integer"},
                        "status":   {"type": "string", "enum": ["ok","low","out"]}
                    }
                }
            },
            "summary": {
                "type": "object",
                "properties": {"ok":{"type":"integer"},"low":{"type":"integer"},"out":{"type":"integer"}}
            }
        }
    }'::jsonb,
    30000,
    TRUE,
    120,
    TRUE,
    'active'
),

-- Regional performance
(
    'report.regional.performance',
    'provider',
    'excel-provider',
    '{
        "type": "object",
        "properties": {
            "period": {"type": "string", "enum": ["today","week","month"], "default": "today"}
        },
        "additionalProperties": false
    }'::jsonb,
    '{
        "type": "object",
        "required": ["regions"],
        "properties": {
            "regions": {
                "type": "array",
                "items": {
                    "type": "object",
                    "properties": {
                        "name":           {"type": "string"},
                        "revenue":        {"type": "number"},
                        "units":          {"type": "integer"},
                        "target":         {"type": "number"},
                        "achievementPct": {"type": "number"}
                    }
                }
            }
        }
    }'::jsonb,
    30000,
    TRUE,
    60,
    TRUE,
    'active'
)

ON CONFLICT (operation_pattern) DO NOTHING;
