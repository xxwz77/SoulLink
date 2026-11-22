#!/bin/bash
# ═══════════════════════════════════════════════════════════════════════
#  SoulLink 联机大厅服务端 · 一键运维脚本（状态检查 / 备份 / 回滚）
#  ═══════════════════════════════════════════════════════════════════════
#  用法（在服务器上执行）：
#      sudo bash ops.sh status        查看服务/端口/磁盘/内存/流量
#      sudo bash ops.sh backup        备份当前二进制 + 数据库到 backup/
#      sudo bash ops.sh rollback      回滚到最近一次备份（交互确认）
#      sudo bash ops.sh rollback <TS> 回滚到指定时间戳备份
#      sudo bash ops.sh traffic       今日/本月流量 + 每小时明细
#      sudo bash ops.sh alarm <MB>    检查本月累计流量是否超阈值
#  ═══════════════════════════════════════════════════════════════════════
set -euo pipefail

APP_DIR="/opt/soullink-server"
SERVICE_NAME="soullink-server"
BIN="SoulLink.Server"
PORT="${SOULLINK_PORT:-5080}"
BACKUP_DIR="$APP_DIR/backup"

# ── 颜色 ──────────────────────────────────────────────────────────────
G='\033[0;32m'; R='\033[0;31m'; Y='\033[0;33m'; N='\033[0m'
ok(){ echo -e "${G}✓${N} $*"; }
warn(){ echo -e "${Y}⚠${N} $*"; }
err(){ echo -e "${R}✗${N} $*"; }

# ══════════ status ══════════
do_status() {
  echo "────────────────── 服务状态 ──────────────────"
  systemctl is-active "$SERVICE_NAME" 2>/dev/null || echo "inactive"
  systemctl status "$SERVICE_NAME" --no-pager -l 2>/dev/null | head -8 || true

  echo "────────────────── 健康检查 ──────────────────"
  if curl -fsS "http://127.0.0.1:$PORT/api/ping" 2>/dev/null; then
    echo ""
    ok "本地 /api/ping 正常"
  else
    err "本地 /api/ping 失败（服务可能没起来）"
  fi

  echo "────────────────── 端口监听 ──────────────────"
  ss -ltnp 2>/dev/null | grep ":$PORT" || warn "$PORT 未监听"

  echo "────────────────── 磁盘 ──────────────────"
  df -h "$APP_DIR" | tail -2

  echo "────────────────── 内存 ──────────────────"
  free -h

  echo "────────────────── 进程 ──────────────────"
  systemctl show "$SERVICE_NAME" -p MainPID -p MemoryCurrent 2>/dev/null || true

  echo "────────────────── 数据库 ──────────────────"
  ls -lh "$APP_DIR"/soul-link.db* 2>/dev/null || warn "无数据库文件"

  echo "────────────────── 流量（若有 vnstat）──────────────────"
  if command -v vnstat >/dev/null 2>&1; then
    vnstat -d | tail -4
  else
    warn "vnstat 未安装，运行: sudo apt-get install -y vnstat && sudo systemctl enable --now vnstat"
  fi
}

# ══════════ backup ══════════
do_backup() {
  mkdir -p "$BACKUP_DIR"
  local ts; ts="$(date +%Y%m%d-%H%M%S)"
  cp -f "$APP_DIR/$BIN" "$BACKUP_DIR/$BIN.$ts" 2>/dev/null || warn "无二进制可备份"
  for db in soul-link.db soul-link.db-wal soul-link.db-shm; do
    [ -f "$APP_DIR/$db" ] && cp -f "$APP_DIR/$db" "$BACKUP_DIR/$db.$ts"
  done
  # 只保留最近 10 份，避免备份把 40G 小盘堆满
  ls -1t "$BACKUP_DIR"/SoulLink.Server.* 2>/dev/null | tail -n +11 | xargs -r rm -f
  ls -1t "$BACKUP_DIR"/soul-link.db.* 2>/dev/null | tail -n +11 | xargs -r rm -f
  ok "已备份 → $BACKUP_DIR/ (时间戳 $ts)"
  ls -lh "$BACKUP_DIR" | tail -n +2
}

