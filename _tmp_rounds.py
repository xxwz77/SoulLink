
# -*- coding: utf-8 -*-
import sqlite3, sys, io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
from datetime import datetime
db = r"E:\Documents\deepseek-harness\SoulLink\dist\data\run-db\run.db"
conn = sqlite3.connect(db); c = conn.cursor()
def lt(ms): return datetime.fromtimestamp(ms/1000).strftime('%H:%M:%S') if ms else None
today0 = datetime(2026,11,26,0,0,0).timestamp()*1000

print("=== 今日 混合精英 的 run 记录 ===")
for r in c.execute("""
  SELECT run_id, start_at_ms, end_at_ms, state, picks, script_count, src_file_start
  FROM run WHERE group_name LIKE '%混合精英%' AND start_at_ms>=? ORDER BY start_at_ms
""", (today0,)).fetchall():
    print(f"  {lt(r[1])}~{lt(r[2]) if r[2] else 'Running'} state={r[3]} picks={r[4]} scripts={r[5]} src={r[6][-14:] if r[6] else '(空)'}")
    print(f"     run_id={r[0]!r}")

print()
print("=== 这些 run 下的 route 数量 ===")
for r in c.execute("""
  SELECT run_id, start_at_ms FROM run WHERE group_name LIKE '%混合精英%' AND start_at_ms>=? ORDER BY start_at_ms
""", (today0,)).fetchall():
    rid = r[0]
    n = c.execute("SELECT COUNT(*) FROM route WHERE run_id=?", (rid,)).fetchone()[0]
    kinds = c.execute("SELECT kind, COUNT(*) FROM route WHERE run_id=? GROUP BY kind", (rid,)).fetchall()
    print(f"  {lt(r[1])} routes={n}  kinds={kinds}")

print()
print("=== 同一路线名在今天出现了几次（=锄地一条龙遍历轮数）===")
for nm in ['00-B001璃月沉玉谷南陵传奇.json','99-ZZZZ997璃月-明蕴镇神像.json']:
    rows = c.execute("""
      SELECT run_id, start_at_ms, name FROM route
      WHERE name LIKE ? AND start_at_ms>=? ORDER BY start_at_ms
    """, ('%'+nm+'%', today0)).fetchall()
    print(f"  {nm}: {len(rows)} 次")
    for x in rows:
        print(f"     {lt(x[1])}")

conn.close()
