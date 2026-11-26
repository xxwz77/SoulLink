
# -*- coding: utf-8 -*-
import sqlite3, sys, io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
from datetime import datetime
db = r"E:\Documents\deepseek-harness\SoulLink\dist\data\run-db\run.db"
conn = sqlite3.connect(db); c = conn.cursor()
def lt(ms): return datetime.fromtimestamp(ms/1000).strftime('%H:%M:%S') if ms else None
today0 = datetime(2026,11,26,0,0,0).timestamp()*1000

print("=== 现在的 混合精英 run ===")
for r in c.execute("""SELECT start_at_ms, end_at_ms, state, picks FROM run
  WHERE group_name LIKE '%混合精英%' AND start_at_ms>=? ORDER BY start_at_ms""", (today0,)).fetchall():
    print(f"  {lt(r[0])}~{lt(r[1]) if r[1] else 'Running'} {r[2]} picks={r[3]}")

print()
print("=== 锄地一条龙 的『进入路线循环 count=1』次数（=轮次）按 run 分 ===")
for r in c.execute("""SELECT run_id, start_at_ms FROM run
  WHERE group_name LIKE '%混合精英%' AND start_at_ms>=? ORDER BY start_at_ms""", (today0,)).fetchall():
    rid, st = r
    rounds = c.execute("""SELECT start_at_ms FROM route
      WHERE run_id=? AND name LIKE '%00-B001%' ORDER BY start_at_ms""", (rid,)).fetchall()
    print(f"  run {lt(st)}: 轮次={len(rounds)}")
    for x in rounds:
        print(f"      {lt(x[0])}")

print()
print("=== 全部今日 route 里 name 含 00-B001 的（不限组）===")
for r in c.execute("""SELECT r.name, r.start_at_ms, r.kind, ru.group_name FROM route r
  LEFT JOIN run ru ON r.run_id=ru.run_id
  WHERE r.name LIKE '%00-B001%' AND r.start_at_ms>=? ORDER BY r.start_at_ms""", (today0,)).fetchall():
    print(f"  {lt(r[1])} kind={r[2]} group={r[3]!r}")

conn.close()
