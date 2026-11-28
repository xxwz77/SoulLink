using System;
using SoulLink.Controls;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SoulLink.Models;
using SoulLink.Services;

namespace SoulLink.Views
{
    /// <summary>
    /// 联机大厅。
    ///
    /// ★ 只有私人房间（无固定房间、无公共池）：
    ///   · 私人房   —— 一个房间，满 2-4 人发车（建房时可选）
    ///   · 私人·大小号 —— 一对房间（大号房 + 小号房），各自满 2-4 人发车
    ///
    /// 房间码 = 通行证，不对外展示（只有创建者能看到）。
    /// 身份 = Windows 账号名，不需要登录。
    /// </summary>
    public partial class LobbyPage : UserControl
    {
        private readonly LobbyAccount _me;
        private readonly List<LobbyRoom> _rooms = new();
        private LobbyRoom? _selected;
        private IDisposable? _autoSub;
        private readonly object _pageKey = new();
        private bool _isPageActive;

        public LobbyPage()
        {
            InitializeComponent();

            // 没有登录、没有管理员：身份就是当前 Windows 账号名
            string user = Environment.UserName;
            _me = new LobbyAccount
            {
                UserName = user,
                NickName = user,
                Role = UserRole.Member
            };

            // 服务端地址从配置读（空 = 未配置，不发任何请求）
            LobbyApi.LoadFromConfig();
            UpdateServerText();

            // 订阅连接状态（指示灯）
            LobbyConnection.Instance.StateChanged += OnConnStateChanged;
            LobbyConnection.Instance.Start();
            UpdateConnIndicator(LobbyConnection.Instance.State, "");

            Unloaded += (_, __) =>
            {
                LobbyConnection.Instance.StateChanged -= OnConnStateChanged;
                StopPolling();
                LobbyConnection.Instance.Release(_pageKey);
                LobbyConnection.Instance.SetInRoom(false);
            };

            if (_rooms.Count == 0)
                DetailPanel.Children.Add(new TextBlock
                {
                    Text = "还没有房间。点右上角「新建房间」创建你自己的。",
                    FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
                    Foreground = UiBrush.Get("TextMuted")
                });
        }

        /// <summary>
        /// 切到本页：声明「正在看联机页」活动 + 启动 5 秒房间列表轮询。
        /// ★ 只有本页可见时才轮询，离开即停（按需访问）。
        /// </summary>
        public void OnPageActivated()
        {
            _isPageActive = true;
            LobbyConnection.Instance.Acquire(_pageKey);
            StartPolling();
            _ = ReloadAsync();
        }

        /// <summary>
        /// 切走本页：释放「正在看联机页」活动 + 停止轮询。
        /// ★ 若本机仍在某个房间里，LobbyConnection.SetInRoom 会单独保持心跳。
        /// </summary>
        public void OnPageDeactivated()
        {
            _isPageActive = false;
            StopPolling();
            LobbyConnection.Instance.Release(_pageKey);
        }

        /// <summary>启动房间列表轮询（每 5 秒，走 TickHub）。</summary>
        private void StartPolling()
        {
            if (_autoSub != null) return;
            _autoSub = Services.TickHub.Register("LobbyPage", 5, async () =>
            {
                try
                {
                    if (!_isPageActive) return;
                    await ReloadAsync().ConfigureAwait(true);
                }
                catch { }
            });
        }

        private void StopPolling()
        {
            _autoSub?.Dispose();
            _autoSub = null;
        }

        /// <summary>
        /// 依据最新房间列表，告知连接层本机是否在房间里（在房间则常驻心跳）。
        /// 房间列表拉完后调用。
        /// </summary>
        private void UpdateInRoomStatus()
        {
            string myDevice = DeviceIdentity.Code;
            bool inRoom = _rooms.Any(r => r.Seats.Any(s =>
                string.Equals(s.UserId, myDevice, StringComparison.OrdinalIgnoreCase)
                && (s.State == SeatState.Online || s.State == SeatState.PoolOnline)));
            LobbyConnection.Instance.SetInRoom(inRoom);
        }

