#!/bin/bash
# ═══════════════════════════════════════════════════════════════════════
#  SoulLink 联机大厅服务端 · 新服务器部署脚本（腾讯云轻量 / Ubuntu）
#  ═══════════════════════════════════════════════════════════════════════
#  与旧 deploy.sh 的区别（不要覆盖旧脚本，本文件是新服务器专用）：
#   1. 幂等：升级时自动备份旧二进制 + 旧数据库，不会把线上数据冲掉。
#   2. 单文件自包含发布（.NET 11 已交叉编译进 ELF），服务器**不需要装 .NET**。
#   3. 显式指定 native 库解压目录，绕开 /tmp noexec 的坑。
#   4. 加了内存/重启上限，防止 4GB 小机上异常时把整机拖死。
#   5. 部署后自动做本地健康检查（curl /api/ping）。
#
#  用法（在服务器上，把本脚本 + SoulLink.Server 传到同一目录后执行）：
#      sudo bash deploy-lighthouse.sh
#  ═══════════════════════════════════════════════════════════════════════
set -euo pipefail

APP_DIR="/opt/soullink-server"
SERVICE_NAME="soullink-server"
BIN="SoulLink.Server"
PORT="${SOULLINK_PORT:-5080}"
# 腾讯云 4C4G 小机：给服务设个合理上限，异常时不至于 OOM 拖死整机
MEM_LIMIT_MB="1024"
TIME_STAMP="$(date +%Y%m%d-%H%M%S)"

echo "══════════════════════════════════════════"
echo "  SoulLink 服务端部署（新服务器专用）"
echo "══════════════════════════════════════════"

# ── 0. 前置检查 ──────────────────────────────────────────────────────
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ ! -f "$SCRIPT_DIR/$BIN" ]; then
    echo "✗ 找不到 $SCRIPT_DIR/$BIN"
    echo "  请先在 Windows 上执行："
    echo "    dotnet publish Server\\SoulLink.Server.csproj -c Release -r linux-x64 --self-contained true \\"
    echo "      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none \\"
    echo "      -o dist-server"
    echo "  再把 dist-server/SoulLink.Server 和本脚本一起传到服务器同一目录。"
    exit 1
fi

# 校验二进制是 linux ELF（防止把 Windows exe 传上来）
if ! head -c 4 "$SCRIPT_DIR/$BIN" | grep -q $'\x7f\x45\x4c\x46'; then
    echo "✗ $BIN 不是 Linux ELF 可执行文件（头 4 字节不是 \\x7fELF）。"
    echo "  请确认用的是 -r linux-x64 交叉编译产物，而不是 Windows exe。"
    exit 1
fi

# ── 1. 建目录 ────────────────────────────────────────────────────────
echo "[1/7] 创建目录 $APP_DIR"
mkdir -p "$APP_DIR"
mkdir -p "$APP_DIR/backup"

# ── 2. 备份旧的二进制与数据库（幂等升级的关键）───────────────────────
if [ -f "$APP_DIR/$BIN" ]; then
    echo "[2/7] 备份旧二进制 → $APP_DIR/backup/$BIN.$TIME_STAMP"
    cp -f "$APP_DIR/$BIN" "$APP_DIR/backup/$BIN.$TIME_STAMP"
fi
for db in soul-link.db soul-link.db-wal soul-link.db-shm; do
    if [ -f "$APP_DIR/$db" ]; then
        cp -f "$APP_DIR/$db" "$APP_DIR/backup/$db.$TIME_STAMP"
        echo "       备份 $db → backup/$db.$TIME_STAMP"
    fi
done

# ── 3. 复制新文件 ────────────────────────────────────────────────────
echo "[3/7] 复制程序文件"
cp -f "$SCRIPT_DIR/$BIN" "$APP_DIR/$BIN"
# 老的部署方式可能带独立 *.so（新方式已内嵌，这里兼容保留）
cp -f "$SCRIPT_DIR"/*.so "$APP_DIR/" 2>/dev/null || true
chmod +x "$APP_DIR/$BIN"
echo "        二进制 SHA256: $(sha256sum "$APP_DIR/$BIN" | cut -d' ' -f1)"

# ── 4. 防火墙（只在 ufw 已启用时才放行，不做全局策略变更）────────────
echo "[4/7] 配置防火墙（仅当 ufw 已启用时放行 $PORT）"
if command -v ufw >/dev/null 2>&1 && ufw status | grep -q "Status: active"; then
    ufw allow "$PORT/tcp" >/dev/null 2>&1 || true
    echo "       已放行 $PORT/tcp"
else
    echo "       ufw 未启用或未安装，跳过（腾讯云安全组层面另行放行）"
fi

# ── 5. 安装 systemd 服务 ─────────────────────────────────────────────
echo "[5/7] 安装 systemd 服务 $SERVICE_NAME"
cat > "/etc/systemd/system/${SERVICE_NAME}.service" <<EOF
[Unit]
Description=SoulLink Lobby Server
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
WorkingDirectory=$APP_DIR
ExecStart=$APP_DIR/$BIN
Restart=always
RestartSec=5
Environment=SOULLINK_URLS=http://0.0.0.0:$PORT
Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=$APP_DIR/.extract
# 4C4G 小机资源上限：内存超限直接杀，不让单个服务拖垮整机
MemoryMax=${MEM_LIMIT_MB}M
# 异常自愈：30 秒内失败 3 次视为循环，10 秒后再试（防 start-limit 静默卡死）
StartLimitIntervalSec=30
StartLimitBurst=3

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable "$SERVICE_NAME" >/dev/null 2>&1 || true

# ── 6. 启动并健康检查 ────────────────────────────────────────────────
echo "[6/7] 启动服务"
systemctl reset-failed "$SERVICE_NAME" 2>/dev/null || true
systemctl restart "$SERVICE_NAME"
sleep 2

echo "[7/7] 健康检查"
if curl -fsS "http://127.0.0.1:$PORT/api/ping" >/dev/null 2>&1; then
    echo "       ✓ 本地 /api/ping 正常："
    curl -s "http://127.0.0.1:$PORT/api/ping"; echo
else
    echo "       ✗ 本地健康检查失败，请执行下面命令看日志："
    echo "         journalctl -u $SERVICE_NAME -n 50 --no-pager"
    echo "         ss -ltnp | grep $PORT"
    exit 1
fi

echo ""
echo "══════════════════════════════════════════"
echo "  部署完成"
echo "══════════════════════════════════════════"
echo "  服务名：$SERVICE_NAME"
echo "  目录：  $APP_DIR"
echo "  数据库：$APP_DIR/soul-link.db"
echo "  备份：  $APP_DIR/backup/"
echo "  端口：  $PORT"
echo ""
echo "  常用命令："
echo "    systemctl status  $SERVICE_NAME           查看状态"
echo "    systemctl restart $SERVICE_NAME           重启"
echo "    journalctl -u $SERVICE_NAME -f            看日志"
echo "    curl http://127.0.0.1:$PORT/api/ping      健康检查"
echo ""
echo "  ⚠ 记得在腾讯云控制台安全组放行 $PORT 端口（见 LOBBY-DEPLOY 文档）"
echo ""
