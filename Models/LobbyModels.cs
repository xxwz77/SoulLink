using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SoulLink.Models
{
    /// <summary>账号等级。</summary>
    public enum UserRole
    {
        /// <summary>普通用户：只能管理自己的房间和自己的小号</summary>
        Member = 0,
        /// <summary>管理员：能看到所有房间、能改任何人的角色</summary>
        Admin = 1
    }

    /// <summary>
    /// 房间类型。
    /// ★ 只保留两种私人房间（用户要求：除了私人房间之外全部移除）。
    /// </summary>
    public enum RoomKind
    {
        /// <summary>私人·四人：一个房间，满 4 人发车</summary>
        PrivateFour = 0,
        /// <summary>私人·大小号：一对房间（大号房 + 小号房），各满 2 人发车</summary>
        PrivatePair = 1
    }

    /// <summary>玩家在房间里的状态。</summary>
    public enum SeatState
    {
        Empty = 0,
        /// <summary>已占位但没上线</summary>
        Reserved = 1,
        /// <summary>已上线（游戏内已登录）</summary>
        Online = 2,
        /// <summary>小号池补位，已上线</summary>
        PoolOnline = 3
    }

    /// <summary>大厅里的一个账号（客户端表示）。</summary>
    public class LobbyAccount : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string UserName { get; set; } = "";
        public string NickName { get; set; } = "";
        public UserRole Role { get; set; } = UserRole.Member;
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>是否已登录游戏（本机在线状态）。</summary>
        private bool _online;
        public bool Online
        {
            get => _online;
            set { if (_online != value) { _online = value; OnChanged(); } }
        }

        // ── 小号池 ──
        /// <summary>该账号下的小号（角色），可参与双人房补位。</summary>
        public ObservableCollection<PoolCharacter> Pool { get; set; } = new();

        public bool IsAdmin => Role == UserRole.Admin;
        public string DisplayName => string.IsNullOrWhiteSpace(NickName) ? UserName : NickName;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>小号池里的一个角色。</summary>
    public class PoolCharacter : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        /// <summary>所属账号 Id。</summary>
        public string OwnerId { get; set; } = "";
        /// <summary>角色名（游戏内昵称）。</summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// 是否已上线（该小号已在游戏里登录）。
        /// ★ 只有上线的角色才能被双人房补位。
        /// </summary>
        private bool _online;
        public bool Online
        {
            get => _online;
            set { if (_online != value) { _online = value; OnChanged(); OnChanged(nameof(StateText)); } }
        }

        /// <summary>被哪个房间占用（空 = 空闲）。</summary>
        private string _usedByRoom = "";
        public string UsedByRoom
        {
            get => _usedByRoom;
            set { if (_usedByRoom != value) { _usedByRoom = value; OnChanged(); OnChanged(nameof(StateText)); } }
        }

        public string StateText =>
            UsedByRoom.Length > 0 ? "使用中" : (Online ? "已上线" : "未上线");

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>房间里的一个座位。</summary>
    public class RoomSeat : INotifyPropertyChanged
    {
        public int Index { get; set; }
        public string UserId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public SeatState State { get; set; }
        /// <summary>若是小号补位，记录小号名。</summary>
        public string PoolName { get; set; } = "";

        public string StateText => State switch
        {
            SeatState.Empty => "空位",
            SeatState.Reserved => "已占位",
            SeatState.Online => "已上线",
            SeatState.PoolOnline => "小号补位",
            _ => "空位"
        };

        public bool IsFilled => State != SeatState.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>一个房间。</summary>
    public class LobbyRoom : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public RoomKind Kind { get; set; }

        /// <summary>房主账号 Id。</summary>
        public string OwnerId { get; set; } = "";
        public string OwnerName { get; set; } = "";

        /// <summary>房间名（自带房间可自定义）。</summary>
        public string Title { get; set; } = "";

        /// <summary>
        /// 房间码：6 位数字，别人凭码加入。
        /// ★ 临时房间才有；官方房间为固定展示。
        /// </summary>
        public string Code { get; set; } = "";

        /// <summary>是否临时房间（用户自建）。</summary>
        public bool IsTemporary { get; set; } = true;

        /// <summary>
        /// 是否三个固定房间之一（四人 / 双人 / 小号池）。
        /// ★ 固定房间永远存在，不能关闭，房间码也不可变。
        /// </summary>
        public bool IsFixed { get; set; }

        /// <summary>私人大小号模式：同一对房间共享的 Id（空 = 非配对）。</summary>
        public string PairId { get; set; } = "";
        /// <summary>配对角色："big" / "small" / 空。</summary>
        public string PairRole { get; set; } = "";

        public List<RoomSeat> Seats { get; set; } = new();

        /// <summary>
        /// 发车后要把成员下线。
        /// ★ 用户要求：发车后要及时下线。
        /// </summary>
        public bool ClearAfterDepart { get; set; } = true;

        /// <summary>是否已发车。</summary>
        private bool _departed;
        public bool Departed
        {
            get => _departed;
            set { if (_departed != value) { _departed = value; OnChanged(); OnChanged(nameof(StatusText)); } }
        }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // ── 派生显示 ──
        /// <summary>
        /// 座位容量。
        /// ★ 用户要求：房间不设人数上限（人多时按需扩展）。
        ///   发车判定用"需求人数"，而不是容量。
        /// </summary>
        public int Capacity => Math.Max(4, Seats.Count);

        /// <summary>发车需要的人数（私人四人 4，大小号每边 2）。</summary>
        public int RequiredCount => Kind switch
        {
            RoomKind.PrivatePair => 2,
            _ => 4
        };
        public int FilledCount
        {
            get
            {
                int n = 0;
                foreach (var s in Seats) if (s.IsFilled) n++;
                return n;
            }
        }
        public int OnlineCount
        {
            get
            {
                int n = 0;
                foreach (var s in Seats)
                    if (s.State == SeatState.Online || s.State == SeatState.PoolOnline) n++;
                return n;
            }
        }

        /// <summary>
        /// 是否可以发车。
        ///   · 私人·四人：4 人上线
        ///   · 私人·大小号：本房 2 人（配对房由服务端校验）
        /// </summary>
        public bool CanDepart
        {
            get
            {
                if (Departed) return false;
                if (Kind == RoomKind.PrivatePair) return BigCount >= 2;
                return OnlineCount >= 4;
            }
        }

        /// <summary>大号（真人）数量。</summary>
        public int BigCount
        {
            get
            {
                int n = 0;
                foreach (var s in Seats)
                    if (s.State == SeatState.Online) n++;
                return n;
            }
        }

        /// <summary>小号补位数量。</summary>
        public int PoolCount
        {
            get
            {
                int n = 0;
                foreach (var s in Seats)
                    if (s.State == SeatState.PoolOnline) n++;
                return n;
            }
        }

        public string StatusText
        {
            get
            {
                if (Departed) return "已发车";
                if (CanDepart) return "可发车";
                return OnlineCount + "/" + Capacity;
            }
        }

        public string KindText => Kind switch
        {
            RoomKind.PrivatePair => PairRole == "small" ? "私人·小号房" : "私人·大号房",
            _ => "私人·四人"
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        /// <summary>对外通知某个属性变了（界面用）。</summary>
        public void OnChangedPublic(string name) => OnChanged(name);

        /// <summary>重新计算并通知所有派生属性。</summary>
        public void Refresh()
        {
            OnChanged(nameof(FilledCount));
            OnChanged(nameof(OnlineCount));
            OnChanged(nameof(StatusText));
            OnChanged(nameof(CanDepart));
        }
    }
}