        private static bool IsLocalAdmin(string user)
        {
            try
            {
                var admin = Environment.GetEnvironmentVariable("SOULLINK_LOBBY_ADMIN");
                if (!string.IsNullOrWhiteSpace(admin))
                    return admin.Split(',').Any(x =>
                        string.Equals(x.Trim(), user, StringComparison.OrdinalIgnoreCase));
                return string.Equals(user, "lummy", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>外部切入本页。</summary>
        public void Refresh()
        {
            _ = ReloadAsync();
            if (_selected != null) RenderDetail(_selected);
        }        // ══════════ 数据同步 ══════════

        /// <summary>
        /// 从服务器拉取房间列表。
        /// ★ 改成调用 HTTP API，不再读本地 JSON。
        /// </summary>
        private async Task LoadRoomsAsync()
        {
            try
            {
                var (ok, msg, list) = await LobbyApi.ListRoomsAsync().ConfigureAwait(true);

                if (!ok)
                {
                    // ★ 未配置服务器地址：不显示"连不上"报错，静默提示去设置地址。
                    if (!LobbyApi.HasServer)
                    {
                        SubText.Text = "";
                        _rooms.Clear();
                        RoomList.Children.Clear();
                        RoomList.Children.Add(new Border
                        {
                            Padding = new Thickness(0, 40, 0, 0),
                            Child = new TextBlock
                            {
                                Text = "尚未配置服务器。\n\n点顶部「服务器」填写新的服务器地址后即可使用联机。",
                                FontSize = 12,
                                Foreground = UiBrush.Get("TextMuted"),
                                HorizontalAlignment = HorizontalAlignment.Center,
                                TextAlignment = TextAlignment.Center,
                                TextWrapping = TextWrapping.Wrap,
                                LineHeight = 22
                            }
                        });
                        LobbyConnection.Instance.SetInRoom(false);
                        return;
                    }

                    SubText.Text = "连不上服务器：" + msg;
                    _rooms.Clear();
                    RoomList.Children.Clear();
                    RoomList.Children.Add(new Border
                    {
                        Padding = new Thickness(0, 40, 0, 0),
                        Child = new TextBlock
                        {
                            // ★ 不打印实际地址：可能是内置默认值，不该让用户看到。
                            //   用户自己填过地址时，那个值在「服务器」对话框里能看到。
                            Text = "连不上服务器。\n\n" + msg
                                 + "\n\n点顶部「服务器」可以修改地址。",
                            FontSize = 12,
                            Foreground = UiBrush.Get("TextMuted"),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            TextAlignment = TextAlignment.Center,
                            TextWrapping = TextWrapping.Wrap,
                            LineHeight = 22
                        }
                    });
                    LobbyConnection.Instance.SetInRoom(false);
                    return;
                }

                _rooms.Clear();
                string myDevice = DeviceIdentity.Code;

                foreach (var dto in list)
                {
                    var r = new LobbyRoom
                    {
                        Id = dto.Id,
                        Code = dto.Code,
                        Title = dto.Title,
                        Kind = dto.Kind == 1 ? RoomKind.PrivatePair : RoomKind.PrivateFour,
                        OwnerId = dto.OwnerDevice,
                        OwnerName = dto.OwnerDevice == myDevice ? "我" : ShortDevice(dto.OwnerDevice),
                        Departed = dto.Departed,
                        PairId = dto.PairId,
                        PairRole = dto.PairRole,
                        DepartSize = dto.DepartSize
                    };

                    foreach (var s in dto.Seats)
                    {
                        r.Seats.Add(new RoomSeat
                        {
                            Index = s.Index,
                            UserId = s.DeviceCode,
                            DisplayName = s.DisplayName,
                            State = s.Online ? SeatState.Online : SeatState.Empty,
                            PoolName = s.RoleName
                        });
                    }
                    _rooms.Add(r);
                }

                // ★ 本机是否在房间里 → 决定是否常驻心跳
                UpdateInRoomStatus();
            }
            catch (Exception ex)
            {
                SubText.Text = "加载失败：" + ex.Message;
            }
        }

        // ══════════ 连接状态 ══════════

        /// <summary>连接状态变化 → 更新指示灯。</summary>
        private bool _connTextClickBound;

        private void OnConnStateChanged(ConnState state, string error)
        {
            try
            {
                Dispatcher.InvokeAsync(() =>
                {
                    UpdateConnIndicator(state, error);

                    // 从断开恢复成已连接 → 自动刷新一次列表
                    if (state == ConnState.Connected)
                        _ = ReloadAsync();
                });
            }
            catch { }
        }

        private void UpdateConnIndicator(ConnState state, string error)
        {
            try
            {
                if (ConnDot == null) return;

                string resource = state switch
                {
                    ConnState.Connected => "MintBrush",   // 绿
                    ConnState.Connecting => "AmberBrush",  // 黄
                    _ => "RoseBrush"                       // 红
                };

                ConnDot.Fill = UiBrush.Get(resource);

                string text = state switch
                {
                    ConnState.Connected => "已连接",
                    ConnState.Connecting => "连接中…",
                    ConnState.Disconnected => "未连接",
                    _ => "未知"
                };

                if (state == ConnState.Disconnected && error.Length > 0)
                    text += "（" + error + "）";

                if (ConnText.Text != text) ConnText.Text = text;
                ConnText.Foreground = state == ConnState.Connected
                    ? UiBrush.Get("MintBrush")
                    : (state == ConnState.Connecting
                        ? UiBrush.Get("AmberBrush")
                        : UiBrush.Get("RoseBrush"));

                ConnDot.ToolTip = text;
            }
            catch { }
        }

        /// <summary>服务器地址完全不显示（只在设置里能改）。</summary>
        private void UpdateServerText()
        {
            try
            {
                if (ConnText != null) ConnText.ToolTip = "连接状态";
                if (ConnDot != null) ConnDot.ToolTip = "连接状态";
            }
            catch { }
        }

        /// <summary>修改服务器地址。</summary>
        private async void Server_Click(object sender, RoutedEventArgs e)
        {
            // ★ 不要用 LobbyApi.BaseUrl 预填：那会把生效地址暴露出来。
            //   只回填用户自己配置过的值（没配过就是空）。
            var dlg = new InputDialogWindow("服务器地址",
                "填服务端地址，例如 http://服务器地址:端口",
                LobbyApi.ConfiguredUrl);
            if (Window.GetWindow(this) is Window owner) dlg.Owner = owner;
            if (dlg.ShowDialog() != true) return;

            string url = (dlg.ResultText ?? "").Trim();
            if (url.Length == 0) return;
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                url = "http://" + url;

            LobbyApi.SaveToConfig(url);
            UpdateServerText();

            // 立即重新检测
            await LobbyConnection.Instance.CheckNowAsync().ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);
        }

        /// <summary>设备码太长，显示后 6 位。</summary>
        private static string ShortDevice(string code)
        {
            if (string.IsNullOrEmpty(code)) return "未知";
            return code.Length <= 8 ? code : ("…" + code.Substring(code.Length - 6));
        }

        // ══════════ 渲染 ══════════

        private async Task ReloadAsync()
        {
            await LoadRoomsAsync().ConfigureAwait(true);
            RoomList.Children.Clear();

            string tag = TabFour.IsChecked == true ? "four"
                       : TabTwo.IsChecked == true ? "two" : "all";

            var list = _rooms.Where(r =>
                tag == "all"
                || (tag == "four" && r.Kind == RoomKind.PrivateFour)
                || (tag == "two" && r.Kind == RoomKind.PrivatePair)).ToList();

            int four = _rooms.Count(r => r.Kind == RoomKind.PrivateFour);
            int pair = _rooms.Count(r => r.Kind == RoomKind.PrivatePair);
            int mine = _rooms.Count(r =>
                string.Equals(r.OwnerId, DeviceIdentity.Code, StringComparison.OrdinalIgnoreCase));

            SubText.Text = "共 " + _rooms.Count + " 个房间（私人 " + four
                         + " · 大小号 " + pair + "）· 我的 " + mine + " 个";

            if (list.Count == 0)
            {
                RoomList.Children.Add(new Border
                {
                    Padding = new Thickness(0, 40, 0, 0),
                    Child = new TextBlock
                    {
                        Text = "还没有房间。\n\n"
                             + "点右上角「新建房间」创建你自己的房间。\n"
                             + "或者点「加入房间」，输入别人给你的房间码。",
                        FontSize = 12,
                        Foreground = UiBrush.Get("TextMuted"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center,
                        LineHeight = 22
                    }
                });

                // 列表变空时，右侧详情也要跟着更新（房间可能被删了）
                RefreshDetailFromList();
                return;
            }

            // ★ 大小号房两两绑定：合并成一张卡片显示（用户要求视觉上包在一起）
            var rendered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var r in list)
            {
                if (rendered.Contains(r.Id)) continue;

                if (r.Kind == RoomKind.PrivatePair && r.PairId.Length > 0)
                {
                    var big = r.PairRole == "big" ? r
                            : list.FirstOrDefault(x => x.PairId == r.PairId && x.PairRole == "big");
                    var small = r.PairRole == "small" ? r
                            : list.FirstOrDefault(x => x.PairId == r.PairId && x.PairRole == "small");

                    if (big != null) rendered.Add(big.Id);
                    if (small != null) rendered.Add(small.Id);

                    RoomList.Children.Add(PairRoomCard(big, small));
                }
                else
                {
                    rendered.Add(r.Id);
                    RoomList.Children.Add(RoomCard(r));
                }
            }
        
            // 列表重建完，右侧详情同样基于新数据重渲染
            RefreshDetailFromList();
        }

        /// <summary>
        /// 大小号房合并卡片：一个卡片里上下展示"大号房 / 小号房"，
        /// 视觉上一个整体，逻辑上两两绑定。
        /// </summary>
        private Border PairRoomCard(LobbyRoom? big, LobbyRoom? small)
        {
            var any = big ?? small;
            if (any == null) return new Border();

            bool sel = _selected != null &&
                       (_selected.Id == big?.Id || _selected.Id == small?.Id);

            string accent = "MintBrush";

            var outer = new StackPanel();

            // 标题行
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock
            {
                Text = any.Title.Replace("（大号）", "").Replace("（小号）", ""),
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = UiBrush.Get("TextPrimary")
            });
            head.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(9, 0, 0, 0),
                Background = UiBrush.Get(accent, 0x28),
                Child = new TextBlock
                {
                    Text = "私人·大小号", FontSize = 9.5,
                    Foreground = UiBrush.Get(accent)
                }
            });
            outer.Children.Add(head);

