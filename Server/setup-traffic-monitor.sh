#!/bin/bash
# ═══════════════════════════════════════════════════════════════════════
#  SoulLink 服务端 · 流量监控安装 + 日报 + 阈值告警（systemd timer）
#  ═══════════════════════════════════════════════════════════════════════
#  用途：腾讯云轻量按出网流量计费，用户刚因流量欠费。本脚本：
#    1. 安装 vnstat 并开机自启（按网卡累计，重启不清零）
#    2. 生成每日流量日报脚本 + systemd timer（每天 08:00 跑）
#    3. 生成阈值告警脚本 + timer（每小时检查本月出网是否超限）
#
#  用法（服务器上执行一次即可）：
#      sudo bash setup-traffic-monitor.sh
#  ═══════════════════════════════════════════════════════════════════════
set -euo pipefail

# 每月出网流量阈值（MB）。腾讯云轻量套餐额度按需改，默认 100 GB = 102400 MB
MONTHLY_LIMIT_MB="${SOULLINK_MONTHLY_LIMIT_MB:-102400}"

echo "══════════════════════════════════════"
echo "  SoulLink 流量监控安装"
echo "══════════════════════════════════════"

# ── 1. 安装 vnstat ────────────────────────────────────────────────────
echo "[1/4] 安装 vnstat"
if ! command -v vnstat >/dev/null 2>&1; then
    apt-get update -y
    apt-get install -y vnstat
else
    echo "  vnstat 已安装"
fi
systemctl enable --now vnstat 2>/dev/null || true

# 等 vnstat 抓到第一个采样（新装后数据库可能为空，稍等几秒）
sleep 3
vnstat -d >/dev/null 2>&1 || echo "  （vnstat 首次采样尚未产生，属正常，等 5 分钟后即有数据）"

# ── 2. 写每日日报脚本 ────────────────────────────────────────────────
echo "[2/4] 生成每日流量日报脚本"
cat > /usr/local/bin/soullink-traffic-daily.sh <<'EOF'
#!/bin/bash
# SoulLink 每日流量日报，输出追加到 /var/log/soullink-traffic.log
LOG=/var/log/soullink-traffic.log
{
  echo "════════ $(date '+%F %T') ════════"
  echo "── 今日 ──"
  vnstat -d | tail -3
  echo "── 本月累计 ──"
  vnstat -m | tail -3
  echo "── 昨日 ──"
  vnstat -d | grep -E "$(date -d yesterday '+%Y-%m-%d')" || echo "  (昨日无记录)"
} >> "$LOG" 2>&1
EOF
chmod +x /usr/local/bin/soullink-traffic-daily.sh

# ── 3. 写阈值告警脚本 ────────────────────────────────────────────────
echo "[3/4] 生成阈值告警脚本（阈值 ${MONTHLY_LIMIT_MB} MB）"
cat > /usr/local/bin/soullink-traffic-alarm.sh <<EOF
#!/bin/bash
# 检查本月出网累计是否超过阈值 ${MONTHLY_LIMIT_MB} MB
LIMIT_MB=${MONTHLY_LIMIT_MB}
TX_MB="\$(vnstat --json m 2>/dev/null | python3 -c 'import sys,json; d=json.load(sys.stdin); print(round(d["interfaces"][0]["traffic"]["month"][-1]["tx"]/1048576))' 2>/dev/null || echo 0)"
if [ "\$TX_MB" -ge "\$LIMIT_MB" ]; then
  # 超限：写日志 + 调用 cloud-init 日志醒目提示；也可在此加短信/webhook 通知
  echo "\$(date '+%F %T') ⚠ 本月出网 \${TX_MB} MB ≥ 阈值 \${LIMIT_MB} MB" >> /var/log/soullink-traffic.log
  echo "⚠ 本月出网 \${TX_MB} MB 已超阈值 \${LIMIT_MB} MB" | systemd-cat -t soullink-traffic-alarm -p warning
fi
EOF
chmod +x /usr/local/bin/soullink-traffic-alarm.sh

# ── 4. 写 systemd timer（每日 + 每小时）───────────────────────────────
echo "[4/4] 安装 systemd timer"

# 日报：每天 08:00
cat > /etc/systemd/system/soullink-traffic-daily.service <<'EOF'
[Unit]
Description=SoulLink daily traffic report
After=network.target

[Service]
Type=oneshot
ExecStart=/usr/local/bin/soullink-traffic-daily.sh
EOF

cat > /etc/systemd/system/soullink-traffic-daily.timer <<'EOF'
[Unit]
Description=SoulLink daily traffic report timer

[Timer]
OnCalendar=*-*-* 08:00:00
Persistent=true

[Install]
WantedBy=timers.target
EOF

# 告警：每小时
cat > /etc/systemd/system/soullink-traffic-alarm.service <<'EOF'
[Unit]
Description=SoulLink monthly traffic threshold alarm
After=network.target

[Service]
Type=oneshot
ExecStart=/usr/local/bin/soullink-traffic-alarm.sh
EOF

cat > /etc/systemd/system/soullink-traffic-alarm.timer <<'EOF'
[Unit]
Description=SoulLink monthly traffic alarm timer

[Timer]
OnCalendar=*-*-* *:00:00
Persistent=true

[Install]
WantedBy=timers.target
EOF

systemctl daemon-reload
systemctl enable --now soullink-traffic-daily.timer
systemctl enable --now soullink-traffic-alarm.timer

echo ""
echo "══════════════════════════════════════"
echo "  流量监控安装完成"
echo "══════════════════════════════════════"
echo "  日报：  每天 08:00 → /var/log/soullink-traffic.log"
echo "  告警：  每小时检查，本月出网 ≥ ${MONTHLY_LIMIT_MB} MB 时写日志+journal"
echo ""
echo "  手动查看："
echo "    vnstat -d                   今日/昨日/本月逐日"
echo "    vnstat -m                   按月汇总"
echo "    vnstat -h                   今日逐小时"
echo "    sudo bash /usr/local/bin/soullink-traffic-alarm.sh  手动触发告警检查"
echo ""
echo "  改阈值：编辑 /usr/local/bin/soullink-traffic-alarm.sh 里的 LIMIT_MB"
echo "  （或重跑本脚本时设环境变量 SOULLINK_MONTHLY_LIMIT_MB）"
echo ""
echo "  ⚠ 腾讯云轻量套餐额度可在控制台「监控」页确认，按实际额度设置阈值"
echo "  ⚠ 建议额度设为套餐的 80%，留 20% 缓冲避免超量欠费"
echo ""
