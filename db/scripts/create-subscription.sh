#!/usr/bin/env sh
# ════════════════════════════════════════════════════════════════════════════
# create-subscription.sh
# Chạy bởi service excel-reporting-setup SAU KHI postgres-excel healthy.
# Lúc này TCP/IP đã sẵn sàng nên subscription connect được.
# Idempotent: bỏ qua nếu subscription đã tồn tại.
#
# LƯU Ý QUAN TRỌNG (cùng một cluster):
#   excel_provider (publisher) và excel_reporting (subscriber) nằm trong CÙNG
#   một PostgreSQL cluster. Nếu để CREATE SUBSCRIPTION tự tạo replication slot,
#   lệnh sẽ TREO vì nó mở kết nối ngược lại chính cluster để tạo slot.
#   => Tạo slot THỦ CÔNG trên publisher trước, rồi CREATE SUBSCRIPTION với
#      WITH (create_slot = false, slot_name = ...).
# ════════════════════════════════════════════════════════════════════════════
set -e

SLOT="excel_reporting_sub"
SUB="excel_reporting_sub"
PUB="excel_reporting_pub"

echo "==> [subscription-setup] Kiểm tra subscription..."

EXISTS=$(psql -h postgres-excel -U excel -d excel_reporting \
    -tAc "SELECT COUNT(*) FROM pg_subscription WHERE subname = '$SUB'")

if [ "$EXISTS" != "0" ]; then
    echo "==> [subscription-setup] Subscription đã tồn tại, bỏ qua."
    exit 0
fi

# ── Bước 1: Tạo replication slot thủ công trên publisher (excel_provider) ──────
# Idempotent: bỏ qua nếu slot đã tồn tại (vd từ lần chạy trước bị hủy giữa chừng).
SLOT_EXISTS=$(psql -h postgres-excel -U excel -d excel_provider \
    -tAc "SELECT COUNT(*) FROM pg_replication_slots WHERE slot_name = '$SLOT'")

if [ "$SLOT_EXISTS" = "0" ]; then
    echo "==> [subscription-setup] Tạo replication slot '$SLOT' trên excel_provider..."
    psql -h postgres-excel -U excel -d excel_provider \
        -c "SELECT pg_create_logical_replication_slot('$SLOT', 'pgoutput')"
else
    echo "==> [subscription-setup] Slot '$SLOT' đã tồn tại, dùng lại."
fi

# ── Bước 2: Tạo subscription, KHÔNG để nó tự tạo slot (tránh treo) ─────────────
echo "==> [subscription-setup] Tạo subscription '$SUB'..."
psql -h postgres-excel -U excel -d excel_reporting \
    -c "CREATE SUBSCRIPTION $SUB
        CONNECTION 'host=postgres-excel port=5432 dbname=excel_provider user=excel password=excel'
        PUBLICATION $PUB
        WITH (create_slot = false, slot_name = '$SLOT')"

echo "==> [subscription-setup] Subscription tạo thành công — initial data copy đang chạy ngầm."