            outer.Children.Add(new TextBlock
            {
                Text = "房主 " + any.OwnerName + "  ·  两间绑定，满员才发车",
                FontSize = 10.5,
                Foreground = UiBrush.Get("TextMuted"),
                Margin = new Thickness(0, 4, 0, 10)
            });

            // 两个子房间
            outer.Children.Add(PairSubRow(big, "大号房", "AmberBrush"));
            outer.Children.Add(PairSubRow(small, "小号房", "DesktopAccent"));

            var card = new Border
            {
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(12, 11, 14, 11),
                Margin = new Thickness(0, 0, 0, 8),
                BorderBrush = sel ? UiBrush.Get(accent, 0xAA) : UiBrush.Get("TextPrimary", 0x1E),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Child = outer
            };
            card.SetResourceReference(Border.BackgroundProperty, "PanelBackground");

            card.MouseLeftButtonUp += async (_, __) =>
            {
                // 点卡片默认选大号房
                var target = big ?? small;
                if (target != null) await SelectRoomAsync(target);
            };

            return card;
        }

        /// <summary>合并卡片里的一行（大号房 / 小号房）。</summary>
        /// <summary>
        /// 大小号卡片里的一个子房间（大号房 / 小号房）。
        /// ★ 做成独立可点的小卡片：点哪个就选中哪个，选中的高亮（用户建议）。
        /// </summary>
        private Border PairSubRow(LobbyRoom? room, string label, string accent)
        {
            bool selected = room != null && _selected != null && _selected.Id == room.Id;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 左侧色条
            grid.Children.Add(new Border
            {
                Width = 3, CornerRadius = new CornerRadius(2),
                Background = UiBrush.Get(accent),
                Margin = new Thickness(0, 1, 9, 1)
            });

            var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            mid.Children.Add(new TextBlock
            {
                Text = label, FontSize = 10.5,
                Foreground = UiBrush.Get(accent),
                FontWeight = FontWeights.SemiBold
            });

            if (room == null)
            {
                mid.Children.Add(new TextBlock
                {
                    Text = "（未创建）", FontSize = 10,
                    Foreground = UiBrush.Get("TextMuted"),
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }
            else
            {
                string codeText = room.Code.Length > 0 ? ("房间码 " + room.Code) : "🔒 需要房间码";
                mid.Children.Add(new TextBlock
                {
                    Text = codeText, FontSize = 10, Margin = new Thickness(0, 2, 0, 0),
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = room.Code.Length > 0
                        ? UiBrush.Get("MintBrush")
                        : UiBrush.Get("AmberBrush")
                });
            }
            Grid.SetColumn(mid, 1);
            grid.Children.Add(mid);

            if (room != null)
            {
                int online = room.Seats.Count(s => s.State == SeatState.Online);
                var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                right.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 5, 0),
                    Fill = online > 0
                        ? UiBrush.Get("MintBrush") : UiBrush.Get("TextSecondary")
                });
                right.Children.Add(new TextBlock
                {
                    Text = online + " 人", FontSize = 10.5,
                    Foreground = online > 0
                        ? UiBrush.Get("MintBrush")
                        : UiBrush.Get("TextMuted")
                });
                Grid.SetColumn(right, 2);
                grid.Children.Add(right);
            }

