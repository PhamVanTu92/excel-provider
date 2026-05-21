# Reporting DB — Setup Guide

Thiết lập `excel_reporting` DB nhận dữ liệu realtime từ `excel_provider`
qua PostgreSQL Logical Replication.

## Kiến trúc

```
excel_provider (source, port 5434)
    └─ PUBLICATION excel_reporting_pub (sales, products, regions)
            │ WAL streaming
            ▼
excel_reporting (replica, port 5434 — cùng cluster)
    └─ SUBSCRIPTION excel_reporting_sub
    └─ pg_notify triggers → ReplicationListenerService (.NET)
            │ NotifyDataChangedAsync
            ▼
    HDOS Ingestion API → realtime frontend
```

## Thứ tự thực hiện (chỉ làm 1 lần)

### Bước 0 — Tạo database excel_reporting

```bash
psql -h localhost -p 5434 -U postgres -c "CREATE DATABASE excel_reporting OWNER excel;"
```

### Bước 1 — Bật Logical Replication (yêu cầu restart PostgreSQL)

```bash
# Kết nối vào excel_provider với quyền superuser
psql -h localhost -p 5434 -U postgres -d excel_provider -f 01_source_setup.sql
```

Sau đó **restart PostgreSQL**:
```bash
# Docker
docker restart <postgres-container-name>

# hoặc systemd
sudo systemctl restart postgresql
```

### Bước 2 — Tạo schema + triggers trên Reporting DB

```bash
psql -h localhost -p 5434 -U excel -d excel_reporting -f 02_reporting_schema.sql
```

> **Lưu ý:** Bước này được ứng dụng tự thực hiện khi khởi động
> (`ReportingDb.InitializeAsync()`). Chạy thủ công chỉ khi cần debug.

### Bước 3 — Tạo Subscription (chạy với quyền superuser)

```bash
psql -h localhost -p 5434 -U postgres -d excel_reporting -f 03_create_subscription.sql
```

PostgreSQL sẽ **tự động copy toàn bộ data hiện có** từ `excel_provider`
và stream ongoing changes sau đó.

---

## Kiểm tra trạng thái

```sql
-- Trên excel_provider: xem publication
SELECT * FROM pg_publication_tables WHERE pubname = 'excel_reporting_pub';

-- Trên excel_provider: xem replication slot
SELECT slot_name, active, confirmed_flush_lsn FROM pg_replication_slots;

-- Trên excel_reporting: xem subscription
SELECT subname, subenabled FROM pg_subscription;

-- Trên excel_reporting: xem lag
SELECT * FROM pg_stat_subscription;

-- Kiểm tra data được sync
SELECT count(*) FROM sales;      -- phải bằng số row ở source
SELECT count(*) FROM products;
SELECT count(*) FROM regions;
```

---

## Lưu ý với Docker

Nếu PostgreSQL chạy trong Docker container:
- `localhost` trong subscription connection string là **IP của container** (thường là `127.0.0.1` bên trong container)
- Nếu gặp lỗi connection refused, thử dùng `host=127.0.0.1` hoặc hostname của container
- Đảm bảo `pg_hba.conf` cho phép replication connections:
  ```
  host replication excel 127.0.0.1/32 md5
  ```

## Xoá subscription (khi cần reset)

```sql
-- Trên excel_reporting (superuser):
DROP SUBSCRIPTION IF EXISTS excel_reporting_sub;

-- Trên excel_provider (superuser) — xoá replication slot:
SELECT pg_drop_replication_slot('excel_reporting_sub');
```
