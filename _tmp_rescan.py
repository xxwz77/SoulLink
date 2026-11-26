
# -*- coding: utf-8 -*-
import sqlite3, os, sys, io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
from datetime import datetime
d = r"E:\Documents\deepseek-harness\SoulLink\dist\data\run-db"
db = os.path.join(d, "run.db")
ts = datetime.now().strftime('%Y%m%d-%H%M%S')
bakdir = os.path.join(d, f"backup-pre-roundrescan-{ts}")
os.makedirs(bakdir, exist_ok=True)
s = sqlite3.connect(db); b = sqlite3.connect(os.path.join(bakdir,"run.db"))
s.backup(b); b.close(); s.close()
print("已备份:", bakdir)

conn = sqlite3.connect(db); c = conn.cursor()
today0 = datetime(2026,11,26,0,0,0).timestamp()*1000
# 今日 run 及关联数据（cascade 会带走 route/pick/err）
n = c.execute("SELECT COUNT(*) FROM run WHERE start_at_ms >= ?", (today0,)).fetchone()[0]
print("今日 run 数:", n)
c.execute("DELETE FROM run WHERE start_at_ms >= ?", (today0,))
conn.commit()
print("已删今日 run（cascade 带走 route/pick_event/err）")
print("今日 run 剩余:", c.execute("SELECT COUNT(*) FROM run WHERE start_at_ms >= ?", (today0,)).fetchone()[0])
print("route 总数:", c.execute("SELECT COUNT(*) FROM route").fetchone()[0])
print("pick 总数:", c.execute("SELECT COUNT(*) FROM pick_event").fetchone()[0])
conn.close()
