# Excel Provider — Onboarding & Architecture

Tài liệu này mô tả toàn bộ kiến trúc, cách tích hợp với HDOS platform, và hướng dẫn phát triển cho **Excel Provider** — một data provider độc lập trong hệ sinh thái HDOS Reporting Platform.

---

## 1. Tổng quan

**Excel Provider** cung cấp 7 loại báo cáo bán hàng từ dữ liệu trong PostgreSQL riêng (`postgres-excel`). Nó tích hợp với HDOS platform thông qua giao thức **gRPC bidirectional streaming** và cung cấp **Management UI** (React) để CRUD dữ liệu bán hàng/sản phẩm.

### Stack kỹ thuật
| Thành phần | Công nghệ |
|---|---|
| Backend | .NET 9 ASP.NET Core, Npgsql, gRPC (Grpc.Net.Client) |
| Frontend | React 18, Vite, TypeScript, TanStack Query v5, Tailwind CSS v3 |
| Database | PostgreSQL 16 (riêng, không chung với HDOS) |
| Container | Docker (multi-stage build) |

---

## 2. Kiến trúc tích hợp với HDOS

```
┌─────────────────────────────── HDOS Platform ───────────────────────────────┐
│                                                                              │
│  Browser → Gateway (5500) → Request API (5000) → Provider Bridge (5400)    │
│                                    ↓                                         │
│                           Ingestion API (5100)                               │
│                                    ↓                                         │
│                   RabbitMQ → Event Processor → SignalR Hub (5200)            │
│                                                        ↓                     │
│                                                    Browser (realtime)        │
└──────────────────────────────────────────────────────────────────────────────┘
          ↑ gRPC stream             ↑ HTTP POST events
          │                         │
┌─────────────────────────────── Excel Provider ──────────────────────────────┐
│                                                                              │
│  excel-provider (5600)  ←→  postgres-excel (5434)                           │
│       ↑                                                                      │
│  excel-provider-ui (3001)   [Management UI — CRUD sales/products]           │
└──────────────────────────────────────────────────────────────────────────────┘
```

### Luồng xử lý operation (báo cáo)

1. User submit operation trên HDOS Dashboard (ví dụ: `report.dashboard.summary`)
2. Request API nhận yêu cầu → ghi vào DB → gửi message đến RabbitMQ
3. Operation Router Worker đọc từ RabbitMQ → forward đến Provider Bridge qua gRPC
4. Provider Bridge stream `OperationRequest` đến Excel Provider (đang giữ kết nối gRPC)
5. Excel Provider xử lý → query `postgres-excel` → trả về JSON result
6. Provider Bridge nhận result → ghi vào DB → gửi vào RabbitMQ
7. Response Dispatcher Worker → cập nhật kết quả → SignalR push đến browser

### Luồng realtime khi data thay đổi

1. User chỉnh sửa data qua Management UI (port 3001)
2. `excel-provider` POST `datasource.updated` event đến **Ingestion API** (HDOS)
3. Event Processor Worker nhận event → gửi `WidgetStale` qua SignalR
4. Browser nhận `WidgetStale` → tự động re-submit operation → chart refresh

---

## 3. Đăng ký với HDOS (Provider Registration)

Excel Provider đăng ký với HDOS bằng **platform token** (client_credentials):

```
Client ID:     excel-provider
Client Secret: excel-secret-dev-2024   (bcrypt-hashed trong HDOS DB)
```

Credentials được seed vào **HDOS database** (không phải postgres-excel) qua file:
→ `db/V009__excel_provider_seed.sql`

File này seed 2 bảng trong HDOS DB:
- `provider_registry` — đăng ký excel-provider với 7 operations
- `operation_registry` — schema params/payload cho từng operation

**Khi start**, Excel Provider gửi `Hello` message qua gRPC → Provider Bridge xác thực → bắt đầu nhận `OperationRequest` stream.

---

## 4. Database Schema (postgres-excel)

Excel Provider dùng DB riêng **không liên quan đến HDOS DB**.

### Bảng `sales`
| Cột | Kiểu | Mô tả |
|---|---|---|
| `id` | BIGSERIAL PK | Auto-increment ID |
| `sale_date` | DATE | Ngày bán |
| `region` | TEXT | North/South/East/West/Central |
| `product` | TEXT | Tên sản phẩm |
| `category` | TEXT | Danh mục |
| `revenue` | DECIMAL(12,2) | Doanh thu |
| `units` | INT | Số lượng |
| `channel` | TEXT | Online/Store |

