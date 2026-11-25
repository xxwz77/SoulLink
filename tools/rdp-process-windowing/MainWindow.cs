using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace SoulLink.WindowingTest
{
    public sealed class SessionVm : INotifyPropertyChanged
    {
        public string Id { get; }
        public string Caption { get; }
        public Color Color { get; }
        public string Status { get; private set; } = "启动中";
        public long Hwnd { get; set; }
        public RectNative Bounds { get; set; }
        public Process? Process { get; set; }
        public NamedPipeClientStream? Pipe { get; set; }
        public readonly SemaphoreSlim WriteLock=new(1,1);
        public readonly TaskCompletionSource<WindowMessage> Ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsSelected { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged;
        public SessionVm(string id, string caption, Color color) { Id=id; Caption=caption; Color=color; }
        public void SetStatus(string value) { Status=value; PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(Status))); }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RectNative { public int Left,Top,Right,Bottom; public int Width=>Right-Left; public int Height=>Bottom-Top; }

    public sealed class MainWindow : Window
    {
        private readonly ObservableCollection<SessionVm> _sessions = new();
        private readonly ListBox _tabs = new() { Width=170, Margin=new Thickness(0,0,10,0) };
        private readonly Border _workspace = new() { Background=new SolidColorBrush(Color.FromArgb(18,255,255,255)), BorderBrush=new SolidColorBrush(Color.FromArgb(45,255,255,255)), BorderThickness=new Thickness(1), ClipToBounds=true };
        private readonly TextBlock _modeText = new() { Text="单窗口堆叠", Foreground=Brushes.White, VerticalAlignment=VerticalAlignment.Center, Margin=new Thickness(10,0,0,0) };
        private bool _split;
        private bool _closing;
        private bool _restoring;
        private SessionVm? _selected;
        private bool _childrenSuppressed;
        private Point _lastOwnerScreen;
        private Size _lastOwnerSize;
        private readonly Dictionary<string,RectNative> _savedSplitBounds = new(StringComparer.Ordinal);
        private readonly DispatcherTimer _followTimer = new() { Interval=TimeSpan.FromMilliseconds(60) };
        private readonly Button _launchButton = new() { Content="启动 4 个模拟连接", Margin=new Thickness(0,0,8,0), Padding=new Thickness(12,7,12,7) };

        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int cx,int cy,uint flags);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd,int command);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd,out RectNative rect);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        private const uint SwpNoactivate=0x10, SwpNozorder=0x4, SwpFramechanged=0x20;
        private static readonly IntPtr HwndTopmost=new(-1), HwndNotopmost=new(-2);

        public MainWindow()
        {
            Title="SoulLink · 独立进程窗口架构验证（无 RDP）";
            Width=1120; Height=720; MinWidth=850; MinHeight=540;
            Left=80; Top=70; WindowStartupLocation=WindowStartupLocation.Manual;
            Background=new SolidColorBrush(Color.FromRgb(13,20,35));
            Content=BuildUi();
            Loaded += async(_,__) => {
                SetEmbeddedChrome(false);
                _lastOwnerScreen = PointToScreen(new Point(0,0));
                _lastOwnerSize = new Size(ActualWidth,ActualHeight);
                await LaunchInitialAsync();
            };
            Closing += async(_,e) => { if(!_closing){e.Cancel=true;await ShutdownAsync();} };
            LocationChanged += (_,__) => QueueOwnerGeometrySync();
            SizeChanged += (_,__) => QueueOwnerGeometrySync();
            IsVisibleChanged += async(_,__) => {
                if(!IsVisible) await SuppressChildrenAsync();
                else await RestoreVisibleAsync();
            };
            Activated += (_,__) => { if(!_childrenSuppressed) _ = RestoreVisibleAsync(); };
            Deactivated += (_,__) => {
                // Owner relationship controls Z-order. Do not hide on transient deactivation.
            };
            StateChanged += async(_,__) =>
            {
                if(WindowState==WindowState.Minimized) await SuppressChildrenAsync();
                else if(WindowState==WindowState.Normal || WindowState==WindowState.Maximized)
                {
                    _lastOwnerScreen=PointToScreen(new Point(0,0));
                    _lastOwnerSize=new Size(ActualWidth,ActualHeight);
                    await Dispatcher.InvokeAsync(async()=>await RestoreVisibleAsync(),DispatcherPriority.Loaded);
                }
            };
            _followTimer.Tick += async(_,__) =>
            {
                if(_closing || _split || _selected==null || WindowState==WindowState.Minimized) return;
                if(_selected.Hwnd==0) return;
                if(!GetWindowRect(new IntPtr(_selected.Hwnd),out var child)) return;
                var target=GetWorkspaceRect();
                if(target.Width<50 || target.Height<50) return;
                if(Math.Abs(child.Left-target.Left)>2 || Math.Abs(child.Top-target.Top)>2 || Math.Abs(child.Width-target.Width)>2 || Math.Abs(child.Height-target.Height)>2)
                    await ArrangeStackedAsync();
            };
            _followTimer.Start();
        }

        private UIElement BuildUi()
        {
            var root=new Grid { Margin=new Thickness(14) };
            root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
            var toolbar=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,12)};
            _launchButton.Click += async(_,__) => { _launchButton.IsEnabled=false; await LaunchExtraAsync(); _launchButton.IsEnabled=true; };
            toolbar.Children.Add(_launchButton);
            var toggle=new Button{Content="切换分屏",Padding=new Thickness(12,7,12,7),Margin=new Thickness(0,0,8,0)};
            toggle.Click += async(_,__) => { _split=!_split; toggle.Content=_split?"切回单窗口":"切换分屏"; _modeText.Text=_split?"自由分屏 · 系统窗口可拖动":"单窗口堆叠"; await ApplyModeAsync(); };
            toolbar.Children.Add(toggle);
            var tile=new Button{Content="重新平铺",Padding=new Thickness(12,7,12,7)};
            tile.Click += async(_,__) => await ArrangeSplitAsync(true);
            toolbar.Children.Add(tile);
            toolbar.Children.Add(_modeText);
            root.Children.Add(toolbar);
            var body=new Grid(); Grid.SetRow(body,1);
            body.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
            body.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
            _tabs.ItemsSource=_sessions;
            _tabs.Background=new SolidColorBrush(Color.FromArgb(20,255,255,255));
            _tabs.Foreground=Brushes.White;
            _tabs.SelectionChanged += async(_,__) => { if(_restoring)return; if(_tabs.SelectedItem is SessionVm vm) await SelectAsync(vm); };
            _tabs.ItemTemplate=CreateTabTemplate();
            body.Children.Add(_tabs);
            Grid.SetColumn(_workspace,1); body.Children.Add(_workspace);
            root.Children.Add(body);
            return root;
        }

        private DataTemplate CreateTabTemplate()
        {
            var template=new DataTemplate(typeof(SessionVm));
            var panel=new FrameworkElementFactory(typeof(StackPanel));
            var title=new FrameworkElementFactory(typeof(TextBlock)); title.SetBinding(TextBlock.TextProperty,new Binding("Caption")); title.SetValue(TextBlock.FontSizeProperty,14.0); title.SetValue(TextBlock.MarginProperty,new Thickness(8,8,8,2));
            var status=new FrameworkElementFactory(typeof(TextBlock)); status.SetBinding(TextBlock.TextProperty,new Binding("Status")); status.SetValue(TextBlock.FontSizeProperty,10.0); status.SetValue(TextBlock.MarginProperty,new Thickness(8,0,8,8)); status.SetValue(TextBlock.OpacityProperty,0.7);
            panel.AppendChild(title);panel.AppendChild(status);template.VisualTree=panel;return template;
        }

        private void QueueOwnerGeometrySync()
        {
            if(_closing || _childrenSuppressed || WindowState==WindowState.Minimized)return;
            _ = Dispatcher.InvokeAsync(async()=>
            {
                if(_split)await FollowOwnerMoveAsync();
                else if(_selected!=null)await ArrangeStackedAsync();
                _lastOwnerScreen=PointToScreen(new Point(0,0));
                _lastOwnerSize=new Size(ActualWidth,ActualHeight);
            },DispatcherPriority.Background);
        }

        private async Task SuppressChildrenAsync()
        {
            if(_childrenSuppressed)return;
            _childrenSuppressed=true;
            _followTimer.Stop();
            await SetAllVisibleAsync(false);
        }

        private async Task LaunchInitialAsync()
        {
            var specs=new[]{("M1","A2 · 模拟",Color.FromRgb(32,80,120)),("M2","A3 · 模拟",Color.FromRgb(52,100,82)),("M3","A4 · 模拟",Color.FromRgb(102,65,110))};
            foreach(var s in specs) await LaunchAsync(s.Item1,s.Item2,s.Item3);
            await SelectAsync(_sessions[0]);
        }

        private async Task LaunchExtraAsync()
        {
            if(_sessions.Count>=8)return;
            int n=_sessions.Count+1;
            await LaunchAsync("M"+n,"测试账号 "+n,Color.FromRgb((byte)(35+n*15),(byte)(60+n*8),(byte)(85+n*12)));
            if(_split)await ArrangeSplitAsync(true); else await SelectAsync(_sessions[^1]);
        }

        private async Task LaunchAsync(string id,string caption,Color color)
        {
            Trace.Write("controller","launch "+id);
            var vm=new SessionVm(id,caption,color); _sessions.Add(vm);
            string exe=Environment.ProcessPath!;
            var process=Process.Start(new ProcessStartInfo(exe,"--child "+id){UseShellExecute=false,WorkingDirectory=AppContext.BaseDirectory})!;
            vm.Process=process;
            var pipe=new NamedPipeClientStream(".","SoulLink.WindowingTest."+id,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(10000); vm.Pipe=pipe; Trace.Write("controller","connected "+id);
            _=ReceiveAsync(vm); Trace.Write("controller","reader started "+id);
            var readyTask=await Task.WhenAny(vm.Ready.Task,Task.Delay(5000));
            if(readyTask!=vm.Ready.Task)throw new TimeoutException("Child ready timeout: "+id);
            Trace.Write("controller","ready received "+id);
            var ready=await vm.Ready.Task;
            vm.Hwnd=ready.Hwnd;vm.SetStatus("PID "+process.Id+" · 已保活");
            await SendAsync(vm,new WindowMessage{Type="show",Visible=false,Activate=false}); Trace.Write("controller","hidden "+id);
        }

        private async Task ReceiveAsync(SessionVm vm)
        {
            try
            {
                while(vm.Pipe!=null && vm.Pipe.IsConnected && !_closing)
                {
                    var message=await JsonPipe.ReadAsync(vm.Pipe,CancellationToken.None);
                    if(message==null)break;
                    if(message.Type=="ready"){vm.Hwnd=message.Hwnd;vm.Ready.TrySetResult(message);continue;}
                    if(message.Hwnd!=0 && message.Hwnd!=vm.Hwnd)vm.Hwnd=message.Hwnd;
                    if(message.Type=="bounds"){
                        vm.Bounds=new RectNative{Left=message.X,Top=message.Y,Right=message.X+message.Width,Bottom=message.Y+message.Height};
                        if(_split && !_childrenSuppressed)_savedSplitBounds[vm.Id]=vm.Bounds;
                    }
                }
            }catch{}
            if(!_closing)Dispatcher.Invoke(()=>vm.SetStatus("子进程已退出"));
        }

        private RectNative GetWorkspaceRect()
        {
            var a=_workspace.PointToScreen(new Point(0,0));var b=_workspace.PointToScreen(new Point(_workspace.ActualWidth,_workspace.ActualHeight));
            return new RectNative{Left=(int)Math.Round(a.X),Top=(int)Math.Round(a.Y),Right=(int)Math.Round(b.X),Bottom=(int)Math.Round(b.Y)};
        }

        private async Task SendAsync(SessionVm vm,WindowMessage message)
        {
            if(vm.Pipe?.IsConnected!=true)return;
            await vm.WriteLock.WaitAsync();
            try{await JsonPipe.WriteAsync(vm.Pipe,message,CancellationToken.None);}catch{}
            finally{vm.WriteLock.Release();}
        }

        private async Task SelectAsync(SessionVm vm)
        {
            if(vm.Hwnd==0)return;
            _selected=vm;_restoring=true;_tabs.SelectedItem=vm;_restoring=false;
            foreach(var s in _sessions)s.IsSelected=ReferenceEquals(s,vm);
            if(_split)
            {
                await SendAsync(vm,new WindowMessage{Type="show",Visible=true,Activate=true});
                SetForegroundWindow(new IntPtr(vm.Hwnd));
            }
            else
            {
                foreach(var s in _sessions)await SendAsync(s,new WindowMessage{Type="show",Visible=false,Activate=false});
                await ArrangeStackedAsync();
            }
        }

        private async Task FollowOwnerMoveAsync()
        {
            if(_sessions.Count==0)return;
            var current=PointToScreen(new Point(0,0));
            double dx=current.X-_lastOwnerScreen.X,dy=current.Y-_lastOwnerScreen.Y;
            if(Math.Abs(dx)<0.1 && Math.Abs(dy)<0.1 && ActualWidth==_lastOwnerSize.Width && ActualHeight==_lastOwnerSize.Height)return;
            foreach(var vm in _sessions)
            {
                if(vm.Hwnd==0)continue;
                RectNative b;
                if(!_savedSplitBounds.TryGetValue(vm.Id,out b)){
                    if(!GetWindowRect(new IntPtr(vm.Hwnd),out b))continue;
                }
                int x=(int)Math.Round(b.Left+dx),y=(int)Math.Round(b.Top+dy);
                SetWindowPos(new IntPtr(vm.Hwnd),HwndNotopmost,x,y,b.Width,b.Height,SwpNoactivate);
                _savedSplitBounds[vm.Id]=new RectNative{Left=x,Top=y,Right=x+b.Width,Bottom=y+b.Height};
            }
            _lastOwnerScreen=current;_lastOwnerSize=new Size(ActualWidth,ActualHeight);
        }

        private async Task ArrangeStackedAsync()
        {
            if(_selected==null || _selected.Hwnd==0 || _split || WindowState==WindowState.Minimized)return;
            var r=GetWorkspaceRect();if(r.Width<50||r.Height<50)return;
            await SendAsync(_selected,new WindowMessage{Type="set-style",Text="embedded"});
            await SendAsync(_selected,new WindowMessage{Type="set-bounds",X=r.Left,Y=r.Top,Width=r.Width,Height=r.Height});
            SetWindowPos(new IntPtr(_selected.Hwnd),new IntPtr(-1),r.Left,r.Top,r.Width,r.Height,SwpNoactivate);
            ShowWindow(new IntPtr(_selected.Hwnd),4);
            SetWindowPos(new IntPtr(_selected.Hwnd),new IntPtr(-2),r.Left,r.Top,r.Width,r.Height,SwpNoactivate);
            await SendAsync(_selected,new WindowMessage{Type="show",Visible=true,Activate=false});
        }

        private async Task ApplyModeAsync()
        {
            if(_split)
            {
                _followTimer.Stop();
                foreach(var s in _sessions)await SendAsync(s,new WindowMessage{Type="set-style",Text="desktop"});
                await ArrangeSplitAsync(true);
            }
            else
            {
                foreach(var s in _sessions)await SendAsync(s,new WindowMessage{Type="set-style",Text="embedded"});
                foreach(var s in _sessions)await SendAsync(s,new WindowMessage{Type="show",Visible=false,Activate=false});
                await SelectAsync(_selected??_sessions[0]);
                _followTimer.Start();
            }
        }

        private async Task ArrangeSplitAsync(bool reset)
        {
            if(!_split || _sessions.Count==0)return;
            var r=GetWorkspaceRect();int count=_sessions.Count;
            int cols=count<=2?count:(int)Math.Ceiling(Math.Sqrt(count));int rows=(int)Math.Ceiling(count/(double)cols);
            int cellW=Math.Max(320,r.Width/Math.Max(1,cols));int cellH=Math.Max(220,r.Height/Math.Max(1,rows));
            for(int i=0;i<count;i++)
            {
                var vm=_sessions[i];RectNative bounds;
                if(!reset && _savedSplitBounds.TryGetValue(vm.Id,out bounds)){}
                else bounds=new RectNative{Left=r.Left+(i%cols)*cellW+4,Top=r.Top+(i/cols)*cellH+4,Right=r.Left+(i%cols)*cellW+cellW-4,Bottom=r.Top+(i/cols)*cellH+cellH-4};
                await SendAsync(vm,new WindowMessage{Type="set-bounds",X=bounds.Left,Y=bounds.Top,Width=bounds.Width,Height=bounds.Height});
                SetWindowPos(new IntPtr(vm.Hwnd),HwndNotopmost,bounds.Left,bounds.Top,bounds.Width,bounds.Height,SwpNoactivate);
                await SendAsync(vm,new WindowMessage{Type="show",Visible=true,Activate=false});
            }
        }

        private async Task SetAllVisibleAsync(bool visible)
        {
            foreach(var s in _sessions)
            {
                if(s.Hwnd!=0)ShowWindow(new IntPtr(s.Hwnd),0);
                await SendAsync(s,new WindowMessage{Type="show",Visible=false,Activate=false});
            }
        }

        private async Task RestoreVisibleAsync()
        {
            if(_closing || !IsVisible || WindowState==WindowState.Minimized)return;
            if(!_childrenSuppressed && _split && _sessions.All(s=>s.Hwnd!=0))return;
            _childrenSuppressed=false;
            if(_split)await ArrangeSplitAsync(false);
            else if(_selected!=null)await SelectAsync(_selected);
            if(!_split)_followTimer.Start();
        }

        private async Task ShutdownAsync()
        {
            if(_closing)return;_closing=true;_followTimer.Stop();
            foreach(var vm in _sessions)
            {
                await SendAsync(vm,new WindowMessage{Type="close"});
                try{if(vm.Process!=null && !vm.Process.WaitForExit(1500))vm.Process.Kill(true);}catch{}
                try{vm.Pipe?.Dispose();}catch{}
            }
            Close();Application.Current.Shutdown();
        }

        private static void SetEmbeddedChrome(bool topmost){}
    }
}
