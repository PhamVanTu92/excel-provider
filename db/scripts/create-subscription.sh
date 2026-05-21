#!/usr/bin/env sh
# ════════════════════════════════════════════════════════════════════════════
# create-subscription.sh
# Chạy bởi service excel-reporting-setup SAU KHI postgres-excel healthy.
# Lúc này TCP/IP đã sẵn sàng nên subscription connect được.
# Idempotent: bỏ qua nếu subscription đã tồn tại.
# ════════════════════════════════════════════════════════════════════════════
set -e

echo "==> [subscription-setup] Kiểm tra subscription..."

EXISTS=$(psql -h postgres-excel -U excel -d excel_reporting \
    -tAc "SELECT COUNT(*) FROM pg_subscription WHERE subname = 'excel_reporting_sub'")

if [ "$EXISTS" = "0" ]; then
    echo "==> [subscription-setup] Tạo subscription excel_reporting_sub..."
    psql -h postgres-excel -U excel -d excel_reporting \
        -c "CREATE SUBSCRIPTION excel_reporting_sub
            CONNECTION 'host=postgres-excel port=5432 dbname=excel_provider user=excel password=excel'
            PUBLICATION excel_reporting_pub"
    echo "==> [subscription-setup] Subscription tạo thành công — initial data copy đang chạy ngầm."
else
    echo "==> [subscription-setup] Subscription đã tồn tại, bỏ qua."
fi
