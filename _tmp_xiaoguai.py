
# -*- coding: utf-8 -*-
import sqlite3, sys, io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
from datetime import datetime
db = r"E:\Documents\deepseek-harness\SoulLink\dist\data\run-db\run.db"
conn = sqlite3.connect(db); c = conn.cursor()
def lt(ms): return datetime.fromtimestamp(ms/1000).strftime('%H:%M:%S') if ms else None

print("=== 小怪-队员 run 完整字段 ===")
r = c.execute("""SELECT run_id, start_at_ms, end_at_ms, dur_ms, state, detail, src_file_start, src_file_end, round_index, round_total, round_host, round_count, is_backfilled, day_split
  FROM run WHERE group_name LIKE '%小怪-队员%' AND start_at_ms > ?""", (datetime(2026,11,26,0,0,0).timestamp()*1000,)).fetchall()
for x in r:
    print(f"  start={lt(x[1])} end={lt(x[2])} dur={x[3]/60000:.1f}min state={x[4]}")
    print(f"  detail={x[5]!r}")
    print(f"  src '{x[6]}' -> '{x[7]}'")
    print(f"  round={x[8]}/{x[9]} host={x[10]!r} count={x[11]} backfilled={x[12]} day_split={x[13]}")

print()
print("=== 该 run 的 route 状态分布（看是否有未结束的）===")
for x in c.execute("SELECT state, COUNT(*) FROM route WHERE run_id=? GROUP BY state", (r[0][0],)).fetchall():
    print(f"  {x[0]}: {x[1]}")
print()
print("=== 该 run 最后 5 条 route ===")
for x in c.execute("SELECT name, start_at_ms, end_at_ms, state FROM route WHERE run_id=? ORDER BY start_at_ms DESC LIMIT 5", (r[0][0],)).fetchall():
    print(f"  {lt(x[1])}~{lt(x[2]) if x[2] else 'open'} {x[3]} {x[0][:45]}")
conn.close()
