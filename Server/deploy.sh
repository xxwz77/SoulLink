#!/bin/bash
# SoulLink 服务端部署脚本（Ubuntu）
# 用法：sudo bash deploy.sh

set -e

APP_DIR="/opt/soullink-server"
SERVICE_NAME="soullink-server"
BIN="SoulLink.Server"

echo "══════════════════════════════════════"
echo "  SoulLink 服务端部署"
echo "══════════════════════════════════════"

# 1. 建目录
echo "[1/5] 创建目录 $APP_DIR"
mkdir -p "$APP_DIR"

# 2. 复制文件（脚本和可执行文件同目录）
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
echo "[2/5] 复制程序文件"
cp -f "$SCRIPT_DIR/$BIN" "$APP_DIR/"
cp -f "$SCRIPT_DIR"/*.so "$APP_DIR/" 2>/dev/null || true
chmod +x "$APP_DIR/$BIN"

# 3. 防火墙放行（如果开了 ufw）
echo "[3/5] 配置防火墙"
if command -v ufw >/dev/null 2>&1; then
    ufw allow 5080/tcp >/dev/null 2>&1 || true
    echo "  已放行 5080 端口（如已启用 ufw）"
else
    echo "  未检测到 ufw，跳过"
fi

# 4. 创建 systemd 服务
echo "[4/5] 安装 systemd 服务"
cat > /etc/systemd/system/${SERVICE_NAME}.service <<EOF
[Unit]
Description=SoulLink Lobby Server
After=network.target

[Service]
Type=simple
WorkingDirectory=$APP_DIR
ExecStart=$APP_DIR/$BIN
Restart=always
RestartSec=5
Environment=SOULLINK_URLS=http://0.0.0.0:5080

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable $SERVICE_NAME

# 5. 启动
echo "[5/5] 启动服务"
systemctl restart $SERVICE_NAME
sleep 2
systemctl status $SERVICE_NAME --no-pager -l | head -15

echo ""
echo "══════════════════════════════════════"
echo "  部署完成"
echo "══════════════════════════════════════"
echo ""
echo "  服务名：$SERVICE_NAME"
echo "  目录：  $APP_DIR"
echo "  数据库：$APP_DIR/soul-link.db"
echo "  端口：  5080"
echo ""
echo "  常用命令："
echo "    systemctl status  $SERVICE_NAME   查看状态"
echo "    systemctl restart $SERVICE_NAME   重启"
echo "    journalctl -u $SERVICE_NAME -f    看日志"
echo ""
echo "  验证：curl http://127.0.0.1:5080/api/ping"
echo ""
echo "  ⚠ 记得在腾讯云控制台的安全组里放行 5080 端口"
echo ""
