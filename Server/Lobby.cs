namespace SoulLink.Server;

/// <summary>
/// 大厅业务逻辑。
/// 与客户端 LobbyService 的规则完全一致，只是把"本机字典"换成了 SQLite。
/// </summary>
public sealed class Lobby
{
    private readonly Db _db;

    public Lobby(Db db) => _db = db;

    // ══════════ 查询 ══════════

    /// <summary>
    /// 列出所有房间。
    /// ★ 所有房间对所有人可见；房间码只对创建者可见。
    /// </summary>
    public List<RoomDto> ListRooms(string myDevice)
    {
        var result = new List<RoomDto>();

        foreach (var r in _db.ListRooms())
        {
            var dto = ToDto(r);
            dto.Seats = _db.ListSeats(r.Id)
                .Select(s => new SeatDto
                {
                    Index = s.SeatIndex,
                    DeviceCode = s.DeviceCode,
                    DisplayName = s.DisplayName,
                    Online = s.Online,
                    RoleName = s.RoleName
                }).ToList();

            // 房间码只给创建者
            if (!string.Equals(r.OwnerDevice, myDevice, StringComparison.OrdinalIgnoreCase))
                dto.Code = "";

            result.Add(dto);
        }
        return result;
    }

    public RoomDto? GetRoom(string roomId, string myDevice)
    {
        var r = _db.FindRoomById(roomId);
        if (r == null) return null;

        var dto = ToDto(r);
        dto.Seats = _db.ListSeats(r.Id)
            .Select(s => new SeatDto
            {
                Index = s.SeatIndex,
                DeviceCode = s.DeviceCode,
                DisplayName = s.DisplayName,
                Online = s.Online,
                RoleName = s.RoleName
            }).ToList();

        if (!string.Equals(r.OwnerDevice, myDevice, StringComparison.OrdinalIgnoreCase))
            dto.Code = "";

        return dto;
    }

    private static RoomDto ToDto(RoomRow r) => new()
    {
        Id = r.Id,
        Code = r.Code,
        Title = r.Title,
        Kind = (int)r.Kind,
        OwnerDevice = r.OwnerDevice,
        PairId = r.PairId,
        PairRole = r.PairRole,
        Departed = r.Departed,
        LastActiveAt = r.LastActiveAt
    };

    // ══════════ 建房 ══════════

    /// <summary>建私人·四人房（每设备最多一个）。</summary>
    public (bool ok, string msg, RoomDto? room) CreateFour(
        string title, string code, string device)
    {
        if (string.IsNullOrWhiteSpace(title)) return (false, "房间名称不能为空", null);
        if (string.IsNullOrWhiteSpace(code)) return (false, "房间码不能为空", null);

        var mine = _db.ListRooms().FirstOrDefault(r =>
            r.OwnerDevice == device && r.Kind == RoomKind.PrivateFour && r.PairId == "");
        if (mine != null)
            return (false, "你已经有一个私人·四人房了：" + mine.Title, null);

        if (_db.FindRoomByCode(code) != null)
            return (false, "房间码「" + code + "」已被占用", null);

        var row = new RoomRow
        {
            Id = Guid.NewGuid().ToString("N"),
            Code = code.Trim(),
            Title = title.Trim(),
            Kind = RoomKind.PrivateFour,
            OwnerDevice = device
        };
        _db.InsertRoom(row);

        // ★ 建房只创建空座位，房主**不自动上线**。
        //   用户要在房间里点「加入房间」才算上线（否则会显示"1 人在线"但没人操作过）。
        for (int i = 0; i < 4; i++)
            _db.UpsertSeat(new SeatRow { RoomId = row.Id, SeatIndex = i });

        return (true, "房间已创建", ToDto(row));
    }

    /// <summary>建私人·大小号房：成对创建，每设备最多一对。</summary>
    public (bool ok, string msg, RoomDto? big, RoomDto? small) CreatePair(
        string title, string bigCode, string smallCode, string device)
    {
        if (string.IsNullOrWhiteSpace(title)) return (false, "房间名称不能为空", null, null);
        if (string.IsNullOrWhiteSpace(bigCode) || string.IsNullOrWhiteSpace(smallCode))
            return (false, "大号房和小号房的房间码都要填", null, null);
        if (string.Equals(bigCode.Trim(), smallCode.Trim(), StringComparison.OrdinalIgnoreCase))
            return (false, "大号房和小号房不能用同一个房间码", null, null);

        var mine = _db.ListRooms().FirstOrDefault(r =>
            r.OwnerDevice == device && r.Kind == RoomKind.PrivatePair);
        if (mine != null)
            return (false, "你已经有一对私人·大小号房了：" + mine.Title, null, null);

        foreach (var c in new[] { bigCode.Trim(), smallCode.Trim() })
            if (_db.FindRoomByCode(c) != null)
                return (false, "房间码「" + c + "」已被占用", null, null);

        string pairId = Guid.NewGuid().ToString("N");

        var big = new RoomRow
        {
            Id = Guid.NewGuid().ToString("N"),
            Code = bigCode.Trim(),
            Title = title.Trim() + "（大号）",
            Kind = RoomKind.PrivatePair,
            OwnerDevice = device,
            PairId = pairId,
            PairRole = "big"
        };
        var small = new RoomRow
        {
            Id = Guid.NewGuid().ToString("N"),
            Code = smallCode.Trim(),
            Title = title.Trim() + "（小号）",
            Kind = RoomKind.PrivatePair,
            OwnerDevice = device,
            PairId = pairId,
            PairRole = "small"
        };

        _db.InsertRoom(big);
        _db.InsertRoom(small);

        // 同样：建房不自动上线
        for (int i = 0; i < 2; i++) _db.UpsertSeat(new SeatRow { RoomId = big.Id, SeatIndex = i });
        for (int i = 0; i < 2; i++) _db.UpsertSeat(new SeatRow { RoomId = small.Id, SeatIndex = i });

        return (true, "成对房间已创建", ToDto(big), ToDto(small));
    }

