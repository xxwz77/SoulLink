
# -*- coding: utf-8 -*-
import sqlite3, sys, io, time
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
from datetime import datetime
db = r"E:\Documents\deepseek-harness\SoulLink\dist\data\run-db\run.db"
def lt(ms): return datetime.fromtimestamp(ms/1000).strftime('%H:%M:%S') if ms else None

def snap(tag):
    conn = sqlite3.connect(db); c = conn.cursor()
    today0 = datetime(2026,11,26,0,0,0).timestamp()*1000
    runs = c.execute("SELECT COUNT(*) FROM run WHERE start_at_ms>=?", (today0,)).fetchone()[0]
    picks = c.execute("SELECT COUNT(*) FROM pick_event WHERE run_id IN (SELECT run_id FROM run WHERE start_at_ms>=?)", (today0,)).fetchone()[0]
    print(f"[{tag}] 今日run={runs} 今日pick={picks}")
    conn.close()

snap("T0")
time.sleep(60)
snap("T+60s")

conn = sqlite3.connect(db); c = conn.cursor()
today0 = datetime(2026,11,26,0,0,0).timestamp()*1000
print()
print("=== 今日 run 的轮次字段 ===")
for r in c.execute("""SELECT group_name, start_at_ms, end_at_ms, state, round_index, round_total, round_host, round_count
  FROM run WHERE start_at_ms>=? ORDER BY start_at_ms""", (today0,)).fetchall():
    rd = f"轮次={r[4]}/{r[5]} host={r[6]!r} count={r[7]}" if (r[5] or r[7]) else "(无轮次)"
    print(f"  {lt(r[1])}~{lt(r[2]) if r[2] else 'Running'} {r[0]!r} state={r[3]}  {rd}")
conn.close()
