#!/bin/bash
set -e
APP_DIR="/opt/soullink-server"

echo "=== 1) 建 home 与解压缓存目录 ==="
sudo mkdir -p /home/soullink "$APP_DIR/cache"
sudo chown -R soullink:soullink /home/soullink "$APP_DIR"
sudo chmod 755 /home/soullink

echo "=== 2) 重写 service（干净版，避免 sed 转义问题）==="
sudo tee /etc/systemd/system/soullink-server.service > /dev/null <<EOF
[Unit]
Description=SoulLink Lobby Server
After=network.target

[Service]
Type=simple
User=soullink
Group=soullink
HomeDirectory=/home/soullink
WorkingDirectory=/opt/soullink-server
ExecStart=/opt/soullink-server/SoulLink.Server
Restart=always
RestartSec=5
Environment=ASPNETCORE_URLS=http://0.0.0.0:5080
Environment=SOULLINK_URLS=http://0.0.0.0:5080
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=/opt/soullink-server/cache
MemoryMax=1G
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
EOF

echo "=== 3) 重启 ==="
sudo systemctl daemon-reload
sudo systemctl restart soullink-server
sleep 5
sudo systemctl status soullink-server --no-pager -l | head -18

echo ""
echo "=== 4) 本机自检 ==="
curl -s -m 5 "http://127.0.0.1:5080/api/ping" || echo "(curl 无输出)"
echo ""