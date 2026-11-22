#!/bin/bash
set -e

APP_DIR="/opt/soullink-server"
SERVICE="soullink-server"
PORT=5080

echo "=== 1) 建目录 ==="
sudo mkdir -p "$APP_DIR"
sudo cp /tmp/SoulLink.Server "$APP_DIR/SoulLink.Server"
sudo chmod +x "$APP_DIR/SoulLink.Server"

echo "=== 2) 建系统用户（不用 root 跑）==="
if ! id soullink >/dev/null 2>&1; then
  sudo useradd -r -s /usr/sbin/nologin soullink
  echo "已创建 soullink 用户"
else
  echo "soullink 用户已存在"
fi
sudo mkdir -p "$APP_DIR/data"
sudo chown -R soullink:soullink "$APP_DIR"

echo "=== 3) systemd 服务 ==="
sudo tee /etc/systemd/system/$SERVICE.service > /dev/null <<EOF
[Unit]
Description=SoulLink Lobby Server
After=network.target

[Service]
Type=simple
User=soullink
WorkingDirectory=$APP_DIR
ExecStart=$APP_DIR/SoulLink.Server
Restart=always
RestartSec=5
Environment=ASPNETCORE_URLS=http://0.0.0.0:$PORT
Environment=SOULLINK_URLS=http://0.0.0.0:$PORT
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1

# 资源限制：服务器 3.7G 内存，给服务端封顶 1G，防止拖垮系统
MemoryMax=1G

# 日志
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
EOF

echo "=== 4) 启服务 ==="
sudo systemctl daemon-reload
sudo systemctl enable $SERVICE
sudo systemctl restart $SERVICE
sleep 3

echo "=== 5) 状态 ==="
sudo systemctl status $SERVICE --no-pager -l | head -20

echo ""
echo "=== 6) 本地自检 ==="
curl -s -m 5 "http://127.0.0.1:$PORT/api/ping" || echo "curl 失败"
echo ""