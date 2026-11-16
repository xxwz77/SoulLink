using Microsoft.Data.Sqlite;

namespace SoulLink.Server;

/// <summary>
/// SQLite 数据访问。单文件数据库，适合轻量服务器。
///
/// 表结构：
///   Devices —— 设备（一台电脑一个），带设备码
///   Rooms   —— 房间（大号房 / 小号房 / 四人房）
///   Seats   —— 房间里的座位（谁上线了）
///   Departs —— 每个设备的发车动作配置
/// </summary>
public sealed class Db
{
    private readonly string _connStr;

    public Db(string dbPath)
    {
        _connStr = "Data Source=" + dbPath;
        Init();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connStr);
        c.Open();
        return c;
    }

    private void Init()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Devices (
    DeviceCode TEXT PRIMARY KEY,     -- 设备码（软件生成，用户唯一标识）
    DisplayName TEXT NOT NULL DEFAULT '',
    CreatedAt TEXT NOT NULL,
    LastSeenAt TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS Rooms (
    Id TEXT PRIMARY KEY,
    Code TEXT NOT NULL,              -- 房间码（通行证）
    Title TEXT NOT NULL,
    Kind INTEGER NOT NULL,           -- 0=四人  1=大小号
    OwnerDevice TEXT NOT NULL,       -- 创建者的设备码
    PairId TEXT NOT NULL DEFAULT '',
    PairRole TEXT NOT NULL DEFAULT '',
    Departed INTEGER NOT NULL DEFAULT 0,
    CreatedAt TEXT NOT NULL,
    LastActiveAt TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_Rooms_Code ON Rooms(Code);

CREATE TABLE IF NOT EXISTS Seats (
    RoomId TEXT NOT NULL,
    SeatIndex INTEGER NOT NULL,
    DeviceCode TEXT NOT NULL DEFAULT '',
    DisplayName TEXT NOT NULL DEFAULT '',
    Online INTEGER NOT NULL DEFAULT 0,
    RoleName TEXT NOT NULL DEFAULT '',
    LastSeenAt TEXT NOT NULL DEFAULT '',   -- 心跳时间，用于判定掉线
    PRIMARY KEY (RoomId, SeatIndex)
);

CREATE TABLE IF NOT EXISTS Departs (
    DeviceCode TEXT PRIMARY KEY,
    Mode TEXT NOT NULL DEFAULT '',
    ActionId TEXT NOT NULL DEFAULT '',
    DragonName TEXT NOT NULL DEFAULT '',
    Groups TEXT NOT NULL DEFAULT '',
    Summary TEXT NOT NULL DEFAULT ''
);
";
        cmd.ExecuteNonQuery();
    }

    // ══════════ 设备 ══════════

    /// <summary>注册或更新设备。返回是否新设备。</summary>
    public bool UpsertDevice(string code, string displayName)
    {
        using var c = Open();

        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT COUNT(*) FROM Devices WHERE DeviceCode=$c";
            q.Parameters.AddWithValue("$c", code);
            if (Convert.ToInt64(q.ExecuteScalar()) > 0)
            {
                using var u = c.CreateCommand();
                u.CommandText = @"UPDATE Devices SET DisplayName=$n, LastSeenAt=$t WHERE DeviceCode=$c";
                u.Parameters.AddWithValue("$c", code);
                u.Parameters.AddWithValue("$n", displayName);
                u.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
                u.ExecuteNonQuery();
                return false;
            }
        }

        using var i = c.CreateCommand();
        i.CommandText = @"INSERT INTO Devices(DeviceCode,DisplayName,CreatedAt,LastSeenAt)
                          VALUES($c,$n,$t,$t)";
        i.Parameters.AddWithValue("$c", code);
        i.Parameters.AddWithValue("$n", displayName);
        i.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        i.ExecuteNonQuery();
        return true;
    }

    /// <summary>更新设备最后活跃时间。</summary>
    public void TouchDevice(string code)
    {
        using var c = Open();
        using var u = c.CreateCommand();
        u.CommandText = "UPDATE Devices SET LastSeenAt=$t WHERE DeviceCode=$c";
        u.Parameters.AddWithValue("$c", code);
        u.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        u.ExecuteNonQuery();
    }

    // ══════════ 房间 ══════════

    public void InsertRoom(RoomRow r)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"INSERT INTO Rooms
            (Id,Code,Title,Kind,OwnerDevice,PairId,PairRole,Departed,CreatedAt,LastActiveAt)
            VALUES($i,$code,$t,$k,$o,$p,$pr,$d,$cr,$la)";
        cmd.Parameters.AddWithValue("$i", r.Id);
        cmd.Parameters.AddWithValue("$code", r.Code);
        cmd.Parameters.AddWithValue("$t", r.Title);
        cmd.Parameters.AddWithValue("$k", (int)r.Kind);
        cmd.Parameters.AddWithValue("$o", r.OwnerDevice);
        cmd.Parameters.AddWithValue("$p", r.PairId);
        cmd.Parameters.AddWithValue("$pr", r.PairRole);
        cmd.Parameters.AddWithValue("$d", r.Departed ? 1 : 0);
        cmd.Parameters.AddWithValue("$cr", r.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$la", r.LastActiveAt.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public List<RoomRow> ListRooms()
    {
        var list = new List<RoomRow>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM Rooms ORDER BY CreatedAt";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(ReadRoom(rd));
        return list;
    }

    public RoomRow? FindRoomByCode(string code)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM Rooms WHERE Code=$c COLLATE NOCASE LIMIT 1";
        cmd.Parameters.AddWithValue("$c", code);
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? ReadRoom(rd) : null;
    }

    public RoomRow? FindRoomById(string id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM Rooms WHERE Id=$i LIMIT 1";
        cmd.Parameters.AddWithValue("$i", id);
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? ReadRoom(rd) : null;
    }

    public void UpdateRoom(RoomRow r)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"UPDATE Rooms SET Code=$code,Title=$t,Departed=$d,LastActiveAt=$la WHERE Id=$i";
        cmd.Parameters.AddWithValue("$i", r.Id);
        cmd.Parameters.AddWithValue("$code", r.Code);
        cmd.Parameters.AddWithValue("$t", r.Title);
        cmd.Parameters.AddWithValue("$d", r.Departed ? 1 : 0);
        cmd.Parameters.AddWithValue("$la", r.LastActiveAt.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void DeleteRoom(string id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM Seats WHERE RoomId=$i; DELETE FROM Rooms WHERE Id=$i;";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>删除闲置超过指定天数的房间（连同座位）。</summary>
    public int PurgeIdleRooms(int days)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var dead = new List<string>();

        using (var c = Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Id, LastActiveAt, PairId FROM Rooms";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                string id = rd.GetString(0);
                if (!DateTime.TryParse(rd.GetString(1), out var la)) continue;
                if (la < cutoff) dead.Add(id);
            }
        }

        foreach (var id in dead) DeleteRoom(id);
        return dead.Count;
    }

    private static RoomRow ReadRoom(SqliteDataReader rd) => new()
    {
        Id = rd.GetString(rd.GetOrdinal("Id")),
        Code = rd.GetString(rd.GetOrdinal("Code")),
        Title = rd.GetString(rd.GetOrdinal("Title")),
        Kind = (RoomKind)(int)rd.GetInt64(rd.GetOrdinal("Kind")),
        OwnerDevice = rd.GetString(rd.GetOrdinal("OwnerDevice")),
        PairId = rd.GetString(rd.GetOrdinal("PairId")),
        PairRole = rd.GetString(rd.GetOrdinal("PairRole")),
        Departed = rd.GetInt64(rd.GetOrdinal("Departed")) != 0,
        CreatedAt = DateTime.Parse(rd.GetString(rd.GetOrdinal("CreatedAt"))),
        LastActiveAt = DateTime.Parse(rd.GetString(rd.GetOrdinal("LastActiveAt")))
    };

    // ══════════ 座位 ══════════

    public List<SeatRow> ListSeats(string roomId)
    {
        var list = new List<SeatRow>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM Seats WHERE RoomId=$r ORDER BY SeatIndex";
        cmd.Parameters.AddWithValue("$r", roomId);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            list.Add(new SeatRow
            {
                RoomId = roomId,
                SeatIndex = (int)rd.GetInt64(rd.GetOrdinal("SeatIndex")),
                DeviceCode = rd.GetString(rd.GetOrdinal("DeviceCode")),
                DisplayName = rd.GetString(rd.GetOrdinal("DisplayName")),
                Online = rd.GetInt64(rd.GetOrdinal("Online")) != 0,
                RoleName = rd.GetString(rd.GetOrdinal("RoleName")),
                LastSeenAt = DateTime.TryParse(rd.GetString(rd.GetOrdinal("LastSeenAt")), out var ls)
                    ? ls : DateTime.UtcNow
            });
        return list;
    }

    public void UpsertSeat(SeatRow s)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"INSERT INTO Seats(RoomId,SeatIndex,DeviceCode,DisplayName,Online,RoleName,LastSeenAt)
            VALUES($r,$i,$d,$n,$o,$rn,$ls)
            ON CONFLICT(RoomId,SeatIndex) DO UPDATE SET
                DeviceCode=$d, DisplayName=$n, Online=$o, RoleName=$rn, LastSeenAt=$ls";
        cmd.Parameters.AddWithValue("$r", s.RoomId);
        cmd.Parameters.AddWithValue("$i", s.SeatIndex);
        cmd.Parameters.AddWithValue("$d", s.DeviceCode);
        cmd.Parameters.AddWithValue("$n", s.DisplayName);
        cmd.Parameters.AddWithValue("$o", s.Online ? 1 : 0);
        cmd.Parameters.AddWithValue("$rn", s.RoleName);
        cmd.Parameters.AddWithValue("$ls", (s.LastSeenAt == default ? DateTime.UtcNow : s.LastSeenAt).ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>更新某设备在房间里的心跳时间。</summary>
    public void TouchSeat(string roomId, string deviceCode)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"UPDATE Seats SET LastSeenAt=$t
                            WHERE RoomId=$r AND DeviceCode=$d AND Online=1";
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$r", roomId);
        cmd.Parameters.AddWithValue("$d", deviceCode);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 把心跳超时的座位标记为离线。
    /// ★ 用于"掉线自动处理"：客户端崩溃/断网后，座位不会永远占着。
    /// </summary>
    public int ExpireStaleSeats(int timeoutSeconds)
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-timeoutSeconds);
        int n = 0;

        using var c = Open();
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT RoomId, SeatIndex, LastSeenAt FROM Seats WHERE Online=1";
            var expiring = new List<(string room, int idx)>();

            using (var rd = q.ExecuteReader())
            {
                while (rd.Read())
                {
                    if (!DateTime.TryParse(rd.GetString(2), out var ls)) continue;
                    if (ls < cutoff)
                        expiring.Add((rd.GetString(0), (int)rd.GetInt64(1)));
                }
            }

            foreach (var (room, idx) in expiring)
            {
                using var u = c.CreateCommand();
                u.CommandText = @"UPDATE Seats SET DeviceCode='', DisplayName='', Online=0,
                                    RoleName='' WHERE RoomId=$r AND SeatIndex=$i";
                u.Parameters.AddWithValue("$r", room);
                u.Parameters.AddWithValue("$i", idx);
                u.ExecuteNonQuery();
                n++;
            }
        }

        return n;
    }

    public void ClearSeats(string roomId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"UPDATE Seats SET DeviceCode='', DisplayName='', Online=0, RoleName=''
                            WHERE RoomId=$r";
        cmd.Parameters.AddWithValue("$r", roomId);
        cmd.ExecuteNonQuery();
    }

    // ══════════ 发车动作 ══════════

    public DepartRow GetDepart(string deviceCode)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM Departs WHERE DeviceCode=$d";
        cmd.Parameters.AddWithValue("$d", deviceCode);
        using var rd = cmd.ExecuteReader();
        if (!rd.Read()) return new DepartRow { DeviceCode = deviceCode };

        return new DepartRow
        {
            DeviceCode = deviceCode,
            Mode = rd.GetString(rd.GetOrdinal("Mode")),
            ActionId = rd.GetString(rd.GetOrdinal("ActionId")),
            DragonName = rd.GetString(rd.GetOrdinal("DragonName")),
            Groups = rd.GetString(rd.GetOrdinal("Groups")),
            Summary = rd.GetString(rd.GetOrdinal("Summary"))
        };
    }

    public void SetDepart(DepartRow d)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"INSERT INTO Departs(DeviceCode,Mode,ActionId,DragonName,Groups,Summary)
            VALUES($d,$m,$a,$g,$gs,$s)
            ON CONFLICT(DeviceCode) DO UPDATE SET
                Mode=$m, ActionId=$a, DragonName=$g, Groups=$gs, Summary=$s";
        cmd.Parameters.AddWithValue("$d", d.DeviceCode);
        cmd.Parameters.AddWithValue("$m", d.Mode);
        cmd.Parameters.AddWithValue("$a", d.ActionId);
        cmd.Parameters.AddWithValue("$g", d.DragonName);
        cmd.Parameters.AddWithValue("$gs", d.Groups);
        cmd.Parameters.AddWithValue("$s", d.Summary);
        cmd.ExecuteNonQuery();
    }
}

// ══════════ 数据行 ══════════

public enum RoomKind { PrivateFour = 0, PrivatePair = 1 }

public sealed class RoomRow
{
    public string Id { get; set; } = "";
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public RoomKind Kind { get; set; }
    public string OwnerDevice { get; set; } = "";
    public string PairId { get; set; } = "";
    public string PairRole { get; set; } = "";
    public bool Departed { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActiveAt { get; set; } = DateTime.UtcNow;
}

public sealed class SeatRow
{
    public string RoomId { get; set; } = "";
    public int SeatIndex { get; set; }
    public string DeviceCode { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Online { get; set; }
    public string RoleName { get; set; } = "";
    /// <summary>最后心跳时间（UTC）。超过阈值视为掉线。</summary>
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}

public sealed class DepartRow
{
    public string DeviceCode { get; set; } = "";
    public string Mode { get; set; } = "";
    public string ActionId { get; set; } = "";
    public string DragonName { get; set; } = "";
    public string Groups { get; set; } = "";
    public string Summary { get; set; } = "";
}
