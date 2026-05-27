# Hướng dẫn Mapping Dữ liệu — Excel Provider → HDOS Charts

> Phiên bản: 1.0 | Dự án: `E:\Project\excel-provider` + `E:\Project\HDOS`

---

## Mục lục

1. [Kiến trúc luồng dữ liệu](#1-kiến-trúc-luồng-dữ-liệu)
2. [Bản đồ Widget → Operation → Handler](#2-bản-đồ-widget--operation--handler)
3. [Chi tiết từng loại biểu đồ](#3-chi-tiết-từng-loại-biểu-đồ)
   - 3.1 `kpi_grid` — Doanh thu hôm nay
   - 3.2 `line_chart` — Xu hướng doanh thu
   - 3.3 `bar_chart` — Khu vực vs Mục tiêu
   - 3.4 `pie_chart` — Kênh bán hàng
   - 3.5 `progress_rows` — Tình trạng tồn kho
   - 3.6 `simple_table` — Bảng sản phẩm & Top performers
4. [Management API — Sửa dữ liệu để thấy biểu đồ thay đổi](#4-management-api--sửa-dữ-liệu-để-thấy-biểu-đồ-thay-đổi)
5. [Kịch bản demo — Xem dữ liệu thay đổi theo thời gian thực](#5-kịch-bản-demo--xem-dữ-liệu-thay-đổi-theo-thời-gian-thực)
6. [Thêm menu mới trên HDOS](#6-thêm-menu-mới-trên-hdos)
7. [Định dạng JSON cho từng chart type (RENDER_CONTRACTS)](#7-định-dạng-json-cho-từng-chart-type-render_contracts)
8. [Quy tắc mapping khi xây biểu đồ mới](#8-quy-tắc-mapping-khi-xây-biểu-đồ-mới)

---

## 1. Kiến trúc luồng dữ liệu

```
┌──────────────────────────────────────────────────────────────────────┐
│                         NGƯỜI DÙNG                                    │
│              Vào HDOS → mở menu → thấy biểu đồ                       │
└─────────────────────────────┬────────────────────────────────────────┘
                              │ HTTP/SignalR
                              ▼
┌──────────────────────────────────────────────────────────────────────┐
│  HDOS Frontend (React)                                                │
│  • Đọc widget config từ DB (chart_type, operation_pattern, params)   │
│  • Gọi backend để lấy data                                           │
│  • Render biểu đồ theo chartType tương ứng                          │
└─────────────────────────────┬────────────────────────────────────────┘
                              │ REST API
                              ▼
┌──────────────────────────────────────────────────────────────────────┐
│  HDOS Request API (port 5000)                                         │
│  • Nhận yêu cầu render widget                                        │
│  • Tìm operation_pattern → provider_id                               │
│  • Forward sang Provider Bridge                                       │
└─────────────────────────────┬────────────────────────────────────────┘
                              │ gRPC stream
                              ▼
┌──────────────────────────────────────────────────────────────────────┐
│  Excel Provider (port 5600)  ← project E:\Project\excel-provider     │
│  • Nhận OperationRequest                                             │
│  • Dispatch đến đúng Handler (DashboardSummaryHandler, v.v.)         │
│  • Handler truy vấn PostgreSQL → tính toán → trả JSON               │
│  • JSON phải tuân theo RENDER_CONTRACTS.md (data field)             │
└─────────────────────────────┬────────────────────────────────────────┘
                              │ SQL
                              ▼
┌──────────────────────────────────────────────────────────────────────┐
│  PostgreSQL (excel_provider DB)                                       │
│  Tables: sales, products, regions                                    │
│  ← BẠN SỬA DỮ LIỆU Ở ĐÂY để biểu đồ HDOS thay đổi                │
└──────────────────────────────────────────────────────────────────────┘
```

**Quy tắc vàng**: Mỗi widget trên HDOS có `chart_type` và `operation_pattern`. Handler tương ứng trả về JSON có `data` field đúng định dạng của `chart_type` đó. Sửa data trong PostgreSQL → biểu đồ tự cập nhật khi refresh.

---

## 2. Bản đồ Widget → Operation → Handler

### Module: Bảng điều khiển kinh doanh (sales-dashboard)

#### Tab 1 — Tổng quan

| Widget | chart_type | operation_pattern | Handler | Dữ liệu nguồn |
|--------|-----------|-------------------|---------|--------------|
| Tổng quan KPI | `kpi_grid` | `report.dashboard.summary` | `DashboardSummaryHandler` | `sales` (hôm nay + hôm qua) + `products` |
| Kênh bán hàng | `pie_chart` | `report.channel.comparison` | `ChannelComparisonHandler` | `sales` (30 ngày, group by channel) |
| Thực tế vs Mục tiêu | `bar_chart` | `report.regional.performance` | `RegionalPerformanceHandler` | `sales` + `regions` (30 ngày) |

#### Tab 2 — Xu hướng

| Widget | chart_type | operation_pattern | Handler | Dữ liệu nguồn |
|--------|-----------|-------------------|---------|--------------|
| Xu hướng doanh thu | `line_chart` | `report.sales.trend` | `SalesTrendHandler` | `sales` (30 ngày theo ngày) |

#### Tab 3 — Sản phẩm & Tồn kho

| Widget | chart_type | operation_pattern | Handler | Dữ liệu nguồn |
|--------|-----------|-------------------|---------|--------------|
| Doanh thu sản phẩm | `simple_table` | `report.product.detail` | `ProductDetailHandler` | `sales` (30 ngày, group by product) |
| Tình trạng tồn kho | `progress_rows` | `report.inventory.status` | `InventoryStatusHandler` | `products` (current_stock, min_stock) |
| Top sản phẩm | `simple_table` | `report.top.performers` | `TopPerformersHandler` | `sales` (30 ngày vs 60 ngày) |

---

## 3. Chi tiết từng loại biểu đồ

### 3.1 `kpi_grid` — Doanh thu hôm nay

**Handler**: `DashboardSummaryHandler.cs`

**Dữ liệu nguồn**: Bảng `sales` ngày hôm nay + hôm qua, bảng `products`.

**Format JSON trả về (data field)**:
```json
{
  "columns": 3,
  "items": [
    {
      "id": "total_revenue",
      "label": "Doanh thu hôm nay",
      "value": 1850000,
      "format": "currency:VND",
      "comparison": {
        "deltaPercent": 12.5,
        "direction": "up",
        "isGood": true,
        "periodLabel": "vs hôm qua"
      },
      "sparkline": [1200000, 1350000, 1500000, 1420000, 1600000, 1750000, 1850000],
      "icon": "TrendingUp",
      "variant": "default"
    },
    { "id": "total_units",    "label": "Sản phẩm bán ra",  "value": 145,     "format": "number",       "comparison": {...}, "sparkline": null, "icon": "BarChart2",  "variant": "default" },
    { "id": "online_revenue", "label": "Doanh thu Online", "value": 1100000, "format": "currency:VND", "comparison": {...}, "sparkline": null, "icon": "Zap",        "variant": "default" },
    { "id": "store_revenue",  "label": "Doanh thu Cửa hàng","value": 750000, "format": "currency:VND", "comparison": {...}, "sparkline": null, "icon": "Building2",  "variant": "default" },
    { "id": "top_region",     "label": "Khu vực dẫn đầu", "value": "North", "format": "text",         "comparison": null,  "sparkline": null, "icon": "MapPin",     "variant": "info"    },
    { "id": "stock_alerts",   "label": "Cảnh báo tồn kho","value": 3,       "format": "number",       "comparison": null,  "sparkline": null, "icon": "ShieldAlert","variant": "danger"  }
  ]
}
```

**Để KPI thay đổi → thêm đơn hàng hôm nay**:
```bash
POST http://localhost:5600/api/data/sales
{
  "date": "2026-05-27",      # ngày HÔM NAY
  "region": "North",
  "product": "Laptop Pro",
  "category": "Electronics",
  "revenue": 9600000,
  "units": 8,
  "channel": "Online"
}
```

**Trường `variant` cho màu sắc thẻ KPI**:
| variant | Màu | Khi nào dùng |
|---------|-----|-------------|
| `"default"` | Trung tính | Chỉ số bình thường |
| `"success"` | Xanh lá | Tốt, đạt mục tiêu |
| `"warning"` | Vàng | Cần chú ý |
| `"danger"` | Đỏ | Vấn đề nghiêm trọng |
| `"info"` | Xanh dương | Thông tin bổ sung |

---

### 3.2 `line_chart` — Xu hướng doanh thu

**Handler**: `SalesTrendHandler.cs`

**Dữ liệu nguồn**: Bảng `sales`, group by ngày/tuần/tháng trong 30 ngày gần nhất.

**Format JSON trả về (data field)**:
```json
{
  "series": [
    {
      "name": "Doanh thu",
      "data": [
        { "x": "2026-04-28", "y": 1250000 },
        { "x": "2026-04-29", "y": 1380000 },
        { "x": "2026-04-30", "y": 980000  },
        { "x": "2026-05-01", "y": 1650000 },
        ...
      ]
    }
  ],
  "axes": {
    "x": { "type": "time",   "label": "Ngày",           "format": "yyyy-MM-dd" },
    "y": { "type": "number", "label": "Doanh thu (VND)", "format": "currency:VND" },
    "y2": null
  },
  "annotations": []
}
```

**Quy tắc mapping dữ liệu**:
- Mỗi ngày trong khoảng → 1 điểm `{x, y}`
- `x` = chuỗi ngày `"yyyy-MM-dd"` (nếu groupBy=day) | `"2026-W20"` (week) | `"2026-05"` (month)
- `y` = tổng `revenue` của ngày đó (null nếu không có data → vẽ gap)
- Ngày có revenue = 0 vẫn xuất hiện (zero-fill), không bỏ qua

**Để biểu đồ tăng vọt** → thêm nhiều đơn hàng cho 1 ngày:
```bash
# Thêm 5 đơn lớn cho ngày hôm nay
for channel in Online Store Online Online Store; do
POST /api/data/sales { "date":"2026-05-27","region":"South","product":"Monitor 24\"","revenue":3500000,"units":10,"channel":"$channel" }
done
```

---

### 3.3 `bar_chart` — Khu vực vs Mục tiêu

**Handler**: `RegionalPerformanceHandler.cs`

**Dữ liệu nguồn**: Bảng `sales` (30 ngày) group by region, bảng `regions` (monthly_target).

**Format JSON trả về (data field)**:
```json
{
  "series": [
    {
      "name": "Thực tế",
      "data": [
        { "x": "Central", "y": 45200 },
        { "x": "East",    "y": 62800 },
        { "x": "North",   "y": 78500 },
        { "x": "South",   "y": 55100 },
        { "x": "West",    "y": 48900 }
      ]
    },
    {
      "name": "Mục tiêu",
      "data": [
        { "x": "Central", "y": 65000 },
        { "x": "East",    "y": 70000 },
        { "x": "North",   "y": 80000 },
        { "x": "South",   "y": 60000 },
        { "x": "West",    "y": 55000 }
      ]
    }
  ],
  "axes": {
    "x": { "type": "category", "label": "Khu vực" },
    "y": { "type": "number",   "label": "Doanh thu (VND)", "format": "currency:VND" },
    "y2": null
  },
  "annotations": []
}
```

**Để thay đổi Mục tiêu → sửa bảng `regions` trực tiếp trong PostgreSQL**:
```sql
UPDATE regions
SET monthly_target = 100000, yearly_target = 1200000
WHERE name = 'North';
```

**Để khu vực North vượt mục tiêu → thêm nhiều đơn hàng region=North**:
```bash
POST /api/data/sales
{ "date":"2026-05-27","region":"North","product":"SSD 1TB","revenue":5000000,"units":50,"channel":"Online" }
```

---

### 3.4 `pie_chart` — Kênh bán hàng (Online vs Cửa hàng)

**Handler**: `ChannelComparisonHandler.cs`

**Dữ liệu nguồn**: Bảng `sales`, tổng revenue group by channel trong 30 ngày.

**Format JSON trả về (data field)**:
```json
{
  "slices": [
    { "label": "Online",    "value": 1250000, "color": null },
    { "label": "Cửa hàng", "value": 875000,  "color": null }
  ],
  "total": 2125000,
  "valueFormat": "currency:VND"
}
```

**Quy tắc mapping**:
- `slices` = mảng các phần của biểu đồ tròn
- `value` = doanh thu tuyệt đối (không phải phần trăm — frontend tự tính %)
- `total` = tổng tất cả slices (pre-computed để frontend không phải tính lại)
- `color: null` = dùng màu palette mặc định của frontend

**Để thay đổi tỷ lệ Online/Store**:
```bash
# Tăng Online: thêm đơn channel=Online
POST /api/data/sales
{ "date":"2026-05-27","region":"East","product":"Keyboard MX","revenue":2400000,"units":20,"channel":"Online" }

# Tăng Store: thêm đơn channel=Store
POST /api/data/sales
{ "date":"2026-05-27","region":"East","product":"Keyboard MX","revenue":2400000,"units":20,"channel":"Store" }
```

---

### 3.5 `progress_rows` — Tình trạng tồn kho

**Handler**: `InventoryStatusHandler.cs`

**Dữ liệu nguồn**: Bảng `products` (current_stock, min_stock).

**Format JSON trả về (data field)**:
```json
{
  "rows": [
    {
      "id":      "P001",
      "label":   "Laptop Pro",
      "sublabel":"Electronics • 45/100 sản phẩm",
      "current": 45,
      "max":     100,
      "percent": 45.0,
      "colorThresholds": [
        { "from": 0,  "to": 30,  "color": "danger"  },
        { "from": 30, "to": 70,  "color": "warning" },
        { "from": 70, "to": 101, "color": "success" }
      ],
      "badge":        "Bình thường",
      "badgeVariant": "warning"
    },
    {
      "id": "P002", "label": "Wireless Mouse",
      "sublabel": "Peripherals • 3/50 sản phẩm",
      "current": 3, "max": 50, "percent": 6.0,
      "colorThresholds": [...],
      "badge": "Rất thấp", "badgeVariant": "danger"
    }
  ],
  "showPercent": true,
  "showValues":  true
}
```

**Quy tắc tính toán**:
- `max` = `max(current_stock, min_stock × 5)` — capacity ước tính
- `percent` = `current / max × 100`
- Màu: 0–30% = đỏ, 30–70% = vàng, 70–100% = xanh

**Để thấy thanh tồn kho thay đổi**:
```bash
# Nhập thêm hàng Wireless Mouse (P002) — thanh sẽ chuyển từ đỏ → xanh
PUT http://localhost:5600/api/data/products/P002/stock
{ "stock": 45 }

# Cạn hàng USB Hub (P003) — thanh sẽ về 0 màu đỏ
PUT http://localhost:5600/api/data/products/P003/stock
{ "stock": 0 }
```

**Danh sách product_id có thể dùng**:
| product_id | Tên sản phẩm | current_stock ban đầu |
|------------|-------------|----------------------|
| P001 | Laptop Pro | 45 |
| P002 | Wireless Mouse | 3 |
| P003 | USB Hub | 0 |
| P004 | Monitor 24" | 22 |
| P005 | Keyboard MX | 8 |
| P006 | Webcam HD | 0 |
| P007 | SSD 1TB | 60 |
| P008 | RAM 16GB | 12 |
| P009 | Headset Pro | 2 |
| P010 | Desk Lamp | 100 |

---

### 3.6 `simple_table` — Bảng sản phẩm & Top performers

**Handler**: `ProductDetailHandler.cs` + `TopPerformersHandler.cs`

**Format JSON trả về (data field)**:
```json
{
  "columns": [
    { "key": "rank",    "label": "#",           "type": "number", "sortable": true,  "format": null,           "align": "center" },
    { "key": "product", "label": "Sản phẩm",    "type": "string", "sortable": true,  "format": null,           "align": "left"   },
    { "key": "revenue", "label": "Doanh thu",   "type": "number", "sortable": true,  "format": "currency:VND", "align": "right"  },
    { "key": "units",   "label": "Số lượng",    "type": "number", "sortable": true,  "format": null,           "align": "right"  },
    { "key": "growth",  "label": "Tăng trưởng", "type": "number", "sortable": true,  "format": "percent:1",    "align": "right"  }
  ],
  "rows": [
    { "rank": 1, "product": "Laptop Pro",    "revenue": 185000000, "units": 154, "growth": 23.5  },
    { "rank": 2, "product": "SSD 1TB",       "revenue": 142000000, "units": 1495,"growth": 12.1  },
    { "rank": 3, "product": "Monitor 24\"",  "revenue": 98000000,  "units": 280, "growth": -4.2  },
    { "rank": 4, "product": "RAM 16GB",      "revenue": 87000000,  "units": 1160,"growth": 8.7   },
    { "rank": 5, "product": "Keyboard MX",   "revenue": 72000000,  "units": 600, "growth": 15.3  }
  ],
  "pagination": {
    "mode": "client",
    "totalRows": 5
  }
}
```

**Format strings (`format` field)**:
| Giá trị | Hiển thị ví dụ |
|---------|---------------|
| `"currency:VND"` | 185.000.000 ₫ |
| `"number"` | 1.495 |
| `"percent:1"` | 23.5% |
| `"date"` | 27/05/2026 |
| `null` | Hiển thị nguyên giá trị |

---

## 4. Management API — Sửa dữ liệu để thấy biểu đồ thay đổi

Base URL: `http://localhost:5600`

### 4.1 Doanh thu (Sales)

#### Xem tất cả đơn hàng
```
GET /api/data/sales
GET /api/data/sales?date=2026-05-27
GET /api/data/sales?region=North
GET /api/data/sales?date=2026-05-27&region=North
```

#### Thêm đơn hàng mới → biểu đồ tăng
```
POST /api/data/sales
Content-Type: application/json

{
  "date":     "2026-05-27",     ← ngày bán (yyyy-MM-dd)
  "region":   "North",          ← North | South | East | West | Central
  "product":  "Laptop Pro",     ← tên sản phẩm (khớp với bảng products)
  "category": "Electronics",    ← Electronics | Peripherals | Storage
  "revenue":  9600000,          ← doanh thu (số dương, VND)
  "units":    8,                ← số lượng bán
  "channel":  "Online"          ← Online | Store
}
```

**Response**: `201 Created` + record vừa tạo kèm `id`

#### Sửa đơn hàng → thay đổi biểu đồ
```
PUT /api/data/sales/{id}
Content-Type: application/json

{
  "revenue": 15000000,   ← chỉ cần gửi field muốn sửa (partial update)
  "units": 12
}
```

#### Xóa đơn hàng → biểu đồ giảm
```
DELETE /api/data/sales/{id}
```
**Response**: `204 No Content`

---

### 4.2 Tồn kho (Products)

#### Xem tất cả sản phẩm và tồn kho
```
GET /api/data/products
```

**Response**:
```json
[
  {
    "productId": "P001",
    "name": "Laptop Pro",
    "category": "Electronics",
    "price": 1200.00,
    "currentStock": 45,
    "minStock": 20,
    "status": "In Stock"
  },
  ...
]
```

#### Cập nhật tồn kho → thanh progress thay đổi
```
PUT /api/data/products/{productId}/stock
Content-Type: application/json

{ "stock": 150 }
```

| product_id | Tên | Tác động khi thay đổi stock |
|-----------|-----|---------------------------|
| P001 | Laptop Pro | KPI "Cảnh báo tồn kho" |
| P002 | Wireless Mouse | Thanh progress màu đỏ |
| P003 | USB Hub | Thanh progress 0% |
| P006 | Webcam HD | Hết hàng |

---

### 4.3 Mục tiêu khu vực (Sửa trực tiếp PostgreSQL)

Bảng `regions` không có API, sửa trực tiếp DB:

```sql
-- Kết nối PostgreSQL
psql -h localhost -p 5434 -U excel -d excel_provider

-- Xem mục tiêu hiện tại
SELECT region_id, name, monthly_target FROM regions ORDER BY region_id;

-- Sửa mục tiêu khu vực North tăng 50%
UPDATE regions
SET monthly_target = 120000, yearly_target = 1440000
WHERE name = 'North';

-- Thêm khu vực mới (nếu muốn)
INSERT INTO regions (region_id, name, manager, monthly_target, yearly_target)
VALUES ('R06', 'Northeast', 'Frank Bui', 75000, 900000);
```

---

## 5. Kịch bản demo — Xem dữ liệu thay đổi theo thời gian thực

### Kịch bản 1: Ngày kinh doanh bùng nổ

```bash
# Thêm 10 đơn hàng lớn cho hôm nay
curl -s -X POST http://localhost:5600/api/data/sales \
  -H "Content-Type: application/json" \
  -d '{"date":"2026-05-27","region":"North","product":"Laptop Pro","category":"Electronics","revenue":12000000,"units":10,"channel":"Online"}'

curl -s -X POST http://localhost:5600/api/data/sales \
  -H "Content-Type: application/json" \
  -d '{"date":"2026-05-27","region":"South","product":"Monitor 24\"","category":"Electronics","revenue":7000000,"units":20,"channel":"Store"}'

curl -s -X POST http://localhost:5600/api/data/sales \
  -H "Content-Type: application/json" \
  -d '{"date":"2026-05-27","region":"East","product":"SSD 1TB","category":"Storage","revenue":5700000,"units":60,"channel":"Online"}'
```

**Kết quả trên HDOS**: KPI "Doanh thu hôm nay" tăng, đường xu hướng hôm nay cao hơn, North dẫn đầu bar chart.

---

### Kịch bản 2: Khủng hoảng tồn kho

```bash
# Cạn hàng đột ngột — 3 sản phẩm hết
curl -X PUT http://localhost:5600/api/data/products/P001/stock -H "Content-Type: application/json" -d '{"stock":0}'
curl -X PUT http://localhost:5600/api/data/products/P004/stock -H "Content-Type: application/json" -d '{"stock":0}'
curl -X PUT http://localhost:5600/api/data/products/P007/stock -H "Content-Type: application/json" -d '{"stock":0}'
```

**Kết quả trên HDOS**: KPI "Cảnh báo tồn kho" = 5 (màu đỏ), 3 thanh progress = 0% màu đỏ.

---

### Kịch bản 3: Nhập hàng — tồn kho phục hồi

```bash
# Nhập hàng đủ cho toàn bộ sản phẩm
for pid in P001 P002 P003 P004 P005 P006 P007 P008 P009 P010; do
  curl -s -X PUT http://localhost:5600/api/data/products/$pid/stock \
    -H "Content-Type: application/json" -d '{"stock":150}'
done
```

**Kết quả**: Tất cả thanh progress xanh, KPI cảnh báo = 0 (màu xanh lá).

---

### Kịch bản 4: So sánh kênh bán

```bash
# Đẩy mạnh Online → pie chart dịch về Online
for i in {1..5}; do
  curl -s -X POST http://localhost:5600/api/data/sales \
    -H "Content-Type: application/json" \
    -d '{"date":"2026-05-27","region":"Central","product":"Keyboard MX","category":"Peripherals","revenue":3600000,"units":30,"channel":"Online"}'
done

# Đẩy mạnh Store → pie chart cân bằng
for i in {1..5}; do
  curl -s -X POST http://localhost:5600/api/data/sales \
    -H "Content-Type: application/json" \
    -d '{"date":"2026-05-27","region":"West","product":"Keyboard MX","category":"Peripherals","revenue":3600000,"units":30,"channel":"Store"}'
done
```

---

## 6. Thêm menu mới trên HDOS

### Bước 1 — Đăng ký operation mới trong excel-provider

**Thêm Handler mới** (`E:\Project\excel-provider\backend\Operations\`):

```csharp
// YourNewHandler.cs
public sealed class YourNewHandler : IOperationHandler
{
    public string OperationPattern => "report.your.operation";

    public async Task<string> ExecuteAsync(
        OperationRequest request,
        Func<int, string, Task> reportProgress,
        CancellationToken ct)
    {
        // Truy vấn DB ...
        // Xây dựng JSON đúng format của chart_type bạn muốn
        // Ví dụ: pie_chart
        var result = new JsonObject
        {
            ["slices"] = new JsonArray
            {
                new JsonObject { ["label"] = "A", ["value"] = 100.0, ["color"] = (JsonNode?)null },
                new JsonObject { ["label"] = "B", ["value"] = 200.0, ["color"] = (JsonNode?)null },
            },
            ["total"]       = 300.0,
            ["valueFormat"] = "currency:VND",
        };
        return result.ToJsonString();
    }
}
```

**Đăng ký trong Program.cs**:
```csharp
builder.Services.AddSingleton<IOperationHandler, YourNewHandler>();
```

**Khai báo operation trong ProviderBridgeClient.cs**:
```csharp
private static readonly string[] SupportedOperations =
[
    ...
    "report.your.operation",   // ← thêm vào đây
];
```

---

### Bước 2 — Thêm SQL migration trong HDOS

Tạo file `E:\Project\HDOS\db\Migrations\V026__your_new_menu.sql`:

```sql
-- Thêm operation vào registry (nếu chưa có)
INSERT INTO operation_registry (operation_pattern, handler_type, provider_id, result_chart_type, status)
VALUES ('report.your.operation', 'provider', 'excel-provider', 'pie_chart', 'active')
ON CONFLICT DO NOTHING;

-- Tạo module mới (nếu cần menu riêng)
INSERT INTO module_groups (id, slug, label, icon, sort_order)
VALUES ('00000000-0000-0000-0001-000000000007', 'your-group', 'Nhóm mới', 'Layers', 70)
ON CONFLICT (slug) DO NOTHING;

INSERT INTO modules (id, group_id, slug, label, icon, sort_order)
VALUES ('00000000-0000-0000-0002-000000000023',
        '00000000-0000-0000-0001-000000000007',
        'your-module', 'Module mới', 'PieChart', 10)
ON CONFLICT (slug) DO NOTHING;

-- Tab
INSERT INTO module_tabs (id, module_id, slug, label, sort_order, is_default)
VALUES ('00000000-0000-0000-0003-000000000008',
        '00000000-0000-0000-0002-000000000023',
        'overview', 'Tổng quan', 0, true)
ON CONFLICT (id) DO NOTHING;

-- Widget
INSERT INTO widgets (tab_id, widget_key, title, chart_type, grid_x, grid_y, grid_w, grid_h,
                     operation_pattern, params_template, sort_order)
VALUES ('00000000-0000-0000-0003-000000000008',
        'your_widget', 'Tiêu đề widget', 'pie_chart',
        0, 0, 6, 5,
        'report.your.operation', '{}', 10)
ON CONFLICT DO NOTHING;
```

---

### Bước 3 — Hoặc dùng Dashboard Designer (UI)

Không cần code SQL! Vào HDOS → **Admin** → **Dashboard Designer**:

1. **Tạo module group**: Menu trái → + Group
2. **Tạo module**: + Module → đặt tên, icon
3. **Thêm tab**: + Tab trong module
4. **Kéo thả widget**: Chọn chart_type, chọn operation, đặt vị trí
5. **Lưu**: widget được ghi vào DB → hiển thị ngay

---

## 7. Định dạng JSON cho từng chart type (RENDER_CONTRACTS)

> Nguồn đầy đủ: `E:\Project\HDOS\docs\RENDER_CONTRACTS.md`

### Tóm tắt nhanh — `data` field của từng chart type

| chart_type | Cấu trúc `data` |
|-----------|----------------|
| `kpi_grid` | `{ columns: N, items: [{id, label, value, format, comparison, sparkline, icon, variant}] }` |
| `line_chart` | `{ series: [{name, data:[{x,y}]}], axes:{x,y,y2}, annotations:[] }` |
| `bar_chart` | Cùng cấu trúc với `line_chart` |
| `area_chart` | Cùng cấu trúc với `line_chart` |
| `pie_chart` | `{ slices:[{label,value,color}], total, valueFormat }` |
| `donut_chart` | Cùng cấu trúc với `pie_chart` |
| `progress_rows` | `{ rows:[{id,label,sublabel,current,max,percent,colorThresholds,badge,badgeVariant}], showPercent, showValues }` |
| `simple_table` | `{ columns:[{key,label,type,sortable,format,align}], rows:[{...}], pagination:{mode:"client",totalRows} }` |
| `kpi` (đơn) | `{ value, format, label, comparison:{previousValue,delta,deltaPercent,direction,isGood,periodLabel}, sparkline }` |
| `gauge` | `{ value, min, max, unit, thresholds:[{from,to,color,label}], target }` |

### Format strings (cột `format`)

```
"currency:VND"  → 1.850.000 ₫
"currency:USD"  → $1,850.00
"number"        → 1,850,000
"percent:1"     → 23.5%
"percent:0"     → 24%
"date"          → 27/05/2026
"text"          → hiển thị nguyên chuỗi
null            → không format
```

### Hướng dẫn axis type

```json
"axes": {
  "x": {
    "type": "time",      ← time | category | number
    "label": "Ngày",
    "format": "yyyy-MM-dd"
  },
  "y": {
    "type": "number",
    "label": "Doanh thu (VND)",
    "format": "currency:VND"
  },
  "y2": null             ← null nếu không có trục Y phụ
}
```

---

## 8. Quy tắc mapping khi xây biểu đồ mới

### ✅ Checklist

**Handler (excel-provider)**:
- [ ] Implement `IOperationHandler`, đặt `OperationPattern` đúng
- [ ] Khai báo pattern trong `SupportedOperations[]` ở `ProviderBridgeClient.cs`
- [ ] Đăng ký handler trong `Program.cs` với `AddSingleton`
- [ ] `ExecuteAsync` trả về **chỉ** `data` field (không phải toàn bộ widget envelope)
- [ ] JSON trả về phải khớp **đúng** cấu trúc của `chart_type` tương ứng trong RENDER_CONTRACTS.md
- [ ] Xử lý graceful khi params thiếu (dùng default values, không throw exception)

**Migration SQL (HDOS)**:
- [ ] Thêm vào `operation_registry` với `result_chart_type` đúng
- [ ] Thêm provider vào `provider_registry` (nếu provider mới)
- [ ] Tạo `module_groups` nếu nhóm mới
- [ ] Tạo `modules` với `slug` unique
- [ ] Tạo `module_tabs` với `is_default=true` cho tab đầu tiên
- [ ] Tạo `widgets` với `chart_type` = `result_chart_type` của operation

**Kiểm tra**:
- [ ] `dotnet build` không có error/warning
- [ ] Excel-provider chạy thành công, log "Welcome received"
- [ ] Mở HDOS → menu mới xuất hiện trong sidebar
- [ ] Widget hiển thị đúng biểu đồ với dữ liệu thật
- [ ] Thêm dữ liệu qua API → refresh HDOS → biểu đồ cập nhật

### ❌ Lỗi thường gặp

| Lỗi | Nguyên nhân | Giải pháp |
|-----|------------|-----------|
| Widget hiển thị `raw_json` (JSON thô) | `result_chart_type` không đúng hoặc chưa set | UPDATE operation_registry |
| Widget trống (no data) | Handler trả về empty array | Kiểm tra DB có data không |
| Widget lỗi đỏ | Handler throw exception | Thêm try/catch, kiểm tra log excel-provider |
| Không thấy menu | Module chưa có tab hoặc tab chưa có widget | Kiểm tra migration |
| KPI không cập nhật | `NotificationService` không gọi được | Kiểm tra kết nối Ingestion API |
| `chart_type` không render | Sai cấu trúc JSON (thiếu field) | Đọc lại RENDER_CONTRACTS.md §11 |