    // ══════════ 上线 / 下线 ══════════

    public (bool ok, string msg, RoomDto? room) Join(
        string code, string device, string displayName, string roleName)
    {
        var room = _db.FindRoomByCode(code);
        if (room == null) return (false, "房间码无效：" + code, null);

        // 大小号房：成员不能同时在别的房间（两两绑定）
        if (room.Kind == RoomKind.PrivatePair && room.PairId.Length > 0)
        {
            foreach (var other in _db.ListRooms())
            {
                if (other.PairId == room.PairId) continue;
                bool used = _db.ListSeats(other.Id)
                    .Any(s => s.Online && s.DeviceCode == device);
                if (used)
                    return (false, "你已经在该房间外的地方上线了，大小号房不能跨房间。", null);
            }
        }

        var seats = _db.ListSeats(room.Id);

        // 已在房间
        var mine = seats.FirstOrDefault(s => s.DeviceCode == device && s.Online);
        if (mine != null)
        {
            mine.DisplayName = displayName.Length > 0 ? displayName : device;
            if (roleName.Length > 0) mine.RoleName = roleName;
            _db.UpsertSeat(mine);
            TouchRoom(room.Id);
            return (true, "已在该房间中", GetRoom(room.Id, device));
        }

        // 找空位；没有就加
        int idx = -1;
        foreach (var s in seats)
            if (!s.Online && s.DeviceCode == "") { idx = s.SeatIndex; break; }

        if (idx < 0) idx = seats.Count == 0 ? 0 : seats.Max(s => s.SeatIndex) + 1;

        _db.UpsertSeat(new SeatRow
        {
            RoomId = room.Id, SeatIndex = idx, DeviceCode = device,
            DisplayName = displayName.Length > 0 ? displayName : device,
            Online = true, RoleName = roleName
        });

        TouchRoom(room.Id);
        return (true, "已上线到「" + room.Title + "」", GetRoom(room.Id, device));
    }

    public bool Leave(string roomId, string device)
    {
        var seats = _db.ListSeats(roomId);
        var mine = seats.FirstOrDefault(s => s.DeviceCode == device);
        if (mine == null) return false;

        mine.DeviceCode = "";
        mine.DisplayName = "";
        mine.RoleName = "";
        mine.Online = false;
        _db.UpsertSeat(mine);

        TouchRoom(roomId);
        return true;
    }

    private void TouchRoom(string roomId)
    {
        var r = _db.FindRoomById(roomId);
        if (r == null) return;
        r.LastActiveAt = DateTime.UtcNow;
        _db.UpdateRoom(r);
    }

    // ══════════ 发车 ══════════

    /// <summary>
    /// 发车。返回所有成员（含"配对房"的成员）各自的发车动作。
    /// 客户端收到后按 DeviceCode 匹配自己，执行自己的那份。
    /// </summary>
    public (bool ok, string msg, List<DepartTarget> targets) Depart(string roomId)
    {
        var room = _db.FindRoomById(roomId);
        if (room == null) return (false, "房间不存在", new List<DepartTarget>());

        var rooms = new List<RoomRow> { room };

        if (room.Kind == RoomKind.PrivatePair && room.PairId.Length > 0)
        {
            var partner = _db.ListRooms().FirstOrDefault(r =>
                r.PairId == room.PairId && r.Id != room.Id);
            if (partner == null) return (false, "找不到配对房间", new List<DepartTarget>());

            int pOnline = _db.ListSeats(partner.Id).Count(s => s.Online);
            int mOnline = _db.ListSeats(room.Id).Count(s => s.Online);

            if (mOnline < 2) return (false, "本房人数不够（需要 2 人，当前 " + mOnline + "）", new List<DepartTarget>());
            if (pOnline < 2) return (false, "配对房间人数不够（需要 2 人，当前 " + pOnline + "）", new List<DepartTarget>());

            rooms.Add(partner);
        }
        else
        {
            int online = _db.ListSeats(room.Id).Count(s => s.Online);
            if (online < 4) return (false, "人数不够（需要 4 人，当前 " + online + "）", new List<DepartTarget>());
        }

        // 收集所有成员 + 各自的发车动作
        var targets = new List<DepartTarget>();
        foreach (var rr in rooms)
        {
            foreach (var s in _db.ListSeats(rr.Id))
            {
                if (!s.Online || s.DeviceCode.Length == 0) continue;
                if (targets.Any(t => t.DeviceCode == s.DeviceCode)) continue;

                var d = _db.GetDepart(s.DeviceCode);
                targets.Add(new DepartTarget
                {
                    DeviceCode = s.DeviceCode,
                    DisplayName = s.DisplayName,
                    Config = new DepartConfigDto
                    {
                        Mode = d.Mode,
                        ActionId = d.ActionId,
                        DragonName = d.DragonName,
                        Groups = d.Groups,
                        Summary = d.Summary
                    }
                });
            }
        }

        // 标记发车
        foreach (var rr in rooms)
        {
            rr.Departed = true;
            rr.LastActiveAt = DateTime.UtcNow;
            _db.UpdateRoom(rr);
        }

        return (true, "已发车", targets);
    }