### Bảng `products`
| Cột | Kiểu | Mô tả |
|---|---|---|
| `product_id` | TEXT PK | Mã sản phẩm (P001..P010) |
| `name` | TEXT | Tên sản phẩm |
| `category` | TEXT | Electronics/Peripherals/Storage |
| `price` | DECIMAL(10,2) | Đơn giá |
| `current_stock` | INT | Tồn kho hiện tại |
| `min_stock` | INT | Mức tồn tối thiểu (cảnh báo Low Stock) |

### Bảng `regions`
| Cột | Kiểu | Mô tả |
|---|---|---|
| `region_id` | TEXT PK | R01..R05 |
| `name` | TEXT | North/South/East/West/Central |
| `manager` | TEXT | Tên quản lý vùng |
| `monthly_target` | DECIMAL(12,2) | Target doanh thu tháng |
| `yearly_target` | DECIMAL(12,2) | Target doanh thu năm |

**Khởi tạo tự động**: Khi start lần đầu, `ExcelProviderDb.InitializeAsync()` tự tạo bảng + seed 5 vùng, 10 sản phẩm, 180 ngày dữ liệu bán hàng mẫu.

---

## 5. Operations (7 loại báo cáo)

| Operation Pattern | Params | Mô tả |
|---|---|---|
| `report.dashboard.summary` | `date?` (ISO date) | KPIs hôm nay: doanh thu, units, top region/product, cảnh báo tồn kho |
| `report.sales.trend` | `fromDate`, `toDate`, `groupBy` (day/week/month) | Xu hướng doanh số theo thời gian |
| `report.inventory.status` | _(none)_ | Trạng thái tồn kho: ok/low/out |
| `report.regional.performance` | `period` (today/week/month) | Hiệu suất vùng vs target |
| `report.channel.comparison` | `fromDate`, `toDate` | So sánh Online vs Store |
| `report.product.detail` | `productName`, `fromDate`, `toDate` | Chi tiết một sản phẩm |
| `report.top.performers` | `period` (week/month/quarter) | Top 5 sản phẩm/vùng + % tăng trưởng |

Mỗi operation được implement trong `backend/Operations/<Name>Handler.cs`, implement interface `IOperationHandler`.

---

## 6. Management API (REST — port 5600)

| Method | Endpoint | Mô tả |
|---|---|---|
| GET | `/api/sales?date=&region=` | Lấy danh sách sales (filter tùy chọn) |
| POST | `/api/sales` | Thêm sale mới |
| PUT | `/api/sales/{id}` | Cập nhật sale theo ID |
| DELETE | `/api/sales/{id}` | Xoá sale |
| GET | `/api/products` | Lấy danh sách sản phẩm |
| PUT | `/api/products/{productId}/stock` | Cập nhật tồn kho (body: `{"stock": number}`) |
| GET | `/health` | Health check |

---

## 7. Biến môi trường

### excel-provider (backend)

| Biến | Mô tả | Default |
|---|---|---|
| `Provider__ClientId` | Client ID đăng ký với HDOS | `excel-provider` |
| `Provider__ClientSecret` | Client secret (plain-text) | `excel-secret-dev-2024` |
| `Provider__TokenEndpoint` | URL lấy platform token | `http://{HDOS_HOST}:5000/api/v1/providers/token` |
| `Provider__BridgeGrpcUrl` | URL của Provider Bridge gRPC | `http://{HDOS_HOST}:5400` |
| `Ingestion__BaseUrl` | URL Ingestion API để push events | `http://{HDOS_HOST}:5100` |
| `Ingestion__TokenEndpoint` | URL lấy token cho Ingestion | `http://{HDOS_HOST}:5000/api/v1/providers/token` |
| `ConnectionStrings__ExcelDb` | Connection string postgres-excel | `Host=postgres-excel;...` |

### excel-provider-ui (frontend nginx)

Nginx proxy `/api/*` → `excel-provider:5600` nên không cần env var thêm khi chạy Docker.

Dev local: set `VITE_EXCEL_API_URL=http://localhost:5600` hoặc dùng Vite proxy (đã cấu hình sẵn).

---

## 8. Setup lần đầu

