
# -*- coding: utf-8 -*-
import sqlite3, sys, io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
from datetime import datetime
db = r"E:\Documents\deepseek-harness\SoulLink\dist\data\run-db\run.db"
conn = sqlite3.connect(db); c = conn.cursor()
def lt(ms): return datetime.fromtimestamp(ms/1000).strftime('%m-%d %H:%M:%S') if ms else None

print("=== 『好感』全部 run（近期）===")
for r in c.execute("""SELECT run_id, group_name, start_at_ms, end_at_ms, state, picks, src_file_start
  FROM run WHERE group_name LIKE '%好感%' ORDER BY start_at_ms DESC LIMIT 15""").fetchall():
    print(f"  {lt(r[2])}~{lt(r[3]) if r[3] else 'Running'} {r[1]!r} state={r[4]} picks={r[5]} src={r[6][-14:] if r[6] else '(空)'}")

print()
print("=== 『好感』的 route 数量 ===")
for r in c.execute("""SELECT run_id, start_at_ms FROM run WHERE group_name LIKE '%好感%' AND start_at_ms > ? ORDER BY start_at_ms DESC LIMIT 5""", (datetime(2026,11,24).timestamp()*1000,)).fetchall():
    n = c.execute("SELECT COUNT(*) FROM route WHERE run_id=?", (r[0],)).fetchone()[0]
    print(f"  run {lt(r[1])}: routes={n}")

print()
print("=== 今日 08:33:15 那两条重复 route 的完整字段 ===")
for r in c.execute("""SELECT route_id, run_id, kind, name, start_at_ms, end_at_ms, dur_ms, state, pick_count, src_file
  FROM route WHERE name LIKE '%00-B001璃月沉玉谷南陵传奇%' AND start_at_ms BETWEEN ? AND ?""",
  (datetime(2026,11,26,8,33,0).timestamp()*1000, datetime(2026,11,26,8,34,0).timestamp()*1000)).fetchall():
    print(f"  route_id={r[0]!r}")
    print(f"     kind={r[2]} start={lt(r[4])} end={lt(r[5])} dur={r[6]} state={r[7]} picks={r[8]}")

conn.close()
