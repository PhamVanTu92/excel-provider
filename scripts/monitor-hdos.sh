#!/usr/bin/env bash
# ════════════════════════════════════════════════════════════════════════════
# monitor-hdos.sh — màn hình theo dõi real-time Excel Provider <-> HDOS
#   ◀ INBOUND  : request HDOS gửi tới qua gRPC bridge + kết quả trả về
#   ▶ OUTBOUND : push event datasource.updated sang HDOS Ingestion API
#
# Dùng:
#   ./scripts/monitor-hdos.sh                       # theo dõi liên tục
#   ./scripts/monitor-hdos.sh <container>           # đổi tên container
#   TAIL=200 ./scripts/monitor-hdos.sh              # đổi số dòng log cũ
# ════════════════════════════════════════════════════════════════════════════
set -euo pipefail

CONTAINER="${1:-excel-provider-excel-provider-1}"
TAIL="${TAIL:-50}"

# ── Màu ANSI ─────────────────────────────────────────────────────────────────
G='\033[32m'; DG='\033[2;32m'; C='\033[36m'; Y='\033[33m'
R='\033[31m'; GR='\033[90m'; W='\033[97m'; X='\033[0m'

# ── Kiểm tra container ───────────────────────────────────────────────────────
if ! docker ps --format '{{.Names}}' | grep -qx "$CONTAINER"; then
    echo -e "${R}[X] Container '$CONTAINER' không chạy. Các container đang chạy:${X}"
    docker ps --format '    {{.Names}}   ({{.Status}})'
    exit 1
fi

emit() { printf "[%s] ${2}%-13s${X} %s\n" "$(date +%H:%M:%S)" "$1" "$3"; }

clear
echo -e "${W}==================================================================${X}"
echo -e "${W}  EXCEL PROVIDER  <->  HDOS    live monitor${X}"
echo -e "${GR}  container: $CONTAINER${X}"
echo -e "${GR}  (green) INBOUND request từ HDOS    (cyan) OUTBOUND push -> HDOS${X}"
echo -e "${GR}  Ctrl+C để thoát${X}"
echo -e "${W}==================================================================${X}"

# ── Stream log ───────────────────────────────────────────────────────────────
docker logs --tail "$TAIL" -f "$CONTAINER" 2>&1 | while IFS= read -r line; do
    if   [[ $line =~ OperationRequest\ received.*requestId=([^,]+),?\ operation=([^ ]+) ]]; then
        emit "<- REQUEST"   "$G"  "${BASH_REMATCH[2]}   req=${BASH_REMATCH[1]}"
    elif [[ $line =~ Terminal\ sent.*status=([A-Za-z]+).*elapsed=([0-9]+) ]]; then
        clr=$G; [[ ${BASH_REMATCH[1]} != Done ]] && clr=$Y
        emit "  -> RESULT"  "$clr" "${BASH_REMATCH[1]}  (${BASH_REMATCH[2]}ms)"
    elif [[ $line =~ No\ handler\ registered\ for\ operation=([^ ]+) ]]; then
        emit "  -> NO HANDLER" "$R" "${BASH_REMATCH[1]}"
    elif [[ $line =~ Handler\ threw\ for\ requestId=([^ ]+) ]]; then
        emit "  -> HANDLER ERR" "$R" "${BASH_REMATCH[1]}"
    elif [[ $line =~ Cancel\ received\ for\ requestId=([^ ]+) ]]; then
        emit "  -> CANCEL"  "$Y" "${BASH_REMATCH[1]}"

    elif [[ $line =~ pushing\ ([0-9]+)\ operations\ to\ HDOS ]]; then
        emit "-> PUSH"      "$C" "${BASH_REMATCH[1]} op -> HDOS Ingestion"
    elif [[ $line =~ WidgetStale\ notification\ sent.*affectedOperations=\[(.*)\] ]]; then
        emit "  -> PUSH OK" "$C" "[${BASH_REMATCH[1]}]"
    elif [[ $line =~ Ingestion\ auth\ not\ configured ]]; then
        emit "  -> PUSH 403" "$Y" "ingestion scope chưa cấu hình (Keycloak)"
    elif [[ $line =~ Ingestion\ API\ returned\ ([0-9]+) ]]; then
        emit "  -> PUSH ${BASH_REMATCH[1]}" "$Y" "Ingestion từ chối"
    elif [[ $line =~ Could\ not\ obtain\ bearer\ token ]]; then
        emit "  -> PUSH SKIP" "$Y" "không lấy được token"
    elif [[ $line =~ pg_notify\ received.*table=([^ ]+) ]]; then
        emit "  . notify"  "$GR" "table=${BASH_REMATCH[1]}"

    elif [[ $line =~ Token\ acquired,\ expires\ in\ ([0-9]+) ]]; then
        emit "[ok] token"  "$DG" "expires ${BASH_REMATCH[1]}s"
    elif [[ $line =~ Token\ endpoint\ returned\ ([0-9]+) ]]; then
        emit "[X] token ${BASH_REMATCH[1]}" "$R" "sai secret hoặc HDOS từ chối"
    elif [[ $line =~ Connecting\ to\ bridge\ at\ ([^ ]+) ]]; then
        emit ".. bridge"   "$GR" "connecting ${BASH_REMATCH[1]}"
    elif [[ $line =~ Hello\ sent ]]; then
        emit ".. hello"    "$GR" "đã gửi Hello"
    elif [[ $line =~ Welcome\ received.*sessionId=([^ ,]+) ]]; then
        emit "[ok] REGISTERED" "$G" "HDOS bridge, session=${BASH_REMATCH[1]}"
    elif [[ $line =~ Disconnect\ received ]]; then
        emit "[X] DISCONNECT" "$R" "bridge ngắt kết nối"
    elif [[ $line =~ ReplicationListenerService\ connected ]]; then
        emit "[ok] listener" "$DG" "đang nghe pg_notify trên excel_reporting"
    fi
done