            // ★ 子卡片：可点、选中高亮
            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6),
                BorderBrush = selected ? UiBrush.Get(accent, 0xAA) : UiBrush.Get("TextPrimary", 0x18),
                BorderThickness = new Thickness(1),
                Cursor = room != null ? Cursors.Hand : Cursors.Arrow,
                Child = grid,
                Tag = room
            };
            card.SetResourceReference(Border.BackgroundProperty, "PanelBackground");

            if (room != null)
            {
                card.MouseLeftButtonUp += async (_, ev) =>
                {
                    ev.Handled = true;   // 不要让外层大卡片也响应
                    await SelectRoomAsync(room);
                };
            }

            return card;
        }

        private Border RoomCard(LobbyRoom r)
        {
            bool sel = _selected?.Id == r.Id;
            bool isPair = r.Kind == RoomKind.PrivatePair;
            string accent = isPair
                ? "MintBrush"
                : "AmberBrush";

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            grid.Children.Add(new Border
            {
                Width = 4, CornerRadius = new CornerRadius(2),
                Background = UiBrush.Get(accent),
                Margin = new Thickness(0, 2, 12, 2)
            });

            var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock
            {
                Text = r.Title, FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = UiBrush.Get("TextPrimary")
            });
            head.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(9, 0, 0, 0),
                Background = UiBrush.Get(accent, 0x28),
                Child = new TextBlock
                {
                    Text = r.KindText, FontSize = 9.5,
                    Foreground = UiBrush.Get(accent)
                }
            });
            mid.Children.Add(head);

            var sub = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            sub.Children.Add(new TextBlock
            {
                Text = "房主 " + r.OwnerName, FontSize = 10.5,
                Foreground = UiBrush.Get("TextMuted")
            });

            if (r.Code.Length > 0)
            {
                sub.Children.Add(new TextBlock
                {
                    Text = "  ·  房间码 ", FontSize = 10.5,
                    Foreground = UiBrush.Get("TextMuted")
                });
                sub.Children.Add(new TextBlock
                {
                    Text = r.Code, FontSize = 11, FontWeight = FontWeights.SemiBold,
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = UiBrush.Get("MintBrush")
                });
            }
            else
            {
                // 别人的房间：房间码不可见，提示需要码才能进
                sub.Children.Add(new TextBlock
                {
                    Text = "  ·  🔒 需要房间码", FontSize = 10.5,
                    Foreground = UiBrush.Get("AmberBrush")
                });
            }
            mid.Children.Add(sub);
            Grid.SetColumn(mid, 1);
            grid.Children.Add(mid);

            // 右侧只显示在线人数
            int onlineNum = r.Seats.Count(s => s.State == SeatState.Online);

            var right = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var countRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            countRow.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                Fill = onlineNum > 0
                    ? UiBrush.Get("MintBrush") : UiBrush.Get("TextSecondary")
            });
            countRow.Children.Add(new TextBlock
            {
                Text = onlineNum + " 人在线", FontSize = 11.5,
                Foreground = onlineNum > 0
                    ? UiBrush.Get("MintBrush")
                    : UiBrush.Get("TextMuted")
            });
            right.Children.Add(countRow);

            if (r.CanDepart)
                right.Children.Add(new TextBlock
                {
                    Text = "可发车", FontSize = 10, Margin = new Thickness(0, 4, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Foreground = UiBrush.Get("MintBrush")
                });

            Grid.SetColumn(right, 2);
            grid.Children.Add(right);

            var card = new Border
            {
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(12, 11, 14, 11),
                Margin = new Thickness(0, 0, 0, 8),
                BorderBrush = sel ? UiBrush.Get(accent, 0xAA) : UiBrush.Get("TextPrimary", 0x1E),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Child = grid
            };
            card.SetResourceReference(Border.BackgroundProperty, "PanelBackground");

            card.MouseLeftButtonUp += async (_, __) => await SelectRoomAsync(r);
            return card;
        }

        private async Task SelectRoomAsync(LobbyRoom r)
        {
            _selected = r;
            await ReloadAsync().ConfigureAwait(true);
            RenderDetail(r);
        }

        private void RenderDetail(LobbyRoom r)
        {
            DetailPanel.Children.Clear();
            ActionPanel.Visibility = Visibility.Visible;

            // ★ 大小号房：详情同时展示大号房 + 小号房，两个区块区分开
            if (r.Kind == RoomKind.PrivatePair && r.PairId.Length > 0)
            {
                var big = r.PairRole == "big" ? r
                        : _rooms.FirstOrDefault(x => x.PairId == r.PairId && x.PairRole == "big");
                var small = r.PairRole == "small" ? r
                          : _rooms.FirstOrDefault(x => x.PairId == r.PairId && x.PairRole == "small");

                DetailTitle.Text = PairTitle(big ?? small);

                bool isOwner = string.Equals((big ?? small)?.OwnerId, DeviceIdentity.Code,
                                             StringComparison.OrdinalIgnoreCase);

                // 总览
                AddRow("类型", "私人·大小号（两间绑定）");
                AddRow("房主", (big ?? small)?.OwnerName ?? "");

                int bOn = big?.Seats.Count(s => s.State == SeatState.Online) ?? 0;
                int sOn = small?.Seats.Count(s => s.State == SeatState.Online) ?? 0;
                int bNeed = big?.RequiredCount ?? 2;
                int sNeed = small?.RequiredCount ?? 2;
                AddRow("在线", "大号 " + bOn + " 人 · 小号 " + sOn + " 人");
                AddRow("发车", (bOn >= bNeed && sOn >= sNeed)
                    ? "✓ 可以发车"
                    : "大号满 " + bNeed + " · 小号满 " + sNeed + " 才能发车");

                // ── 两个子区块 ──
                DetailPanel.Children.Add(PairDetailBlock(big, "大号房", "这里是大号（真人）",
                    "AmberBrush"));

                DetailPanel.Children.Add(PairDetailBlock(small, "小号房", "这里是小号（补位）",
                    "DesktopAccent"));

                // 按钮
                // ★ 同上：用设备码比对
                string myDev = DeviceIdentity.Code;
                bool iAmIn = (big?.Seats.Any(s => string.Equals(s.UserId, myDev, StringComparison.OrdinalIgnoreCase)) ?? false)
                          || (small?.Seats.Any(s => string.Equals(s.UserId, myDev, StringComparison.OrdinalIgnoreCase)) ?? false);

                BtnJoin.Visibility = iAmIn ? Visibility.Collapsed : Visibility.Visible;
                BtnDepart.Visibility = (isOwner && bOn >= bNeed && sOn >= sNeed)
                    ? Visibility.Visible : Visibility.Collapsed;
                BtnEditCode.Visibility = isOwner ? Visibility.Visible : Visibility.Collapsed;
                BtnLeave.Visibility = iAmIn ? Visibility.Visible : Visibility.Collapsed;
                BtnDelete.Visibility = isOwner ? Visibility.Visible : Visibility.Collapsed;
                return;
            }

            // ── 普通房间（私人，发车人数 2-4）──
            DetailTitle.Text = r.Title;

            bool isOwner4 = string.Equals(r.OwnerId, DeviceIdentity.Code, StringComparison.OrdinalIgnoreCase);
            // ★ 座位上的 UserId 是**设备码**（不是 Windows 用户名），必须用设备码比对
            bool iAmIn4 = r.Seats.Any(s =>
                string.Equals(s.UserId, DeviceIdentity.Code, StringComparison.OrdinalIgnoreCase));

            AddRow("类型", r.KindText);
            AddRow("房主", r.OwnerName);
            if (r.Code.Length > 0) AddRow("房间码", r.Code, mono: true);
            AddRow("在线", r.Seats.Count(s => s.State == SeatState.Online) + " 人");
            AddRow("发车", "满 " + r.RequiredCount + " 人发车");

            var online = r.Seats.Where(s => s.State == SeatState.Online).ToList();

            DetailPanel.Children.Add(new TextBlock
            {
                Text = "在线成员（" + online.Count + "）",
                FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = UiBrush.Get("TextMuted"),
                Margin = new Thickness(0, 16, 0, 8)
            });

            if (online.Count == 0)
            {
                DetailPanel.Children.Add(new TextBlock
                {
                    Text = "还没有人在线", FontSize = 11,
                    Foreground = UiBrush.Get("TextMuted")
                });
            }

            foreach (var s in online)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                row.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center,
                    Fill = UiBrush.Get("MintBrush")
                });

                var name = new TextBlock
                {
                    Text = s.DisplayName, FontSize = 11.5,
                    Foreground = UiBrush.Get("TextPrimary"),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(name, 1);
                row.Children.Add(name);

                DetailPanel.Children.Add(row);
            }

            BtnJoin.Visibility = iAmIn4 ? Visibility.Collapsed : Visibility.Visible;
            BtnDepart.Visibility = (isOwner4 && r.CanDepart) ? Visibility.Visible : Visibility.Collapsed;
            BtnEditCode.Visibility = isOwner4 ? Visibility.Visible : Visibility.Collapsed;
            BtnLeave.Visibility = iAmIn4 ? Visibility.Visible : Visibility.Collapsed;
            BtnDelete.Visibility = isOwner4 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>成对房间的总标题（去掉"（大号）"/"（小号）"后缀）。</summary>
        private static string PairTitle(LobbyRoom? any)
        {
            if (any == null) return "大小号房";
            return any.Title.Replace("（大号）", "").Replace("（小号）", "");
        }

        /// <summary>详情里的一个子区块（大号房 / 小号房）。</summary>
        private Border PairDetailBlock(LobbyRoom? room, string label, string hint, string accent)
        {
            var sp = new StackPanel();

            // 标题行：色条 + 名称
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new Border
            {
                Width = 3, Height = 14, CornerRadius = new CornerRadius(2),
                Background = UiBrush.Get(accent),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            head.Children.Add(new TextBlock
            {
                Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = UiBrush.Get(accent),
                VerticalAlignment = VerticalAlignment.Center
            });
            head.Children.Add(new TextBlock
            {
                Text = "  " + hint, FontSize = 9.5,
                Foreground = UiBrush.Get("TextMuted"),
                VerticalAlignment = VerticalAlignment.Center
            });
            sp.Children.Add(head);

            if (room == null)
            {
                sp.Children.Add(new TextBlock
                {
                    Text = "（未创建）", FontSize = 11,
                    Foreground = UiBrush.Get("TextMuted"),
                    Margin = new Thickness(0, 6, 0, 0)
                });
            }
            else
            {
                // 房间码
                var codeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
                codeRow.Children.Add(new TextBlock
                {
                    Text = "房间码 ", FontSize = 10,
                    Foreground = UiBrush.Get("TextMuted")
                });
                codeRow.Children.Add(new TextBlock
                {
                    Text = room.Code.Length > 0 ? room.Code : "🔒 需要房间码",
                    FontSize = room.Code.Length > 0 ? 12 : 10,
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = room.Code.Length > 0
                        ? UiBrush.Get("MintBrush")
                        : UiBrush.Get("AmberBrush")
                });
                sp.Children.Add(codeRow);

                // 成员
                var online = room.Seats.Where(s => s.State == SeatState.Online).ToList();
                sp.Children.Add(new TextBlock
                {
                    Text = online.Count + " 人在线" + (online.Count == 0 ? "" : "："),
                    FontSize = 10,
                    Foreground = UiBrush.Get("TextMuted"),
                    Margin = new Thickness(0, 6, 0, 0)
                });

                foreach (var s in online)
                {
                    var row = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(6, 3, 0, 0)
                    };
                    row.Children.Add(new System.Windows.Shapes.Ellipse
                    {
                        Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 7, 0),
                        Fill = UiBrush.Get(accent)
                    });
                    row.Children.Add(new TextBlock
                    {
                        Text = s.DisplayName, FontSize = 11,
                        Foreground = UiBrush.Get("TextPrimary")
                    });
                    sp.Children.Add(row);
                }
            }

            var card = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(11, 10, 11, 10),
                Margin = new Thickness(0, 12, 0, 0),
                BorderBrush = UiBrush.Get(accent, 0x44),
                BorderThickness = new Thickness(1),
                Child = sp
            };
            card.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
            return card;
        }


        private void AddRow(string label, string value, bool mono = false)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            row.Children.Add(new TextBlock
            {
                Text = label, FontSize = 11,
                Foreground = UiBrush.Get("TextMuted"),
                VerticalAlignment = VerticalAlignment.Center
            });

            var v = new TextBlock
            {
                Text = value, FontSize = 11.5,
                Foreground = UiBrush.Get("TextPrimary"),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            if (mono) { v.FontFamily = new FontFamily("Consolas"); v.FontSize = 13; }
            Grid.SetColumn(v, 1);
            row.Children.Add(v);

            DetailPanel.Children.Add(row);
        }

        // ══════════ 事件 ══════════

        private async void Tab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton me) return;
            TabAll.IsChecked = me == TabAll;
            TabFour.IsChecked = me == TabFour;
            TabTwo.IsChecked = me == TabTwo;
            _selected = null;
            DetailPanel.Children.Clear();
            DetailTitle.Text = "房间详情";
            ActionPanel.Visibility = Visibility.Collapsed;
            await ReloadAsync();
        }

        /// <summary>我的发车动作：设置本用户在房间发车时要执行什么。</summary>
        private async void MyDepart_Click(object sender, RoutedEventArgs e)
        {
            // 配置在窗口内点「保存当前账号」时就已经写入，关闭窗口只是退出。
            var dlg = new MyDepartWindow(_me.UserName) { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
            await ReloadAsync();
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            // ReloadAsync 内部会重建列表 + 重渲染详情
            await ReloadAsync().ConfigureAwait(true);
        }

        /// <summary>
        /// 用最新的 _rooms 重建右侧详情。
        /// 房间被删了就清空详情。
        /// </summary>
        private void RefreshDetailFromList()
        {
            try
            {
                if (_selected == null) return;

                var fresh = _rooms.FirstOrDefault(x => x.Id == _selected.Id);
                if (fresh != null)
                {
                    _selected = fresh;
                    RenderDetail(fresh);
                }
                else
                {
                    // 房间没了（被别人关了 / 过期销毁）
                    _selected = null;
                    DetailPanel.Children.Clear();
                    DetailTitle.Text = "房间详情";
                    ActionPanel.Visibility = Visibility.Collapsed;
                }
            }
            catch { }
        }

        /// <summary>新建房间：选模式 → 填名称 → 填房间码。</summary>
        private async void CreateRoom_Click(object sender, RoutedEventArgs e)
        {
            var pick = new LobbyRoomKindWindow { Owner = Window.GetWindow(this) };
            if (pick.ShowDialog() != true) return;

            if (pick.SelectedKind == RoomKind.PrivatePair)
                await CreatePairRoomFlowAsync();
            else
                await CreateFourRoomFlowAsync();
        }

        private async Task CreateFourRoomFlowAsync()
        {
            var exist = _rooms.FirstOrDefault(r =>
                r.OwnerId == _me.UserName && r.Kind == RoomKind.PrivateFour);
            if (exist != null)
            {
                ThemedMessage.Info(Window.GetWindow(this), "提示", "你已经有一个私人房间了：「" + exist.Title + "」。\n\n"
                    + "每种模式各能建一个，可以先关掉原来的再建。");
                await SelectRoomAsync(exist);
                return;
            }

            var nameDlg = new InputDialogWindow("房间名称", "给自己房间起个名字", _me.DisplayName + " 的房间");
            if (Window.GetWindow(this) is Window o1) nameDlg.Owner = o1;
            if (nameDlg.ShowDialog() != true) return;
            string title = (nameDlg.ResultText ?? "").Trim();
            if (title.Length == 0) { ThemedMessage.Info(Window.GetWindow(this), "提示", "房间名称不能为空。"); return; }

            // ★ 发车人数 2-4 可配
            var sizeDlg = new LobbyDepartSizeWindow("发车人数",
                "这个房间满多少人自动发车（2-4 人可选）。", 4);
            if (Window.GetWindow(this) is Window os) sizeDlg.Owner = os;
            if (sizeDlg.ShowDialog() != true) return;
            int departSize = sizeDlg.SelectedSize;

            while (true)
            {
                // ★ 房主自己设置房间码（用户要求：自己设更安全，相当于只有自己知道的通行证）。
                var codeDlg = new InputDialogWindow("房间码",
                    "自己设一个只有你知道的码，发给队友才能进来。任意字符都行。", "");
                if (Window.GetWindow(this) is Window o2) codeDlg.Owner = o2;
                if (codeDlg.ShowDialog() != true) return;

                string code = (codeDlg.ResultText ?? "").Trim();
                if (code.Length == 0) { ThemedMessage.Info(Window.GetWindow(this), "提示", "房间码不能为空。"); continue; }

                var res = await LobbyApi.CreateFourAsync(title, code, departSize).ConfigureAwait(true);
                if (!res.Ok) { ThemedMessage.Info(Window.GetWindow(this), "提示", res.Message); return; }

                await ReloadAsync();
                var created = _rooms.FirstOrDefault(x => x.Id == res.Room?.Id);
                if (created != null) await SelectRoomAsync(created);

                ThemedMessage.Info(Window.GetWindow(this), "提示", "房间已创建。\n\n名称：" + title + "\n房间码：" + code
                    + "\n发车人数：" + departSize + " 人"
                    + "\n\n★ 房间码只你自己能看到，把它发给队友，他们点「加入房间」填码进来。");
                return;
            }
        }

        private async Task CreatePairRoomFlowAsync()
        {
            var exist = _rooms.FirstOrDefault(r =>
                r.OwnerId == _me.UserName && r.Kind == RoomKind.PrivatePair);
            if (exist != null)
            {
                ThemedMessage.Info(Window.GetWindow(this), "提示", "你已经有一对私人·大小号房了：「" + exist.Title + "」。\n\n"
                    + "每种模式各能建一个，可以先关掉原来的再建。");
                await SelectRoomAsync(exist);
                return;
            }

            var nameDlg = new InputDialogWindow("房间名称", "这一对房间共用同一个名字", _me.DisplayName + " 的大小号房");
            if (Window.GetWindow(this) is Window o1) nameDlg.Owner = o1;
            if (nameDlg.ShowDialog() != true) return;
            string title = (nameDlg.ResultText ?? "").Trim();
            if (title.Length == 0) { ThemedMessage.Info(Window.GetWindow(this), "提示", "房间名称不能为空。"); return; }

            // ★ 大号房发车人数（2-4）
            var bigSizeDlg = new LobbyDepartSizeWindow("大号房发车人数",
                "大号房满多少人发车（2-4 人可选）。", 2);
            if (Window.GetWindow(this) is Window ob) bigSizeDlg.Owner = ob;
            if (bigSizeDlg.ShowDialog() != true) return;
            int bigSize = bigSizeDlg.SelectedSize;

            // ★ 小号房发车人数（2-4）
            var smallSizeDlg = new LobbyDepartSizeWindow("小号房发车人数",
                "小号房满多少人发车（2-4 人可选）。", 2);
            if (Window.GetWindow(this) is Window os2) smallSizeDlg.Owner = os2;
            if (smallSizeDlg.ShowDialog() != true) return;
            int smallSize = smallSizeDlg.SelectedSize;

            var bigDlg = new InputDialogWindow("大号房房间码", "给大号房设一个码", "");
            if (Window.GetWindow(this) is Window o2) bigDlg.Owner = o2;
            if (bigDlg.ShowDialog() != true) return;
            string bigCode = (bigDlg.ResultText ?? "").Trim();
            if (bigCode.Length == 0) { ThemedMessage.Info(Window.GetWindow(this), "提示", "大号房房间码不能为空。"); return; }

            while (true)
            {
                var smallDlg = new InputDialogWindow("小号房房间码", "给小号房设一个码（不能和大号房相同）", "");
                if (Window.GetWindow(this) is Window o3) smallDlg.Owner = o3;
                if (smallDlg.ShowDialog() != true) return;

                string smallCode = (smallDlg.ResultText ?? "").Trim();
                if (smallCode.Length == 0) { ThemedMessage.Info(Window.GetWindow(this), "提示", "小号房房间码不能为空。"); continue; }
                if (string.Equals(smallCode, bigCode, StringComparison.OrdinalIgnoreCase))
                { ThemedMessage.Info(Window.GetWindow(this), "提示", "大号房和小号房不能用同一个码。"); continue; }

                var res = await LobbyApi.CreatePairAsync(title, bigCode, smallCode, bigSize, smallSize).ConfigureAwait(true);
                if (!res.Ok) { ThemedMessage.Info(Window.GetWindow(this), "提示", res.Message); return; }

                await ReloadAsync();
                var created = _rooms.FirstOrDefault(x => x.Id == res.Big?.Id);
                if (created != null) await SelectRoomAsync(created);

                ThemedMessage.Info(Window.GetWindow(this), "提示", "成对房间已创建。\n\n" + title + "\n"
                    + "大号房码：" + bigCode + "（满 " + bigSize + " 个大号）\n"
                    + "小号房码：" + smallCode + "（满 " + smallSize + " 个小号）\n\n"
                    + "两边都满各自人数才能发车。这两个码只有你能看到。");
                return;
            }
        }

        /// <summary>加入房间：输入房间码 + 指定用哪个用户上线。</summary>
        private async void Join_Click(object sender, RoutedEventArgs e)
        {
            string code;
            string displayName;
            string roleName = "";

            // ★ 始终让用户选账号（自己的房间也一样，因为一台机器可能有 A1/A2/A3）。
            //   区别只在于：自己的房间→房间码预填并锁定；大小号房→可选进哪一间。
            bool isMyRoom = _selected != null
                && string.Equals(_selected.OwnerId, DeviceIdentity.Code, StringComparison.OrdinalIgnoreCase);

            string lockCode = "";
            List<(string title, string code)>? offerRooms = null;

            if (isMyRoom && _selected != null)
            {
                // 大小号房：列出两间让用户选
                if (_selected.Kind == RoomKind.PrivatePair && _selected.PairId.Length > 0)
                {
                    offerRooms = _rooms
                        .Where(x => x.PairId == _selected.PairId)
                        .Select(x => (x.Title, x.Code))
                        .ToList();
                }
                else
                {
                    lockCode = _selected.Code;
                }
            }

            var dlg = new JoinRoomWindow(_me.UserName, lockCode, offerRooms)
            {
                Owner = Window.GetWindow(this)
            };
            if (dlg.ShowDialog() != true) return;

            code = dlg.RoomCode;
            displayName = dlg.DisplayName;
            roleName = dlg.RoleName;

            var res = await LobbyApi.JoinAsync(code, displayName, roleName)
                                    .ConfigureAwait(true);

            if (!res.Ok)
            {
                ThemedMessage.Info(Window.GetWindow(this), "提示", res.Message);
                return;
            }

            await ReloadAsync();
            var room = _rooms.FirstOrDefault(r =>
                string.Equals(r.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));
            if (room != null) await SelectRoomAsync(room);
        }

        private async void Leave_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            var r = _selected;

            var res = await LobbyApi.LeaveAsync(r.Id).ConfigureAwait(true);

            // ★ 离开后强制重新拉数据，并且**保持选中**（让用户看到自己已下线）
            await ReloadAsync().ConfigureAwait(true);

            var again = _rooms.FirstOrDefault(x => x.Id == r.Id);
            if (again != null)
            {
                _selected = again;
                RenderDetail(again);
            }
            else
            {
                _selected = null;
                DetailPanel.Children.Clear();
                ActionPanel.Visibility = Visibility.Collapsed;
            }
        }

        private async void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            var r = _selected;

            string extra = r.Kind == RoomKind.PrivatePair ? "\n\n（配对的大小号房也会一起关闭）" : "";
            if (!ThemedMessage.Confirm(Window.GetWindow(this), "确认", "关闭房间「" + r.Title + "」？" + extra)) return;

            var del = await LobbyApi.DeleteAsync(r.Id).ConfigureAwait(true);
            if (!del.Ok)
            {
                ThemedMessage.Info(Window.GetWindow(this), "提示", del.Message);
                return;
            }

            _selected = null;
            DetailPanel.Children.Clear();
            DetailTitle.Text = "房间详情";
            ActionPanel.Visibility = Visibility.Collapsed;
            await ReloadAsync();
        }

        private async void EditCode_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            var r = _selected;

            var dlg = new InputDialogWindow("修改房间码", "新房间码（任意字符）", r.Code);
            if (Window.GetWindow(this) is Window o) dlg.Owner = o;
            if (dlg.ShowDialog() != true) return;

            string code = (dlg.ResultText ?? "").Trim();
            if (code.Length == 0) { ThemedMessage.Info(Window.GetWindow(this), "提示", "房间码不能为空。"); return; }

            var chg = await LobbyApi.ChangeCodeAsync(r.Id, code).ConfigureAwait(true);
            if (!chg.Ok) { ThemedMessage.Info(Window.GetWindow(this), "提示", chg.Message); return; }

            await ReloadAsync();
            var again = _rooms.FirstOrDefault(x => x.Id == r.Id);
            if (again != null) RenderDetail(again);
        }

        /// <summary>
        /// 执行本用户的发车动作。
        /// 独立于大厅界面运行，失败只记日志不打断界面。
        /// </summary>
        /// <summary>
        /// 为**指定账号**执行它的发车动作。
        /// BGI 路径严格按该账号查（不会错用别人的）。
        /// </summary>
        private async Task RunDepartForUserAsync(string user, LobbyApi.DepartConfig d)
        {
            try
            {
                switch (d.Mode)
                {
                    case "action":
                    {
                        var act = App.Config.Actions.FirstOrDefault(a => a.Id == d.ActionId);
                        if (act == null)
                        {
                            LogBus.Warn("发车动作失败：" + user + " 的快捷动作不存在");
                            return;
                        }
                        var r = await ActionService.ExecuteAsync(act, App.Config);
                        LogBus.Write(r.Ok ? LogLevel.Ok : LogLevel.Error,
                            "发车动作「" + user + " · " + act.Name + "」" + (r.Ok ? "已执行" : "失败"),
                            r.Message);
                        break;
                    }

                    case "dragon":
                    case "groups":
                    {
                        // ★ 严格按这个账号查 BGI 路径
                        // ★ 统一走解析器：手动指定 > 正在运行 > 记忆
                        string exe = BgiPathResolver.GetPath(user);

                        if (exe.Length == 0 || !System.IO.File.Exists(exe))
                        {
                            LogBus.Warn("发车动作失败：" + user + " 没有可用的 BGI 路径");
                            return;
                        }

                        string args = d.Mode == "dragon"
                            ? "--startOneDragon \"" + d.DragonName + "\""
                            : "--startGroups " + d.Groups;

                        // 找该账号的会话
                        var sessions = await SessionService.QueryAsync();
                        var s = SessionService.Find(sessions, user);
                        if (s == null)
                        {
                            LogBus.Warn("发车动作失败：" + user + " 没有会话（未上线）");
                            return;
                        }

                        // ★ 停 BGI 是**必须**的：一条龙 / 配置组都要求 BGI 空闲，
                        //   不先停会冲突。停止方式读该账号 BGI 的 cancelTaskHotkey。
                        //   关闭原神是可选项，读本地配置（不经过服务器）。
                        //   全部按 SessionId 过滤，只影响这个账号的进程。
                        bool closeGenshin = false;
                        try
                        {
                            App.Config.Settings.DepartCloseGenshin?
                               .TryGetValue(user, out closeGenshin);
                        }
                        catch { }

                        var prep = await BgiStartGuard.BeforeStartAsync(s.Id, exe).ConfigureAwait(true);
                        if (!prep.Ok) { LogBus.Warn("发车已阻止：" + user, prep.Message); return; }

                        LogBus.Write(prep.Ok ? LogLevel.Info : LogLevel.Warn,
                            "发车准备「" + user + "」", prep.Message);

                        var rr = await SystemLauncher.LaunchAsync(s.Id, exe, args);
                        LogBus.Write(rr.Ok ? LogLevel.Ok : LogLevel.Error,
                            "发车动作 BGI「" + user + "」" + (rr.Ok ? "已启动" : "启动失败"),
                            args + " · " + rr.Message);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                LogBus.Error("发车动作异常（" + user + "）", ex.Message);
            }
        }

        /// <summary>发车。</summary>
        private async void Depart_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            var r = _selected;

            var depRes = await LobbyApi.DepartAsync(r.Id).ConfigureAwait(true);
            bool ok = depRes.Ok;
            string msg = depRes.Message;
            var members = depRes.Targets.Select(t => t.DisplayName).ToList();
            if (!ok)
            {
                ThemedMessage.Info(Window.GetWindow(this), "提示", msg);
                return;
            }

            // ★ 发车后执行发车动作。
            //   服务端下发所有成员（DeviceCode + 各自的动作配置），
            //   本机只执行**自己设备码**那一条。
            string execNote = "";
            try
            {
                var lines = new List<string>();
                string myDevice = DeviceIdentity.Code;

                foreach (var t in depRes.Targets)
                {
                    if (string.IsNullOrEmpty(t.Config.Mode)) continue;

                    // 只执行本机的
                    if (!string.Equals(t.DeviceCode, myDevice, StringComparison.OrdinalIgnoreCase))
                        continue;

                    lines.Add((t.DisplayName.Length > 0 ? t.DisplayName : t.DeviceCode)
                              + " → " + t.Config.Summary);
                    _ = RunDepartForUserAsync(t.DisplayName, t.Config);
                }

                if (lines.Count > 0)
                    execNote = "\n\n本机开始执行：\n" + string.Join("\n", lines);
                else
                    execNote = "\n\n（本机还没设置发车动作，这次不执行。"
                             + "\n点顶部「发车动作」设置。其他成员的客户端会各跑各的。）";
            }
            catch { }

            ThemedMessage.Info(Window.GetWindow(this), "已发车",
                "向 " + members.Count + " 位成员下发开跑通知：\n"
                + string.Join("、", members)
                + execNote + "\n\n发车后成员会自动下线。");

            await ReloadAsync();
            var after = _rooms.FirstOrDefault(x => x.Id == r.Id);
            if (after != null) RenderDetail(after);
        }
    }
}
