using System.Text.Json.Serialization;

namespace SoulLink.Server;

// ══════════ API 数据契约 ══════════
// 客户端与服务器之间传的 JSON 结构。
// 所有请求都带 DeviceCode（设备码）作为身份。

/// <summary>通用的设备码请求头。</summary>
public static class Hdr
{
    public const string Device = "X-Device-Code";
}

// ── 房间 ──

public sealed class RoomDto
{
    public string Id { get; set; } = "";
    /// <summary>房间码。只有创建者能看到（其他人的请求里为空）。</summary>
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public int Kind { get; set; }
    public string OwnerDevice { get; set; } = "";
    public string PairId { get; set; } = "";
    public string PairRole { get; set; } = "";
    public bool Departed { get; set; }
    public DateTime LastActiveAt { get; set; }
    public List<SeatDto> Seats { get; set; } = new();
}

public sealed class SeatDto
{
    public int Index { get; set; }
    public string DeviceCode { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Online { get; set; }
    public string RoleName { get; set; } = "";
}

// ── 请求体 ──

public sealed class CreateRoomRequest
{
    public string Title { get; set; } = "";
    public string Code { get; set; } = "";
    public int Kind { get; set; }
}

public sealed class CreatePairRequest
{
    public string Title { get; set; } = "";
    public string BigCode { get; set; } = "";
    public string SmallCode { get; set; } = "";
}

public sealed class JoinRequest
{
    public string Code { get; set; } = "";
    /// <summary>显示名（例如账号名 A1 或备注名）</summary>
    public string DisplayName { get; set; } = "";
    /// <summary>小号房需要填角色名</summary>
    public string RoleName { get; set; } = "";
}

public sealed class DepartRequest
{
    public string RoomId { get; set; } = "";
}

public sealed class ChangeCodeRequest
{
    public string RoomId { get; set; } = "";
    public string NewCode { get; set; } = "";
}

public sealed class DepartConfigDto
{
    public string Mode { get; set; } = "";
    public string ActionId { get; set; } = "";
    public string DragonName { get; set; } = "";
    public string Groups { get; set; } = "";
    public string Summary { get; set; } = "";
}

// ── 响应 ──

public sealed class ApiResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";

    /// <summary>发车时返回：房间内所有成员的设备码 + 发车动作</summary>
    public List<DepartTarget> Targets { get; set; } = new();

    public static ApiResult Good(string msg = "") => new() { Ok = true, Message = msg };
    public static ApiResult Bad(string msg) => new() { Ok = false, Message = msg };
}

public sealed class DepartTarget
{
    public string DeviceCode { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DepartConfigDto Config { get; set; } = new();
}