    /// <summary>发车后清理：全员下线（大小号房成对清）。</summary>
    public void DepartAndClear(string roomId)
    {
        var room = _db.FindRoomById(roomId);
        if (room == null) return;

        _db.ClearSeats(room.Id);
        room.Departed = false;
        room.LastActiveAt = DateTime.UtcNow;
        _db.UpdateRoom(room);

        if (room.Kind == RoomKind.PrivatePair && room.PairId.Length > 0)
        {
            var partner = _db.ListRooms().FirstOrDefault(r =>
                r.PairId == room.PairId && r.Id != room.Id);
            if (partner != null)
            {
                _db.ClearSeats(partner.Id);
                partner.Departed = false;
                partner.LastActiveAt = DateTime.UtcNow;
                _db.UpdateRoom(partner);
            }
        }
    }

    // ══════════ 改码 / 关闭 ══════════

    public (bool ok, string msg) ChangeCode(string roomId, string newCode, string device)
    {
        var r = _db.FindRoomById(roomId);
        if (r == null) return (false, "房间不存在");
        if (r.OwnerDevice != device) return (false, "只有房主能改房间码");

        newCode = newCode.Trim();
        if (newCode.Length == 0) return (false, "房间码不能为空");

        var exist = _db.FindRoomByCode(newCode);
        if (exist != null && exist.Id != roomId)
            return (false, "房间码「" + newCode + "」已被占用");

        r.Code = newCode;
        r.LastActiveAt = DateTime.UtcNow;
        _db.UpdateRoom(r);
        return (true, "房间码已改为 " + newCode);
    }

    public (bool ok, string msg) Delete(string roomId, string device)
    {
        var r = _db.FindRoomById(roomId);
        if (r == null) return (false, "房间不存在");
        if (r.OwnerDevice != device) return (false, "只有房主能关闭");

        if (r.Kind == RoomKind.PrivatePair && r.PairId.Length > 0)
        {
            foreach (var other in _db.ListRooms().Where(x => x.PairId == r.PairId).ToList())
                _db.DeleteRoom(other.Id);
        }
        else
        {
            _db.DeleteRoom(roomId);
        }
        return (true, "已关闭");
    }

    // ══════════ 心跳 / 掉线处理 ══════════

    /// <summary>心跳超时阈值（秒）。超过这个时间没心跳视为掉线。</summary>
    public const int HeartbeatTimeoutSeconds = 60;

    /// <summary>
    /// 客户端心跳：更新本机所有座位的时间戳。
    /// 返回仍需清理的过期座位数（供监控）。
    /// </summary>
    public void Heartbeat(string device)
    {
        foreach (var r in _db.ListRooms())
        {
            var seats = _db.ListSeats(r.Id);
            if (seats.Any(s => s.Online &&
                    string.Equals(s.DeviceCode, device, StringComparison.OrdinalIgnoreCase)))
            {
                _db.TouchSeat(r.Id, device);
            }
        }
    }

    /// <summary>把心跳超时的座位标记为离线。</summary>
    public int ExpireStale() => _db.ExpireStaleSeats(HeartbeatTimeoutSeconds);

    // ══════════ 发车动作配置 ══════════

    public DepartConfigDto GetDepartConfig(string device)
    {
        var d = _db.GetDepart(device);
        return new DepartConfigDto
        {
            Mode = d.Mode, ActionId = d.ActionId,
            DragonName = d.DragonName, Groups = d.Groups, Summary = d.Summary
        };
    }

    public void SetDepartConfig(string device, DepartConfigDto dto)
    {
        _db.SetDepart(new DepartRow
        {
            DeviceCode = device,
            Mode = dto.Mode ?? "",
            ActionId = dto.ActionId ?? "",
            DragonName = dto.DragonName ?? "",
            Groups = dto.Groups ?? "",
            Summary = dto.Summary ?? ""
        });
    }
}