### Bước 1: Seed HDOS Database
```bash
# Chạy V009 migration vào HDOS postgres (host port 5433)
psql -h <HDOS_HOST> -p 5433 -U hdos -d hdos -f db/V009__excel_provider_seed.sql
```
> Chỉ cần làm 1 lần. Migration này đăng ký excel-provider và 7 operations vào HDOS DB.

### Bước 2: Cấu hình .env
```bash
cp .env.example .env
# Sửa HDOS_HOST thành IP thực của server chạy HDOS
```

### Bước 3: Chạy stack
```bash
docker compose up -d
```

### Bước 4: Kiểm tra
```bash
# Excel Provider backend
curl http://localhost:5600/health

# Management UI
open http://localhost:3001
```

---

## 9. Dev workflow (local)

```bash
# 1. Chạy postgres-excel local
docker run -d --name postgres-excel -p 5434:5432 \
  -e POSTGRES_DB=excel_provider \
  -e POSTGRES_USER=excel \
  -e POSTGRES_PASSWORD=excel \
  postgres:16-alpine

# 2. Chạy backend (cần HDOS đang chạy hoặc mock)
cd backend
dotnet run

# 3. Chạy frontend (proxy /api → localhost:5600 tự động)
cd frontend
npm install
npm run dev
# → http://localhost:5173
```

---

## 10. Thêm operation mới

1. Tạo `backend/Operations/MyNewHandler.cs` implement `IOperationHandler`:
   ```csharp
   public sealed class MyNewHandler : IOperationHandler {
       public string OperationPattern => "report.my.new";
       public async Task<string> ExecuteAsync(OperationRequest req, ...) { ... }
   }
   ```

2. Đăng ký trong `backend/Program.cs`:
   ```csharp
   builder.Services.AddSingleton<IOperationHandler, MyNewHandler>();
   ```

3. Seed vào HDOS DB (thêm vào `provider_registry.operations` array và `operation_registry`):
   ```sql
   -- Chạy vào HDOS postgres
   INSERT INTO operation_registry (operation_pattern, handler_type, provider_id, ...)
   VALUES ('report.my.new', 'provider', 'excel-provider', ...);

   UPDATE provider_registry
   SET operations = array_append(operations, 'report.my.new')
   WHERE provider_id = 'excel-provider';
   ```

4. Rebuild và restart:
   ```bash
   docker compose up -d --build excel-provider
   ```

---

## 11. Cấu trúc thư mục

```
excel-provider/
├── backend/                    ← .NET 9 ASP.NET Core service
│   ├── Config/                 ProviderOptions, IngestionOptions
│   ├── Database/               ExcelProviderDb (Npgsql — tạo bảng + seed)
│   ├── Grpc/                   ProviderBridgeClient (gRPC stream), TokenService
│   ├── Management/             DataController (REST API), DataManagementService
│   ├── Operations/             7 IOperationHandler implementations
│   ├── Services/               NotificationService (push events to Ingestion API)
│   ├── Program.cs
│   ├── appsettings.json
│   ├── Excel.Provider.csproj
│   └── Dockerfile
├── frontend/                   ← React 18 + Vite + Tailwind standalone UI
│   ├── src/
│   │   ├── api/                client.ts, sales.ts, products.ts
│   │   ├── components/         SummaryBar, SaleModal, StatusBadge, Toast, DeleteConfirm
│   │   └── pages/              SalesTab, ProductsTab
│   ├── nginx.conf              SPA fallback + proxy /api → excel-provider:5600
│   └── Dockerfile
├── proto/
│   └── provider.proto          gRPC contract (shared với HDOS platform)
├── db/
│   └── V009__excel_provider_seed.sql   Seed vào HDOS DB (không phải postgres-excel)
├── docker-compose.yml
├── .env.example
└── ONBOARDING.md               (file này)
```

---

## 12. Kết nối với HDOS (tóm tắt nhanh)

| HDOS Service | Port | Excel Provider dùng để |
|---|---|---|
| `request-api` | 5000 | Lấy platform token (client_credentials) |
| `provider-bridge` | 5400 | Nhận operation requests qua gRPC stream |
| `ingestion-api` | 5100 | Push `datasource.updated` events |

**Không cần** kết nối trực tiếp với: gateway, rabbitmq, redis, keycloak, realtime-hub, postgres (HDOS).
