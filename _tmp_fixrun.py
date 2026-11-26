
# -*- coding: utf-8 -*-
import sqlite3, sys, io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
from datetime import datetime
db = r"E:\Documents\deepseek-harness\SoulLink\dist\data\run-db\run.db"
conn = sqlite3.connect(db); c = conn.cursor()
def lt(ms): return datetime.fromtimestamp(ms/1000).strftime('%H:%M:%S') if ms else None

# 找出被「历史回填收尾」误关的今日 run（detail 含该文案 且 src 是今天）
today = datetime(2026,11,26,0,0,0).timestamp()*1000
rows = c.execute("""
  SELECT run_id, group_name, start_at_ms, end_at_ms, detail FROM run
  WHERE start_at_ms >= ? AND detail LIKE '%历史回填收尾%'
""", (today,)).fetchall()
print("被回填误收尾的今日 run:", len(rows))
for r in rows:
    print(f"  {lt(r[2])}~{lt(r[3])} {r[1]!r}")
    # 恢复为 Running（清 end/dur/state/detail）
    c.execute("""UPDATE run SET end_at_ms=NULL, dur_ms=0, state='Running', detail='', end_day='', is_cross_day=0
                 WHERE run_id=?""", (r[0],))
    print(f"     -> 已恢复 Running")

# 同时把这些 run 里被误关的 route 也恢复（end==start 且 state=Finished 的）
n = 0
for rid, g, st, et, d in rows:
    n += c.execute("""UPDATE route SET end_at_ms=NULL, dur_ms=0, state='Running'
                      WHERE run_id=? AND end_at_ms IS NOT NULL AND end_at_ms=start_at_ms""", (rid,)).rowcount
print("恢复 route(未结束的):", n)

conn.commit()
print()
print("=== 恢复后今日 run ===")
for r in c.execute("""SELECT group_name, start_at_ms, end_at_ms, state, round_index, round_total, round_count
  FROM run WHERE start_at_ms>=? ORDER BY start_at_ms""", (today,)).fetchall():
    rd = f"轮次={r[4]}/{r[5]} cnt={r[6]}" if (r[5] or r[6]) else "(无轮次)"
    print(f"  {lt(r[1])}~{lt(r[2]) if r[2] else 'Running'} {r[0]!r} {r[3]} {rd}")
conn.close()
