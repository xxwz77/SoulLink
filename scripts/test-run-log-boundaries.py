"""Offline regression for the production scoped-log SQL. No service or account data."""
from pathlib import Path
import sqlite3

source = (Path(__file__).resolve().parents[1] / 'Services/RunRecordStore.cs').read_text(encoding='utf-8-sig')
sql = source.split('ReadRunLogAsync(', 1)[1].split('cmd.CommandText = @"', 1)[1].split('";', 1)[0]
db = sqlite3.connect(':memory:')
db.executescript('''
CREATE TABLE run(run_id TEXT PRIMARY KEY,account,start_at_ms,end_at_ms,src_file_start,src_file_end);
CREATE TABLE route(route_id TEXT PRIMARY KEY,run_id,kind,start_at_ms,end_at_ms,src_file);
CREATE TABLE raw_event(raw_id INTEGER PRIMARY KEY,account,source_day,src_file,ts_ms,line_no,raw_text,raw_truncated,chunk_offset,line_offset);
INSERT INTO run VALUES ('one','A',0,50,'f','f'),('two','A',50,100,'f','f'),('three','B',0,100,'f','f');
INSERT INTO route VALUES ('r1','one','route',10,25,'f'),('r2','one','route',25,40,'f'),('r3','two','route',55,90,'f');
''')
for ts in (5, 15, 25, 30, 49, 50, 60, None):
    db.execute("INSERT INTO raw_event(account,source_day,src_file,ts_ms,raw_text) VALUES ('A','d','f',?,?)", (ts, str(ts)))
for user, day, file in [('B','d','f'), ('A','other','f'), ('A','d','bad')]:
    db.execute('INSERT INTO raw_event(account,source_day,src_file,ts_ms,raw_text) VALUES (?,?,?,15,?)', (user, day, file, 'foreign'))
params = dict(user='A', day='d', file='f', run='one', route='', now=100, until=101, take=200, skip=0)
params['from'] = 0

def check(expected, **override):
    assert [row[3] for row in db.execute(sql, params | override)] == expected, override

check(['5','15','25','30','49'])
check(['15'], route='r1')
check(['30'], route='r2')
check([], route='r3')
check([], user='B')
check([], file='bad')
check([], day='missing')
check([], run='missing')
check(['60'], run='two')
check(['15','25'], take=2, skip=1)
check(['5'], until=15)
print('11 scoped-query assertions passed')