# ══════════ rollback ══════════
do_rollback() {
  local ts="${1:-}"
  if [ -z "$ts" ]; then
    ts="$(ls -1t "$BACKUP_DIR"/SoulLink.Server.* 2>/dev/null | head -1 | sed 's/.*SoulLink\.Server\.//')"
    [ -z "$ts" ] && { err "没有可用备份"; exit 1; }
    warn "将回滚到最近备份：$ts"
    read -r -p "确认回滚？[y/N] " ans
    [ "$ans" = "y" ] || [ "$ans" = "Y" ] || { echo "已取消"; exit 0; }
  fi

  systemctl stop "$SERVICE_NAME"
  cp -f "$BACKUP_DIR/$BIN.$ts" "$APP_DIR/$BIN"
  chmod +x "$APP_DIR/$BIN"
  # 数据库一起回滚（WAL 写入的库，只回二进制可能读不了）
  [ -f "$BACKUP_DIR/soul-link.db.$ts" ] && cp -f "$BACKUP_DIR/soul-link.db.$ts" "$APP_DIR/soul-link.db"
  rm -f "$APP_DIR/soul-link.db-wal" "$APP_DIR/soul-link.db-shm"
  systemctl reset-failed "$SERVICE_NAME" 2>/dev/null || true
  systemctl start "$SERVICE_NAME"
  sleep 2
  if curl -fsS "http://127.0.0.1:$PORT/api/ping" >/dev/null 2>&1; then
    ok "回滚完成，服务已恢复"
  else
    err "回滚后健康检查失败，看日志: journalctl -u $SERVICE_NAME -n 50 --no-pager"
    exit 1
  fi
}

# ══════════ traffic ══════════
do_traffic() {
  command -v vnstat >/dev/null 2>&1 || { err "vnstat 未安装"; exit 1; }
  echo "── 今日（tx=出网计费 / rx=入网）──"
  vnstat -d | tail -3
  echo "── 本月 ──"
  vnstat -m | tail -3
  echo "── 今日逐小时 ──"
  vnstat -h | tail -26
}

# ══════════ alarm ══════════
do_alarm() {
  local limit_mb="${1:-}"
  [ -z "$limit_mb" ] && { echo "用法: sudo bash ops.sh alarm <MB>"; exit 1; }
  command -v vnstat >/dev/null 2>&1 || { err "vnstat 未安装"; exit 1; }
  # 用 JSON 精确取本月 tx（出网）字节数
  local tx_mb
  tx_mb="$(vnstat --json m | python3 -c 'import sys,json; d=json.load(sys.stdin); print(round(d["interfaces"][0]["traffic"]["month"][-1]["tx"]/1048576))' 2>/dev/null || echo 0)"
  echo "本月出网累计：${tx_mb} MB（阈值 ${limit_mb} MB）"
  if [ "$tx_mb" -ge "$limit_mb" ]; then
    err "⚠ 已超阈值！本月出网 ${tx_mb} MB ≥ ${limit_mb} MB，请立即排查流量来源"
    exit 2
  else
    ok "未超阈值"
  fi
}

# ══════════ 分发 ══════════
case "${1:-}" in
  status)  do_status ;;
  backup)  do_backup ;;
  rollback) do_rollback "${2:-}" ;;
  traffic) do_traffic ;;
  alarm)   do_alarm "${2:-}" ;;
  *)
    echo "SoulLink 运维脚本用法："
    echo "  sudo bash ops.sh status         一键状态检查"
    echo "  sudo bash ops.sh backup         备份二进制+数据库"
    echo "  sudo bash ops.sh rollback [TS]  回滚（不带参数=最近一次）"
    echo "  sudo bash ops.sh traffic        今日/本月/每小时流量"
    echo "  sudo bash ops.sh alarm <MB>     检查本月出网是否超阈值"
    exit 1 ;;
esac
