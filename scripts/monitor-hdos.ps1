<#
.SYNOPSIS
    Màn hình theo dõi real-time luồng Excel Provider <-> HDOS.

.DESCRIPTION
    Tail log của container backend và tô màu theo 2 chiều:
      ◀ INBOUND  : request HDOS gửi tới qua gRPC bridge + kết quả trả về
      ▶ OUTBOUND : push event datasource.updated sang HDOS Ingestion API
    Kèm các dòng vòng đời kết nối (token / bridge / welcome / disconnect).

.PARAMETER Container
    Tên container backend. Mặc định: excel-provider-excel-provider-1

.PARAMETER Tail
    Số dòng log cũ hiển thị trước khi stream. Mặc định: 50

.PARAMETER NoFollow
    Chỉ in log hiện có rồi thoát (không stream liên tục).

.EXAMPLE
    pwsh scripts/monitor-hdos.ps1
    pwsh scripts/monitor-hdos.ps1 -Tail 200
#>
[CmdletBinding()]
param(
    [string]$Container = "excel-provider-excel-provider-1",
    [int]   $Tail      = 50,
    [switch]$NoFollow
)

$ErrorActionPreference = "Stop"

# ── Kiểm tra container có chạy không ───────────────────────────────────────────
$running = docker ps --format "{{.Names}}"
if (-not ($running -contains $Container)) {
    Write-Host "[X] Container '$Container' khong chay. Cac container dang chay:" -ForegroundColor Red
    docker ps --format "    {{.Names}}   ({{.Status}})"
    Write-Host "`nDung: scripts\monitor-hdos.ps1 -Container <ten>" -ForegroundColor DarkGray
    exit 1
}

# ── Banner ─────────────────────────────────────────────────────────────────────
Clear-Host
Write-Host "==================================================================" -ForegroundColor DarkCyan
Write-Host "  EXCEL PROVIDER  <->  HDOS    live monitor" -ForegroundColor White
Write-Host "  container: $Container" -ForegroundColor DarkGray
Write-Host "  (green) INBOUND request tu HDOS    (cyan) OUTBOUND push -> HDOS" -ForegroundColor DarkGray
Write-Host "  Ctrl+C de thoat" -ForegroundColor DarkGray
Write-Host "==================================================================" -ForegroundColor DarkCyan

# ── Bộ đếm phiên ───────────────────────────────────────────────────────────────
$script:nReq  = 0   # request nhận
$script:nDone = 0   # result tra ve
$script:nPush = 0   # push HDOS thanh cong
$script:nWarn = 0   # push bi tu choi / canh bao

function Emit([string]$tag, [string]$text, [string]$color) {
    $ts = (Get-Date).ToString("HH:mm:ss")
    Write-Host ("[{0}] {1,-12}" -f $ts, $tag) -ForegroundColor $color -NoNewline
    Write-Host (" {0}" -f $text)
}

# ── Stream log ─────────────────────────────────────────────────────────────────
# Lưu ý: docker ghi log container ra stderr -> gộp 2>&1 và ép từng dòng về string.
$dockerArgs = @("logs", "--tail", "$Tail")
if (-not $NoFollow) { $dockerArgs += "-f" }
$dockerArgs += $Container

& docker @dockerArgs 2>&1 | ForEach-Object {
    $line = [string]$_

    switch -Regex ($line) {

        # ─── INBOUND : request tu HDOS ────────────────────────────────────────
        'OperationRequest received .*requestId=(\S+?),? operation=(\S+)' {
            $script:nReq++
            Emit "<- REQUEST" ("{0}   req={1}" -f $Matches[2], $Matches[1]) "Green"
            break
        }
        'Terminal sent .*status=(\w+).*elapsed=(\d+)' {
            $script:nDone++
            $clr = if ($Matches[1] -eq 'Done') { 'Green' } else { 'Yellow' }
            Emit "  -> RESULT" ("{0}  ({1}ms)" -f $Matches[1], $Matches[2]) $clr
            break
        }
        'No handler registered for operation=(\S+)' {
            Emit "  -> NO HANDLER" $Matches[1] "Red"; break
        }
        'Handler threw for requestId=(\S+)' {
            Emit "  -> HANDLER ERR" $Matches[1] "Red"; break
        }
        'Cancel received for requestId=(\S+)' {
            Emit "  -> CANCEL" $Matches[1] "DarkYellow"; break
        }

        # ─── OUTBOUND : push sang HDOS ────────────────────────────────────────
        'pushing (\d+) operations to HDOS' {
            Emit "-> PUSH" ("{0} op -> HDOS Ingestion" -f $Matches[1]) "Cyan"; break
        }
        'WidgetStale notification sent .*affectedOperations=\[(.*?)\]' {
            $script:nPush++
            Emit "  -> PUSH OK" ("[{0}]" -f $Matches[1]) "Cyan"; break
        }
        'Ingestion auth not configured' {
            $script:nWarn++
            Emit "  -> PUSH 403" "ingestion scope chua cau hinh (Keycloak)" "Yellow"; break
        }
        'Ingestion API returned (\d+)' {
            $script:nWarn++
            Emit "  -> PUSH $($Matches[1])" "Ingestion tu choi" "Yellow"; break
        }
        'Could not obtain bearer token' {
            $script:nWarn++
            Emit "  -> PUSH SKIP" "khong lay duoc token" "Yellow"; break
        }
        'pg_notify received.*table=(\S+)' {
            Emit "  . notify" ("table={0}" -f $Matches[1]) "DarkGray"; break
        }

        # ─── Vong doi ket noi ─────────────────────────────────────────────────
        'Token acquired, expires in (\d+)' {
            Emit "[ok] token" ("expires {0}s" -f $Matches[1]) "DarkGreen"; break
        }
        'Token endpoint returned (\d+)' {
            Emit "[X] token $($Matches[1])" "sai secret hoac HDOS tu choi" "Red"; break
        }
        'Connecting to bridge at (\S+)' {
            Emit ".. bridge" ("connecting {0}" -f $Matches[1]) "DarkGray"; break
        }
        'Hello sent' { Emit ".. hello" "da gui Hello" "DarkGray"; break }
        'Welcome received .*sessionId=(\S+)' {
            Emit "[ok] REGISTERED" ("HDOS bridge, session={0}" -f $Matches[1]) "Green"; break
        }
        'Disconnect received' { Emit "[X] DISCONNECT" "bridge ngat ket noi" "Red"; break }
        'ReplicationListenerService connected' {
            Emit "[ok] listener" "dang nghe pg_notify tren excel_reporting" "DarkGreen"; break
        }

        default { }   # bo qua cac dong khac
    }
}

# ── Tóm tắt (chỉ in khi -NoFollow, vì -f stream vô hạn) ─────────────────────────
if ($NoFollow) {
    Write-Host "------------------------------------------------------------------" -ForegroundColor DarkCyan
    Write-Host ("  request nhan : {0}   result tra : {1}" -f $script:nReq, $script:nDone)
    Write-Host ("  push OK      : {0}   push canh bao: {1}" -f $script:nPush, $script:nWarn)
}
