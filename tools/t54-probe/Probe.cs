using Microsoft.Data.Sqlite;

public static class Probe
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string db = args[0];
        string mode = args.Length > 1 ? args[1] : "count";
        var csb = new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var conn = new SqliteConnection(csb.ToString());
        conn.Open();
        void Run(string sql, string tag)
        {
            Console.WriteLine("---- " + tag + " ----");
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                using var r = cmd.ExecuteReader();
                var names = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToList();
                Console.WriteLine("(cols) " + string.Join(" | ", names));
                int n = 0;
                while (r.Read())
                {
                    var row = new string[r.FieldCount];
                    for (int i = 0; i < r.FieldCount; i++) row[i] = r.IsDBNull(i) ? "NULL" : r.GetValue(i).ToString() ?? "";
                    Console.WriteLine("  " + string.Join(" | ", row.Select(c => c.Length > 70 ? c[..70] + "…" : c)));
                    if (++n >= 70) { Console.WriteLine("  …truncated"); break; }
                }
                if (n == 0) Console.WriteLine("  (no rows)");
            }
            catch (Exception ex) { Console.WriteLine("  [err] " + ex.Message); }
        }
        if (mode == "count")
        {
            Run("SELECT count(*) AS neg_routes FROM route WHERE end_at_ms < start_at_ms", "负时长 route 数");
            Run("SELECT count(*) AS neg_runs FROM run WHERE end_at_ms < start_at_ms", "负时长 run 数");
            Run("SELECT count(*) AS total_routes FROM route", "route 总数");
            Run("SELECT count(*) AS total_runs FROM run", "run 总数");
        }
        else if (mode == "samples")
        {
            Run(@"SELECT route_id, run_id, name, start_at_ms, end_at_ms, dur_ms, src_file
                  FROM route WHERE end_at_ms < start_at_ms
                  ORDER BY start_at_ms LIMIT 70", "负时长 route 样例");
            Run(@"SELECT run_id, account, group_name, start_at_ms, end_at_ms, dur_ms, src_file_start, src_file_end
                  FROM run WHERE end_at_ms < start_at_ms LIMIT 10", "负时长 run 样例");
        }
        else if (mode == "context")
        {
            // given a run_id, dump same-run routes ordered by start
            string runId = args[2];
            Run("SELECT route_id,name,kind,start_at_ms,end_at_ms,dur_ms,state FROM route WHERE run_id='" + runId.Replace("'", "") + "' ORDER BY start_at_ms", "同 run 路线");
            Run("SELECT run_id,group_name,start_at_ms,end_at_ms,dur_ms,state,day_split,is_cross_day FROM run WHERE run_id='" + runId.Replace("'", "") + "'", "run 行");
        }
        return 0;
    }
}
