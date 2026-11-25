using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SoulLink.Models;

namespace SoulLink.Services;

public static partial class WebServer
{
	private sealed class MiyousheApiRequest
	{
		public string? account { get; set; }

		/// <summary>t50：设默认角色时传该账号下某个角色的 GameUid；空串/缺省 = 清除默认。</summary>
		public string? gameUid { get; set; }

		public string? session { get; set; }

		public bool? enabled { get; set; }

		public int? hour { get; set; }

		public string? salt { get; set; }

		public string? action { get; set; }

		public string? region { get; set; }

		public string? task { get; set; }

		public bool? excluded { get; set; }

		public List<string>? forums { get; set; }

		public string? osSalt { get; set; }

		public int? delaySeconds { get; set; }

		public int? maxRetries { get; set; }

		public int? backoffSeconds { get; set; }

		public string? path { get; set; }

		public bool? verify { get; set; }

		public bool? probeOnly { get; set; }
	}

	private sealed class InputRequest
	{
		public string action { get; set; } = "";

		public double x { get; set; } = -1.0;

		public double y { get; set; } = -1.0;

		public string button { get; set; } = "left";

		public int delta { get; set; }

		public string? key { get; set; }
	}

	private sealed class SettingsDto
	{
		public int fps { get; set; }

		public int quality { get; set; }

		public int keepSeconds { get; set; }

		public int streamFps { get; set; }

		public int videoBitrate { get; set; }

		public int nvencPreset { get; set; }

		public int? vbvBufferIncreasePercent { get; set; }

		public int? captureMode { get; set; }
	}

	private static HttpListener? _listener;

	private static CancellationTokenSource? _cts;

	private static Task? _loop;

	private static int _port;

	public const int WebDirectReservedSpan = 61;

	private const string AuthCookie = "soullink_sid";

	private static readonly object _sessionsGate = new object();

	private static DateTime _lastLiveRequest = DateTime.MinValue;

	private static readonly TimeSpan LiveIdleTimeout = TimeSpan.FromSeconds(10L);

	public static int RunningPort
	{
		get
		{
			HttpListener? listener = _listener;
			if (listener == null || !listener.IsListening)
			{
				return 0;
			}
			return _port;
		}
	}

	public static bool LoopbackOnly { get; private set; }

	public static string ReplayRoot
	{
		get
		{
			try
			{
				return Path.Combine(Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? ".", "capture", "录制回放");
			}
			catch
			{
				return Path.Combine(Environment.CurrentDirectory, "capture", "录制回放");
			}
		}
	}

	public static int ResolveInternalPort(int externalPort)
	{
		if (externalPort <= 0 || externalPort > 65535)
		{
			return 0;
		}
		int num = externalPort + 61 + 1;
		for (int i = num; i <= num + 20 && i <= 65535; i++)
		{
			if (IsPortFreeEverywhere(i))
			{
				return i;
			}
		}
		for (int j = externalPort + 1; j < num; j++)
		{
			if (IsPortFreeEverywhere(j))
			{
				return j;
			}
		}
		return 0;
	}

	public static bool IsPortFreeEverywhere(int port)
	{
		if (IsAnywhereListening(port))
		{
			return false;
		}
		return CanBindLoopback(port);
	}

	public static bool CanBindLoopback(int port)
	{
		if (port <= 0 || port > 65535)
		{
			return false;
		}
		try
		{
			HttpListener httpListener = new HttpListener();
			httpListener.Prefixes.Add("http://127.0.0.1:" + port + "/");
			httpListener.Start();
			httpListener.Stop();
			httpListener.Close();
			return true;
		}
		catch
		{
			return false;
		}
	}

	public static bool IsAnywhereListening(int port)
	{
		if (port <= 0)
		{
			return false;
		}
		try
		{
			IPEndPoint[] activeTcpListeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
			for (int i = 0; i < activeTcpListeners.Length; i++)
			{
				if (activeTcpListeners[i].Port == port)
				{
					return true;
				}
			}
		}
		catch
		{
		}
		return false;
	}

	private static bool PasswordEnabled()
	{
		return !string.IsNullOrWhiteSpace(App.Config?.Settings?.WebPassword);
	}

	private static string EnsureAuthToken()
	{
		try
		{
			string text = App.Config?.Settings?.WebAuthToken ?? "";
			if (!string.IsNullOrWhiteSpace(text))
			{
				return text;
			}
			text = Guid.NewGuid().ToString("N");
			if (App.Config?.Settings != null)
			{
				App.Config.Settings.WebAuthToken = text;
				try
				{
					ConfigStore.Save(App.Config);
				}
				catch
				{
				}
			}
			return text;
		}
		catch
		{
			return Guid.NewGuid().ToString("N");
		}
	}

	private static bool IsAuthorized(HttpListenerContext ctx)
	{
		if (IsLoopbackRequest(ctx))
		{
			return true;
		}
		try
		{
			Cookie cookie = ctx.Request.Cookies["soullink_sid"];
			if (cookie == null || string.IsNullOrEmpty(cookie.Value))
			{
				return false;
			}
			string text = (PasswordEnabled() ? EnsureAuthToken() : "");
			if (text.Length == 0)
			{
				return false;
			}
			return string.Equals(cookie.Value, text, StringComparison.Ordinal);
		}
		catch
		{
			return false;
		}
	}

	private static bool IsLoopbackRequest(HttpListenerContext ctx)
	{
		try
		{
			IPAddress iPAddress = ctx.Request.RemoteEndPoint?.Address;
			if (iPAddress == null)
			{
				return false;
			}
			if (IPAddress.IsLoopback(iPAddress))
			{
				return true;
			}
			if (iPAddress.IsIPv4MappedToIPv6)
			{
				return IPAddress.IsLoopback(iPAddress.MapToIPv4());
			}
			return false;
		}
		catch
		{
			return false;
		}
	}

	private static void IssueSession(HttpListenerContext ctx)
	{
		try
		{
			string text = EnsureAuthToken();
			DateTime dateTime = DateTime.UtcNow.AddYears(1);
			ctx.Response.Headers["Set-Cookie"] = "soullink_sid=" + text + "; Path=/; Expires=" + dateTime.ToString("R") + "; Max-Age=31536000; SameSite=Lax";
		}
		catch
		{
		}
	}

	public static void EnsureLiveRefresh()
	{
	}

	public static void MarkLiveActivity()
	{
		_lastLiveRequest = DateTime.UtcNow;
	}

	public static bool IsLiveActive()
	{
		return DateTime.UtcNow - _lastLiveRequest < LiveIdleTimeout;
	}

	public static void Apply(int port)
	{
		try
		{
			Stop();
			if (port <= 0)
			{
				return;
			}
			if (port < 1024 || port > 65535)
			{
				LogBus.Warn("网页端口无效", "端口需在 1024~65535，当前 " + port);
				return;
			}
			HttpListener l = new HttpListener();
			l.Prefixes.Add("http://127.0.0.1:" + port + "/");
			try
			{
				l.Start();
			}
			catch (Exception ex)
			{
				l.Close();
				LogBus.Error("网页服务启动失败", "回环端口 " + port + "：" + ex.Message);
				return;
			}
			_listener = l;
			_port = port;
			LoopbackOnly = true;
			_cts = new CancellationTokenSource();
			CancellationToken token = _cts.Token;
			_loop = Task.Run(() => LoopAsync(l, token), token);
			LogBus.Ok("网页服务已启动（回环）", "http://127.0.0.1:" + port + "/");
		}
		catch (Exception ex2)
		{
			LogBus.Error("网页服务异常", ex2.Message);
		}
	}

	public static void Stop()
	{
		try
		{
			_cts?.Cancel();
		}
		catch
		{
		}
		try
		{
			_listener?.Stop();
		}
		catch
		{
		}
		try
		{
			_listener?.Close();
		}
		catch
		{
		}
		_listener = null;
		_cts = null;
		_loop = null;
		_port = 0;
		LoopbackOnly = false;
	}

	public static string LocalIp()
	{
		List<(int, string)> list = new List<(int, string)>();
		try
		{
			NetworkInterface[] allNetworkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
			foreach (NetworkInterface networkInterface in allNetworkInterfaces)
			{
				if (networkInterface.OperationalStatus != OperationalStatus.Up || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
				{
					continue;
				}
				string text = (networkInterface.Name + " " + networkInterface.Description).ToLowerInvariant();
				if (text.Contains("virtual") || text.Contains("vethernet") || text.Contains("hyper-v") || text.Contains("vmware") || text.Contains("virtualbox") || text.Contains("loopback") || text.Contains("tunnel") || text.Contains("tap") || text.Contains("oray") || text.Contains("gameviewer") || text.Contains("wintun") || text.Contains("clash"))
				{
					continue;
				}
				foreach (UnicastIPAddressInformation unicastAddress in networkInterface.GetIPProperties().UnicastAddresses)
				{
					if (unicastAddress.Address.AddressFamily != AddressFamily.InterNetwork)
					{
						continue;
					}
					string text2 = unicastAddress.Address.ToString();
					if (!text2.StartsWith("169.254.") && !text2.StartsWith("127."))
					{
						int item = 0;
						if (text2.StartsWith("192.168."))
						{
							item = 3;
						}
						else if (text2.StartsWith("10."))
						{
							item = 2;
						}
						else if (text2.StartsWith("172."))
						{
							item = 1;
						}
						list.Add((item, text2));
					}
				}
			}
		}
		catch
		{
		}
		if (list.Count > 0)
		{
			return list.OrderByDescending(((int score, string ip) c) => c.score).First().Item2;
		}
		return "127.0.0.1";
	}

	public static string LanUrl(int port)
	{
		return "http://" + LocalIp() + ":" + port + "/";
	}

	private static async Task LoopAsync(HttpListener listener, CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			HttpListenerContext ctx = null;
			try
			{
				ctx = await listener.GetContextAsync().ConfigureAwait(continueOnCapturedContext: false);
				Task.Run(() => HandleAsync(ctx), ct);
			}
			catch (ObjectDisposedException)
			{
				break;
			}
			catch (HttpListenerException)
			{
				break;
			}
			catch
			{
			}
		}
	}

	private static async Task HandleAsync(HttpListenerContext ctx)
	{
		_ = 36;
		try
		{
			string text = (ctx.Request.Url?.AbsolutePath ?? "/").Trim();
			string text2 = ctx.Request.HttpMethod ?? "GET";
			if (text2.Equals("POST", StringComparison.OrdinalIgnoreCase))
			{
				if (text == "/api/input")
				{
					if (!IsAuthorized(ctx))
					{
						await WriteText(ctx, 401, "未登录");
					}
					else
					{
						await HandleInput(ctx);
					}
				}
				else if (text == "/api/settings")
				{
					if (!IsAuthorized(ctx))
					{
						await WriteText(ctx, 401, "未登录");
					}
					else
					{
						await HandleSettings(ctx);
					}
				}
				else if (text.StartsWith("/api/miyoushe/", StringComparison.OrdinalIgnoreCase))
				{
					if (!IsAuthorized(ctx))
					{
						await WriteText(ctx, 401, "未登录");
					}
					else
					{
						await HandleMiyousheAsync(ctx, text);
					}
				}
				else
				{
					await WriteText(ctx, 404, "404");
				}
				return;
			}
			if (!text2.Equals("GET", StringComparison.OrdinalIgnoreCase) && !text2.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
			{
				await WriteText(ctx, 405, "只支持 GET / POST");
				return;
			}
			if (text == "/api/login")
			{
				string text3 = ctx.Request.QueryString["pw"] ?? "";
				if (!PasswordEnabled())
				{
					await WriteJson(ctx, new
					{
						ok = true
					});
				}
				else if (text3 == App.Config.Settings.WebPassword)
				{
					IssueSession(ctx);
					await WriteJson(ctx, new
					{
						ok = true
					});
				}
				else
				{
					await Task.Delay(300);
					await WriteJson(ctx, new
					{
						ok = false,
						error = "口令错误"
					});
				}
				return;
			}
			if (!IsAuthorized(ctx))
			{
				if (text == "/" || text == "/index.html")
				{
					await WriteHtml(ctx, LoginHtml());
				}
				else
				{
					await WriteText(ctx, 401, "未登录");
				}
				return;
			}
			switch (text)
			{
			case "/":
			case "/index.html":
				await WriteHtml(ctx, LoadIndexHtml());
				return;
			case "/api/list":
				await WriteJson(ctx, ApiList());
				return;
			case "/api/status":
				await WriteJson(ctx, ApiStatus());
				return;
			case "/api/live":
				await WriteLiveFrame(ctx);
				return;
			case "/api/sessions":
				await WriteJson(ctx, ApiSessions());
				return;
			case "/api/bgi/ledger":
				await WriteJson(ctx, ApiBgiLedger(ctx));
				return;
			case "/api/bgi/stats":
				await WriteJson(ctx, ApiBgiStats(ctx));
				return;
			case "/api/bgi/runs":
			{
				HttpListenerContext ctx2 = ctx;
				await WriteJson(ctx2, await ApiBgiRunsAsync(ctx));
				return;
			}
			case "/api/bgi/runs/detail":
			{
				HttpListenerContext ctx2 = ctx;
				await WriteJson(ctx2, await ApiBgiRunDetailAsync(ctx));
				return;
			}
			case "/api/bgi/runs/groups":
			{
				HttpListenerContext ctx2 = ctx;
				await WriteJson(ctx2, await ApiBgiRunGroupsAsync(ctx));
				return;
			}
			case "/api/bgi/picks":
			{
				HttpListenerContext ctx2 = ctx;
				await WriteJson(ctx2, await ApiBgiPicksAsync(ctx));
				return;
			}
			case "/api/bgi/ingest/status":
				await WriteJson(ctx, ApiBgiIngestStatus());
				return;
			case "/api/bgi/ingest/rescan":
				await WriteJson(ctx, ApiBgiIngestRescan(ctx));
				return;
			case "/api/bgi/spans":
			case "/api/bgi/spans/totals":
			case "/api/bgi/day_span":
			{
				HttpListenerContext ctx2 = ctx;
				await WriteJson(ctx2, await ApiBgiSpansAsync(ctx, text));
				return;
			}
			case "/api/settings":
				await HandleSettings(ctx);
				return;
			}
			if (text.StartsWith("/api/miyoushe/", StringComparison.OrdinalIgnoreCase))
			{
				await HandleMiyousheAsync(ctx, text);
			}
			else if (text == "/api/pagever")
			{
				await WriteJson(ctx, new
				{
					ok = true,
					pageVer = PageVersion()
				});
			}
			else if (text.StartsWith("/file/", StringComparison.OrdinalIgnoreCase))
			{
				await WriteFile(ctx, text.Substring(6));
			}
			else
			{
				await WriteText(ctx, 404, "404");
			}
		}
		catch
		{
			try
			{
				ctx.Response.Abort();
			}
			catch
			{
			}
		}
	}

	private static string LoadIndexHtml()
	{
		try
		{
			string text = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
			if (text.Length > 0)
			{
				string path = Path.Combine(text, "web-index.html");
				if (File.Exists(path))
				{
					string text2 = File.ReadAllText(path, Encoding.UTF8);
					if (text2.Length > 100)
					{
						return text2;
					}
				}
			}
		}
		catch
		{
		}
		return "<!DOCTYPE html>\n<html lang=\"zh-CN\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1, viewport-fit=cover\">\n<title>SoulLink</title>\n<style>\n  /* ══════════ 配色系统 ══════════\n     ★ 与应用内主题保持一致（ThemeManager.cs 的颜色）：\n         暗色                              亮色\n       Bg0      #05060c         →        #faf2fc\n       Bg1      #0b0f22         →        #f3ebfc\n       TextPri  #f2f5ff         →        #30254c\n       TextSec  rgba(194,232,255,.71) →  #392b56\n       TextMut  #a5abcd         →        #5c4c78\n       Cyan     #4fe3ff         →        #0087a7\n       Mint     #41f0c0         →        #008869\n       Rose     #ff6b8b         →        #c02753\n\n     实现方式：全部走 CSS 变量，<html data-theme=\"dark|light\"> 一键切换，\n     选择记在 localStorage，刷新后保持。\n  */\n  :root, :root[data-theme=\"dark\"] {\n    --bg0:#05060c; --bg1:#0b0f22;\n    --card:#121a2e; --card2:#0e1526;\n    --card-hover:#182236; --card-active:#1c2740;\n    --border:#25314b; --border-hi:#3b4c6e;\n    --txt:#f2f5ff;\n    --txt2:rgba(194,232,255,.78);\n    --txt3:#a5abcd;\n    --txt4:#7b86a8;\n    --accent:#4fe3ff; --accent-bg:#1a3050; --accent-border:#2f4a72;\n    --violet:#8b6bff;\n    --mint:#41f0c0; --mint-bg:#163328; --mint-border:#1f4d3c;\n    --rose:#ff6b8b; --rose-bg:#341f28; --rose-border:#5a2f3e;\n    --magenta:#ff5fd2; --magenta-bg:#331e3a; --magenta-border:#4d2a58;\n    --shadow:0 3px 18px rgba(0,0,0,.42);\n    --shadow-sm:0 2px 10px rgba(0,0,0,.28);\n    /* 背景装饰 */\n    --nebula1:rgba(139,107,255,.46);\n    --nebula2:rgba(79,227,255,.34);\n    --glow:rgba(139,107,255,.16);\n  }\n  :root[data-theme=\"light\"] {\n    --bg0:#faf2fc; --bg1:#f3ebfc;\n    --card:#ffffff; --card2:#fdf9ff;\n    --card-hover:#f7f0ff; --card-active:#f0e6fc;\n    --border:#e2d6f0; --border-hi:#c9b4e4;\n    --txt:#30254c;\n    --txt2:#392b56;\n    --txt3:#5c4c78;\n    --txt4:#8678a0;\n    --accent:#0087a7; --accent-bg:#e0f4fa; --accent-border:#a8dcea;\n    --violet:#7649c4;\n    --mint:#008869; --mint-bg:#dff7ef; --mint-border:#9ddcca;\n    --rose:#c02753; --rose-bg:#fbe4eb; --rose-border:#efb0c2;\n    --magenta:#c83491; --magenta-bg:#fbe3f3; --magenta-border:#eeabd8;\n    --shadow:0 3px 18px rgba(102,66,139,.13);\n    --shadow-sm:0 2px 10px rgba(102,66,139,.09);\n    /* 背景装饰：亮色下星云改淡紫，星点改深色才看得见 */\n    /* ★ 亮色下必须用更浓的颜色，否则叠在 #faf2fc 上完全看不出\n          （用户实测：.20 透明度肉眼不可见） */\n    --nebula1:rgba(118,73,196,.42);\n    --nebula2:rgba(0,140,175,.32);\n    --glow:rgba(139,107,255,.18);\n  }\n\n  *{box-sizing:border-box;-webkit-tap-highlight-color:transparent;\n    -webkit-user-select:none;user-select:none}\n  /* ══════════ 背景：多层渐变 + 星点，模拟应用内的星空主题 ══════════\n     ★ 用户要求：「你没做网页背景啊」——原来只有纯色 var(--bg0)。\n       现在用 CSS 多层 radial-gradient 叠出：\n         · 左上角紫色星云\n         · 右下角青色星云\n         · 中间微弱光晕\n         · 细密星点（radial-gradient 重复）\n     全部走 CSS，不引入任何图片资源，零体积。\n  */\n  /* ★ html 不能设不透明背景 —— 它会盖住 body 上的渐变。\n     实测：html{background:#05060c} + body{background-image:radial-gradient(...)}\n     结果是只看到纯色，渐变完全不可见。 */\n  html{min-height:100%;background:transparent}\n  body{margin:0;color:var(--txt);\n       font-family:-apple-system,BlinkMacSystemFont,\"Microsoft YaHei\",sans-serif;\n       -webkit-font-smoothing:antialiased;\n       min-height:100vh;\n       /* ★ 背景直接画在 body 上（不要用负 z-index 伪元素 ——\n            那会跑到 html 背后被纯色盖住，实测就是这个原因导致\"看不见背景\"）。\n            用 background-attachment:fixed 让渐变固定在视口，滚动时不跟着走。 */\n       background-color:var(--bg0);\n       /* ★ 星云：柔和的大面积渐变，边缘渐隐到透明 */\n       background-image:\n         radial-gradient(ellipse 80% 60% at 12% -8%, var(--nebula1) 0%, transparent 60%),\n         radial-gradient(ellipse 75% 65% at 100% 105%, var(--nebula2) 0%, transparent 58%),\n         radial-gradient(ellipse 90% 70% at 50% 42%, var(--glow) 0%, transparent 65%);\n       background-attachment:fixed;\n       background-repeat:no-repeat;\n       transition:background-color .25s,color .25s}\n\n\n\n  header{position:sticky;top:0;z-index:30;padding:13px 16px 11px;\n         background:color-mix(in srgb, var(--bg0) 82%, transparent);\n         backdrop-filter:blur(18px);\n         border-bottom:1px solid var(--border);\n         display:flex;align-items:center;gap:11px;\n         transition:background .22s,border-color .22s}\n  .logo{width:30px;height:30px;border-radius:9px;flex:0 0 auto;\n        background:linear-gradient(135deg,var(--violet),var(--accent));\n        display:flex;align-items:center;justify-content:center;\n        font-size:15px;font-weight:700;color:#fff;\n        box-shadow:var(--shadow-sm)}\n  .ttl{font-size:15px;font-weight:650;letter-spacing:.2px;color:var(--txt)}\n  .ver{font-size:10.5px;color:var(--txt4);margin-top:1px}\n  .right{margin-left:auto;display:flex;align-items:center;gap:8px}\n  .pill{font-size:11px;padding:4px 11px;border-radius:20px;\n        background:var(--accent-bg);color:var(--accent);\n        border:1px solid var(--accent-border);font-weight:550}\n  .themebtn{width:32px;height:32px;border-radius:9px;flex:0 0 auto;\n            margin-left:2px;\n            border:1px solid var(--border);background:var(--card);\n            color:var(--txt2);font-size:15px;cursor:pointer;\n            display:flex;align-items:center;justify-content:center;\n            transition:.15s}\n  .themebtn:hover{border-color:var(--border-hi);background:var(--card-hover)}\n  .themebtn:active{transform:scale(.94)}\n\n\n\n  main{padding:14px 14px 40px;max-width:720px;margin:0 auto;background:transparent}\n\n  .stat{background:linear-gradient(150deg,var(--card),var(--card2));\n        border:1px solid var(--border);border-radius:15px;\n        padding:16px 16px 15px;margin-bottom:14px;\n        box-shadow:var(--shadow);transition:.22s}\n  .stat h2{font-size:13px;margin:0 0 3px;color:var(--txt);\n           font-weight:600;letter-spacing:.3px}\n  .stat .desc{font-size:11px;color:var(--txt4);margin-bottom:13px}\n  .metrics{display:flex;gap:9px}\n  .metric{flex:1;background:var(--card2);border:1px solid var(--border);\n          border-radius:11px;padding:11px 8px;text-align:center;transition:.22s}\n  .metric .v{font-size:21px;font-weight:700;color:var(--accent);\n             line-height:1.15;font-variant-numeric:tabular-nums}\n  .metric .k{font-size:10px;color:var(--txt3);margin-top:3px}\n\n  .sect-t{font-size:12.5px;color:var(--txt3);margin:18px 0 10px;\n          font-weight:600;letter-spacing:.3px}\n  .urow{background:linear-gradient(150deg,var(--card),var(--card2));\n        border:1px solid var(--border);border-radius:14px;\n        padding:13px 14px;margin-bottom:10px;\n        display:flex;align-items:flex-start;gap:11px;cursor:pointer;\n        box-shadow:var(--shadow-sm);\n        transition:border-color .15s,background .15s,transform .1s}\n  .urow:hover{border-color:var(--border-hi);background:var(--card-hover)}\n  .urow:active{background:var(--card-active);transform:scale(.995)}\n\n  .udot{width:8px;height:8px;border-radius:50%;flex:0 0 auto;margin-top:6px;\n        background:var(--txt4)}\n  .udot.on{background:var(--mint);box-shadow:0 0 8px var(--mint-border)}\n  .uinfo{flex:1;min-width:0}\n  .un{font-size:13.5px;font-weight:600;display:flex;align-items:center;\n      gap:7px;flex-wrap:wrap;color:var(--txt)}\n  .us{font-size:11px;color:var(--txt3);margin-top:3px}\n  .tag{font-size:9.5px;padding:2px 7px;border-radius:5px;font-weight:550;\n       background:var(--accent-bg);color:var(--accent)}\n  .tag.rec{background:var(--magenta-bg);color:var(--magenta)}\n  .tag.bgi{background:var(--mint-bg);color:var(--mint)}\n  .tag.off{background:var(--rose-bg);color:var(--rose)}\n  .uact{display:flex;gap:6px;flex:0 0 auto}\n\n  .ibtn{padding:8px 14px;border-radius:9px;border:1px solid var(--accent-border);\n        background:var(--accent-bg);color:var(--accent);font-size:11.5px;\n        font-weight:550;cursor:pointer;white-space:nowrap;transition:.15s}\n  .ibtn:hover{border-color:var(--accent);filter:brightness(1.08)}\n  .ibtn:active{transform:scale(.97)}\n  .ibtn.on{background:var(--accent);color:var(--bg0);border-color:var(--accent)}\n\n  /* ══════════ 折叠卡片总览（2026-11-23 网页改版）══════════\n     每账号一张卡片，默认收起，点头部展开功能按钮。 */\n  .ucard{background:linear-gradient(150deg,var(--card),var(--card2));\n         border:1px solid var(--border);border-radius:14px;margin-bottom:10px;\n         box-shadow:var(--shadow-sm);overflow:hidden}\n  .ucard-head{display:flex;align-items:center;gap:10px;padding:12px 14px;cursor:pointer;\n              user-select:none;transition:background .15s}\n  .ucard-head:hover{background:var(--card-hover)}\n  .ucard-head .chev{margin-left:auto;font-size:10px;color:var(--txt3);\n                    transition:transform .18s;display:inline-block;flex:0 0 auto}\n  .ucard.open .ucard-head .chev{transform:rotate(90deg)}\n  /* ★ 2026-11-24（T30）：展开/折叠用可过渡属性，不再 display 硬切换。\n     display:none→block 无法过渡（属性本身不可动画），悬停展开会「啪」一下很突兀。\n     改用 grid-template-rows 0fr→1fr（现代做法）：\n       · 不需要预先知道内容高度（max-height 方案要写死一个「够大」的值，容易夹断或留白）；\n       · 内层 .ucard-body-inner 设 min-height:0，让 grid 行能真正收缩到 0。\n     时长 180ms + ease：足够看清展开方向，又不会让悬停响应变「拖」。 */\n  .ucard-body{display:grid;grid-template-rows:0fr;opacity:0;\n              transition:grid-template-rows .18s ease,opacity .18s ease,padding .18s ease;\n              padding:0 14px 0;border-top:1px solid transparent;overflow:hidden}\n  .ucard-body-inner{min-height:0;overflow:hidden}\n  .ucard.open .ucard-body{grid-template-rows:1fr;opacity:1;padding:0 14px 13px;\n                          border-top-color:var(--border)}\n  .ucard-actions{display:flex;gap:8px;flex-wrap:wrap;margin-top:11px}\n  .uact-btn{padding:9px 14px;border-radius:9px;border:1px solid var(--accent-border);\n            background:var(--accent-bg);color:var(--accent);font-size:12px;\n            font-weight:550;cursor:pointer;transition:.15s}\n  .uact-btn:hover{border-color:var(--accent);filter:brightness(1.08)}\n  .uact-btn:active{transform:scale(.97)}\n\n  /* ══════════ 直播画质设置（滑杆/下拉，2026-11-23 手机端原型）══════════ */\n  .qgroup{display:flex;align-items:center;gap:8px;flex:0 0 auto}\n  .qgroup>span{font-size:10px;color:var(--txt3);white-space:nowrap}\n  .qgroup input[type=range]{width:76px;height:20px;cursor:pointer;accent-color:var(--accent)}\n  .qgroup .qval{font-size:10.5px;color:var(--txt2);min-width:40px;text-align:right;\n                 font-variant-numeric:tabular-nums}\n  .qguide{font-size:10px;color:var(--txt4);line-height:1.6;margin-top:6px}\n  .capgroup{display:flex;align-items:center;gap:8px;flex:0 0 auto}\n  .capgroup>span{font-size:10px;color:var(--txt3);white-space:nowrap}\n  .capbackend{font-size:10.5px;color:var(--mint);font-weight:600;white-space:nowrap;\n              font-variant-numeric:tabular-nums}\n  .select-mini{height:26px;line-height:1;padding:0 16px 0 6px;font-size:11px;\n                color:var(--txt);background:var(--card);border:1px solid var(--border);\n                border-radius:8px;cursor:pointer}\n\n  /* ══════════ 功能页（hash 路由：运行分析 / 摩拉收益）══════════ */\n  body.page-func main>.stat,\n  body.page-func main>.sect-t,\n  body.page-func main>#users,\n  body.page-func main>#empty,\n  body.page-func main>#viewer,\n  body.page-func main>#saved{display:none !important}\n  .page-back{display:inline-flex;align-items:center;gap:5px;padding:7px 12px;\n             border-radius:8px;border:1px solid var(--border);background:var(--card2);\n             color:var(--txt2);font-size:12px;cursor:pointer;margin-bottom:12px}\n  .page-back:hover{border-color:var(--border-hi);color:var(--txt)}\n  .userswitch{display:flex;gap:6px;flex-wrap:wrap;margin:2px 0 14px}\n  .userswitch .uchip{padding:6px 12px;border-radius:20px;border:1px solid var(--border);\n                      background:var(--card2);color:var(--txt3);font-size:11.5px;\n                      cursor:pointer;transition:.15s;font-weight:550}\n  .userswitch .uchip:hover{border-color:var(--border-hi);color:var(--txt2)}\n  .userswitch .uchip.on{background:var(--accent);color:#fff;border-color:var(--accent)}\n  .ldtable{width:100%;border-collapse:collapse;font-size:11.5px}\n  .ldtable th{text-align:left;color:var(--txt4);font-weight:600;padding:7px 8px;\n              border-bottom:1px solid var(--border);white-space:nowrap}\n  .ldtable td{padding:7px 8px;border-bottom:1px solid var(--border);color:var(--txt2)}\n  .ldtable tr:last-child td{border-bottom:none}\n  .badge-fail{color:var(--rose)} .badge-ok{color:var(--mint)}\n  .num{font-variant-numeric:tabular-nums}\n  .mtxt{font-size:13px;color:var(--txt2)}\n  .pageempty{padding:18px 14px;color:var(--txt4);font-size:12px}\n\n  .detail{margin-top:10px;padding-top:10px;border-top:1px solid var(--border)}\n  .dline{display:flex;gap:10px;font-size:11.5px;line-height:1.85}\n  .dk{color:var(--txt4);flex:0 0 56px;font-size:11px}\n  .dv{color:var(--txt2);flex:1;word-break:break-all}\n\n  #viewer{display:none;margin-top:14px}\n\n/* ── 独立窗口模式（?view=账号Id）───────────────────────────\n   用户要求「查看画面要打开一个新页面」：新标签页只显示画面，\n   把总览、账号列表、录像列表全部隐藏，画面占满可视区。 */\nbody.viewonly main>.stat,\nbody.viewonly main>.sect-t,\nbody.viewonly main>#users,\nbody.viewonly main>#empty,\nbody.viewonly #viewer .sect-t,\nbody.viewonly #saved,\nbody.viewonly #pback{display:none !important}\n/* 让画面尽可能大：去掉外层留白和卡片内边距，并垂直居中 */\n/* ★ 整个页面**不滚动**：header 固定高度，剩下全给画面。\n   之前留了滚动条是因为 main 的 min-height + 默认 padding 叠加超出视口。 */\nhtml.viewonly-root,body.viewonly{overflow:hidden;height:100%;margin:0;padding:0}\nbody.viewonly{display:flex;flex-direction:column;height:100vh}\nbody.viewonly main{flex:1 1 auto;min-height:0;max-width:100%;padding:0;margin:0;\n                   display:flex;align-items:center;justify-content:center;overflow:hidden}\nbody.viewonly #viewer{display:flex !important;margin:0;padding:0;width:100%;height:100%;\n                      align-items:center;justify-content:center}\nbody.viewonly #viewerBox{padding:0;margin:0;width:100%;height:100%;display:flex;\n                         align-items:center;justify-content:center}\nbody.viewonly #viewerTitle{display:none}\nbody.viewonly header{padding:6px 12px}\nbody.viewonly header .logo{width:26px;height:26px;font-size:14px}\nbody.viewonly header .ttl{font-size:13px}\nbody.viewonly header .ver{display:none}\nbody.viewonly #liveCount{display:none}\n/* 画面：宽度撑满，高度由图片比例决定（16:9 就正好是 16:9）。\n   ★ 不要给 #scr 固定 height —— 那会让图片在容器里居中缩成小块，\n     两侧露出 #scr 的黑色背景，看起来就是\"画面很小 + 黑边\"。\n   ★ 也不要 height:100%：同理。容器必须由图片撑开。 */\n/* ★ 画面按\"能装进可视区\"缩放：宽和高都不超过，比例不变。\n   这样无论窗口是宽扁还是竖长，画面都是最大的那一个，\n   多余空间均匀分布在四周（居中），而不是全堆在底部。 */\nbody.viewonly #scr{height:auto;background:transparent;width:100%;\n                   min-height:160px;   /* ★ 首帧到达前也保持可视高度，避免 #scr 塌陷成空白 */\n                   display:flex;align-items:center;justify-content:center}\n/* 画面按可用区域缩放：宽高都不越界，比例不变，四周均匀留白 */\nbody.viewonly #scr canvas{width:auto;height:auto;max-width:100%;\n                          max-height:calc(100vh - 46px);object-fit:contain;\n                          margin:0 auto;display:none}\nbody.viewonly #scr canvas.show{display:block}\nbody.viewonly #viewerBox{background:transparent;box-shadow:none;border:none}\n  #viewerBox{background:linear-gradient(150deg,var(--card),var(--card2));\n             border:1px solid var(--border);border-radius:14px;\n             padding:13px;box-shadow:var(--shadow);transition:.22s}\n  #viewerTitle{font-size:13px;font-weight:600;color:var(--txt);\n               margin-bottom:11px;display:flex;align-items:center;gap:8px}\n  /* 容器宽度决定画面宽度；高度交给图片自己撑（见 img 规则）。\n     背景用透明而不是纯黑 —— 纯黑在画面比容器小时会露出黑边。 */\n  #scr{position:relative;background:transparent;border-radius:11px;overflow:hidden;\n       border:1px solid var(--border);touch-action:none;\n       min-height:220px;   /* ★ canvas 未出首帧时保持容器高度，连接中提示可见，不塌陷空白 */\n       /* ★ 2026-11-24：cursor:none —— 画面区不显示本机系统光标（原 cursor:default）。\n          远端光标已画进视频帧（WGC IsCursorCaptureEnabled=true，方案见\n          AUDIT-20261124-03-CURSOR-UX.md §4.1），再显示本机光标会双影\n          （本机零延迟 vs 帧内 444ms，视觉极糟）。rcCursor 是远端位置的\n          低延迟回显（输入 RTT 级），不是本机影子，与 cursor:none 不冲突。 */\n       cursor:none}\n  #scr *{cursor:none}          /* 画布/涟漪/提示层一律无本机光标 */\n  #scr button{cursor:pointer}  /* 可点按钮（如重新连接）恢复指针 */\n  /* ★ 宽度撑满、高度按比例自适应，并封顶在可视区内。\n     历史教训：写成 height:100% 会让图在容器里居中缩小 → 画面变小 + 黑边。 */\n  /* ★ 2026-11-23：JPEG 轮询通路按用户指令整个移除（「很卡很卡」），\n     画面只有 <canvas id=\"vframe\">（WebCodecs 解码直绘）。\n     默认隐藏，JS 在首帧解码成功后加 .show（viewonly 的 display 规则\n     优先级高于这里，见上面的 body.viewonly 块）。 */\n  #scr canvas{display:none;width:100%;height:auto;max-height:70vh;\n              object-fit:contain;pointer-events:none}\n  #scr canvas.show{display:block}\n  /* 占位提示层里的文字不可点（不挡 #scr 的手势），但重连按钮要可点：\n     文字层 pointer-events:none，按钮单独恢复 auto。 */\n  #ph{position:absolute;inset:0;display:flex;align-items:center;\n      justify-content:center;color:#8b9bc4;font-size:12.5px;\n      padding:20px;text-align:center;pointer-events:none;flex-direction:column;\n      gap:10px;line-height:1.7}\n  #ph #reconn{pointer-events:auto}\n  #rip{position:absolute;width:46px;height:46px;margin:-23px 0 0 -23px;\n       border-radius:50%;border:2px solid var(--accent);opacity:0;pointer-events:none}\n  #rip.go{animation:rp .5s ease-out}\n  /* ══════════ 远端光标回显（2026-11-24，替代已删除的 #plCursor 蓝球）══════════\n     蓝球（本机鼠标影子）已删：它不反映远端光标，信息量为零（见\n     AUDIT-20261124-03-CURSOR-UX.md）。成熟产品的正解是「远端光标状态回传 +\n     客户端合成绘制」：服务端把实际光标位置（GetCursorPos 归一化）随输入响应\n     回传 cursor:{nx,ny,visible}，网页画一个自绘十字光标（对齐远端 hotspot，\n     不是圆球），延迟 ≈ 输入 RTT 而非画面 444ms。\n     - 几何与 norm() 同源（canvas 实际矩形 + object-fit 黑边），见 frameRect()；\n     - pointer-events:none 不挡点击；z-index 与旧蓝球一致；\n     - 白色描边 + 半透明黑底，在亮/暗画面上都可见。 */\n  #rcCursor{position:absolute;left:0;top:0;width:0;height:0;\n            pointer-events:none;display:none;z-index:12;will-change:transform}\n  #rcCursor .rc-dot{position:absolute;left:-7px;top:-7px;width:14px;height:14px}\n  #rcCursor .rc-dot::before,#rcCursor .rc-dot::after{content:'';position:absolute;\n            background:#fff;box-shadow:0 0 3px rgba(0,0,0,.85);border-radius:1px}\n  #rcCursor .rc-dot::before{left:6px;top:0;width:2px;height:14px}   /* 竖线 */\n  #rcCursor .rc-dot::after{left:0;top:6px;width:14px;height:2px}    /* 横线 */\n  #rcCursor .rc-dot .rc-ring{position:absolute;inset:-4px;border:1.5px solid #fff;\n            border-radius:50%;opacity:.55;box-shadow:0 0 3px rgba(0,0,0,.7)}\n  @keyframes rp{0%{opacity:.95;transform:scale(.3)}100%{opacity:0;transform:scale(1.6)}}\n\n  .row{display:flex;align-items:center;justify-content:space-between;\n       margin-top:11px;gap:10px;flex-wrap:wrap}\n  .row label{font-size:11.5px;color:var(--txt3);flex:0 0 auto;font-weight:500}\n  .seg{display:flex;gap:6px;flex-wrap:wrap}\n  .seg button{padding:6px 12px;border-radius:8px;border:1px solid var(--border);\n              background:var(--card2);color:var(--txt3);font-size:11px;\n              font-weight:500;cursor:pointer;transition:.15s}\n  .seg button:hover{border-color:var(--border-hi);color:var(--txt2)}\n  .seg button.on{background:var(--accent-bg);color:var(--accent);\n                 border-color:var(--accent-border)}\n\n  /* 顶栏里的画面控制（viewonly 时才显示） */\n.hdrinfo{font-size:10.5px;color:var(--txt3);margin-right:4px}\n\n/* ★ 用 flex + 统一间距，保证垂直居中对齐（之前按钮高低不齐就是缺这个） */\n.hdrctrl{display:flex;align-items:center;gap:10px;flex-wrap:nowrap}\n  /* ══════════ 顶栏「设置」弹出菜单（2026-11-23）══════════\n     默认只显示「设置」按钮；点开浮出面板，面板内放抓屏/鼠标/直播画质/录制参数。\n     点面板外/ESC 自动收起（JS 已有）。 */\n  .settingswrap{position:relative;flex:0 0 auto}\n  .settingsbtn{height:22px;padding:0 12px;font-size:10.5px;border-radius:8px;\n               border:1px solid var(--border);background:var(--card);color:var(--txt2);\n               cursor:pointer;white-space:nowrap}\n  .settingsbtn:hover{border-color:var(--accent);color:var(--accent)}\n  .settingsbtn.on{background:var(--accent);color:#fff;border-color:var(--accent)}\n  /* ★ 弹出面板定位在「设置」按钮正下方（right:0 贴按钮右缘），\n      不飘到页面角落；z-index 高于 header，有背景/边框/阴影。 */\n  .settingsmenu{position:absolute;top:30px;right:0;z-index:120;min-width:320px;\n                max-width:min(90vw,420px);max-height:70vh;overflow:auto;\n                background:var(--card);border:1px solid var(--border-hi);\n                border-radius:12px;box-shadow:var(--shadow);padding:10px 12px;\n                display:none}\n  .settingsmenu.open{display:block}\n  .settingsmenu .mgroup{padding:8px 0;border-top:1px solid var(--border)}\n  .settingsmenu .mgroup:first-child{border-top:none;padding-top:0}\n  .settingsmenu .mtitle{font-size:10px;color:var(--txt4);margin-bottom:6px;\n                         font-weight:600;letter-spacing:.3px}\n  .settingsmenu .hfield,.settingsmenu .qgroup{margin-bottom:6px;display:flex}\n\n  /* ══════════ 顶栏收敛（2026-11-23：抓屏/鼠标/直播画质折进紧凑单行）══════════\n     ★ 原来 hgroup 各自 padding 3px 8px + gap 6px，三组叠加顶栏过高。\n       这里收紧：hdrctrl 允许换行但不撑高、hgroup 降 padding、gtag 更小、\n       select/range 降高度，qguide 改单行省略。 */\n  .hdrctrl{flex-wrap:wrap;row-gap:4px}\n  .hgroup{padding:1px 6px;gap:4px;border-radius:7px}\n  .gtag{font-size:9px}\n  .hfield>span,.qgroup>span{font-size:9.5px}\n  .hfield select,.select-mini{height:22px;padding:0 14px 0 5px;font-size:10.5px;\n      background-position:calc(100% - 9px) 9px, calc(100% - 5px) 9px;background-size:3px 3px,3px 3px}\n  .qgroup{height:22px}\n  .qgroup input[type=range]{width:64px;height:16px}\n  .qgroup .qval{font-size:9.5px;min-width:32px}\n  .qguide{font-size:9px;line-height:1.4;margin-top:2px;white-space:nowrap;\n           overflow:hidden;text-overflow:ellipsis;max-width:100%}\n  .hbtn{height:22px;padding:0 10px;font-size:10.5px}\n  .capbackend{font-size:10px}\n  .hdrinfo{font-size:9.5px}\n  #kbBadge{font-size:9.5px}\n  .pill{font-size:10px;padding:3px 9px}\n  header{padding:8px 12px 7px;gap:8px}\n\n/* ★ 两组设置各自成块，用底色+边框区分，避免\"帧率\"到底指哪个的歧义。\n     data-kind 只用于配色，不参与逻辑。 */\n.hgroup{display:flex;align-items:center;gap:6px;padding:3px 8px;\n        border-radius:9px;border:1px solid var(--border);\n        background:color-mix(in srgb, var(--card) 65%, transparent)}\n.hgroup[data-kind=\"record\"]{--gtag:var(--accent)}\n.hgroup[data-kind=\"view\"]{--gtag:#7dd3a0}\n.gtag{font-size:9.5px;font-weight:600;letter-spacing:.4px;\n      color:var(--gtag);white-space:nowrap;padding-right:2px;\n      border-right:1px solid var(--border);margin-right:2px}\n\n/* 每一项 = 小标签 + 下拉，标签在上方不占横向空间 */\n.hfield{display:flex;align-items:center;gap:4px;flex:0 0 auto}\n.hfield>span{font-size:10px;color:var(--txt3);white-space:nowrap;letter-spacing:.2px}\n\n/* 原生 select 统一外观：和按钮等高（26px）、同圆角、同配色 */\n.hfield select{\n  height:26px;line-height:1;padding:0 22px 0 8px;font-size:11.5px;\n  color:var(--txt);background:var(--card);border:1px solid var(--border);\n  border-radius:8px;cursor:pointer;white-space:nowrap;\n  /* 右侧留出原生箭头的空间，避免文字被压 */\n  appearance:none;-webkit-appearance:none;-moz-appearance:none;\n  background-image:linear-gradient(45deg,transparent 50%,currentColor 50%),\n                   linear-gradient(135deg,currentColor 50%,transparent 50%);\n  background-position:calc(100% - 13px) 11px, calc(100% - 9px) 11px;\n  background-size:4px 4px, 4px 4px;background-repeat:no-repeat;\n}\n.hfield select:hover{border-color:var(--accent)}\n.hfield select:focus{outline:none;border-color:var(--accent)}\n\n.hbtn{height:26px;padding:0 12px;font-size:11.5px;border-radius:8px;\n      border:1px solid var(--border);background:var(--card);color:var(--txt);\n      cursor:pointer;white-space:nowrap;display:inline-flex;\n      align-items:center;justify-content:center}\n.hbtn:hover{border-color:var(--accent)}\n.hbtn.on{background:var(--accent);color:#fff;border-color:var(--accent)}\n\n/* 可折叠的区块标题（「录像回放」用）：默认收起，点了才取数据 */\n.sect-toggle{cursor:pointer;user-select:none;display:flex;align-items:center;gap:6px}\n.sect-toggle:hover{color:var(--accent)}\n.sect-toggle .chev{transition:transform .18s;display:inline-block;font-size:9px}\n.sect-toggle.open .chev{transform:rotate(90deg)}\nbody.viewonly header{flex-wrap:nowrap;gap:8px}\nbody.viewonly .right{gap:6px;flex-wrap:nowrap}\n/* 键盘状态用小圆点+文字提示，不再占一整行 */\n#kbBadge{display:none;font-size:10.5px;color:var(--accent);white-space:nowrap}\n#kbTip{display:none;margin-top:10px;padding:10px 12px;border-radius:9px;\n         background:var(--accent-bg);border:1px solid var(--accent-border);\n         color:var(--accent);font-size:11.5px;line-height:1.6}\n\n  .item{background:var(--card2);border:1px solid var(--border);border-radius:10px;\n        padding:11px 12px;margin-bottom:8px;cursor:pointer;\n        display:flex;align-items:center;gap:10px;transition:.15s}\n  .item:hover{border-color:var(--border-hi);background:var(--card-hover)}\n  .inm{font-size:12.5px;color:var(--txt);font-weight:500}\n  .imt{font-size:10.5px;color:var(--txt4);margin-top:3px}\n  .badge{padding:2px 7px;border-radius:5px;font-size:9.5px;\n         background:var(--accent-bg);color:var(--accent);margin-left:6px;font-weight:550}\n\n  #player{display:none;margin-top:13px}\n  #vid{width:100%;display:block;background:#000;border-radius:11px;\n       border:1px solid var(--border)}\n  .tools{display:flex;gap:8px;margin-top:10px}\n  .tools button{flex:1;padding:10px;border-radius:9px;border:1px solid var(--border);\n                background:var(--card2);color:var(--txt2);font-size:11.5px;\n                font-weight:500;cursor:pointer;transition:.15s}\n  .tools button:hover{border-color:var(--border-hi);color:var(--txt)}\n  .tools button:active{background:var(--card-active)}\n\n  .empty{color:var(--txt4);font-size:12.5px;text-align:center;padding:40px 20px;\n         border:1px dashed var(--border);border-radius:13px;line-height:1.7}\n\n  /* ══════════ 星空 ══════════\n     ★ 用户反馈：「不是太密，是矩阵，很难看，圆点，没有星星的感觉」\n       原来用 radial-gradient + repeat 平铺 = 规则网格 = 噪点观感。\n\n     ★ 现在用 box-shadow 画星：\n         · 1 个 1px 的圆点元素，用 100+ 个 box-shadow 在**随机坐标**画星\n         · 分三层：远星(80颗/暗/小) 中星(45颗/中) 近星(18颗/亮/带光晕)\n         · 坐标是伪随机生成的，不成行列、不重复\n         · 纯 CSS，零图片体积\n  */\n  /* ★ 关键：body 必须建立层叠上下文（isolation），\n        否则 z-index:0 的固定元素会和 body 背景同层，\n        被 background-color 盖住 —— 这就是\"星点看不见\"的真正原因。 */\n  /* 内容层（背景渐变已画在 body 上，无需额外层级处理） */\n  video,header,main,.stat,.urow{position:relative}\n\n\n/* ══ 运行分析（t13 / 3.3.47）══ */\n.ra-toolbar{display:flex;flex-wrap:wrap;gap:8px 12px;align-items:flex-end;\n  padding:10px 12px;background:var(--card2);border:1px solid var(--border);\n  border-radius:8px;margin:12px 0 10px}\n.ra-f{display:flex;flex-direction:column;gap:3px;font-size:11px;color:var(--txt3)}\n.ra-f>span{font-size:10.5px;opacity:.85}\n.ra-f select,.ra-f input{background:var(--card);color:var(--txt);\n  border:1px solid var(--border);border-radius:5px;padding:3px 6px;font-size:11.5px;\n  font-family:inherit;min-width:86px;outline:none}\n.ra-f select:focus,.ra-f input:focus{border-color:var(--accent)}\n.ra-f-kw input{min-width:190px}\n.ra-toolbar .uact-btn{margin-left:auto}\n.ra-filterbar{font-size:11px;color:var(--accent);margin:-4px 0 10px;padding-left:2px}\n.ra-metrics{flex-wrap:wrap}\n.ra-metrics .metric{min-width:112px}\n.ra-sec{margin-bottom:10px}\n.ra-scroll{overflow-x:auto;-webkit-overflow-scrolling:touch}\n.ra-table{min-width:640px}\n.ra-table th{white-space:nowrap}\n.ra-mono{font-variant-numeric:tabular-nums;font-size:11px;white-space:nowrap}\n.ra-name{max-width:280px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}\n.ra-pickd{max-width:320px;font-size:11px}\n.ra-pickitems{line-height:1.6}\n.ra-runrow{cursor:pointer}\n.ra-runrow:hover{background:var(--card2)}\n.ra-runrow.ra-open{background:var(--card2)}\n.ra-exp{color:var(--txt4);font-size:10px;text-align:center;user-select:none}\n.ra-detailrow>td{background:var(--card2);padding:0!important}\n.ra-detail{padding:12px 14px;border-left:2px solid var(--accent)}\n.ra-detail-loading{padding:14px;color:var(--txt4);font-size:11.5px}\n.ra-dsum{display:flex;flex-wrap:wrap;gap:14px;font-size:11.5px;color:var(--txt2);\n  padding-bottom:8px;margin-bottom:8px;border-bottom:1px solid var(--border)}\n.ra-sub{font-size:11.5px;color:var(--txt3);margin:12px 0 6px;font-weight:600}\n.ra-badge{display:inline-block;padding:1px 7px;border-radius:10px;font-size:10.5px;\n  line-height:1.7;white-space:nowrap;background:var(--card);border:1px solid var(--border)}\n.ra-badge.badge-ok{color:var(--mint);border-color:var(--mint)}\n.ra-badge.badge-fail{color:var(--rose);border-color:var(--rose)}\n.ra-badge.badge-warn{color:#e0a33e;border-color:#e0a33e}\n.ra-badge.badge-run{color:var(--accent);border-color:var(--accent)}\n.ra-badge.badge-js{color:#8fb8ff;border-color:#8fb8ff}\n.ra-badge.badge-path{color:#9fd89f;border-color:#9fd89f}\n.ra-err{margin:6px 0;border:1px solid var(--border);border-radius:6px;overflow:hidden}\n.ra-err>summary{cursor:pointer;padding:7px 10px;font-size:11.5px;\n  display:flex;gap:8px;align-items:baseline;flex-wrap:wrap;background:var(--card)}\n.ra-err>summary:hover{background:var(--card2)}\n.ra-errmsg{flex:1;min-width:200px;color:var(--txt2);overflow:hidden;\n  text-overflow:ellipsis;white-space:nowrap}\n.ra-errmeta{font-size:10.5px;color:var(--txt4);white-space:nowrap}\n.ra-stack{margin:0;padding:10px 12px;background:var(--bg);color:var(--txt2);\n  font-size:11px;line-height:1.55;overflow-x:auto;max-height:340px;\n  overflow-y:auto;white-space:pre;font-family:Consolas,Menlo,monospace}\n.ra-pager{display:flex;gap:10px;align-items:center;justify-content:center;\n  padding:10px 0 2px;font-size:11.5px}\n.ra-pg{background:var(--card2);color:var(--txt2);border:1px solid var(--border);\n  border-radius:5px;padding:4px 11px;cursor:pointer;font-size:11.5px;font-family:inherit}\n.ra-pg:hover:not(:disabled){border-color:var(--accent);color:var(--accent)}\n.ra-pg:disabled{opacity:.35;cursor:not-allowed}\n.ra-pg-info{color:var(--txt4);font-size:11px}\n.ra-pickcounts{display:flex;gap:8px;flex-wrap:wrap;margin-bottom:4px}\n.ra-pc{padding:5px 12px;border-radius:6px;font-size:11.5px;\n  background:var(--card2);border:1px solid var(--border)}\n.ra-pc b{font-size:15px;margin-right:4px;font-variant-numeric:tabular-nums}\n.ra-pc-picked b{color:var(--mint)}\n.ra-pc-notarget b{color:#e0a33e}\n.ra-pc-skipped b{color:var(--rose)}\n.ra-source{margin-top:10px;padding:8px 11px;font-size:11px;line-height:1.7;\n  color:var(--txt3);background:var(--card2);border-left:2px solid var(--accent);\n  border-radius:0 5px 5px 0}\n.ra-skel{display:flex;flex-direction:column;gap:8px;padding:12px 0}\n.ra-skel-row{height:34px;border-radius:6px;background:var(--card2);\n  animation:raPulse 1.3s ease-in-out infinite}\n.ra-skel-row:nth-child(2){animation-delay:.16s}\n.ra-skel-row:nth-child(3){animation-delay:.32s}\n@keyframes raPulse{0%,100%{opacity:.45}50%{opacity:.85}}\n@media(max-width:620px){\n  .ra-toolbar{flex-direction:column;align-items:stretch}\n  .ra-toolbar .uact-btn{margin-left:0}\n  .ra-f-kw input{min-width:0;width:100%}\n  .ra-metrics .metric{min-width:calc(50% - 5px)}\n}\n</style>\n</head>\n<body>\n\n<header>\n  <div class=\"logo\">S</div>\n  <div>\n    <div class=\"ttl\">SoulLink</div>\n    <div class=\"ver\" id=\"ver\"></div>\n  </div>\n  <div class=\"right\">\n    <!-- ★ 3.3.8：实时画面的操作全部移到顶栏。\n         原来它们在画面下方单独占一整行，白吃掉可视高度（用户明确要求）。 -->\n    <span id=\"kbBadge\">⌨ 键盘已开</span>\n    <span id=\"fpsinfo\" class=\"hdrinfo\"></span>\n    <span class=\"pill\" id=\"liveCount\">— 在线</span>\n        <div class=\"hdrctrl\" id=\"viewctrls\" style=\"display:none\">\n      <button id=\"pauseBtn\" class=\"hbtn\">暂停</button>\n      <button id=\"closeViewer\" class=\"hbtn\">收起</button>\n      <div class=\"settingswrap\">\n        <button id=\"settingsBtn\" class=\"settingsbtn\" type=\"button\">设置</button>\n        <div class=\"settingsmenu\" id=\"settingsMenu\">\n          <div class=\"mgroup\">\n            <div class=\"mtitle\">抓屏方式（配置 vs 实际）</div>\n            <select id=\"capMode\" class=\"hfield select-mini\">\n              <option value=\"0\">自动(DDA→WGC→BitBlt)</option>\n              <option value=\"1\">DXGI DDA</option>\n              <option value=\"2\">WGC</option>\n              <option value=\"3\">BitBlt</option>\n            </select>\n            <span class=\"capbackend\" id=\"capBackend\">—</span>\n          </div>\n          <div class=\"mgroup\">\n            <div class=\"mtitle\">鼠标模式</div>\n            <div class=\"seg\" id=\"mouseModeSeg\">\n              <button id=\"mmAbs\" class=\"on\" type=\"button\">绝对</button>\n              <button id=\"mmRel\" type=\"button\">相对</button>\n            </div>\n          </div>\n          <div class=\"mgroup\">\n            <div class=\"mtitle\">鼠标锁定（桌面转视角）</div>\n            <label class=\"hfield\"><span>逃生门</span>\n              <select id=\"plEscape\" class=\"hfield select-mini\">\n                <option value=\"ctrl_alt_shift\">Ctrl+Alt+Shift</option>\n                <option value=\"ctrl_alt\">Ctrl+Alt</option>\n                <option value=\"alt\">单纯 Alt</option>\n                <option value=\"alt_hold\">长按 Alt</option>\n              </select>\n            </label>\n            <label class=\"qgroup\"><span>灵敏度</span><input id=\"plSens\" type=\"range\" min=\"0.1\" max=\"3\" step=\"0.1\" value=\"1.0\"><span class=\"qval\" id=\"plSensV\">1.0</span></label>\n            <div class=\"qguide\">ESC 一键开菜单 · 逃生门纯解锁 · 移动端自动禁用</div>\n          </div>\n          <div class=\"mgroup\">\n            <div class=\"mtitle\">直播画质</div>\n            <label class=\"qgroup\"><span>码率</span><input id=\"qBitrate\" type=\"range\" min=\"1000\" max=\"500000\" step=\"1000\" value=\"8000\"><span class=\"qval\" id=\"qBitrateV\">8M</span></label>\n            <label class=\"qgroup\"><span>帧率</span><input id=\"qStreamFps\" type=\"range\" min=\"30\" max=\"120\" step=\"15\" value=\"30\"><span class=\"qval\" id=\"qStreamFpsV\">30fps</span></label>\n            <label class=\"qgroup\"><span>预设</span><select id=\"qPreset\" class=\"hfield select-mini\">\n              <option value=\"1\">P1 最快</option>\n              <option value=\"3\">P3 均衡</option>\n              <option value=\"5\">P5 高质</option>\n              <option value=\"7\">P7 最慢</option>\n            </select></label>\n            <label class=\"qgroup\"><span>VBV</span><input id=\"qVbv\" type=\"range\" min=\"0\" max=\"200\" step=\"10\" value=\"0\"><span class=\"qval\" id=\"qVbvV\">0%</span></label>\n            <div class=\"qguide\">挂机用默认 · vbv 50-100% 画质更清晰 · preset 复杂画面调高</div>\n          </div>\n          <div class=\"mgroup\">\n            <div class=\"mtitle\">录制参数（影响所有观看者）</div>\n            <label class=\"hfield\"><span>帧率</span>\n              <select id=\"selFps\">\n                <option value=\"5\">5 fps</option>\n                <option value=\"10\">10 fps</option>\n                <option value=\"15\">15 fps</option>\n                <option value=\"30\">30 fps</option>\n              </select>\n            </label>\n            <label class=\"hfield\"><span>画质</span>\n              <select id=\"selQuality\">\n                <option value=\"60\">标准 60</option>\n                <option value=\"75\">清晰 75</option>\n                <option value=\"90\">高清 90</option>\n              </select>\n            </label>\n            <label class=\"hfield\"><span>保留</span>\n              <select id=\"selKeep\">\n                <option value=\"10\">10 秒</option>\n                <option value=\"30\">30 秒</option>\n                <option value=\"60\">60 秒</option>\n                <option value=\"120\">120 秒</option>\n              </select>\n            </label>\n            <label class=\"hfield\"><span>直播帧率</span>\n              <select id=\"selLiveFps\">\n                <option value=\"30\">30 fps</option>\n                <option value=\"45\">45 fps</option>\n                <option value=\"60\">60 fps</option>\n                <option value=\"120\">120 fps</option>\n              </select>\n            </label>\n          </div>\n        </div>\n      </div>\n    </div>\n        <button class=\"themebtn\" id=\"themeBtn\" title=\"切换明暗主题\">\n      <span id=\"themeIcon\">\ud83c\udf19</span>\n    </button>\n  </div>\n</header>\n\n<main>\n  <!-- ══ 运行状态总览 ══ -->\n  <div class=\"stat\">\n    <h2>运行状态</h2>\n    <div class=\"desc\">监控各挂机账号的 BGI 运行情况 · 数据来自数据中心</div>\n    <div class=\"metrics\">\n      <div class=\"metric\"><div class=\"v\" id=\"mTotal\">0</div><div class=\"k\">绑定账号</div></div>\n      <div class=\"metric\"><div class=\"v\" id=\"mOnline\">0</div><div class=\"k\">会话在线</div></div>\n      <div class=\"metric\"><div class=\"v\" id=\"mBgi\">0</div><div class=\"k\">BGI 运行</div></div>\n    </div>\n  </div>\n\n  <div class=\"sect-t\">账号</div>\n  <div id=\"users\"></div>\n  <div class=\"empty\" id=\"empty\" style=\"display:none\"></div>\n\n  <!-- ══ 实时画面（点\"查看画面\"才展开）══ -->\n  <div id=\"viewer\">\n   <div id=\"viewerBox\">\n    <div id=\"viewerTitle\">实时画面</div>\n    <div id=\"remoteSwitch\" class=\"userswitch\" style=\"display:none\"></div>\n    <div id=\"scr\">\n      <!-- ★ 2026-11-23：画面只有 WebCodecs 解码直绘的 canvas（用户指令：\n           「我压根不需要 jpeg 序列，只要视频流」—— /live 轮询通路已移除）。\n           #ph 是提示层：连接中 / 断开（内部含重连按钮，见 CSS 的 pointer-events）。 -->\n      <canvas id=\"vframe\"></canvas>\n      <div id=\"ph\"><span id=\"phtxt\">连接中…</span><button id=\"reconn\" class=\"hbtn\" style=\"display:none\">重新连接</button></div>\n      <div id=\"rip\"></div>\n      <!-- ★ 2026-11-24：#plCursor 蓝球已删（本机鼠标影子，信息量为零）。\n           换成 #rcCursor：远端光标位置的回显（位置由服务端 cursor 回传驱动，\n           见 JS 的 updateRemoteCursor；不跟本机鼠标，无任何本地预测）。 -->\n      <div id=\"rcCursor\"><div class=\"rc-dot\"><div class=\"rc-ring\"></div></div></div>\n    </div>\n    <!-- ★ 3.3.8：这里的操作行已全部移到顶栏（见 <header>）。\n         画面区只留视频本身，不再被按钮占掉可视高度。\n         原来的「刷新 1/0.5/0.25 秒」也已删除：那个间隔对使用者没有意义。 -->\n\n    <!-- ★ 3.3.17：录像列表改成**点了才取**。\n         /api/list 要扫磁盘目录 + 读每个 meta.json，打开画面时不需要。 -->\n    <div class=\"sect-t sect-toggle\" id=\"savedToggle\">录像回放 <span class=\"chev\">▸</span></div>\n    <div id=\"savedWrap\" style=\"display:none\"><div id=\"saved\"></div></div>\n    <div id=\"player\">\n      <video id=\"vid\" controls playsinline preload=\"metadata\"></video>\n      <div class=\"imt\" id=\"pinfo\" style=\"text-align:center;margin-top:8px\"></div>\n      <div class=\"tools\">\n        <button id=\"pback\">关闭播放器</button>\n        <button id=\"dl\">下载</button>\n      </div>\n    </div>\n   </div>\n  </div>\n\n  <!-- ══ 运行分析页（#/ledger/:id；t13 改名不改路由，旧书签仍可用）══ -->\n  <div id=\"ledgerPage\" style=\"display:none\">\n    <button class=\"page-back\" id=\"ledgerBack\" type=\"button\">‹ 返回总览</button>\n    <div class=\"sect-t\" id=\"ledgerTitle\">运行分析</div>\n    <div class=\"userswitch\" id=\"ledgerSwitch\" style=\"display:none\"></div>\n    <div id=\"ledgerBody\"></div>\n  </div>\n\n  <!-- ══ 摩拉收益页（#/earnings/:id，2026-11-23 网页改版）══ -->\n  <div id=\"earningsPage\" style=\"display:none\">\n    <button class=\"page-back\" id=\"earningsBack\" type=\"button\">‹ 返回总览</button>\n    <div class=\"sect-t\">摩拉收益</div>\n    <div class=\"userswitch\" id=\"earningsSwitch\" style=\"display:none\"></div>\n    <div id=\"earningsBody\"></div>\n  </div>\n</main>\n\n<script>\nconst $ = id => document.getElementById(id);\n\n// ══════════ 明暗主题 ══════════\n//\n//  ★ 与应用内主题联动（用户要求：「把应用的亮色与暗色主题做进去」）。\n//    优先级：用户手动选择（localStorage）> 应用当前主题（API 下发）> 系统偏好。\n//    应用里改主题后，网页重新加载会自动跟随；用户也可以在本页手动覆盖。\n\nlet themeMode = 'auto';   // auto | dark | light\nlet appIsDark = true;     // 来自后端 /api/sessions 的 theme 字段\n\nfunction applyTheme() {\n  let dark;\n  if (themeMode === 'dark') dark = true;\n  else if (themeMode === 'light') dark = false;\n  else if (themeMode !== 'auto' && typeof appIsDark === 'boolean') dark = appIsDark;\n  else dark = appIsDark;\n\n  document.documentElement.setAttribute('data-theme', dark ? 'dark' : 'light');\n  const icon = $('themeIcon');\n  if (icon) icon.textContent = dark ? '\ud83c\udf19' : '☀\ufe0f';\n  try { localStorage.setItem('soullink-theme', themeMode); } catch (e) {}\n}\n\n// 启动时读用户偏好（★ 整个主题初始化必须容错，\n//   任何一个元素缺失都不能让后面的 loadUsers 停下 —— 实测踩过：\n//   主题按钮没渲染出来时 $('themeBtn').onclick 抛异常，\n//   导致账号列表永远空白。）\ntry {\n  const saved = localStorage.getItem('soullink-theme');\n  if (saved === 'dark' || saved === 'light' || saved === 'auto') themeMode = saved;\n} catch (e) {}\ntry { applyTheme(); } catch (e) { console.warn('主题初始化失败', e); }\n\n// 点击：在\"跟随应用\"和\"手动取反\"之间轮换\nconst tb = $('themeBtn');\nif (tb) tb.onclick = () => {\n  const cur = document.documentElement.getAttribute('data-theme') === 'dark';\n  // 先手动切到相反主题；如果本来就是手动模式且与应用相反，则回到 auto\n  if (themeMode === 'auto') {\n    themeMode = cur ? 'light' : 'dark';\n  } else if (themeMode === (cur ? 'dark' : 'light')) {\n    themeMode = 'auto';\n  } else {\n    themeMode = cur ? 'light' : 'dark';\n  }\n  applyTheme();\n  // 短暂提示当前模式\n  const btn = $('themeBtn');\n  const tips = { auto: '跟随应用', dark: '暗色', light: '亮色' };\n  btn.title = '主题：' + tips[themeMode] + '（点击切换）';\n};\nlet users = [], cur = null;\n// ══════════ 两组完全不同的参数，别混 ══════════\n//\n//   录制（recFps/recQuality/recKeep）\n//     · 由**代理**执行：多久截一次屏、压多清、内存留多久\n//     · 写回应用配置，影响所有观看者\n//\n//   播放（viewFps）\n//     · 由**本浏览器**执行：多久来取一次已经录好的帧\n//     · 只存在本机 localStorage，不改配置、不影响别人\n//     · 0 = 跟随录制帧率（取图频率 = 录制频率，最省事）\n//\n//   ★ 上限关系：实际取图频率 = min(viewFps, recFps)，\n//     取再快也只能拿到同一帧（代理还没录新的）。\nlet paused = false;\nlet recFps = 5, recQuality = 75, recKeep = 30, streamFps = 30, actualLiveFps = 0;\nlet videoBitrate = 8000, nvencPreset = 3, vbvBufferIncreasePercent = 0;   // 直播画质（2026-11-23）\nlet captureMode = 0;                       // 抓屏后端（0=自动/1=DDA/2=WGC/3=BitBlt）\nlet viewFps = 0;              // 0 = 跟随录制（2026-11-23 起只用于鼠标移动节流，\n                              // 不再控制拉帧 —— 画面是 /stream 视频流直推）\nlet liveMs = 500;\nconst VIEWFPS_KEY = 'sl_view_fps';\n\nfunction computeLiveMs() {\n  const recordHz = Math.max(1, Math.min(30, recFps || 10));\n  let hz = (viewFps > 0) ? viewFps : recordHz;\n  if (hz > recordHz) hz = recordHz;      // 不可能比录制更快拿到新画面\n  liveMs = Math.max(33, Math.round(1000 / hz));\n}\nlet kbMode = false;   // 右键已直接支持，不再需要开关\n\nasync function loadUsers() {\n  try {\n    const r = await fetch('/api/sessions');\n    const d = await r.json();\n    users = (d.sessions || []).filter(s => s.bound);\n\n    // ★ 3.3.5：直连代理的口令（后端随会话列表下发，本接口本身已鉴权）\n    //   必须拿到它，否则 <img src> 打到代理会 401（浏览器不会给 img 加请求头）。\n    window.__slToken = d.directToken || \"\";\n\n    // ★ 录制参数：以应用设置为准（用户要求「两边同步」）\n    applySettings(d);\n\n    // ★ 跟随应用主题：后端下发当前是暗色还是亮色\n    if (typeof d.themeDark === 'boolean') {\n      appIsDark = d.themeDark;\n      applyTheme();     // 用户手动选过就忽略，否则自动跟随\n    }\n    $('ver').textContent = d.version ? ('v' + d.version) : '';\n    const on = users.filter(u => u.online).length;\n    $('liveCount').textContent = users.filter(u => u.bgiRunning).length + ' BGI';\n    $('mTotal').textContent = users.length;\n    $('mOnline').textContent = on;\n    $('mBgi').textContent = users.filter(u => u.bgiRunning).length;\n    renderUsers();\n  } catch (e) {\n    $('empty').style.display = 'block';\n    $('empty').textContent = '加载失败，请刷新重试';\n  }\n}\n\n// ═══════════════════════════════════════════════════════════════════════════\n//  ★★ 2026-11-24（T30）：卡片悬停自动展开/折叠（方案 C）+ 点击固定 ★★\n//\n//  【方案 C 是什么】鼠标在**整个卡片**（含展开区）内就不折叠，移出整个卡片才折叠。\n//    → 所以 mouseenter/mouseleave 必须绑在 .ucard 上，**不能**绑 .ucard-head：\n//      绑 head 的话，鼠标一进入展开区就等于「离开 head」→ 立刻折叠 → 根本点不到按钮。\n//\n//  【两个延迟，各自防不同的误操作】\n//    · HOVER_IN_DELAY_MS = 280ms（移入延迟）\n//        防「快速划过一排卡片引发连环展开」——这是最主要的误操作来源。\n//        取 280：<200ms 时鼠标快速掠过 3~4 张卡片会连续炸开；>400ms 时\n//        「想展开一张卡片」会明显觉得迟滞。280 落在「掠过不触发」与「停留即响应」之间。\n//    · HOVER_OUT_DELAY_MS = 500ms（移出延迟）\n//        防「从卡片移到旁边的按钮/滚动条时立刻收起」——移出过程要经过卡片边缘，\n//        期间指针会短暂离开 .ucard，立即折叠会导致展开区闪一下又回去。\n//        取 500 比移入更长：**误折叠的代价（要重新悬停+再等 280ms）远大于误展开的代价**，\n//        所以不对称地偏向「宁可多开一会儿」。\n//\n//  【多卡片互斥】全局只记一个 _hoverCard；移入新卡片时先取消旧卡片的全部定时器\n//    并清掉它的 open（除非它是被点击固定的）。否则会多张同时展开、页面跳动。\n//\n//  【点击语义】点击 = 用户**显式固定**，之后移出不再自动折叠，直到再次点击或移入别卡。\n//    理由：用户主动点击就是要「钉住」这张卡片去看/操作里面的按钮；\n//    如果移出还折回，他就得一直把鼠标悬在卡片上才能点按钮，这明显违背点击意图。\n//        → 但「移入其他卡片」时会解除固定（否则固定态会一直占着，体验割裂）。\n// ═══════════════════════════════════════════════════════════════════════════\nconst __canHover = window.matchMedia\n  ? window.matchMedia('(hover: hover) and (pointer: fine)').matches\n  : false;\nconst HOVER_IN_DELAY_MS = 280;\nconst HOVER_OUT_DELAY_MS = 500;\n\nlet _hoverCard = null;      // 当前因悬停而展开的卡片（全局唯一）\nlet _lastPointerDownAt = 0; // 最近一次 pointerdown 时刻（触屏模拟悬停兜底）\n\n// 触屏兜底：有些设备同时报告 hover 能力但实际是触屏，\n//   此时 mouseenter 常由 touch 合成的 pointerdown 触发 —— 刚按下 <200ms 就来的 enter\n//   几乎一定是触摸模拟，不是真悬停，跳过自动展开逻辑。\ndocument.addEventListener('pointerdown', () => { _lastPointerDownAt = Date.now(); },\n  { capture: true, passive: true });\n\nfunction __cancelCardTimers(card) {\n  if (!card) return;\n  if (card.__hoverInT) { clearTimeout(card.__hoverInT); card.__hoverInT = null; }\n  if (card.__hoverOutT) { clearTimeout(card.__hoverOutT); card.__hoverOutT = null; }\n}\n\n// 取消旧卡片的悬停展开（多卡片互斥的落地点）\nfunction __collapseHoverCard() {\n  const old = _hoverCard;\n  _hoverCard = null;\n  if (!old) return;\n  __cancelCardTimers(old);\n  // 悬停展开的卡片：移入别卡时收起（多卡片互斥，避免页面跳动）。\n  //   ★ 被点击固定的卡片**也在此收起**（解除固定）。\n  //     依据：任务③明确要求「鼠标从 A 移到 B 时 A 应该折叠，否则多张同时展开、页面跳动」。\n  //     若固定卡不收，用户固定 A 后再去操作 B，屏幕上会同时开两张 → 正是要避免的现象。\n  //     语义上「固定」= 相对当前这张卡保持展开（移出不折），但「注意力只有一处」，\n  //     移入另一张时让位，符合直觉且不产生页面跳动。\n  old.classList.remove('open');\n  old.__pinned = false;\n}\n\nfunction renderUsers() {\n  const box = $('users');\n  // ★ T30：重建前先清掉旧卡片的悬停定时器与全局引用。\n  //   否则旧 setTimeout 仍会在已移除的节点上执行（无害但泄漏），\n  //   更糟的是 _hoverCard 会指向一个已脱离文档的节点，导致「互斥」逻辑对空气作用、\n  //   新渲染出来的卡片反而不能正确收起上一张。\n  if (_hoverCard) { __cancelCardTimers(_hoverCard); _hoverCard = null; }\n  box.querySelectorAll('.ucard').forEach(__cancelCardTimers);\n  box.innerHTML = '';\n  if (!users.length) {\n    $('empty').style.display = 'block';\n    $('empty').textContent = '还没有绑定账号。在「设置 → BetterGI 路径」里绑定后，这里会出现。';\n    return;\n  }\n  $('empty').style.display = 'none';\n\n  // ★ 2026-11-23 网页改版：每账号一张可折叠卡片（默认收起），\n  //   点卡片头部展开该账号的功能按钮（查看画面 / 运行分析 / 摩拉收益）。\n  for (const u of users) {\n    const card = document.createElement('div');\n    card.className = 'ucard';\n\n    const tags = [];\n    if (u.bgiRunning) tags.push('<span class=\"tag bgi\">BGI ' +\n      (u.bgiLogIdleMinutes >= 0 ? '空闲 ' + u.bgiLogIdleMinutes + ' 分' : '运行中') + '</span>');\n    if (u.recording) tags.push('<span class=\"tag rec\">录制中</span>');\n    if (!u.online) tags.push('<span class=\"tag off\">离线</span>');\n\n    let sub = '';\n    if (!u.online) sub = '会话未连接';\n    else sub = esc(u.screen) + ' · 通道就绪 · ' + (u.bgiRunning ? 'BGI#' + u.bgiPid : 'BGI 未运行');\n\n    // 运行详情（展开体里显示，与旧版同源同字段）\n    let detail = '';\n    if (u.bgiRunning) {\n      const parts = [];\n      if (u.configGroup) parts.push(['配置组', u.configGroup]);\n      if (u.taskName) parts.push(['任务', u.taskName]);\n      if (u.routeFile) parts.push(['路线', u.routeFile]);\n      if (u.round || u.role) parts.push(['轮次/角色',\n        [u.round, u.role].filter(Boolean).join(' · ')]);\n      if (u.eta) parts.push(['预计', u.eta]);\n      if (u.action) parts.push(['当前动作', u.action]);\n      if (u.pickTotal > 0) parts.push(['拾取', u.pickTotal + ' 个']);\n      if (u.errorCount > 0) parts.push(['报错', u.errorCount + ' 次']);\n\n      detail = parts.length === 0\n        ? '<div class=\"detail\"><div class=\"dline\"><span class=\"dk\">状态</span>'\n          + '<span class=\"dv\">等待日志中的状态信息…</span></div></div>'\n        : '<div class=\"detail\">'\n          + parts.map(p => '<div class=\"dline\"><span class=\"dk\">' + esc(p[0])\n              + '</span><span class=\"dv\">' + esc(String(p[1])) + '</span></div>').join('')\n          + '</div>';\n    }\n\n    card.innerHTML =\n      '<div class=\"ucard-head\">'\n      + '<span class=\"udot' + (u.online ? ' on' : '') + '\"></span>'\n      + '<div class=\"uinfo\">'\n      + '<div class=\"un\">' + esc(u.label) + tags.join('') + '</div>'\n      + '<div class=\"us\">' + sub + '</div>'\n      + '</div>'\n      + '<span class=\"chev\">▸</span>'\n      + '</div>'\n      + '<div class=\"ucard-body\">'\n      // ★ T30：内层容器 —— grid 0fr→1fr 过渡要求子项能收缩（min-height:0），\n      //   直接放内容会在折叠态撑出高度，导致「折叠了但还占位」。\n      + '<div class=\"ucard-body-inner\">'\n      + detail\n      + '<div class=\"ucard-actions\">'\n      + '<button class=\"uact-btn\" data-act=\"remote\">查看画面</button>'\n      + '<button class=\"uact-btn\" data-act=\"ledger\">运行分析</button>'\n      + '<button class=\"uact-btn\" data-act=\"earnings\">摩拉收益</button>'\n      + '</div>'\n      + '</div>'\n      + '</div>';\n\n    // ── 点击：显式切换 + 固定（T30）──────────────────────────────────────\n    //   ★ 点击仍绑在 .ucard-head 上：只有点头部才切换，点展开区里的内容不会误折叠。\n    //   ★ 点击时必须取消进行中的悬停定时器，否则「点开 → 移出」会立刻被 500ms 后的\n    //     自动折叠覆盖，用户会觉得点了没用。\n    card.querySelector('.ucard-head').onclick = () => {\n      __cancelCardTimers(card);\n      const willOpen = !card.classList.contains('open');\n      card.classList.toggle('open', willOpen);\n      card.__pinned = willOpen;   // 点开 = 固定；点收 = 解除\n      if (willOpen) {\n        // 点开这张 → 别的**未固定**的悬停卡收起（注意力只有一处）。\n        //   __collapseHoverCard 内部已保护 __pinned 的卡，这里不必再判。\n        if (_hoverCard && _hoverCard !== card) __collapseHoverCard();\n        // ★ 本卡是「点击固定」，不是「悬停展开」→ 不占用 _hoverCard。\n        //   否则悬停逻辑会把它当自己的卡，移出时按 _hoverCard 清理，导致固定态被误清。\n        if (_hoverCard === card) _hoverCard = null;\n      } else {\n        // 点收：若它恰好是悬停卡，一并让出\n        if (_hoverCard === card) _hoverCard = null;\n      }\n    };\n\n    // ── 悬停：自动展开/折叠（仅鼠标设备，T30 方案 C）─────────────────────\n    //   绑在**整个 .ucard**上：鼠标从头部移进展开区不会触发 mouseleave，\n    //   所以能正常点到里面的按钮（这正是方案 C 的关键）。\n    if (__canHover) {\n      card.addEventListener('mouseenter', () => {\n        // 触屏兜底：刚 pointerdown 完就来的 enter 是触摸模拟，不是真悬停\n        if (Date.now() - _lastPointerDownAt < 200) return;\n        __cancelCardTimers(card);\n        // ★ 已展开的卡片：不再排队展开，但**也不能抢 _hoverCard**。\n        //   如果它是被点击固定的，把它记成 _hoverCard 会导致「鼠标只是划过，\n        //   移出后就被折叠」—— 固定态被悬停逻辑劫持，用户会觉得点了没保住。\n        if (card.classList.contains('open')) return;\n        card.__hoverInT = setTimeout(() => {\n          card.__hoverInT = null;\n          // 互斥：先收起上一张悬停卡\n          if (_hoverCard && _hoverCard !== card) __collapseHoverCard();\n          // ★ 再收起「其它任何已展开的卡片」—— 特别是被点击固定的那张。\n          //   固定的卡片不占用 _hoverCard（见 onclick），所以上面的 _collapseHoverCard 管不到它；\n          //   不补这一步就会出现「固定 A 后悬停 B，A 和 B 同时展开」→ 页面跳动（任务③明令禁止）。\n          document.querySelectorAll('#users .ucard.open').forEach(function (other) {\n            if (other === card) return;\n            __cancelCardTimers(other);\n            other.classList.remove('open');\n            other.__pinned = false;\n            if (_hoverCard === other) _hoverCard = null;\n          });\n          _hoverCard = card;\n          card.classList.add('open');\n        }, HOVER_IN_DELAY_MS);\n      });\n      card.addEventListener('mouseleave', () => {\n        __cancelCardTimers(card);\n        // 被点击固定的卡片：移出不折叠（用户显式要求钉住）\n        if (card.__pinned) return;\n        card.__hoverOutT = setTimeout(() => {\n          card.__hoverOutT = null;\n          if (card.__pinned) return;      // 延迟期间被点击固定了 → 不折\n          card.classList.remove('open');\n          if (_hoverCard === card) _hoverCard = null;\n        }, HOVER_OUT_DELAY_MS);\n      });\n    }\n\n    // 功能按钮：hash 路由同页跳转（不再 window.open 新标签）\n    card.querySelectorAll('.uact-btn').forEach(btn => {\n      btn.onclick = e => {\n        e.stopPropagation();\n        const act = btn.getAttribute('data-act');\n        location.hash = act === 'remote'\n          ? ('#/remote/' + encodeURIComponent(u.id))\n          : ('#/' + act + '/' + encodeURIComponent(u.id));\n      };\n    });\n\n    box.appendChild(card);\n  }\n}\n\nfunction closeViewer() {\n  plLifecycleExit();\n  stopLive();\n  $('player').style.display = 'none';\n  const v = $('vid'); v.pause(); v.removeAttribute('src'); v.load();\n  cur = null;\n  $('viewer').style.display = 'none';\n\n  // ★ 2026-11-23 hash 路由：收起 = 回总览（同页跳转），\n  //   不再 window.close / 跳 \"/\"（那是旧 ?view= 独立窗口的语义）。\n  location.hash = '#/';\n}\n\n// ══════════ 视频流播放（2026-11-23 按用户指令重写）══════════════\n//\n// ★★ 用户指令：「我压根不需要 jpeg 序列，只要视频流，上个工作会话还保留它，很卡很卡」。\n//\n//   /live（单帧 JPEG 轮询）通路**整个移除**，不再是降级备胎。\n//   /stream（H.264 Annex-B + WebCodecs）是唯一画面通路：\n//     · 断开 → 画面上明确提示 + 自动退避重连（1s→2s→4s→8s）+ 手动重连按钮；\n//     · 重连是全新的 HTTP 连接，从头等 SPS/IDR 再起播（不从半个 GOP 里捡）；\n//     · 页面隐藏 → 断流省资源；回前台 → 自动重连；\n//     · 暂停 → 断流；继续 → 重连。\n//   （保留的 liveMs/computeLiveMs 只服务鼠标移动节流和设置回显，与拉帧无关。）\nlet liveRunning = false;      // 是否处于「应播放」状态（true 时才允许重连）\n// ★ T34：fps 轮询句柄 —— 必须声明在这里（而非函数定义处），\n//   因为 startLive/stopLive 定义在更早位置会引用它；若用后置 let 会落进 TDZ。\nlet __fpsPollTimer = null;\nlet failStreak = 0;           // 连续连接失败次数（出画面即清零）\nlet reconnectTimer = null;    // 自动重连的 setTimeout 句柄\n\nconst webCodecsSupported = (typeof VideoDecoder !== 'undefined')\n    && (typeof EncodedVideoChunk !== 'undefined');\n\n// ═════════════ 播放器状态必须放在模块级（2026-11-23 修复）═════════════\n//   旧版 h264Gener/h264Reader/dirtyFrame 声明在 pullFrame() 内部，\n//   每次 pullFrame 都重建闭包 —— 代际永远等于自己，作废机制完全失效：\n//   stopLive() 之后流还在拉、切账号时两条流并发、decode 回调还在画旧画面。\nlet h264Gen = 0;              // 代际：停流/重连/切账号时 +1，作废旧流的一切回调\nlet h264Reader = null;        // /stream 的 ReadableStreamDefaultReader\nlet h264Abort = null;         // /stream 的 AbortController（取消 fetch 僵尸连接）\nlet h264Active = false;       // /stream 连接是否在跑\n\n// ── P0 传输层（2026-11-24）流协议版本 ──\nlet streamVersion = 1;         // 流协议版本（1=裸 Annex-B；2=length-prefix 帧边界，由 /api/info 下发）\nlet streamVersionKnown = false;// 是否已从 /api/info 取到（每会话缓存一次）\nlet _lastIdrReqMs = 0;         // 客户端按需 IDR 限流：上次请求时刻（500ms 最小间隔）\n\n// 收掉当前流（作废回调 + 断开连接），不动画面\nfunction abortStream() {\n  h264Gen++;                  // 读流循环、decode 回调全部作废\n  h264Active = false;\n  const r = h264Reader; h264Reader = null;\n  const ac = h264Abort; h264Abort = null;\n  if (ac) { try { ac.abort(); } catch (e) {} }\n  if (r) { try { r.cancel().catch(() => {}); } catch (e) {} }\n}\n\n// 停流 + 隐藏画面（切账号 / 暂停用）\nfunction stopH264() {\n  abortStream();\n  $('vframe').classList.remove('show');\n}\n\nfunction stopLive() {\n  liveRunning = false;\n  // ★ T34：停画面就停 fps 轮询（避免后台继续每 2s 请求 /api/info）\n  if (typeof stopFpsPolling === 'function') stopFpsPolling();\n  stopH264();                  // 也断掉视频流（旧版漏了，流会在后台一直跑）\n  closeWsInput();              // 断开输入 WebSocket（切账号/暂停时释放，下次输入时按新 base 重建）\n  if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }\n}\n\nfunction startLive() {\n  stopLive();\n  if (!cur) return;\n  if (!cur.online) { setPh('该账号不在线', false); return; }\n  liveRunning = true;\n  // ★ T34：开画面就启动 fps 轮询（每 2s 刷新「推送实测 fps」）\n  if (typeof startFpsPolling === 'function') startFpsPolling();\n  failStreak = 0;\n  if (!webCodecsSupported) {\n    // 没有降级通路（用户指令），只能明确告知\n    setPh('当前浏览器不支持 H.264 视频流播放（需要 WebCodecs，请用新版 Chrome/Edge）', false);\n    return;\n  }\n  setPh('连接中…', false);\n  startStream();\n}\n\n// 断开后自动重连：按失败次数指数退避，正在排队时不重复排\nfunction scheduleReconnect() {\n  if (!liveRunning || reconnectTimer) return;\n  const delay = Math.min(1000 * Math.pow(2, failStreak), 8000);\n  reconnectTimer = setTimeout(() => {\n    reconnectTimer = null;\n    if (liveRunning) startStream();\n  }, delay);\n}\n\n// 提示层：msg=null 隐藏；否则显示文本（可选重连按钮）。\n// 半透明底色盖在最后一帧上 —— 用户能看到断开瞬间的画面，而不是黑屏。\nfunction setPh(msg, showReconn) {\n  const ph = $('ph');\n  if (!ph) return;\n  if (msg === null) { ph.style.display = 'none'; return; }\n  ph.style.display = 'flex';\n  $('phtxt').textContent = msg;\n  $('reconn').style.display = showReconn ? 'inline-flex' : 'none';\n}\n\n// 手动重连按钮：清退避、立即重连\nconst __rc = $('reconn');\nif (__rc) __rc.onclick = () => {\n  if (!cur || !liveRunning) return;\n  if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }\n  failStreak = 0;\n  setPh('连接中…', false);\n  startStream();\n};\n\n// ── 直连代理（3.3.5）───────────────────────────────────────────\n//   用户在局域网里访问 http://主机:10800，但画面/按键直连代理的端口：\n//     http://主机:(sessions[i].directPort)\n//   这样主程序只在加载列表时被访问一次，之后不碰任何流量。\n//\n//   主机名沿用当前页面访问用的那个（手机连的 192.168.x.x 不会变成 localhost）。\nfunction directToken() { return window.__slToken || ''; }\n\nfunction directBase(sess) {\n  const p = sess && sess.directPort;\n  if (!p || p <= 0) return '';\n  return location.protocol + '//' + location.hostname + ':' + p;\n}\n\n// ── P0 传输层（2026-11-24）：流协议版本协商 ──\n//   从 /api/info 读 streamVersion（2 = 帧边界 length-prefix），每会话缓存一次。\n//   无直连端口（base 为空，走主程序代理）时不询问，保持旧协议。\nasync function resolveStreamVersion() {\n  if (streamVersionKnown) return streamVersion;\n  try {\n    const base = directBase(cur);\n    if (base) {\n      const headers = {};\n      if (directToken()) headers['X-SoulLink-Token'] = directToken();\n      const r = await fetch(base + '/api/info', { headers });\n      const d = await r.json();\n      if (d && typeof d.streamVersion === 'number' && d.streamVersion >= 2) streamVersion = d.streamVersion;\n    }\n  } catch (e) { /* /api/info 失败 → 保持旧协议 */ }\n  streamVersionKnown = true;\n  return streamVersion;\n}\n\n// ── P0 传输层（2026-11-24）：客户端按需 IDR（限流 500ms，与服务端 300ms 双保险）──\n//   解码失败/花屏时先请求关键帧而非断流重连（重连 = 新订阅 + 等 IDR + 首帧，代价极大）。\nasync function requestIdr() {\n  const now = Date.now();\n  if (now - _lastIdrReqMs < 500) return false;   // 客户端限流\n  _lastIdrReqMs = now;\n  try {\n    const base = directBase(cur);\n    if (!base) return false;\n    const headers = {};\n    if (directToken()) headers['X-SoulLink-Token'] = directToken();\n    const r = await fetch(base + '/api/idr', { method: 'POST', headers });\n    const d = await r.json();\n    return !!(d && d.ok);\n  } catch (e) { return false; }\n}\n\n// ═════════════ Annex-B → AVCC 的格式选择（2026-11-23，查证规范后定）═════════════\n//\n// 依据 W3C「AVC (H.264) WebCodecs Registration」§2/§3：\n//   · VideoDecoderConfig.description 存在 → 码流按 AVCC（长度前缀）解析，\n//     description 必须是 AVCDecoderConfigurationRecord（即 MP4 的 avcC）。\n//   · description 不存在 → 码流按 Annex-B（起始码）解析，\n//     且 key chunk 必须自带解码所需的全部参数集（SPS/PPS）。\n//\n// 旧版把 Annex-B 转成长度前缀喂 decoder 却不给 description ——\n// 按 AVCC 解析无起始码的裸 NAL，与规范相矛盾，必然一帧都解不出来。\n//\n// ★ 本实现选择：不给 description + 纯 Annex-B。\n//   理由：① 服务端 MfFrameEncoder 输出本来就是带 00000001 起始码的\n//            Annex-B，且 IDR 帧带 SPS/PPS，正好是规范对 annexb 模式\n//            key chunk 的要求 —— 零转换、零重组；\n//         ② 走 AVCC 得自己拼 AVCDecoderConfigurationRecord（含 SPS/PPS\n//            重组和长度表），多一层易错的手工活，收益为零。\n//   EncodedVideoChunk.data 直接给「起始码+NAL」的原始字节。\n// ══════════════════════════════════════════════════════════════════\n\n  // 从 SPS NAL（已去掉起始码、**含 1 字节 NAL 头**）解析 profile_idc /\n  // constraint flags / level_idc，拼成 WebCodecs 的 codec 字符串\n  // 'avc1.PPCCLL'（PP=profile, CC=constraint, LL=level，各 2 位十六进制，\n  // 见 RFC 6381 §3.4）。解析失败返回 null，由调用方走默认值。\n  // ★ SPS NAL 布局（ITU-T H.264 §7.3.2.1）：\n  //   byte0 = NAL 头（type=7，必须跳过！）\n  //   byte1 = profile_idc\n  //   byte2 = constraint_set0..5 + reserved_zero（codec 串要整个字节，含保留位）\n  //   byte3 = level_idc\n  //   （单测曾抓到没跳 NAL 头的 bug：nal[0]=0x67 被当 profile → 解析必失败）\n  function avcCodecFromSps(sps) {\n    if (!sps || sps.length < 4) return null;\n    const p = sps[1], c = sps[2], l = sps[3];\n    // 合理性检查：profile_idc 只可能是这些已定义值\n    const known = [44, 66, 77, 83, 86, 88, 100, 110, 118, 122, 128,\n                   138, 139, 134, 135, 244, 247];\n    if (!known.includes(p)) return null;\n    const hex = n => n.toString(16).padStart(2, '0').toUpperCase();\n    return 'avc1.' + hex(p) + hex(c) + hex(l);\n  }\n  // ═════════════ splitAnnexB：重写（2026-11-23，单测校验过）═════════════\n  // 旧版三处错误：\n  //   ① 循环里 `i += 2`/`i += 3` 跳过起始码后会**越过下一个起始码**的检测位置\n  //     （起始码可以紧贴在前一个 NAL 尾部，跳着扫会漏检）；\n  //   ② 检测 4 字节起始码要求 i+4 < len，缓冲末尾恰好停在 00 00 00 01 时\n  //     检测不到 → 起始码被永久丢弃 → 丢帧；\n  //   ③ 前导零归属错：00 00 00 00 01 应整体视为起始码区（Annex-B 的\n  //     leading_zero_8bits 属于起始码不属于上一个 NAL），否则切出的 NAL\n  //     尾部挂着起始码前缀字节。\n  //\n  // 新实现：逐字节状态扫描，起始码区 = 遇到 1 时的整段连零 + 这个 1，\n  // 即 at = i - zeros、len = zeros + 1（3/4 字节起始码及任意长度前导零\n  // 都正确），上一个 NAL 止于 at，下一个 NAL 始于 i+1。\n  // 末段（最后一个起始码之后）一律留到下一轮 —— 可能是不完整的 NAL，\n  // 下轮新起始码到达时被安全切出，不丢帧也不吐半 NAL。\n  function splitAnnexB(buf, complete) {\n    const units = [];\n    const starts = [];      // 每个起始码的位置（at）与长度（len）\n    let zeros = 0;\n    for (let i = 0; i < buf.length; i++) {\n      const b = buf[i];\n      if (b === 0) { zeros++; continue; }\n      if (b === 1 && zeros >= 2) {\n        starts.push({ at: i - zeros, len: zeros + 1 });\n        zeros = 0;\n        continue;\n      }\n      zeros = 0;\n    }\n    if (!starts.length) {\n      // 整个缓冲没有起始码：流头垃圾或起始码前缀还没到齐。\n      // 全部保留；超过 1MB 仍无起始码视为坏流，清空（防内存无限涨）。\n      return { units, pending: buf.length > 1048576 ? new Uint8Array(0) : buf };\n    }\n    for (let k = 0; k < starts.length - 1; k++) {\n      const from = starts[k].at + starts[k].len;\n      const to = starts[k + 1].at;\n      if (to > from) units.push(buf.subarray(from, to));\n    }\n    // 末段从最后一个起始码起保留（含起始码本身，下轮重新定位）。\n    // 末段超过 4MB 说明坏流（单个 NAL 不可能这么大），丢弃防涨。\n    const lastStart = starts[starts.length - 1];\n    // ★ P0 传输层（2026-11-24）：framed 模式（complete=true）下帧是完整的\n    //   （length-prefix 保证），最后一个 NAL 也直接切出，不再「末段留到下一轮」。\n    if (complete) {\n      const from = lastStart.at + lastStart.len;\n      if (buf.length > from) units.push(buf.subarray(from));\n      return { units, pending: new Uint8Array(0) };\n    }\n    if (buf.length - lastStart.at > 4 * 1048576) {\n      return { units, pending: new Uint8Array(0) };\n    }\n    return { units, pending: buf.subarray(lastStart.at) };\n  }\n\n  // ═════════ WebGL2 呈现层：消除 2D drawImage 的 GPU 回读（2026-11-24）═════════════\n  // 2D canvas 的 drawImage(VideoFrame) 会把硬解出的 GPU 纹理读回 CPU 再传回 GPU\n  //（Chrome 已知行为），1080p 下每帧多一次全尺寸拷贝并可能阻塞 output 回调。\n  // 改用 WebGL2 的 texImage2D 直接把 VideoFrame 当纹理源上传（I420/NV12/RGBA 均由\n  // 浏览器零回读转换），再画全屏三角形。渲染器只建一次（模块级），跨流/重连复用，\n  // 不重复建 GL 资源。WebGL2 不可用（罕见）时回退 2D drawImage（原路径）。\n  let _glRenderer = undefined;   // undefined=未初始化 / null=不可用 / 对象=可用\n  function getGlRenderer(canvas) {\n    if (_glRenderer !== undefined) return _glRenderer;\n    _glRenderer = null;\n    let gl = null;\n    try {\n      gl = canvas.getContext('webgl2', {\n        alpha: false, antialias: false, depth: false, stencil: false,\n        premultipliedAlpha: false, desynchronized: true, powerPreference: 'high-performance'\n      });\n    } catch (e) { gl = null; }\n    if (!gl) return _glRenderer;   // 无 WebGL2 → 回退 2D\n\n    try {\n      const VS = `#version 300 es\nlayout(location=0) in vec2 aPos;\nout vec2 vUV;\nvoid main(){ vUV = aPos*0.5 + 0.5; gl_Position = vec4(aPos, 0.0, 1.0); }`;\n      const FS = `#version 300 es\nprecision mediump float;\nin vec2 vUV;\nuniform sampler2D uTex;\nout vec4 outColor;\nvoid main(){ outColor = texture(uTex, vec2(vUV.x, 1.0 - vUV.y)); }`;\n\n      const vs = gl.createShader(gl.VERTEX_SHADER);\n      gl.shaderSource(vs, VS); gl.compileShader(vs);\n      if (!gl.getShaderParameter(vs, gl.COMPILE_STATUS)) throw new Error('vs: ' + gl.getShaderInfoLog(vs));\n      const fs = gl.createShader(gl.FRAGMENT_SHADER);\n      gl.shaderSource(fs, FS); gl.compileShader(fs);\n      if (!gl.getShaderParameter(fs, gl.COMPILE_STATUS)) throw new Error('fs: ' + gl.getShaderInfoLog(fs));\n\n      const prog = gl.createProgram();\n      gl.attachShader(prog, vs); gl.attachShader(prog, fs);\n      gl.linkProgram(prog);\n      if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) throw new Error('link: ' + gl.getProgramInfoLog(prog));\n\n      // 全屏三角形（三个顶点覆盖整个裁剪空间）\n      const vao = gl.createVertexArray();\n      gl.bindVertexArray(vao);\n      const buf = gl.createBuffer();\n      gl.bindBuffer(gl.ARRAY_BUFFER, buf);\n      gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);\n      gl.enableVertexAttribArray(0);\n      gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);\n\n      const tex = gl.createTexture();\n      gl.bindTexture(gl.TEXTURE_2D, tex);\n      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);\n      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);\n      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);\n      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);\n\n      gl.useProgram(prog);\n      gl.uniform1i(gl.getUniformLocation(prog, 'uTex'), 0);\n\n      gl.bindVertexArray(null);\n      gl.deleteShader(vs); gl.deleteShader(fs);\n\n      _glRenderer = {\n        draw(frame, w, h) {\n          if (gl.isContextLost()) return;   // GPU 上下文丢失（罕见）：跳过本帧，等上层重连\n          gl.bindVertexArray(vao);\n          gl.activeTexture(gl.TEXTURE0);\n          gl.bindTexture(gl.TEXTURE_2D, tex);\n          // ★ VideoFrame 直接作纹理源：浏览器内部转 RGBA，不走 CPU 读回\n          gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, frame);\n          gl.viewport(0, 0, w, h);\n          gl.drawArrays(gl.TRIANGLES, 0, 3);\n          gl.bindVertexArray(null);\n        }\n      };\n    } catch (e) {\n      console.warn('[H264] WebGL2 呈现初始化失败，回退 2D drawImage:', e);\n      _glRenderer = null;\n    }\n    return _glRenderer;\n  }\n\n  // H.264 主循环：拉 /stream → 切 NAL → 找 SPS 配置 decoder → 解码画 canvas\n  async function startStream() {\n    const myGen = ++h264Gen;\n    h264Active = true;\n    const base = directBase(cur);\n    const tok = directToken();\n    // ★ P0 传输层（2026-11-24）：帧边界协商。agent 支持 streamVersion>=2 时走\n    //   /stream?v=2（4 字节大端长度前缀），网页端按长度切帧、不再扫起始码。\n    await resolveStreamVersion();\n    const framed = !!(base && streamVersion >= 2);\n    let q = (base && tok) ? '?token=' + encodeURIComponent(tok) : '';\n    if (framed) q += (q ? '&' : '?') + 'v=2';\n    const url = (base || '') + '/stream' + q;\n\n    let decoder = null;\n    let configured = false;\n    let firstKey = false;\n    let gotFrame = false;        // 本轮是否已出过画面（区分「连不上」和「断了」）\n    let stallTimer = null;       // 画面看门狗 setInterval 句柄（函数级声明，finally 才能安全 clear）\n    let lastFrameAt = 0;         // 最近一次解码出帧时间戳（函数级声明，output/finally 共用）\n    // ★ 2026-11-24（T26）：首关键帧兜底（修 firstKey 死锁）—— 必须在函数级声明，finally 才能清\n    let firstKeyWaitTimer = null;   // 「还没拿到首 key」时的 IDR 重试定时器\n    let firstKeyIdrTries = 0;       // 已请求次数（上限 5）\n    let noFrameSince = 0;           // 本轮「从未出过画面」的起始时刻（看门狗用）\n    let noFrameIdrSent = false;     // 5s 未出画面时是否已补发过 IDR（只发一次）\n    let lastCodec = '';          // 最近一次 SPS 解析出的 codec 串（IDR 恢复时 reconfigure 用）\n    let idrRecoveryCount = 0;    // 本轮已尝试按需 IDR 恢复次数（≥2 次失败则断流重连）\n    let auNals = [];             // 当前 AU 的 NAL（纯 NAL 体，无起始码；framed 下 = 一帧的 NAL）\n    let auHasKey = false, auHasData = false;\n\n    // 解码帧直接画到 DOM 里的 <canvas id=\"vframe\">，不再经过 <img>。\n    // 旧版每帧 canvas.toDataURL('image/jpeg') 等于把省下的带宽又花回去\n    //（JPEG 重编码 + base64 膨胀 1.33 倍），已删除。\n    const canvas = $('vframe');\n    // 呈现层：优先 WebGL2（零回读），不可用才回退 2D drawImage\n    const glR = getGlRenderer(canvas);\n    const ctx = glR ? null : canvas.getContext('2d', { alpha: false });\n\n    // 失败 = 停流 + 提示 + 自动退避重连（没有 JPEG 降级，用户指令移除）\n    const fail = (msg, e) => {\n      if (myGen !== h264Gen) return;      // 已换代：静默退出，别干扰新流\n      if (e) console.warn(msg, e); else console.warn(msg);\n      abortStream();                        // 只收流，画面留着（冻结在最后一帧）\n      setPh(gotFrame\n          ? '视频流已断开 · 正在自动重连（' + Math.min(1000 * Math.pow(2, failStreak), 8000) + 'ms 后）'\n          : '连接不上视频流（第 ' + (failStreak + 1) + ' 次）· 正在自动重连', true);\n      failStreak++;\n      scheduleReconnect();\n    };\n\n    // ★ P0 传输层（2026-11-24）：从 SPS 解析 codec 并 configure（framed/裸流共用）。\n    //   解析失败退 Main@L4.0 兜底；优先硬解，老内核不认识 hint 再降级重试。\n    function tryConfigure(nal) {\n      const codec = avcCodecFromSps(nal);\n      if (!codec) console.warn('[H264] SPS 解析失败，用兜底 avc1.4D4028（Main@4.0）');\n      lastCodec = codec || 'avc1.4D4028';\n      const cfg = { codec: lastCodec, optimizeForLatency: true };\n      try {\n        decoder.configure(Object.assign({}, cfg, { hardwareAcceleration: 'prefer-hardware' }));\n        configured = true;\n      } catch (e) {\n        try {\n          decoder.configure(cfg);\n          configured = true;\n        } catch (e2) {\n          fail('[H264] decoder.configure 失败:', e2);\n        }\n      }\n    }\n\n    // ★ P0 传输层（2026-11-24）：解码错误先按需 IDR 恢复，而非直接断流重连。\n    //   请求 IDR（限流）→ reset 解码器 → 用已知 codec reconfigure → firstKey=false 等新 IDR。\n    //   连续 ≥2 次仍失败才 fail()（重连 = 新订阅 + 等 IDR + 首帧，代价大，留作兜底）。\n    async function recoverViaIdr() {\n      if (myGen !== h264Gen) return;\n      console.warn('[H264] 尝试按需 IDR 恢复（第 ' + (idrRecoveryCount + 1) + ' 次）');\n      const ok = await requestIdr();\n      if (myGen !== h264Gen) return;\n      if (!ok) { fail('[H264] IDR 请求失败，断开重连', null); return; }\n      if (decoder && decoder.state !== 'closed') {\n        try { await decoder.reset(); } catch (e) {}\n      }\n      if (myGen !== h264Gen) return;\n      configured = false;\n      firstKey = false;                 // 丢首 key 前的 delta，等新 IDR\n      auNals = []; auHasKey = false; auHasData = false;\n      if (lastCodec && decoder) {\n        try {\n          decoder.configure({ codec: lastCodec, optimizeForLatency: true, hardwareAcceleration: 'prefer-hardware' });\n          configured = true;\n        } catch (e) {\n          try { decoder.configure({ codec: lastCodec, optimizeForLatency: true }); configured = true; }\n          catch (e2) { fail('[H264] IDR 恢复 reconfigure 失败:', e2); return; }\n        }\n      }\n      lastFrameAt = Date.now();         // 给看门狗一个恢复窗口\n    }\n\n    try {\n      decoder = new VideoDecoder({\n        output: (frame) => {\n          if (myGen !== h264Gen) { frame.close(); return; }\n          try {\n            if (canvas.width !== frame.displayWidth\n                || canvas.height !== frame.displayHeight) {\n              canvas.width = frame.displayWidth;\n              canvas.height = frame.displayHeight;\n            }\n            if (glR) glR.draw(frame, canvas.width, canvas.height);\n            else if (ctx) ctx.drawImage(frame, 0, 0);\n            canvas.classList.add('show');\n            setPh(null, false);\n            lastFrameAt = Date.now();     // 真的画出一帧 → 重置画面看门狗\n            if (!gotFrame) {\n              gotFrame = true;\n              failStreak = 0;             // 出画面了，失败计数清零\n            }\n          } finally { frame.close(); }\n        },\n        error: (e) => {\n          // ★ P0 传输层（2026-11-24）：解码错误先按需 IDR 恢复（≤2 次），失败才断流重连。\n          console.warn('[H264] 解码错误:', e);\n          if (idrRecoveryCount < 2) {\n            idrRecoveryCount++;\n            recoverViaIdr();\n          } else {\n            fail('[H264] 解码错误，IDR 恢复失败，断开重连:', e);\n          }\n        }\n      });\n\n      // ── AbortController：取消僵尸 fetch（2026-11-24 修复）──\n      //   旧版 await fetch 无 signal，若服务端 SYN 后不吐首字节（半开/编码器卡死），\n      //   fetch 永久 pending，abortStream 里 reader.cancel() 只能取消「已拿到 body 的\n      //   reader」，卡在 fetch 阶段无法取消 → 僵尸连接 + h264Active 卡 true。\n      const ac = new AbortController();\n      h264Abort = ac;\n      const FIRST_BYTE_MS = 8000;   // 首字节超时：8s 内响应头未回 → 主动 abort\n      let firstByteTimer = setTimeout(() => {\n        if (myGen === h264Gen) {\n          console.warn('[H264] /stream 首字节 ' + FIRST_BYTE_MS + 'ms 无响应，主动取消');\n          try { ac.abort(); } catch (e) {}\n        }\n      }, FIRST_BYTE_MS);\n      let resp;\n      try {\n        resp = await fetch(url, { cache: 'no-store', signal: ac.signal });\n      } finally {\n        clearTimeout(firstByteTimer);\n      }\n      if (myGen !== h264Gen) { try { resp && resp.body && resp.body.cancel(); } catch (e) {} return; }\n      if (!resp.ok || !resp.body) throw new Error('HTTP ' + resp.status);\n      const reader = resp.body.getReader();\n      h264Reader = reader;\n\n      // ── 拉流自愈：无数据超时 → 断流重连（2026-11-23）──\n      //   修「DXGI 播一会儿自己停」：服务端 /stream 断开不一定走 done/异常，\n      //   可能只是编码器停转/后端切换卡住后不再吐数据，read() 永远 pending。\n      //   只在**出过画面之后**才启用看门狗 —— 首帧慢（软编 warmup/等 IDR）不算断流。\n      const STALL_MS = 10000;       // 出过画面后 10s 无「新解码帧」= 画面卡住，触发带退避重连\n      // ★ 看门狗以「解码帧」为准，而非「网络字节」（2026-11-23 修复）：\n      //   旧实现 lastDataAt 在每次 reader.read() 返回字节时刷新，但 /stream 连接保持、\n      //   编码器停转时 read() 会永久 pending；更糟的是即便连接还活着，也可能只吐\n      //   SPS/PPS/SEI 等非画面字节 —— 这些字节一直刷新 lastDataAt，看门狗永远不触发。\n      //   改为 lastFrameAt：只有 decoder output 真的画出一帧才更新，画面卡住必重连。\n      lastFrameAt = Date.now();\n      noFrameSince = Date.now();   // ★ T26：本轮「从未出过画面」的起点（看门狗用）\n      stallTimer = setInterval(() => {\n        if (myGen !== h264Gen) return;\n        if (gotFrame && Date.now() - lastFrameAt >= STALL_MS) {\n          console.warn('[H264] 画面 ' + STALL_MS + 'ms 无新帧，自动重连');\n          fail('[H264] 画面无新帧超时', null);\n          return;\n        }\n        // ★★ 2026-11-24（T26）：**从未出过画面**也要兜底 ★★\n        //   原看门狗有 `gotFrame &&` 前置条件 → 首帧永远不来时它**根本不启用**，\n        //   这正是本案的形态：连上了、字节在流、但一帧都解不出来，看门狗全程沉默。\n        //   分级处理：\n        //     5s 仍未出画面 → 再要一次 IDR（可能是 500ms 限流吞掉了首次请求）\n        //     10s 仍未出画面 → 断流重连（重连 = 新订阅 = 服务端按需 IDR，最可靠的兜底）\n        if (!gotFrame) {\n          const stuck = Date.now() - noFrameSince;\n          if (stuck >= 10000) {\n            console.warn('[H264] 10s 未出任何画面，断流重连');\n            fail('[H264] 首帧超时', null);\n            return;\n          }\n          if (stuck >= 5000 && !noFrameIdrSent) {\n            noFrameIdrSent = true;   // 只补发一次（避免每拍都发）\n            console.warn('[H264] 5s 未出画面，补发一次 IDR 请求');\n            requestIdr();\n          }\n        }\n      }, 1000);\n\n      // ══════════════════════════════════════════════════════════════════\n      //  ★★ 2026-11-24（T26）：首帧 IDR 兜底 —— 修「firstKey 死锁」★★\n      //\n      //  【为什么会死锁】\n      //    :1536  if (!firstKey && !isKey && !isSps && !isPps) continue;  ← 首 key 前丢光所有 delta\n      //    :1537  if (isKey) firstKey = true;\n      //  但 requestIdr() **只在 recoverViaIdr() 里被调用**（:1288），\n      //  而 recoverViaIdr 由 decoder.error / 连续解码失败触发。\n      //  ⇒ 如果连上的那一刻服务端编码器**已经在跑**、下一帧不是 IDR，\n      //     firstKey 永远是 false → 永远丢 delta → **永久黑屏**，且没有任何东西会去请求 IDR。\n      //\n      //  【为什么以前能用】（时间线见报告）\n      //    首次连接时编码器刚创建，NVENC 启动第一帧必是 IDR → firstKey 立刻 true。\n      //    agent 重启 / 页面重连后，编码器已在跑，起点不是 IDR → 触发死锁。\n      //\n      //  【修法】连接建立后若还没拿到首帧，主动要一次 IDR，并**带兜底重试**\n      //    （单次请求可能撞上 500ms 客户端限流 / 服务端 300ms 限流而失败）。\n      //    不破坏任何既有语义：只是多了一条「主动要关键帧」的路径。\n      {\n        firstKeyWaitTimer = setInterval(() => {\n          if (myGen !== h264Gen) return;\n          if (firstKey) { clearInterval(firstKeyWaitTimer); firstKeyWaitTimer = null; return; }\n          firstKeyIdrTries++;\n          if (firstKeyIdrTries > 5) {   // 最多 5 次（约 10s），之后交给下面的看门狗\n            clearInterval(firstKeyWaitTimer); firstKeyWaitTimer = null;\n            console.warn('[H264] 首关键帧请求 5 次仍未到位，交给看门狗处理');\n            return;\n          }\n          console.warn('[H264] 首关键帧未到（firstKey=false），主动请求 IDR 第 ' + firstKeyIdrTries + ' 次');\n          requestIdr();\n        }, 2000);\n      }\n\n      // ── AU（access unit）攒批 ──\n      // ★ WebCodecs 的 chunk.data 必须是完整 AU（AVC Registration §2/§4）：\n      //   annexb 模式的 key chunk 必须同时含 SPS/PPS/IDR。逐 NAL 喂\n      //   decode() 是非法的（自测页实测：decode×14 出 0 帧 + \"Decoding error\"）。\n      //   裸 Annex-B 模式按「SPS/PPS/SEI/新 slice（type 1-5）出现 = 上一个 AU 结束」攒批；\n      //   framed 模式一帧 = 一完整 AU，读完整帧后立即 flush（见下方分支）。\n      //   auNals / auHasKey / auHasData 已在 startStream 顶部声明（framed/裸流/IDR 恢复共用）。\n      let ts = 0;                // AU 时间戳（单调微秒）\n\n      // ── P0：落后检测 + 主动跳帧（2026-11-23，用户实测 4.3 秒画面延迟）──\n      //\n      // 根因链：服务端 200ms 批量推流 + 解码跟不上 → decodeQueue 积压\n      // → WebCodecs 不会主动丢帧 → 每帧落后 66ms 逐帧累积 → 4.3 秒。\n      //\n      // 策略（直播标准做法）：\n      //   · decodeQueueSize > SKIP_QUEUE 阈值 → 停止喂 delta 帧（在 AU 层丢，\n      //     不浪费带宽解码再扔），只放行 key 帧；\n      //   · 丢帧期间若来了 key AU → 立刻恢复喂（解码链从关键帧重建）；\n      //   · 连续跳帧计数达 SKIP_RESET_LIMIT → 直接断流重连重同步\n      //     （无限 GOP 下本地 reset 等不到 IDR，重连=新订阅=按需 IDR，兜底手段）。\n      //   丢的是「追赶过去的旧画面」，用户看到的永远是较新的帧。\n      // ── 阈值跟随 streamFps（2026-11-24 修复：原 SKIP_QUEUE=2 / RESET=10 过激）──\n      // · SKIP_QUEUE：解码队列积压超过约 100~133ms 的帧数才丢 delta —— 单帧解码抖动\n      //   （1 帧）不误杀，持续积压仍被有界兜住，不会重现 4.3s 累积；\n      // · SKIP_RESET_LIMIT：连续跳帧达约 1 秒（帧数）仍无法消化 → 断流重连重同步\n      //   （原 10 帧 ≈ 330ms@30fps 过激，易被瞬时抖动误触发整段重连）。\n      const SKIP_QUEUE = Math.max(4, Math.round((streamFps > 0 ? streamFps : 30) / 10));  // 30→4(~133ms) 60→6 120→12\n      const SKIP_RESET_LIMIT = Math.max(30, (streamFps > 0 ? streamFps : 30));            // 30→30(~1s) 60→60 120→120\n      let skipping = false;          // 是否处于跳帧模式\n      let skippedAUs = 0;            // 本轮连续跳过的 AU 数\n      let skippedTotal = 0;          // 累计跳过（日志用）\n      let decodeErrors = 0;          // 本轮 decode() 同步失败计数（日志用）\n\n      const flushAu = () => {\n        if (!auHasData || !configured) { auNals = []; auHasKey = false; auHasData = false; return; }\n\n        // ── 跳帧判定 ──\n        if (decoder.decodeQueueSize > SKIP_QUEUE && !auHasKey) {\n          // 积压且不是关键帧：整个 AU 丢弃（不解码旧画面）\n          auNals = []; auHasKey = false; auHasData = false;\n          skippedAUs++; skippedTotal++;\n          skipping = true;\n          if (skippedAUs >= SKIP_RESET_LIMIT) {\n            // 兜底：队列长期消化不动 → 重连式重同步（2026-11-23 修复 W2）\n            //   无限 GOP 下 decoder.reset() 后服务端不会主动发 SPS/IDR，本地 reset\n            //   干等新 IDR 是死路（每轮白付 10s 黑屏）。改为直接断流重连：\n            //   重连 = 新订阅，服务端按需发 IDR，立刻重建解码链。\n            console.warn('[H264] 解码积压 ' + skippedAUs + ' 帧，直接断流重连重同步');\n            fail('[H264] 解码积压过重，重连重同步', null);\n            return;\n          }\n          return;\n        }\n        if (auHasKey) {\n          // 关键帧永远放行：既是恢复点，也顺带清掉跳帧状态\n          if (skipping) console.warn('[H264] 跳帧 ' + skippedAUs + ' 个 AU 后由 IDR 恢复');\n          skipping = false; skippedAUs = 0;\n        }\n        // AU → 单个 annexb 字节块（4 字节起始码 + 各 NAL）\n        let total = 0;\n        for (const n of auNals) total += 4 + n.length;\n        const data = new Uint8Array(total);\n        let off = 0;\n        for (const n of auNals) {\n          data[off] = 0; data[off+1] = 0; data[off+2] = 0; data[off+3] = 1;\n          data.set(n, off + 4);\n          off += 4 + n.length;\n        }\n        try {\n          const decP = decoder.decode(new EncodedVideoChunk({\n            type: auHasKey ? 'key' : 'delta',\n            timestamp: ts,\n            data\n          }));\n          // ★★ 2026-11-24（T25 BUG-2）：decode() 在 closed/error 态返回 **undefined** ★★\n          //   原来直接链式写 .decode(...).catch(...)，decoder 处于 closed 时 decode() 返回 undefined，\n          //   于是读 undefined 的属性 catch 直接抛 TypeError：\n          //     \"Cannot read properties of undefined (reading 'catch')\"\n          //   **这个报错每帧都在刷**，既污染 console 又说明解码链在异常态空转，\n          //   会掩盖真正的问题（也干扰排障）。\n          //   规范里 decode() 正常返回 Promise，但实现允许在不可用状态下返回 undefined，\n          //   所以必须先判空再用。\n          //   ★ 同步错误（InvalidStateError/DataError）由外层 catch 处理，这里只管异步 rejection。\n          if (decP && typeof decP.catch === 'function') {\n            decP.catch(e => {\n              // 异步 rejection（decoder 已 close/error 态时 decode() 的 Promise 会 reject，\n              // 同步 try 捕不到，不接会成未处理 Promise）→ 计数 + 限频日志，不吞。\n              // 真正的解码错误仍由 decoder.error 回调统一走 fail() 重连。\n              decodeErrors++;\n              if (decodeErrors <= 3 || decodeErrors % 100 === 0) {\n                console.warn('[H264] decode() 异步失败，丢弃本 AU:', e);\n              }\n            });\n          } else if (!decP) {\n            // decode() 返回 undefined = 解码器不可用（closed / 未就绪 / 正在 reset）。\n            decodeErrors++;\n            if (decodeErrors <= 3 || decodeErrors % 100 === 0) {\n              console.warn('[H264] decode() 返回 undefined（解码器状态: ' + (decoder && decoder.state) + '），丢弃本 AU');\n            }\n            // ★★ 2026-11-24（T26）：这条路径**必须触发恢复**，不能只记日志 ★★\n            //   原来这里只 console.warn，什么恢复动作都不做：\n            //   而 recoverViaIdr() 只由 decoder.error / 连续解码失败触发，\n            //   decode() 返回 undefined **既不抛异常也不触发 error 回调** →\n            //   firstKey 永远 false → 永远丢帧 → **永久黑屏，且无人请求 IDR**。\n            //   这就是死锁的第 4 环。\n            //   首次出现时立刻要一次 IDR（内部有 500ms 客户端限流，不会风暴）。\n            if (decodeErrors === 1) requestIdr();\n          }\n        } catch (e) {\n          // 同步抛错（InvalidStateError/DataError）：解码器饱和或状态异常时丢弃本 AU，\n          // 等下一帧；异步错误仍由 decoder.error 回调统一走 fail() 重连。\n          decodeErrors++;\n          if (decodeErrors <= 3 || decodeErrors % 100 === 0) {\n            console.warn('[H264] decode() 同步失败，丢弃本 AU:', e);\n          }\n        }\n        ts += Math.round(1000000 / (streamFps > 0 ? streamFps : 30));  // 跟随服务端 streamFps（默认 30fps）；单调微秒\n        auNals = [];\n        auHasKey = false;\n        auHasData = false;\n      };\n\n      if (framed) {\n        // ── framed 模式（v2）：读 [4B 大端长度][数据]，一帧 = 一完整 AU，立即 flush ──\n        //   帧边界由长度前缀确定，网络块边界 ≠ 帧边界不再切错帧（花屏/卡死根因之一）。\n        let frameBuf = new Uint8Array(0);\n        let wantLen = 4, frameLen = 0;\n        while (myGen === h264Gen) {\n          const { done, value } = await reader.read();\n          if (myGen !== h264Gen) break;\n          if (done) break;\n          const merged = new Uint8Array(frameBuf.length + value.length);\n          merged.set(frameBuf, 0);\n          merged.set(value, frameBuf.length);\n          frameBuf = merged;\n          while (true) {\n            if (wantLen === 4) {\n              if (frameBuf.length < 4) break;\n              frameLen = ((frameBuf[0] << 24) | (frameBuf[1] << 16) | (frameBuf[2] << 8) | frameBuf[3]) >>> 0;\n              frameBuf = frameBuf.subarray(4);\n              wantLen = frameLen;\n            }\n            if (frameBuf.length < frameLen) break;\n            const one = frameBuf.subarray(0, frameLen);\n            frameBuf = frameBuf.subarray(frameLen);\n            wantLen = 4;\n            // 一完整帧 = 一 AU：切 NAL（complete=true 连末 NAL 一起切出）→ 攒 auNals → 立即 flush\n            const nals = splitAnnexB(one, true).units;\n            if (!nals.length) continue;\n            auNals = []; auHasKey = false; auHasData = false;\n            for (const nal of nals) {\n              const type = nal[0] & 0x1f;\n              const isKey = (type === 5);\n              const isSps = (type === 7), isPps = (type === 8);\n              const isVcl = (type >= 1 && type <= 5);\n              if (isSps && !configured) tryConfigure(nal);\n              if (!configured) continue;\n              if (!firstKey && !isKey && !isSps && !isPps) continue;   // 首 key 前丢 delta\n              if (isKey) firstKey = true;\n              auNals.push(nal);\n              if (isVcl) auHasData = true;\n              if (isKey) auHasKey = true;\n            }\n            flushAu();   // 帧边界 = AU 边界，立即解码（消除裸 Annex-B 的「等下一个 VCL」1 帧延迟）\n          }\n        }\n      } else {\n        // ── 老协议（裸 Annex-B）：扫起始码 + 跨读攒批（兼容旧 agent / 主程序代理）──\n        let pending = new Uint8Array(0);\n        while (myGen === h264Gen) {\n          const { done, value } = await reader.read();\n          if (myGen !== h264Gen) break;      // 换代：立刻退出，不碰已换人的 reader\n          if (done) break;\n\n          const merged = new Uint8Array(pending.length + value.length);\n          merged.set(pending, 0);\n          merged.set(value, pending.length);\n          pending = merged;\n\n          const nals = splitAnnexB(pending);\n          pending = nals.pending;\n          if (!nals.units.length) continue;\n\n          for (const nal of nals.units) {\n            if (myGen !== h264Gen) break;\n            const type = nal[0] & 0x1f;\n            const isKey = (type === 5);\n            const isSps = (type === 7), isPps = (type === 8);\n            const isVcl = (type >= 1 && type <= 5);   // slice/IDR = 图像数据\n\n            // 首个 SPS 之前绝不能 decode —— 没 configure 的 decoder 会抛 InvalidStateError。\n            if (isSps && !configured) tryConfigure(nal);\n\n            if (!configured) continue;       // 等 SPS\n\n            // ── AU 边界判定：SPS/PPS/新 VCL = 新 AU 开始 ──\n            if (isVcl && auHasData) flushAu();   // 新图像数据 → 先结上一 AU\n            if (isSps || isPps) {\n              // 参数集挂在「即将开始的 AU」上：若已攒着 AU 尾巴，先结掉\n              if (auHasData) flushAu();\n            }\n\n            // ★ 从 IDR 起播：重连后第一个 key 之前的 delta 一律丢弃 ——\n            //   半个 GOP 的 delta 帧缺参考帧，喂进去只会花屏/报错。\n            if (!firstKey && !isKey && !isSps && !isPps) continue;\n            if (isKey) firstKey = true;\n\n            auNals.push(nal);\n            if (isVcl) auHasData = true;   // ★ 只有 VCL 算「AU 有图像」；SPS/PPS/SEI\n                                          //   是参数，单独不能成 AU（否则会 flush\n                                          //   出没有图像数据的非法 chunk）\n            if (isKey) auHasKey = true;\n          }\n          // network chunk 边界不等于 AU 边界，攒批的 AU 留到下一个 VCL 到达\n          // 或流结束时 flush（见下方 done 分支）。\n        }\n      }\n      // 流结束：把攒着的最后一个 AU 喂掉\n      if (myGen === h264Gen) {\n        flushAu();\n        if (skippedTotal > 0) console.info('[H264] 本轮累计跳帧 ' + skippedTotal + ' 个 AU（解码积压保护）');\n        fail('[H264] 流结束', null);\n      }\n    } catch (e) {\n      fail('[H264] 流断开:', e);\n    } finally {\n      if (stallTimer) { clearInterval(stallTimer); stallTimer = null; }\n      // ★ 2026-11-24（T26）：首关键帧重试定时器必须一并清掉，否则换代后仍在跑\n      if (firstKeyWaitTimer) { clearInterval(firstKeyWaitTimer); firstKeyWaitTimer = null; }\n      if (myGen === h264Gen) {\n        h264Active = false;\n        h264Reader = null;\n        h264Abort = null;\n      }\n      try { if (decoder && decoder.state !== 'closed') decoder.close(); } catch (e) {}\n    }\n  }\n\n// ── 输入 ────────────────────────────────────────────────────\n// ★ 2026-11-23 输入链路修复（INPUT-CHANNEL-GAP-20261123.md）：\n//   · down/up/click 走**同一 FIFO 串行队列**（sendInputOrdered）→ 保序，\n//     替代旧「每个 pointer 事件一次独立 fetch 并发无序」导致的 up 丢失/粘键；\n//   · up/releaseall 传输失败重试 1 次（幂等，重发无害）；down/click 不重试（避免双按/双击）；\n//   · move 走 noWait 不等回包（RTT 不再拖慢拖动，latest-wins 由 movePending 保留）；\n//   · 页面 pagehide/beforeunload/visibilitychange(hidden) → releaseAllInputs 防粘键。\n\n// ── P0 传输层（2026-11-24）：输入 WebSocket 持久通道 ──\n//   move/click/wheel/key 优先走 /ws/input（低延迟 + 天然保序），HTTP POST /api/input 保留为降级。\n//   WS 握手失败 / 连接中断 → wsInputFailed=true，本次会话内永久回退 HTTP。\nlet wsInput = null;             // 当前 WebSocket 实例\n// ★★ 2026-11-24（T32 P0-1）：失败**不再永久降级**，改为指数退避重连 ★★\n//\n//   【原来的 bug】wsInputFailed 一旦置 true，getWsInput() 直接 return null，\n//   本次会话内**永不恢复**。而现实中 agent 重启/网络抖动都会造成一次握手失败 ——\n//   于是用户被永久钉在 HTTP 降级路径上，那条路径**没有保序**（见 P0-2），\n//   表现就是「拖动落点偏移」，且**刷新页面才能好**（实际也很难想到要刷新）。\n//\n//   【修法】失败后进入退避：下次允许尝试的时刻 wsRetryAt，间隔指数增长 500→1s→2s…→30s 封顶。\n//   重连成功后立即清零，恢复正常 WS 通道。\nlet wsInputFailed = false;      // WS 当前是否处于「失败退避」状态\nlet wsRetryAt = 0;              // 下次允许尝试重建 WS 的时刻（Date.now()）\nlet wsRetryDelay = 500;         // 当前退避间隔（指数增长，封顶 30s）\nlet wsInputBase = '';           // WS 绑定的直连 base（切账号时据此重建）\nlet wsInflight = null;          // 当前在途请求的 resolve（WS 单 in-flight 保序）\nlet wsQueue = [];               // 排队中的 { body, resolve }\n\nfunction wsInputUrl() {\n  const base = directBase(cur);\n  if (!base) return '';\n  const wsBase = base.replace(/^http/, 'ws');   // http→ws, https→wss\n  const tok = directToken();\n  return wsBase + '/ws/input' + (tok ? '?token=' + encodeURIComponent(tok) : '');\n}\n\nfunction closeWsInput() {\n  const ws = wsInput;\n  wsInput = null; wsInputBase = ''; wsInflight = null; wsQueue = [];\n  if (ws) { try { ws.close(); } catch (e) {} }\n}\n\nfunction failWsQueue() {\n  while (wsQueue.length) {\n    const item = wsQueue.shift();\n    if (item && item.resolve) item.resolve(null);\n  }\n  if (wsInflight) { const r = wsInflight; wsInflight = null; r(null); }\n}\n\n// ★ T32 P0-1：标记一次 WS 失败 → 进入退避，而不是永久放弃\nfunction markWsFailed() {\n  wsInputFailed = true;\n  wsRetryAt = Date.now() + wsRetryDelay;\n  // 指数退避：500 → 1000 → 2000 → ... → 30000 封顶\n  wsRetryDelay = Math.min(wsRetryDelay * 2, 30000);\n}\n\n// ★ T32 P0-1：WS 重连成功 → 立刻恢复正常通道并清零退避\nfunction markWsHealthy() {\n  if (wsInputFailed || wsRetryDelay !== 500) {\n    // 只在「从失败中恢复」时打日志，避免正常连接刷屏\n    if (wsInputFailed) console.info('[WS] 输入通道已恢复走 WebSocket（退出 HTTP 降级）');\n  }\n  wsInputFailed = false;\n  wsRetryAt = 0;\n  wsRetryDelay = 500;\n}\n\nfunction getWsInput() {\n  if (typeof WebSocket === 'undefined') return null;\n  // ★ T32 P0-1：处于失败退避期 —— 只在到达重试时刻后才允许重建\n  if (wsInputFailed) {\n    if (Date.now() < wsRetryAt) return null;\n    wsInputFailed = false;   // 退避到期：放行一次尝试（成功会走 markWsHealthy）\n  }\n  const base = directBase(cur);\n  if (!base) return null;\n  if (wsInput && wsInputBase === base && (wsInput.readyState === WebSocket.CONNECTING || wsInput.readyState === WebSocket.OPEN)) return wsInput;\n  closeWsInput();   // 切账号 / 旧连接已关 → 重建\n  try {\n    const ws = new WebSocket(wsInputUrl());\n    wsInput = ws; wsInputBase = base;\n    // ★ T32 P0-1：连上就说明通道健康 —— 清零退避，退出 HTTP 降级\n    ws.onopen = () => { markWsHealthy(); pumpWs(); };\n    ws.onmessage = (ev) => {\n      let d = null;\n      try { d = JSON.parse(ev.data); } catch (e) {}\n      const resolve = wsInflight; wsInflight = null;\n      if (resolve) resolve(d);\n      pumpWs();\n    };\n    // ★ T32 P0-1：异常断开也进入退避（正常 closeWsInput 会先把 wsInput 置 null，此处不会误判）\n    ws.onclose = () => {\n      const wasCurrent = (wsInput === ws);\n      if (wasCurrent) { wsInput = null; wsInputBase = ''; }\n      failWsQueue();\n      if (wasCurrent && !wsInputFailed) markWsFailed();\n    };\n    // ★ T32 P0-1：失败进入退避（不再永久降级）\n    ws.onerror = () => { markWsFailed(); if (wsInput === ws) { wsInput = null; wsInputBase = ''; } failWsQueue(); try { ws.close(); } catch (e) {} };\n  } catch (e) { markWsFailed(); wsInput = null; }\n  return wsInput;\n}\n\nfunction wsSend(body) {\n  return new Promise(resolve => {\n    wsQueue.push({ body, resolve });\n    pumpWs();\n  });\n}\n\nfunction pumpWs() {\n  if (wsInflight || wsQueue.length === 0) return;\n  const ws = wsInput;\n  if (!ws || ws.readyState !== WebSocket.OPEN) return;   // 未 open：等 onopen 回调再 pump\n  const item = wsQueue.shift();\n  wsInflight = item.resolve;\n  try { ws.send(JSON.stringify(item.body)); }\n  catch (e) { wsInflight = null; item.resolve(null); pumpWs(); }\n}\n\nasync function postInputRaw(body, httpOnly) {\n  const base = directBase(cur);\n  const fullBody = Object.assign({}, body, base ? { sid: cur.sessionId } : {});\n  // ★ WS 优先（低延迟 + 保序），HTTP 降级\n  //   ★ T32：httpOnly=true 时跳过 WS —— 供 sendInputNoWait 在 HTTP 降级模式下\n  //     用队列保序时冻结传输方式（详见 sendInputNoWait 的注释）。\n  if (!httpOnly) {\n    const ws = getWsInput();\n    if (ws && ws.readyState === WebSocket.OPEN) {\n      return await wsSend(fullBody);\n    }\n  }\n  const url = base ? (base + '/api/input') : ('/api/input?sid=' + cur.sessionId);\n  const headers = { 'Content-Type': 'application/json' };\n  if (base && directToken()) headers['X-SoulLink-Token'] = directToken();\n  const r = await fetch(url, {\n    method: 'POST', headers: headers,\n    body: JSON.stringify(fullBody)\n  });\n  return r.json();\n}\n\n// ── 远端光标位置回传的消费端（2026-11-24，AUDIT-03 §4.3 P1）─────────\n//   服务端在执行 move/click 后把实际光标位置随响应回传（cursor:{nx,ny,visible}）。\n//   这里统一消费：有就画 rcCursor（低延迟回显），没有就静默跳过——\n//   服务端配套未上线时零副作用（字段不存在 → 不显示，也不报错）。\nfunction consumeCursorEcho(resp) {\n  try {\n    const c = resp && resp.cursor;\n    if (!c || typeof c.nx !== 'number' || typeof c.ny !== 'number') return;\n    const vis = (typeof c.visible === 'boolean') ? c.visible : true;\n    updateRemoteCursor(c.nx, c.ny, vis);\n  } catch (e) { /* 回显失败不影响输入链路 */ }\n}\n\n// 通用发送（仍可 await 结果；wheel/key/moverel 等非保序、非高频动作沿用）\nasync function sendInput(action, nx, ny, extra) {\n  if (!cur) return null;\n  try {\n    const resp = await postInputRaw(Object.assign({ action, x: nx, y: ny }, extra || {}));\n    consumeCursorEcho(resp);\n    return resp;\n  } catch { return null; }\n}\n\n// ── 按键状态动作的 FIFO 串行队列（保序）──────────────────────\n// down/up/click 是「状态转移」，必须严格按产生顺序送达代理：\n//   拖动 = down → (n×move) → up，若 down/up 并发乱序，远端会停在按下态。\n// 这里所有 down/up/click 进同一个队列，单 in-flight 逐条 await 发送，天然保序。\nlet inputQueue = [];           // FIFO：{ body, resolve }\nlet inputSending = false;      // 队列是否正在发送头部\nconst INPUT_RETRYABLE = new Set(['up', 'releaseall']);   // 幂等：重发无害\n\nfunction sendInputOrdered(action, nx, ny, extra) {\n  if (!cur) return Promise.resolve(null);\n  const body = Object.assign({ action, x: nx, y: ny }, extra || {});\n  return new Promise(resolve => {\n    inputQueue.push({ body, resolve });\n    pumpInput();\n  });\n}\n\nfunction pumpInput() {\n  if (inputSending || inputQueue.length === 0) return;\n  const item = inputQueue.shift();\n  inputSending = true;\n  (async () => {\n    let resp = null;\n    try { resp = await postInputRaw(item.body, item.httpOnly); }\n    catch (e) { resp = null; }\n    // up/releaseall 幂等：传输失败（fetch 抛异常）重试 1 次；\n    // down/click 不重试——down 重试可能双 LEFTDOWN 反而粘键，click 重试会双击。\n    // ★ T32：move 也**不**重试 —— 它是过时位置，重发只会造成光标跳动；\n    //   且 fireAndForget 语义本就是「发出去就不管了」。\n    if (resp == null && !item.fireAndForget && INPUT_RETRYABLE.has(item.body.action)) {\n      try { resp = await postInputRaw(item.body, item.httpOnly); }\n      catch (e) { resp = null; }\n    }\n    if (resp == null || resp.ok === false) {\n      // ★ T32：fireAndForget（move）失败不刷屏 —— HTTP 降级下高频 move 失败是常态，\n      //   每 8ms 一条 warn 会把 console 冲爆、反而掩盖真问题。\n      if (!item.fireAndForget) console.warn('[input] ' + item.body.action + ' 发送失败', resp);\n    }\n    else { consumeCursorEcho(resp); }   // down/up/click 响应也可能带光标位置（move 亦然，P0-2 后走队列）\n    item.resolve(resp);\n    inputSending = false;\n    pumpInput();               // 串行发下一条\n  })();\n}\n\n// 单向发送：fire-and-forget，不等回包（move/moverel 高频位移专用）\nfunction sendInputNoWait(action, nx, ny, extra) {\n  if (!cur) return;\n  const body = Object.assign({ action, x: nx, y: ny }, extra || {});\n\n  // ★★ 2026-11-24（T32 P0-2）：HTTP 降级下 move **必须保序** ★★\n  //\n  //   【原来的 bug】这里直接 postInputRaw(body) 发一条独立 fetch。\n  //   WS 模式下没问题（同一条 TCP 连接天然有序），但 **HTTP 降级时**：\n  //     · move 走 fetch A（并发、不等回包）\n  //     · up   走 sendInputOrdered → pumpInput（单 in-flight 串行）→ fetch B\n  //     ⇒ A、B 是**两条独立 HTTP 请求，浏览器/网络不保证谁先到**\n  //     ⇒ up(P3) 先到、move(P1) 后到 → 服务端先松手再把光标拽回 P1\n  //     ⇒ **落点偏移**（用户说的「操作太快落点飘逸」）\n  //\n  //   【修法】HTTP 模式下把 move 也塞进 inputQueue，用同一条单 in-flight 串行链保序。\n  //     代价：HTTP 模式下 move 会排队（比并发慢）。\n  //     但这正是该付的代价 —— 用户明确表示「落点偏移比 move 慢严重得多」。\n  //     且 WS 一旦恢复（P0-1 退避重连），自动回到快速直发路径。\n  //\n  //   fireAndForget=true：语义仍是「不等回包」（resolve 空函数），不改变 noWait 契约；\n  //     光标回显仍由 pumpInput 在响应到达时照常消费。\n  const ws = getWsInput();\n  if (ws && ws.readyState === WebSocket.OPEN) {\n    // WS：保持原样（低延迟 + TCP 天然保序），性能不受影响\n    //   ★ 回包虽不等（noWait 语义不变），但拿到了就读光标位置：\n    //     move 是光标位置最密集的来源，8ms 节流 + latest-wins 已天然限频。\n    postInputRaw(body).then(r => { if (r && r.ok !== false) consumeCursorEcho(r); }).catch(() => {});\n    return;\n  }\n\n  // HTTP 降级：进串行队列保序（单 in-flight），但不等回包。\n  //\n  //   ★★ 必须用 httpOnly=true 冻结传输方式，不能让 postInputRaw 再去判一次 WS ★★\n  //     否则会出现这个竞态：\n  //       ① 此刻 WS 未就绪 → 走到这里，决定「入队走 HTTP」\n  //       ② 入队后、pumpInput 真正发送前，WS 恰好连上\n  //       ③ postInputRaw 内部再判一次 WS，发现可用 → **走 wsSend 而不是 HTTP**\n  //     ⇒ 结果虽然也能发出去（顺序甚至更好），但「入队保序」的意图被绕过，\n  //       且若同一条 body 被其它路径也发一次，就会**重复注入一次 move**。\n  //     传 httpOnly 让 postInputRaw 直接走 fetch，行为可预测。\n  inputQueue.push({ body: body, resolve: function () {}, fireAndForget: true, httpOnly: true });\n  pumpInput();\n}\n\n// ── 按住心跳（Web 前端断连看门狗的「我还活着」信号）────────────\n// 代理端只能通过「是否有输入活动」判断前端是否断连；但「按住不动」是正常操作，\n// 会产生 0 输入活动。所以按住期间每 2s 发一次 noop 心跳，代理据此区分：\n//   活着但静止按住（有心跳） ≠ 前端已断开（心跳停了 → 看门狗释放）。\n// 阈值：代理 WebIdleReleaseMs=5000 > 2×2000，静止按住不会误释放。\nfunction hasHeldInput() {\n  if (drag && drag.moved) return true;\n  if (look && look.mode === 'drag') return true;\n  return false;\n}\nlet heldHeartbeat = null;\nfunction ensureHeldHeartbeat() {\n  if (heldHeartbeat) return;\n  heldHeartbeat = setInterval(() => {\n    if (hasHeldInput()) sendInputNoWait('noop', -1, -1, {});\n  }, 2000);\n}\nensureHeldHeartbeat();\n\n// ★ 释放所有按住的键：切后台/刷新/关页时兜底（防「松手远端没松开」）。\n//   直接 fire-and-forget + keepalive（卸载阶段仍能送达）；幂等，重发无害。\nfunction releaseAllInputs() {\n  if (!cur) return;\n  inputQueue.length = 0;       // 丢弃未发送的 down/up/click，避免它们在 releaseall 之后到达又粘键\n  const base = directBase(cur);\n  const body = Object.assign({ action: 'releaseall', x: -1, y: -1 }, base ? { sid: cur.sessionId } : {});\n  const url = base ? (base + '/api/input') : ('/api/input?sid=' + cur.sessionId);\n  const headers = { 'Content-Type': 'application/json' };\n  if (base && directToken()) headers['X-SoulLink-Token'] = directToken();\n  try {\n    fetch(url, { method: 'POST', headers: headers, body: JSON.stringify(body), keepalive: true }).catch(() => {});\n  } catch (e) {}\n}\n\nfunction norm(e) {\n  // ★ 画面唯一来源是 <canvas id=\"vframe\">（JPEG img 已随轮询通路一起移除）。\n  //   canvas.width/height 是解码帧的原始分辨率；还没出画面时退回 16:9 兜底，\n  //   此时点击坐标本来就不该生效（ph 提示层挡着）。\n  const cv = $('vframe');\n  const iw = (cv && cv.width > 0) ? cv.width : 16;\n  const ih = (cv && cv.height > 0) ? cv.height : 9;\n  // ★ 2026-11-24 修落点偏移：用 canvas 自身实际显示矩形（含 max-height 夹住 +\n  //   object-fit letterbox 黑边），而非 #scr 的 rect。#scr 高度可能 > canvas 显示高度，\n  //   旧实现按 #scr 算居中偏移 → 纵向落点系统性偏移（见 CURSOR-AND-POINTER-LOCK）。\n  const rect = cv && cv.classList.contains('show') ? cv.getBoundingClientRect() : $('scr').getBoundingClientRect();\n  // canvas 内 object-fit:contain 的黑边：视频内容区 = min(rect.w, rect.h * 比例)\n  const dwContent = Math.min(rect.width, rect.height * iw / ih);\n  const dhContent = Math.min(rect.height, rect.width * ih / iw);\n  const ox = (rect.width - dwContent) / 2, oy = (rect.height - dhContent) / 2;\n  const cx = (e.touches ? e.touches[0].clientX : e.clientX) - rect.left - ox;\n  const cy = (e.touches ? e.touches[0].clientY : e.clientY) - rect.top - oy;\n  return { nx: Math.max(0, Math.min(1, cx / dwContent)), ny: Math.max(0, Math.min(1, cy / dhContent)) };\n}\n\n// ══════════ 远端光标回显（2026-11-24，替代已删的 #plCursor 蓝球）═════════════\n//   蓝球是「本机鼠标的零延迟影子」，不回答用户唯一关心的问题（远端光标在哪），\n//   已整体删除（AUDIT-20261124-03-CURSOR-UX.md §4.2 C4-C7）。\n//   替代者 #rcCursor 是**服务端上报的远端光标位置**的回显：\n//     · 数据源：输入响应里的 cursor:{nx,ny,visible} 字段（服务端在执行\n//       move/click 后回传 GetCursorPos 实际位置，见 AUDIT §4.3 P1）——\n//       消费端在这里，发送端是服务端成员的配套改动（未上线前 rcCursor\n//       静默不显示，无副作用）；\n//     · 延迟 ≈ 输入 RTT（回环 3ms 级）+ 8ms 节流，而不是画面 444ms；\n//     · 几何与 norm() 同源（frameRect()），保证回显位置 = 实际落点坐标系；\n//     · 节流跟随 moverel/move 的既有 8ms latest-wins 节奏，无新定时器\n//       （性能约束：不新建周期任务）。\nfunction frameRect() {\n  // 从 norm() 提取的公共几何：canvas 实际显示矩形 + object-fit:contain 黑边。\n  const cv = $('vframe');\n  const iw = (cv && cv.width > 0) ? cv.width : 16;\n  const ih = (cv && cv.height > 0) ? cv.height : 9;\n  const rect = cv && cv.classList.contains('show') ? cv.getBoundingClientRect() : $('scr').getBoundingClientRect();\n  const dwContent = Math.min(rect.width, rect.height * iw / ih);\n  const dhContent = Math.min(rect.height, rect.width * ih / iw);\n  const ox = (rect.width - dwContent) / 2, oy = (rect.height - dhContent) / 2;\n  return { rect, ox, oy, dwContent, dhContent };\n}\nconst rcCursorEl = $('rcCursor');\nlet rcCursorVisible = false;      // 远端光标当前是否可见（visible:false 时隐藏）\nfunction updateRemoteCursor(nx, ny, visible) {\n  if (!rcCursorEl) return;\n  if (visible === false) { rcCursorEl.style.display = 'none'; rcCursorVisible = false; return; }\n  // ★ 2026-11-24 修复「点一下光标跳到右上角」：\n  //   rcCursor 是 #scr 的绝对定位子元素，所以位移基准必须用 #scr 的矩形；\n  //   而画面内容（canvas 显示区域 + object-fit:contain 黑边）要用 #vframe 的矩形算。\n  //   旧实现把两者混用：frameRect() 返回的是 #vframe 的 rect（视口坐标），\n  //   又拿它减 #scr.left 再加进 translate —— 当 canvas 不是从 #scr 左上角铺满时，\n  //   系数完全错位，光标就飞到角落。\n  //   正确做法：全部换算到 #scr 的局部坐标系再定位。\n  const scrRect = $('scr').getBoundingClientRect();\n  const g = frameRect();\n  // 画面内容在 #scr 局部坐标系里的左上角与尺寸\n  const contentLeft = (g.rect.left - scrRect.left) + g.ox;\n  const contentTop  = (g.rect.top  - scrRect.top ) + g.oy;\n  const x = contentLeft + nx * g.dwContent;\n  const y = contentTop  + ny * g.dhContent;\n  rcCursorEl.style.transform = 'translate(' + x + 'px,' + y + 'px)';\n  rcCursorEl.style.display = 'block';\n  rcCursorVisible = true;\n}\nfunction hideRemoteCursor() {\n  if (rcCursorEl) rcCursorEl.style.display = 'none';\n  rcCursorVisible = false;\n}\n\nfunction ripple(e) {\n  const el = $('rip'), rect = $('scr').getBoundingClientRect();\n  el.style.left = ((e.touches ? e.touches[0].clientX : e.clientX) - rect.left) + 'px';\n  el.style.top = ((e.touches ? e.touches[0].clientY : e.clientY) - rect.top) + 'px';\n  el.classList.remove('go'); void el.offsetWidth; el.classList.add('go');\n}\n\nlet drag = null;\n\n// ══════════ 鼠标模式：绝对 / 相对（月光式，2026-11-23）══════════\n//   绝对（默认）= 现状：点哪指哪 + 长按拖动（触屏状态机原样）。\n//   相对 = 月光语义：单指滑动 → moverel 转视角；单指点一下 → 绝对定位 + 左键点击；\n//           双指 → 滚轮（上下滑）/ 右键（点一下）。\n//   选择持久化到 localStorage，退出画面/刷新后保留。\nconst MOUSE_MODE_KEY = 'sl_mouse_mode';\nlet mouseMode = 'absolute';    // 'absolute' | 'relative'\ntry {\n  const m = localStorage.getItem(MOUSE_MODE_KEY);\n  if (m === 'relative' || m === 'absolute') mouseMode = m;\n} catch (e) {}\n\nfunction applyMouseMode() {\n  const abs = $('mmAbs'), rel = $('mmRel');\n  if (abs) abs.classList.toggle('on', mouseMode === 'absolute');\n  if (rel) rel.classList.toggle('on', mouseMode === 'relative');\n}\nconst __mmAbs = $('mmAbs'), __mmRel = $('mmRel');\nif (__mmAbs) __mmAbs.onclick = () => { mouseMode = 'absolute'; try { localStorage.setItem(MOUSE_MODE_KEY, 'absolute'); } catch (e) {} applyMouseMode(); };\nif (__mmRel) __mmRel.onclick = () => { mouseMode = 'relative'; try { localStorage.setItem(MOUSE_MODE_KEY, 'relative'); } catch (e) {} applyMouseMode(); };\napplyMouseMode();\n\n// ══════════ Pointer Lock（桌面鼠标转视角，2026-11-23）═════════════\n// ★ 对齐 POINTER-LOCK-DESIGN-20261123.md + decision/pointer-lock-esc-shortcut。\n//   进入：桌面左键点画面 → requestPointerLock()，成功 pointerLocked=true，光标隐藏（浏览器自动）。\n//   转视角：锁定态 mousemove 的 movementX/Y × sensitivity → 复用 lookDelta/lookAcc/flushLook 8ms 合并。\n//   点击：复用现有 pointerdown/up drag 状态机（锁定态 pointermove 被跳过，单击=click）。\n//   逃生门：ESC = 浏览器强制解锁 + 补发一次 Esc 开菜单（方案 C）；自定义快捷键 = 纯解锁不弹菜单。\nconst PL_SENS_KEY = 'sl_pl_sens';\nconst PL_ESC_KEY = 'sl_pl_escape';\nconst PL_SUPPORTED = ('pointerLockElement' in document) && ('requestPointerLock' in document.body);\nlet pointerLocked = false;\nlet plLockPending = null;      // 首击挂起：{nx,ny} 待锁定结果决定是否补发 click\nlet plExitByShortcut = false;    // 快捷键逃生门主动退出（不补发 ESC）\nlet plExitByLifecycle = false;   // 生命周期/断线/关画面主动退出（不补发 ESC）\nlet plLockCooldownUntil = 0;     // Esc 后重锁冷却（规范：立即重锁会被拒）\n// ★ 2026-11-24：Pointer Lock 改为「用户显式要求才进」。\n//   默认 false → 左键是普通点击，光标始终可见（修复\"点一下就没光标\"）。\n//   只有顶栏锁定按钮把它置 true 后，下一次左键才真正进入锁定态（用于游戏转视角）。\nlet plLockWanted = false;\nlet plSensitivity = 1.0;         // movementX/Y 灵敏度（浏览器单位不统一）\nlet plEscape = 'ctrl_alt_shift'; // 逃生门快捷键\nlet plAltHoldTimer = null;       // 长按 Alt 计时器\n\ntry {\n  const s = parseFloat(localStorage.getItem(PL_SENS_KEY));\n  if (isFinite(s) && s >= 0.1 && s <= 3) plSensitivity = s;\n  const esc = localStorage.getItem(PL_ESC_KEY);\n  if (esc === 'ctrl_alt_shift' || esc === 'alt' || esc === 'alt_hold' || esc === 'ctrl_alt') plEscape = esc;\n} catch (e) {}\n\nfunction applyPlEscape() {\n  const el = $('plEscape');\n  if (el) el.value = plEscape;\n  const r = $('plSens');\n  if (r) r.value = String(plSensitivity);\n  const sv = $('plSensV');\n  if (sv) sv.textContent = plSensitivity.toFixed(1);\n}\n\n// 逃生门快捷键命中判定（全部为修饰键组合，不会误注入远端——现有 keydown 会过滤纯修饰键）\nfunction matchPlEscape(e) {\n  if (plEscape === 'ctrl_alt_shift') return e.ctrlKey && e.altKey && e.shiftKey;\n  if (plEscape === 'ctrl_alt') return e.ctrlKey && e.altKey;\n  if (plEscape === 'alt') return e.altKey && (e.key === 'Alt');\n  return false;\n}\n\ndocument.addEventListener('keydown', e => {\n  if (!pointerLocked) return;\n  // 长按 Alt：按下即计时，800ms 后仍按住 → 触发（alt_hold 不走 matchPlEscape）\n  if (plEscape === 'alt_hold' && e.key === 'Alt' && !e.repeat) {\n    if (plAltHoldTimer) clearTimeout(plAltHoldTimer);\n    plAltHoldTimer = setTimeout(() => {\n      plAltHoldTimer = null;\n      if (pointerLocked) doPlShortcutExit();\n    }, 800);\n    return;\n  }\n  if (plEscape !== 'alt_hold' && matchPlEscape(e)) {\n    doPlShortcutExit();\n  }\n});\n\ndocument.addEventListener('keyup', e => {\n  if (e.key === 'Alt' && plAltHoldTimer) { clearTimeout(plAltHoldTimer); plAltHoldTimer = null; }\n});\n\nfunction doPlShortcutExit() {\n  if (!pointerLocked) return;\n  plExitByShortcut = true;\n  try { document.exitPointerLock(); } catch (err) {}\n}\n\n// 生命周期/断线/关画面主动退出锁定（不补发 ESC，不弹菜单）\nfunction plLifecycleExit() {\n  if (pointerLocked) {\n    plExitByLifecycle = true;\n    try { document.exitPointerLock(); } catch (err) {}\n  }\n  if (plAltHoldTimer) { clearTimeout(plAltHoldTimer); plAltHoldTimer = null; }\n}\n\ndocument.addEventListener('pointerlockchange', () => {\n  const nowLocked = document.pointerLockElement === $('scr');\n  if (nowLocked && !pointerLocked) {\n    // 锁定成功\n    pointerLocked = true;\n    plExitByShortcut = false;\n    plExitByLifecycle = false;\n    plLockPending = null;    // 首击挂起 → 吃掉（对齐月光：进锁定那次不点远端）\n    return;\n  }\n  if (!nowLocked && pointerLocked) {\n    // 解锁\n    pointerLocked = false;\n    if (plAltHoldTimer) { clearTimeout(plAltHoldTimer); plAltHoldTimer = null; }\n    // 冲刷残留相对位移\n    if (lookAcc.timer) { clearTimeout(lookAcc.timer); lookAcc.timer = null; }\n    flushLook();\n    const byShortcut = plExitByShortcut;\n    const byLifecycle = plExitByLifecycle;\n    plExitByShortcut = false;\n    plExitByLifecycle = false;\n    // ★ 方案 C：ESC 一键开菜单。排除法判定触发源：\n    //   非快捷键退出、非生命周期退出、且页面仍聚焦 → 只剩浏览器 ESC 强制解锁 → 补发 Esc 开菜单。\n    if (!byShortcut && !byLifecycle && document.hasFocus()) {\n      sendInput('key', -1, -1, { key: 'Esc' });\n    }\n    // Esc 后冷却：规范要求 default unlock gesture 后立即重锁会被拒\n    plLockCooldownUntil = Date.now() + 500;\n    return;\n  }\n});\n\ndocument.addEventListener('pointerlockerror', () => {\n  // 请求锁定失败（非手势/不支持/冷却期内）：保持未锁定，并补发挂起的首击\n  if (!document.pointerLockElement) pointerLocked = false;\n  if (plLockPending) {\n    const p = plLockPending; plLockPending = null;\n    sendInputOrdered('click', p.nx, p.ny, { button: 'left' });\n  }\n});\n\n// 锁定态 mousemove：movementX/Y × sensitivity → 复用 lookDelta 8ms 合并\ndocument.addEventListener('mousemove', e => {\n  if (!pointerLocked) return;\n  if (e.movementX || e.movementY) {\n    lookDelta(e.movementX * plSensitivity, e.movementY * plSensitivity);\n  }\n});\n\n// 设置控件绑定\nconst __plEscape = $('plEscape'), __plSens = $('plSens');\nif (__plEscape) __plEscape.onchange = () => { plEscape = __plEscape.value; try { localStorage.setItem(PL_ESC_KEY, plEscape); } catch (e) {} };\nif (__plSens) __plSens.oninput = () => { plSensitivity = parseFloat(__plSens.value) || 1.0; const sv = $('plSensV'); if (sv) sv.textContent = plSensitivity.toFixed(1); try { localStorage.setItem(PL_SENS_KEY, String(plSensitivity)); } catch (e) {} };\napplyPlEscape();\n// 相对模式手势状态（月光：单指滑动转视角 / 点按点击 / 双指滚轮或右键）\nconst REL_TAP_PX = 10;\nlet relPoints = new Map();   // pointerId -> { sx, sy, lx, ly, total }\nlet relWasTwo = false;       // 本次手势是否出现过双指\nlet relMaxMove = 0;          // 本次手势最大累计位移（判定「点一下」）\nlet relPinchY = null;        // 双指中点 Y（滚轮用）\nfunction relReset() {\n  relPoints.clear();\n  relWasTwo = false;\n  relMaxMove = 0;\n  relPinchY = null;\n}\n\n// ══════════ 触屏手势状态机（手机转视角/拖动/点击，2026-11-22）══════════\n// 判定依据（成熟手游标准方案，用户确认）：\n//   点一下屏幕（按下即抬起，几乎没动）      → 左键单击\n//   按下后**立即**滑动（长按窗口内越过位移阈值）→ 转视角（moverel 相对位移）\n//   按住不动超过长按阈值后再滑动           → 左键按住拖动（down + move + up）\n//   双指                                    → 滚轮/缩放（未实现，见 LOOK-OPTIMIZE 文档）\n//\n// ★ 转视角发 moverel 相对位移、不碰按键；拖动发 down + 绝对 move + up。\n//   依据：游戏转视角只认相对位移（绝对 SetCursorPos 不转视角，见 HANDOFF 4.2.6），\n//         而拖物品/拖地图是\"光标跟随手指\"，用绝对坐标。\n// ★ 游戏内打开地图时滑动不会转视角 —— 游戏自己处理 UI 层，我们不用管。\nlet look = null;            // 触屏滑动状态 { id, sx, sy, mode, lastX, lastY, nx, ny, tapTimer }\nconst LOOK_TAP_MS = 250;    // 长按阈值：按住不动超过它 = 进入拖动（可调范围 200~300ms）\nconst LOOK_SLIDE_PX = 10;   // 位移阈值：抖动/误触容差（手指抖动比鼠标的 8px 更大）\n\n// 转视角位移累积 + 节流（合并后发 moverel，避免手指高频抖动打爆代理有界队列）\nlet lookAcc = { dx: 0, dy: 0, lastSent: 0, timer: null };\nconst LOOK_HZ_MS = 8;       // 合并窗口：与桌面 LookInputQueue 的 8ms 对齐\n\nfunction flushLook() {\n  const d = lookAcc.dx, y = lookAcc.dy;\n  lookAcc.dx = lookAcc.dy = 0;\n  if (d || y) sendInput('moverel', -1, -1, { dx: d, dy: y });\n}\nfunction lookDelta(dx, dy) {\n  lookAcc.dx += dx; lookAcc.dy += dy;\n  const now = Date.now();\n  if (now - lookAcc.lastSent >= LOOK_HZ_MS) {\n    lookAcc.lastSent = now;\n    if (lookAcc.timer) { clearTimeout(lookAcc.timer); lookAcc.timer = null; }\n    flushLook();\n  } else if (!lookAcc.timer) {\n    lookAcc.timer = setTimeout(() => {\n      lookAcc.timer = null;\n      lookAcc.lastSent = Date.now();\n      flushLook();\n    }, LOOK_HZ_MS - (now - lookAcc.lastSent));\n  }\n}\n\n$('scr').addEventListener('pointerdown', e => {\n  if (e.pointerType === 'touch') {\n    // ★ 相对模式：走月光手势（滑动转视角 / 点按点击 / 双指滚轮·右键）\n    if (mouseMode === 'relative') {\n      if (relPoints.size === 0) { relWasTwo = false; relMaxMove = 0; relPinchY = null; }\n      relPoints.set(e.pointerId, { sx: e.clientX, sy: e.clientY, lx: e.clientX, ly: e.clientY, total: 0 });\n      if (relPoints.size >= 2) { relWasTwo = true; relPinchY = null; }\n      $('scr').setPointerCapture(e.pointerId);\n      return;\n    }\n    // ★ 命中虚拟按钮的区域由按钮层自己吃掉（阻止事件穿透），走不到这里。\n    //   走到这里 = 落在空白处：记录起点，进入 pending 状态。\n    const p = norm(e);\n    look = { id: e.pointerId, sx: e.clientX, sy: e.clientY, mode: 'pending',\n             lastX: e.clientX, lastY: e.clientY, nx: p.nx, ny: p.ny, tapTimer: null };\n    look.tapTimer = setTimeout(() => {\n      // 长按阈值内没怎么动 → 进入拖动模式：先按住左键\n      if (look && look.mode === 'pending') {\n        look.mode = 'drag';\n        sendInputOrdered('down', look.nx, look.ny, { button: 'left' });\n      }\n    }, LOOK_TAP_MS);\n    $('scr').setPointerCapture(e.pointerId);\n    return;\n  }\n\n  // 鼠标 / 触控笔：保持桌面原有行为不变\n  const btn = e.button === 2 ? 'right' : (e.button === 1 ? 'middle' : 'left');\n  // ★★ 2026-11-24 关键修复：「点一下鼠标左键，光标跳到右上角 / 完全没光标」★★\n  //\n  //  根因链（用户实测）：\n  //    1) 左键点击 → 触发 requestPointerLock() → 进入 Pointer Lock；\n  //    2) Pointer Lock 锁定态下浏览器**自动隐藏系统光标**；\n  //    3) 我们又给 #scr 设了 cursor:none（防双影）；\n  //    4) rcCursor 靠服务端 cursor 回传驱动，但首次点击后没有 move 事件 →\n  //       没有新的 cursor 回传 → rcCursor 停在旧位置或干脆没显示；\n  //    5) 且 updateRemoteCursor 的几何基准混用了 #vframe 与 #scr，\n  //       画布非满铺时算出的位置完全错位 → 光标\"跳到右上角\"。\n  //\n  //  修复策略：**左键绝不再触发 Pointer Lock**。\n  //    Pointer Lock 本来是给「转视角」用的（游戏类相对移动），\n  //    而用户的诉求是「正常操作桌面」——左键就该是普通点击。\n  //    转视角仍可用右键拖动（下面的 btn === 'right' 分支），那条路径不锁光标。\n  //    如需真正的锁定态，由用户显式点顶栏的锁定按钮进入（见 plEnterByButton）。\n  //\n  //  这样左键 = 普通点击（发 click），光标始终可见，交互立刻恢复正常。\n  if (e.pointerType === 'mouse' && btn === 'left' && PL_SUPPORTED && window.isSecureContext\n      && plLockWanted && !pointerLocked && Date.now() >= plLockCooldownUntil) {\n    // 仅当用户**显式要求**锁定（顶栏按钮置位 plLockWanted）时才进入 Pointer Lock。\n    const p0 = norm(e);\n    plLockPending = { nx: p0.nx, ny: p0.ny };\n    try { $('scr').requestPointerLock(); } catch (err) {}\n    return;\n  }\n  const p = norm(e);\n  // ★ 右键 = 转视角模式（与桌面版 RdpConsole「按住右键拖动」一致）：\n  //   按住右键 + 移动 → 复用触屏的 lookDelta/flushLook（8ms 合并 + moverel）。\n  //   游戏只认相对位移（Raw Input），绝对坐标 SetCursorPos 不转视角，\n  //   所以这里绝不发 move/down，只发 moverel —— 见 HANDOFF 4.2.6。\n  //   右键单击（按下即抬起没怎么动）仍发 click right，见 pointerup。\n  if (btn === 'right') {\n    look = { id: e.pointerId, sx: e.clientX, sy: e.clientY, mode: 'look',\n             lastX: e.clientX, lastY: e.clientY, nx: p.nx, ny: p.ny,\n             tapTimer: null, mouseRight: true };\n    $('scr').setPointerCapture(e.pointerId);\n    return;\n  }\n  drag = { sx: e.clientX, sy: e.clientY, moved: false, nx: p.nx, ny: p.ny, btn };\n  $('scr').setPointerCapture(e.pointerId);\n});\n// ★★ 鼠标移动：latest-wins 单 in-flight（2026-11-23 重写）★★\n//\n//   旧版问题（用户实测「点右键要 5-7 秒才见菜单」的帮凶之一）：\n//     · 节流窗口 = liveMs（画面帧间隔，15fps≈66ms）被夹到 50ms ——\n//       输入响应被画面帧率拖累，这个耦合本来就是错的；\n//     · 每个 pointermove 独立 fetch，快速滑动并发几十个请求，\n//       浏览器每域 6 连接 → 点击类请求排在 move 后面排队。\n//\n//   新版：与 lookAcc(moverel) 同源的「合并 + 8ms 节流」策略——\n//     · pointermove 只更新 pending（绝不发 fetch）；\n//     · move 走 noWait 不等回包（RTT 不再拖慢拖动）；\n//     · latest-wins：movePending 永远只记最新位置，中间位置不发；\n//     · 8ms 节流窗口与画面帧率完全解耦（MOVE_MIN_INTERVAL_MS）。\n//   实测（60Hz 滑动 3s、RTT 3ms）：旧版 3s 只发 46 个请求（≈15/s，\n//   全被 50ms 窗口夹死）；新版跟随到 125Hz 且并发恒为 1。\nconst MOVE_MIN_INTERVAL_MS = 8;   // 与 LOOK_HZ_MS 对齐；125Hz 上限足够顺滑\nlet movePending = null;           // 待发的最新位置（latest-wins）\nlet moveLastSentAt = 0;           // 上次实际发出的时刻（节流用）\n\nfunction sendMoveThrottled(nx, ny) {\n  movePending = { nx, ny };       // 永远只记最新位置，旧的自然被覆盖\n  pumpMove();\n}\n\n// ★★ 2026-11-24（T27）：丢弃「待发的旧坐标」—— 修拖动松手落点偏移 ★★\n//\n//   【问题】sendMoveThrottled 是 latest-wins + 8ms 节流：\n//     pointermove 只把坐标记进 movePending，节流窗口内**不立即发**（靠 pumpMove._t 定时器补发）。\n//   于是「快速拖动 → 立刻松手」时会出现：\n//     t0  pointermove(P1) → movePending = P1，timer 排在 8ms 后\n//     t1  pointerup(P2)   → 发 up(P2)，服务端把光标定位到 P2 并松开\n//     t2  timer 到期      → 发出迟到的 move(P1)  ← ★ 光标又被拽回 P1\n//   ⇒ 用户看到「落点偏移」，**操作越快越明显**（节流窗口内更容易有存货）。\n//\n//   【修法】凡是「释放类」动作（up / click / cancel），发之前先清掉待发 move。\n//     顺序必须：先 clearTimeout + 清 movePending，再发 up。\n//     否则 timer 会在 up 之后补发一条旧坐标，把落点拽走。\nfunction dropPendingMove() {\n  if (pumpMove._t) { clearTimeout(pumpMove._t); pumpMove._t = null; }\n  movePending = null;\n  // ★★ 2026-11-24（T32 P0-3）：还要清掉**已经排队但还没发出**的 move ★★\n  //\n  //   为什么需要：dropPendingMove 原本只清 movePending（尚未进入队列的那一个），\n  //   但在 HTTP 降级模式下，move 会被 P0-2 塞进 inputQueue 排队。\n  //   此时若只清 movePending，队列里可能还压着 move(P1)，\n  //   up(P3) 排到它后面 → 服务端先收到 move(P1) 再收到 up(P3)？\n  //     不对 —— 队列是 FIFO 且串行，move(P1) 会先到，up 后到，顺序是对的。\n  //   但真正的问题是这个 move(P1) **本身已经过时**（用户早已移到 P3），\n  //   它会让光标先闪到 P1 再被 up 拉到 P3 —— 视觉上仍是一次「抽动」。\n  //   更要紧的是：如果 up 入队之前队列里压了多条旧 move，up 要等它们全部发完，\n  //   **松手响应被拖慢**。所以释放动作前应该把这些过时的 move 直接丢弃。\n  //\n  //   ★ 关键：只过滤 inputQueue（尚未发出的），**绝不能碰 inputSending 正在发的那一条**。\n  //     在途那条的 resolve 挂在 pumpInput 的闭包里，清掉它会让 resolve 链断裂 →\n  //     调用方的 Promise 永远 pending（up 的重试判定、光标回显都会失效）。\n  //     inputQueue 是纯数组、被 shift 出来后才置 inputSending=true，\n  //     所以在途项**不在** inputQueue 里 —— 过滤操作天然安全。\n  inputQueue = inputQueue.filter(function (it) {\n    return !(it && it.body && it.body.action === 'move');\n  });\n}\n\nfunction pumpMove() {\n  if (!movePending) return;                      // 有货才发；move 已改 noWait，不再有 in-flight 等待\n  const now = Date.now();\n  const wait = MOVE_MIN_INTERVAL_MS - (now - moveLastSentAt);\n  if (wait > 0) {                                 // 节流窗口内：只排一个 timer\n    if (!pumpMove._t) {\n      pumpMove._t = setTimeout(() => { pumpMove._t = null; pumpMove(); }, wait);\n    }\n    return;\n  }\n  const p = movePending; movePending = null;\n  moveLastSentAt = Date.now();\n  // ★ noWait：不等回包，仅靠 8ms 节流 + timer 控制频率；latest-wins 由 movePending 保留。\n  //   单 in-flight 语义改为「节流窗口内最多排一条」——避免 RTT 拖慢拖动（WAN 下旧实现退化 1/RTT）。\n  sendInputNoWait('move', p.nx, p.ny);\n}\n\n$('scr').addEventListener('pointermove', e => {\n  // ★ 锁定态：转视角已由 document mousemove（movementX/Y）处理；clientX/Y 冻结为常量，\n  //   跳过绝对 move，避免重复发固定坐标。点击（down/up/click）仍走下面状态机。\n  if (pointerLocked && e.pointerType === 'mouse') return;\n  // ★ 2026-11-24：蓝球（本机鼠标影子）已删——它不反映远端光标，信息量为零。\n  //   远端光标可见性由两条正道负责：帧内光标（WGC 合成，服务端配套）+\n  //   rcCursor 低延迟回显（服务端 cursor 回传驱动，见 updateRemoteCursor）。\n  //   这里不再做任何本地影子绘制。\n  // ── 相对模式（月光）：单指滑动转视角 / 双指滚轮 ──\n  if (e.pointerType === 'touch' && mouseMode === 'relative' && relPoints.has(e.pointerId)) {\n    const pt = relPoints.get(e.pointerId);\n    const dx = e.clientX - pt.lx, dy = e.clientY - pt.ly;\n    pt.lx = e.clientX; pt.ly = e.clientY;\n    pt.total += Math.hypot(dx, dy);\n    relMaxMove = Math.max(relMaxMove, pt.total);\n    if (relPoints.size >= 2) {\n      const pts = [...relPoints.values()];\n      const my = (pts[0].ly + pts[1].ly) / 2;\n      if (relPinchY !== null) {\n        const steps = Math.round((my - relPinchY) / 8);\n        if (steps !== 0) sendInput('wheel', -1, -1, { delta: steps * 120 });\n      }\n      relPinchY = my;\n      return;\n    }\n    lookDelta(dx, dy);   // 单指滑动 → moverel 转视角（复用 8ms 合并）\n    return;\n  }\n  // ── 触屏 / 鼠标右键：转视角 / 拖动状态机 ──\n  // （mouseRight 的右键 look 直接进 look 模式：桌面版没有「先长按判定」，\n  //   按住右键滑动就是转视角，位移阈值只防抖动误触）\n  if (look && e.pointerId === look.id) {\n    const dx = e.clientX - look.lastX, dy = e.clientY - look.lastY;\n    look.lastX = e.clientX; look.lastY = e.clientY;\n\n    if (look.mode === 'pending') {\n      const moved = Math.hypot(e.clientX - look.sx, e.clientY - look.sy);\n      if (moved < LOOK_SLIDE_PX) return;   // 抖动容差，不算滑动\n      // 长按窗口内越过位移阈值 → 转视角\n      look.mode = 'look';\n      if (look.tapTimer) { clearTimeout(look.tapTimer); look.tapTimer = null; }\n      lookDelta(dx, dy);\n      return;\n    }\n    if (look.mode === 'look') {\n      // ★ 鼠标右键没有「点一下=click」的需求冲突：滑动即转视角。\n      //   但要防抖：几乎没动（<1px）就不发，避免点住不动时狂发 0 位移。\n      if (look.mouseRight && Math.abs(dx) + Math.abs(dy) < 1) return;\n      lookDelta(dx, dy);\n      return;\n    }\n    if (look.mode === 'drag') {\n      // 拖动：光标跟随手指，用绝对坐标（拖物品/地图）\n      const p = norm(e);\n      sendMoveThrottled(p.nx, p.ny);\n      return;\n    }\n    return;\n  }\n\n  // ── 鼠标 / 触控笔：保持桌面原有行为 ──\n  const p = norm(e);\n  if (!drag) { sendMoveThrottled(p.nx, p.ny); return; }\n\n  const dx = e.clientX - drag.sx, dy = e.clientY - drag.sy;\n  if (!drag.moved) {\n    if (Math.hypot(dx, dy) < 8) {\n      sendMoveThrottled(p.nx, p.ny);\n      return;\n    }\n    drag.moved = true;\n    sendInputOrdered('down', drag.nx, drag.ny, { button: drag.btn });\n  }\n  sendMoveThrottled(p.nx, p.ny);\n});\n$('scr').addEventListener('pointerup', e => {\n  // ── 相对模式（月光）：点一下=绝对定位+左键；双指点一下=右键 ──\n  if (e.pointerType === 'touch' && mouseMode === 'relative' && relPoints.has(e.pointerId)) {\n    relPoints.delete(e.pointerId);\n    if (relPoints.size < 2) relPinchY = null;\n    if (relPoints.size === 0) {\n      const p = norm(e);\n      if (relWasTwo) {\n        if (relMaxMove < REL_TAP_PX) { ripple(e); sendInputOrdered('click', p.nx, p.ny, { button: 'right' }); }\n      } else {\n        if (relMaxMove < REL_TAP_PX) { ripple(e); sendInputOrdered('click', p.nx, p.ny, { button: 'left' }); }\n      }\n      if (lookAcc.timer) { clearTimeout(lookAcc.timer); lookAcc.timer = null; }\n      flushLook();\n      relReset();\n    }\n    return;\n  }\n  // ── 触屏 / 鼠标右键 ──\n  if (look && e.pointerId === look.id) {\n    const was = look; look = null;\n    if (was.tapTimer) { clearTimeout(was.tapTimer); lookAcc.timer && clearTimeout(lookAcc.timer), lookAcc.timer = null; }\n    if (was.mode === 'pending') {\n      // 没动就抬起 → 左键单击\n      const p = norm(e);\n      ripple(e); sendInputOrdered('click', p.nx, p.ny, { button: 'left' });\n    } else if (was.mode === 'drag') {\n      // ★ T27：转视角拖动结束也是「释放」，先清待发 move 再发 up\n      dropPendingMove();\n      sendInputOrdered('up', -1, -1, { button: 'left' });\n    } else if (was.mode === 'look' && was.mouseRight) {\n      // ★ 鼠标右键转视角结束：几乎没动 = 右键单击（菜单照常出）；\n      //   拖动过 = 纯转视角，不发 click（不然转完视角还会弹出菜单）。\n      const movedTotal = Math.hypot(e.clientX - was.sx, e.clientY - was.sy);\n      if (movedTotal < LOOK_SLIDE_PX) {\n        const p = norm(e);\n        ripple(e); sendInputOrdered('click', p.nx, p.ny, { button: 'right' });\n      }\n    }\n    // 转视角模式：最后残留的位移已由 timer 冲刷，无需额外动作\n    if (lookAcc.timer) { clearTimeout(lookAcc.timer); lookAcc.timer = null; }\n    flushLook();\n    return;\n  }\n\n  // ═══════════════════════════════════════════════════════════════════════\n  //  ★★ 2026-11-24（T28）：页面版本轮询 + 自动刷新 ★★\n  //\n  //  【要解决的问题】\n  //    改前端后用户必须自己 Ctrl+F5。响应头虽已是 no-store，但那只对**新请求**生效,\n  //    已打开的页面不会自己重拉。把这件事推给用户是缺陷。\n  //\n  //  【三个设计决策】\n  //    ① 版本来源：GET /api/pagever → { pageVer }（服务端对**实际返回的 HTML** 做 SHA-256 前 12 位）。\n  //       不用程序版本号 —— 只改 web-index.html 时版本号不变，会漏更新。\n  //    ② 刷新时机：**空闲才刷**。记录最后一次用户输入时刻，若 <5s 内有操作则**推迟**，\n  //       最多推迟 2 分钟；超过就强制刷（避免用户一直在操作导致永远不更新）。\n  //       理由：刷新会重连视频流（画面黑一下 + 等 IDR），用户正在拖动时刷会**打断操作**，\n  //       比晚几分钟更新更糟。\n  //    ③ 死循环保护：**60 秒内最多刷新一次**，且**最多连续 3 次**；\n  //       超过 3 次就停止并 warn —— 说明服务端返回的 pageVer 在刷新后没变（部署不同步），\n  //       再刷也没用。\n  // ═══════════════════════════════════════════════════════════════════════\n  (function startPageVersionWatch() {\n    const PAGEVER_POLL_MS = 30000;      // 轮询间隔\n    const IDLE_MS = 5000;               // 至少空闲 5s 才刷\n    const MAX_DEFER_MS = 120000;        // 最多推迟 2 分钟，超时强制刷\n    const MIN_RELOAD_GAP_MS = 60000;    // 两次刷新至少间隔 60s\n    const MAX_CONSECUTIVE_RELOADS = 3;  // 连续刷新上限（防死循环）\n\n    // ★ 关键：本页「已应用」的版本必须**跨 reload 持久化**，否则 reload 后又把\n    //   appliedVer 设成当前值，下次检测到更新就永远无法与「已应用」区分 —— 会漏刷。\n    //   做法：第一次拿到版本时写入 sessionStorage，之后一直以它为准（跨 reload 保留）。\n    let knownVer = null;        // 服务端当前版本（每拍从 /api/pagever 取）\n    let appliedVer = null;      // 本页面**已应用**的版本（首次加载时从 sessionStorage 恢复）\n    let lastInputAt = Date.now();   // 最后一次用户操作时刻\n    let pendingSince = 0;       // 发现新版本、开始等待空闲的时刻（0=无待处理）\n    let lastReloadAt = 0;       // 上次刷新时刻（跨 reload 用 sessionStorage 持久化）\n    let consecutive = 0;        // 连续刷新次数\n\n    // 跨 reload 保存「上次刷新时间 / 连续次数」—— 否则 reload 后计数清零，保护失效\n    // 跨 reload 恢复：刷新时间、连续次数、**已应用版本**\n    try {\n      const saved = sessionStorage.getItem('__pageReloadGuard');\n      if (saved) {\n        const g = JSON.parse(saved);\n        lastReloadAt = g.t || 0;\n        consecutive = g.n || 0;\n        if (g.v) appliedVer = g.v;   // ★ 恢复「本页已应用版本」\n      }\n    } catch (e) {}\n\n    function saveGuard() {\n      try { sessionStorage.setItem('__pageReloadGuard', JSON.stringify({ t: lastReloadAt, n: consecutive, v: appliedVer })); } catch (e) {}\n    }\n\n    // 任何输入都刷新 lastInputAt（用捕获阶段，避免被 stopPropagation 漏掉）\n    ['pointerdown','pointermove','pointerup','wheel','keydown','touchstart'].forEach(function (ev) {\n      document.addEventListener(ev, function () { lastInputAt = Date.now(); }, { capture: true, passive: true });\n    });\n\n    async function fetchPageVer() {\n      try {\n        // ★★ 2026-11-24（T32 BUG 修复）：必须用**页面自身的来源**，不是 directBase ★★\n        //\n        //   【原来的错误】这里写的是 directBase(cur)，即 agent 直连口（19081）。\n        //   但 /api/pagever 定义在**主程序** Services/WebServer.cs（19080）—— agent 没有这个端点。\n        //   实测：19081/api/pagever 返回 404 ⇒ **T28 的自动刷新从未生效过**。\n        //\n        //   【为什么该跟页面来源走】版本轮询要回答的问题是「**这个页面**该不该刷新」，\n        //   而页面本身是主程序 19080 发出来的（/api/sessions、/api/settings、/api/list\n        //   也都是相对路径 = 主程序）。agent 只负责画面流与输入注入，与页面版本无关。\n        //   用相对路径（''）即「当前页面同源」，天然正确。\n        //\n        //   【口令】主程序 WebServer 用 cookie 鉴权（IsAuthorized），同源请求自动带 cookie；\n        //   不需要 X-SoulLink-Token（那是 agent 校验的）。\n        const r = await fetch('/api/pagever', { cache: 'no-store', credentials: 'same-origin' });\n        if (!r.ok) return null;\n        const d = await r.json();\n        return (d && d.pageVer) || null;\n      } catch (e) { return null; }\n    }\n\n    function tryReload(now) {\n      if (!pendingSince) return;\n      // 保护 ①：两次刷新至少间隔 MIN_RELOAD_GAP_MS\n      if (now - lastReloadAt < MIN_RELOAD_GAP_MS) return;\n      // 保护 ②：连续刷新不超过 MAX_CONSECUTIVE_RELOADS\n      if (consecutive >= MAX_CONSECUTIVE_RELOADS) {\n        console.warn('[版本更新] 已连续刷新 ' + consecutive + ' 次仍未稳定，停止自动刷新（请手动 Ctrl+F5 或检查部署）');\n        pendingSince = 0;\n        return;\n      }\n      // 条件 ③：空闲（或已推迟超时）才刷\n      const idleFor = now - lastInputAt;\n      const waited = now - pendingSince;\n      if (idleFor < IDLE_MS && waited < MAX_DEFER_MS) {\n        return;   // 还在操作，且未超时 → 等下一拍\n      }\n      if (idleFor < IDLE_MS) {\n        console.warn('[版本更新] 等空闲超 ' + Math.round(MAX_DEFER_MS / 1000) + 's，强制刷新');\n      }\n      console.warn('[版本更新] ' + appliedVer + ' → ' + knownVer + '，自动刷新页面');\n      lastReloadAt = now;\n      consecutive++;\n      // ★ 关键：把 appliedVer 更新为**即将加载**的版本再 reload。\n      //   这样即使 reload 因故没发生（被打断/浏览器拦截），也不会在高频 tick 里反复触发；\n      //   而 sessionStorage 里存的是「刷新后应有的版本」，新页面启动时即以此为基线。\n      appliedVer = knownVer;\n      pendingSince = 0;\n      saveGuard();\n      location.reload();\n    }\n\n    async function tick() {\n      const v = await fetchPageVer();\n      if (!v) return;\n\n      // 首拍：建立基线。「本页已应用版本」若 sessionStorage 里没有，就用当前值并记住。\n      //   ★ 一旦写过就不再改 —— 只有 reload 之后的新页面才会重新建立基线。\n      if (appliedVer === null) {\n        appliedVer = v;\n        knownVer = v;\n        saveGuard();\n        return;\n      }\n\n      knownVer = v;\n\n      // 服务端版本 == 本页已应用版本 → 无需刷新；顺带清掉待处理窗口\n      if (v === appliedVer) {\n        if (pendingSince) { pendingSince = 0; console.warn('[版本更新] 服务端已回到本页版本，取消刷新'); }\n        return;\n      }\n\n      // 检测到新版本：开一个待处理窗口（只开一次），等空闲再刷\n      if (!pendingSince) {\n        pendingSince = Date.now();\n        console.warn('[版本更新] 检测到新版本 ' + appliedVer + ' → ' + v + '，等空闲后刷新');\n      }\n      tryReload(Date.now());\n    }\n\n    // 每 5s 检查一次「是否可以刷新」（比轮询密，保证空闲一出现就能刷）\n    setInterval(function () { if (pendingSince) tryReload(Date.now()); }, 5000);\n    setInterval(tick, PAGEVER_POLL_MS);\n    tick();\n  })();\n\n  // ── 鼠标 / 触控笔 ──\n  // ★ 首击挂起兜底：requestPointerLock 既未成功也未报错（罕见），pointerup 时补发 click\n  if (!drag && plLockPending) {\n    const p = plLockPending; plLockPending = null;\n    sendInputOrdered('click', p.nx, p.ny, { button: 'left' });\n    return;\n  }\n  if (!drag) return;\n  const moved = drag.moved, btn = drag.btn || 'left';\n  drag = null;\n  const p = norm(e);\n  // ★ T27：先丢掉待发的旧 move 坐标，再发 up/click ——\n  //   否则 8ms 节流的 timer 会在 up 之后补发一条迟到 move，把光标拽回上一个点（落点偏移）。\n  dropPendingMove();\n  if (moved) sendInputOrdered('up', p.nx, p.ny, { button: btn });\n  else { ripple(e); sendInputOrdered('click', p.nx, p.ny, { button: btn }); }\n});\n$('scr').addEventListener('pointercancel', () => {\n  if (mouseMode === 'relative' && relPoints.size > 0) {\n    if (lookAcc.timer) { clearTimeout(lookAcc.timer); lookAcc.timer = null; }\n    flushLook();\n    relReset();\n    return;\n  }\n  if (look) {\n    // ★ T27：取消时同样先丢掉待发 move，否则取消后会再补一条迟到 move\n    if (look.mode === 'drag') { dropPendingMove(); sendInputOrdered('up', -1, -1, { button: 'left' }); }\n    if (look.mode === 'look' && look.mouseRight) {\n      // 右键转视角被系统打断：不补发 click（无法区分用户意图），只退出\n      if (lookAcc.timer) { clearTimeout(lookAcc.timer); lookAcc.timer = null; }\n      flushLook();\n      look = null;\n      return;\n    }\n    look = null;\n    if (lookAcc.timer) { clearTimeout(lookAcc.timer); lookAcc.timer = null; }\n    flushLook();\n    return;\n  }\n  // ★ T27：同 pointercancel 上的 drag 分支 —— 先清待发 move 再发 up\n  if (drag && drag.moved) { dropPendingMove(); sendInputOrdered('up', -1, -1, { button: drag.btn || 'left' }); }\n  drag = null;\n});\n$('scr').addEventListener('wheel', e => {\n  e.preventDefault();\n  sendInput('wheel', -1, -1, { delta: e.deltaY > 0 ? -120 : 120 });\n}, { passive: false });\n$('scr').addEventListener('contextmenu', e => e.preventDefault());\n$('scr').addEventListener('dblclick', e => e.preventDefault());\n$('scr').addEventListener('selectstart', e => e.preventDefault());\n\nfunction keyName(e) {\n  const k = e.key;\n  const map = { ' ': 'Space', 'Enter': 'Enter', 'Escape': 'Esc', 'Backspace': 'Backspace',\n    'Tab': 'Tab', 'Delete': 'Delete', 'ArrowUp': 'Up', 'ArrowDown': 'Down',\n    'ArrowLeft': 'Left', 'ArrowRight': 'Right', 'Home': 'Home', 'End': 'End',\n    'PageUp': 'PageUp', 'PageDown': 'PageDown' };\n  if (map[k]) return map[k];\n  if (k.length === 1) return k.toUpperCase();\n  return k;\n}\ndocument.addEventListener('keydown', e => {\n  if (!kbMode || e.target.tagName === 'INPUT') return;\n  if (['Control','Alt','Shift','Meta'].includes(e.key)) return;\n  // 锁定态 ESC 由 pointerlockchange 统一补发（浏览器解锁 + 补发一次 Esc 开菜单）；\n  // 这里拦掉，避免「ESC keydown 派发时」键盘注入又发一次导致双重开菜单。\n  if (pointerLocked && e.key === 'Escape') return;\n  // ★★ 2026-11-24（t24 P0-2）：过滤浏览器自动重复 ★★\n  //\n  //   原来没有这个判断：长按一个键，浏览器每 ~30ms 派发一次 keydown（e.repeat=true），\n  //   每次都发一条完整的 key 动作（按下+抬起+服务端 20ms Sleep）到服务端。\n  //\n  //   实测（docs/research/T24-DRAG-RELEASE-20261124.md）：服务端 key 处理 28.6ms/条，\n  //   浏览器 repeat 间隔 ~30ms —— 刚好卡在临界点，积压恒为 1 条；\n  //   一旦 repeat 更快（部分系统/无障碍设置可到 15ms 甚至 5ms），实测积压线性增长：\n  //     repeat=15ms → 积压 9 条；repeat=5ms → 积压 11 条。\n  //   积压会把排在后面的 up/click 一起堵住，表现为「松手很久才判定松开」。\n  //\n  //   ★ 标准做法：长按的重复由**远端系统自己产生**（真实键盘也是这样）。\n  //     这里只发第一次 keydown，浏览器自己的 repeat 一律丢掉；抬起由 keyup 处理 —— 语义无损失。\n  if (e.repeat) return;\n\n  // ★★ 2026-11-24（T25 BUG-1）：不能无条件 preventDefault ★★\n  //\n  //   原来这里对所有按键都 e.preventDefault()，于是浏览器的本地快捷键\n  //   （F5 刷新 / Ctrl+R 重载 / F12 开发者工具 / Ctrl+Shift+I / Ctrl+W 关页 / Ctrl+T 新标签）\n  //   全被吞掉，用户原话：「浏览器不响应这个快捷键」。\n  //\n  //   规则：**放行浏览器快捷键，只对\"要发往远端\"的普通键 preventDefault**。\n  //   · 放行清单（本地生效）：F5 / F12 / Ctrl+R / Ctrl+Shift+I / Ctrl+W / Ctrl+T / Ctrl+Shift+R\n  //   · 另外：只按修饰键（Ctrl/Alt/Shift/Meta）时不拦截。\n  const localShortcuts = (\n    e.key === 'F5' || e.key === 'F12' ||\n    ((e.ctrlKey || e.metaKey) && ['r','R','w','W','t','T','i','I'].includes(e.key)) ||\n    ((e.ctrlKey || e.metaKey) && e.shiftKey && ['i','I','r','R'].includes(e.key))\n  );\n  if (localShortcuts) return;   // 交给浏览器，不注入远端、不 preventDefault\n\n  const parts = [];\n  if (e.ctrlKey) parts.push('Ctrl');\n  if (e.altKey) parts.push('Alt');\n  if (e.shiftKey) parts.push('Shift');\n  parts.push(keyName(e));\n  e.preventDefault();\n  sendInput('key', -1, -1, { key: parts.join('+') });\n});\n\n$('closeViewer').onclick = closeViewer;\n\n// 录像回放：点了才展开 + 才取数据（避免打开画面时白扫一遍磁盘）\nconst savedToggle = $('savedToggle');\nif (savedToggle) savedToggle.onclick = () => {\n  const wrap = $('savedWrap');\n  const open = savedToggle.classList.toggle('open');\n  if (wrap) wrap.style.display = open ? 'block' : 'none';\n  if (open) ensureSaved();     // ← 只有展开时才请求 /api/list\n};\n\n// ── 录制参数：与应用设置双向同步（3.3.12）──────────────────\n//\n//   ★ 用户要求：「把这些设置都加到浏览器，两边同步」\n//\n//   单向下发是不够的 —— 网页改了也要回写应用配置，\n//   并且立刻下发给正在录制的代理（不用等用户去应用里重开一次）。\nfunction applySettings(d) {\n  if (typeof d.recordFps === 'number' && d.recordFps > 0) recFps = d.recordFps;\n  if (typeof d.recordQuality === 'number' && d.recordQuality > 0) recQuality = d.recordQuality;\n  if (typeof d.recordKeepSeconds === 'number' && d.recordKeepSeconds > 0) recKeep = d.recordKeepSeconds;\n  if (typeof d.streamFps === 'number' && d.streamFps > 0) streamFps = d.streamFps;\n  if (typeof d.videoBitrate === 'number' && d.videoBitrate > 0) videoBitrate = d.videoBitrate;\n  if (typeof d.nvencPreset === 'number' && d.nvencPreset > 0) nvencPreset = d.nvencPreset;\n  if (typeof d.vbvBufferIncreasePercent === 'number') vbvBufferIncreasePercent = d.vbvBufferIncreasePercent;\n  if (typeof d.captureMode === 'number') captureMode = d.captureMode;\n\n  // viewFps 从 localStorage 恢复（2026-11-23 起仅用于鼠标移动节流，\n  // 「取图」下拉框已随 JPEG 轮询通路移除）\n  try {\n    const v = localStorage.getItem(VIEWFPS_KEY);\n    if (v !== null) viewFps = parseInt(v) || 0;\n  } catch (e) { }\n\n  computeLiveMs();\n  syncDropdowns();\n}\n\n// 把当前值回填到下拉菜单（值不在预设列表里就动态补一个选项）\nfunction syncDropdowns() {\n  // 先清掉上次补的动态选项，避免反复变化时累积\n  document.querySelectorAll('option[data-dyn]').forEach(o => o.remove());\n  setSelect('selFps', recFps, v => v + ' fps');\n  setSelect('selLiveFps', streamFps, v => v + ' fps');\n  setSelect('selQuality', recQuality, v => qualityLabel(v));\n  setSelect('selKeep', recKeep, v => v + ' 秒');\n  syncQuality();\n  syncCapture();\n}\n\n// 抓屏方式下拉回显 + 当前实际后端（从 /api/info 刷新）\nfunction syncCapture() {\n  const sel = $('capMode');\n  if (sel) sel.value = String(captureMode);\n}\n// ★ T34：fpsinfo 的单一渲染入口 —— 分辨率 + 实测推送 fps + 配置 fps\n//   （原来有两处各自写 textContent 会互相覆盖，统一到这里）\nlet __fpsInfo = { screen: '', cfg: 0, act: 0 };\nfunction renderFpsInfo() {\n  const el = $('fpsinfo');\n  if (!el) return;\n  const parts = [];\n  if (__fpsInfo.screen) parts.push(__fpsInfo.screen);\n  if (__fpsInfo.act || __fpsInfo.cfg) {\n    parts.push('推送实测 ' + (__fpsInfo.act ? __fpsInfo.act.toFixed(1) : '—') + ' fps'\n               + (__fpsInfo.cfg ? ' · 配置 ' + __fpsInfo.cfg + ' fps' : ''));\n  }\n  el.textContent = parts.join(' · ');\n}\n\n// ★ T34：实测 fps 是**动态值**，只在进入画面时取一次会一直是旧值（甚至 0）。\n//   2 秒一次轻量轮询：只请求 /api/info（几十字节 JSON）+ 写一个 textContent，开销可忽略；\n//   只在「画面打开中」才跑，关闭自动停。\n//   ★ __fpsPollTimer 声明在文件上方（见「fps 轮询句柄」），不在此处 —— 避免 startLive\n//     （定义在更早位置）调用时落到 let 的 TDZ。\nfunction startFpsPolling() {\n  stopFpsPolling();\n  __fpsPollTimer = setInterval(function () {\n    if (!cur || !liveRunning || paused) return;   // 没在看画面 → 不打扰服务端\n    refreshCaptureBackend();\n  }, 2000);\n}\nfunction stopFpsPolling() {\n  if (__fpsPollTimer) { clearInterval(__fpsPollTimer); __fpsPollTimer = null; }\n}\n\nasync function refreshCaptureBackend() {\n  if (!cur) return;\n  const base = directBase(cur);\n  if (!base) { const b = $('capBackend'); if (b) b.textContent = '—'; return; }\n  try {\n    const headers = {};\n    if (directToken()) headers['X-SoulLink-Token'] = directToken();\n    const r = await fetch(base + '/api/info', { headers });\n    const d = await r.json();\n    const b = $('capBackend');\n    if (b) b.textContent = (d && d.captureBackend) ? d.captureBackend : '—';\n    // ★★ 2026-11-24（T34）：区分显示「配置 fps」与「实测 fps」★★\n    //\n    //   用户看到浏览器里 `fps: 66.0 (render)` 会误以为「服务端在推 66fps」。\n    //   但那是**浏览器渲染帧率**（合成器/requestAnimationFrame），\n    //   与「服务端实际推了多少帧」是两件事：\n    //     · 渲染 fps 可远高于接收 fps —— 浏览器会把同一帧重复绘制多次；\n    //     · **只有实测推送 fps 才反映服务端真实产出能力与卡顿**。\n    //   分开显示，避免用户拿渲染 fps 判断「推送正常」。\n    if (d) {\n      __fpsInfo.cfg = (typeof d.configuredFps === 'number' && d.configuredFps > 0) ? d.configuredFps : 0;\n      __fpsInfo.act = (typeof d.actualFps === 'number' && d.actualFps > 0) ? d.actualFps : 0;\n      renderFpsInfo();\n    }\n  } catch (e) {\n    const b = $('capBackend'); if (b) b.textContent = '—';\n  }\n}\n\n// 直播画质滑杆/下拉回显\nfunction syncQuality() {\n  const bit = $('qBitrate'), bitV = $('qBitrateV');\n  if (bit) bit.value = videoBitrate;\n  if (bitV) bitV.textContent = Math.round(videoBitrate / 1000) + 'M';\n  const fps = $('qStreamFps'), fpsV = $('qStreamFpsV');\n  if (fps) fps.value = streamFps;\n  if (fpsV) fpsV.textContent = streamFps + 'fps';\n  const pre = $('qPreset');\n  if (pre) pre.value = String(nvencPreset);\n  const vbv = $('qVbv'), vbvV = $('qVbvV');\n  if (vbv) vbv.value = vbvBufferIncreasePercent;\n  if (vbvV) vbvV.textContent = vbvBufferIncreasePercent + '%';\n}\n\nfunction qualityLabel(v) {\n  if (v >= 85) return '高清 ' + v;\n  if (v >= 70) return '清晰 ' + v;\n  return '标准 ' + v;\n}\n\n// 把 value 选中到下拉框。预设里没有这个值就补一项（并标记，下次可清掉）。\n// ★ 补出来的项带 data-dyn=\"1\"，syncDropdowns 开头会先清掉旧的动态项，\n//   否则配置反复变化时选项会越堆越多。\nfunction setSelect(id, value, fmt) {\n  const el = $(id); if (!el) return;\n  const v = String(value);\n  let opt = el.querySelector('option[value=\"' + v + '\"]:not([data-dyn])');\n  if (!opt) {\n    opt = document.createElement('option');\n    opt.value = v;\n    opt.textContent = fmt(value);\n    opt.setAttribute('data-dyn', '1');\n    el.appendChild(opt);\n  }\n  el.value = v;\n}\n\n// 提交改动：先乐观更新界面，再等后端确认（失败就回滚）\nlet saveTimer = null;\nasync function pushSettings(patch) {\n  try {\n    const r = await fetch('/api/settings', {\n      method: 'POST',\n      headers: { 'Content-Type': 'application/json' },\n      body: JSON.stringify(patch)\n    });\n    const d = await r.json();\n    if (d && d.ok) {\n      recFps = d.fps; recQuality = d.quality; recKeep = d.keepSeconds;\n      if (typeof d.streamFps === 'number' && d.streamFps > 0) streamFps = d.streamFps;\n      else if (typeof d.liveFps === 'number' && d.liveFps > 0) streamFps = d.liveFps;\n      if (typeof d.videoBitrate === 'number' && d.videoBitrate > 0) videoBitrate = d.videoBitrate;\n      if (typeof d.nvencPreset === 'number' && d.nvencPreset > 0) nvencPreset = d.nvencPreset;\n      if (typeof d.vbvBufferIncreasePercent === 'number') vbvBufferIncreasePercent = d.vbvBufferIncreasePercent;\n      if (typeof d.captureMode === 'number') captureMode = d.captureMode;\n      computeLiveMs(); syncDropdowns(); startLive();\n      return true;\n    }\n  } catch (e) { }\n  return false;\n}\n\nfunction bindSelect(id, onPick) {\n  const el = $(id); if (!el) return;\n  el.onchange = () => { onPick(parseInt(el.value) || 0); };\n}\n// 录制组：改了就回写应用配置 + 通知代理\nbindSelect('selFps',     v => pushSettings({ fps: v }));\nbindSelect('selLiveFps', v => pushSettings({ liveFps: v }));\nbindSelect('selQuality', v => pushSettings({ quality: v }));\nbindSelect('selKeep',    v => pushSettings({ keepSeconds: v }));\n\n// 直播画质组（2026-11-23 手机端原型）：改完回写应用配置 + 即时下发\nfunction bindRange(id, valId, fmt, onPick) {\n  const el = $(id), val = $(valId);\n  if (!el) return;\n  const update = () => { if (val) val.textContent = fmt(el.value); };\n  el.oninput = update;\n  el.onchange = () => { onPick(parseInt(el.value) || 0); };\n}\nbindRange('qBitrate',   'qBitrateV',   v => Math.round(v / 1000) + 'M', v => pushSettings({ videoBitrate: v }));\nbindRange('qStreamFps', 'qStreamFpsV', v => v + 'fps',                  v => pushSettings({ streamFps: v }));\nbindRange('qVbv',       'qVbvV',       v => v + '%',                    v => pushSettings({ vbvBufferIncreasePercent: v }));\nconst qPre = $('qPreset');\nif (qPre) qPre.onchange = () => pushSettings({ nvencPreset: parseInt(qPre.value) || 3 });\n\n// 抓屏方式（2026-11-23）：切换 → pushSettings 改 CaptureMode → 下发 agent 热切\n//   → 延时等 agent 帧边界切换完成 → 断开旧 /stream reader 重连拿新 SPS/IDR。\n//   为什么不立即重连：热切在 worker 帧边界排队，立即重连会拿到旧后端；\n//   且后端分辨率可能变化，旧 decoder 配置对新 SPS 会花屏，必须重连重新 configure。\nconst capSel = $('capMode');\nif (capSel) capSel.onchange = async () => {\n  const mode = parseInt(capSel.value) || 0;\n  const ok = await pushSettings({ captureMode: mode });\n  if (ok) {\n    if (liveRunning && !paused) {\n      setTimeout(() => {\n        refreshCaptureBackend();\n        if (liveRunning && !paused) {\n          failStreak = 0;\n          setPh('抓屏后端已切换 · 正在重连画面…', false);\n          startLive();      // stopLive + startStream：断旧 reader 重连新流\n        }\n      }, 1200);\n    }\n  } else syncCapture();\n};\n\n// 设置弹出菜单（2026-11-23）：点「设置」展开，点面板外/ESC 收起\nconst settingsBtn = $('settingsBtn'), settingsMenu = $('settingsMenu');\nif (settingsBtn && settingsMenu) {\n  settingsBtn.onclick = e => {\n    e.stopPropagation();\n    const open = settingsMenu.classList.toggle('open');\n    settingsBtn.classList.toggle('on', open);\n  };\n  document.addEventListener('pointerdown', e => {\n    if (!settingsMenu.classList.contains('open')) return;\n    if (!settingsMenu.contains(e.target) && e.target !== settingsBtn) {\n      settingsMenu.classList.remove('open');\n      settingsBtn.classList.remove('on');\n    }\n  });\n  document.addEventListener('keydown', e => {\n    if (e.key === 'Escape' && settingsMenu.classList.contains('open')) {\n      settingsMenu.classList.remove('open');\n      settingsBtn.classList.remove('on');\n    }\n  });\n}\n\n// 「本机播放」下拉框已随 JPEG 轮询通路移除（2026-11-23）：\n//   画面帧率由服务端 /stream 直推，本机没有取图频率概念。\n//   viewFps 仅保留作为鼠标移动节流的基准值，localStorage 读取仍在 applySettings。\n// ── 键盘：点画面即开启（不再用顶栏按钮）───────────────────\n//   原来有个「键盘」按钮占着顶栏，而页面本来就已经全局监听 keydown。\n//   现在改成 RDP 客户端的习惯：点一下画面 = 键盘发往远程，\n//   点画面外 = 释放（避免 F5/Ctrl+W 被吞掉）。\nfunction setKbMode(on) {\n  kbMode = on;\n  const badge = $('kbBadge');\n  if (badge) badge.style.display = on ? 'inline' : 'none';\n}\n$('scr').addEventListener('pointerdown', () => setKbMode(true));\ndocument.addEventListener('pointerdown', e => {\n  if (!e.target.closest('#scr')) setKbMode(false);\n}, true);\n\n// 页面可见性（2026-11-23 随「移除 JPEG 轮询」重写）：\n//   隐藏 → 断流省资源（挂机监控页常年开着的场景）；\n//   回前台 → 自动重连（画面断开时的提示也会被新一轮连接刷新）。\n//   ★ pause 状态下不自动恢复（用户点了暂停就尊重暂停）。\ndocument.addEventListener('visibilitychange', () => {\n  if (document.hidden) {\n    plLifecycleExit();        // ★ 页面隐藏 → 退出 Pointer Lock（浏览器也会自动解锁，这里主动清状态）\n    releaseAllInputs();       // ★ 切后台先释放所有按住的键，防「松手远端没松开」\n    if (liveRunning && h264Active) {\n      abortStream();          // 只断流，画面冻在最后一帧\n      setPh('页面已隐藏，流已暂停 · 回到页面自动重连', false);\n    }\n  } else {\n    if (liveRunning && !paused && !h264Active && !reconnectTimer) {\n      setPh('连接中…', false);\n      startStream();\n    }\n  }\n});\n\n// ★ 卸载/切后台释放按键：pagehide/beforeunload 触发时 fire-and-forget 发 releaseall。\n//   只释放真正按过的键（代理侧 PressedButtons/PressedKeys 只抬按过的键，不会误触）。\nwindow.addEventListener('pagehide', () => releaseAllInputs());\nwindow.addEventListener('beforeunload', () => releaseAllInputs());\n\n$('pauseBtn').onclick = () => {\n  paused = !paused;\n  $('pauseBtn').classList.toggle('on', paused);\n  $('pauseBtn').textContent = paused ? '继续' : '暂停';\n  // 暂停 = 断流（不是空转跳帧），继续 = 立刻重连\n  if (paused) {\n    stopLive();\n    setPh('已暂停', false);\n  } else { startLive(); }\n};\n// 滚轮用真实鼠标滚轮（见 #scr 的 wheel 监听），不需要按钮\n\n// ── 录像 ────────────────────────────────────────────────────\n// ── 录像列表：按需加载（3.3.17）───────────────────────────\n//\n//   ★ 用户原则：「网页从应用拿数据，也应该遵循 api 接口按需取用」\n//\n//   /api/list 要扫 录制回放\\ 下的所有目录、读每个 meta.json ——\n//   打开画面时根本不需要它。这里改成：\n//     · 打开画面 → 清空列表，不发请求\n//     · 用户真的展开「录像回放」→ 才取一次\n//     · 同一账号 30 秒内重复点开 → 用缓存，不重复扫盘\nlet savedFetchedFor = null;   // 已取过哪个账号的列表\nlet savedFetchedAt = 0;\nconst SAVED_TTL_MS = 30000;\n\nfunction resetSavedList() {\n  savedFetchedFor = null;\n  savedFetchedAt = 0;\n  const box = $(\"saved\");\n  if (box) box.innerHTML = \"\";\n  // 收起折叠：换账号后不该默认展开（那就又变成\"一打开就扫盘\"了）\n  const t = $(\"savedToggle\"), w = $(\"savedWrap\");\n  if (t) t.classList.remove(\"open\");\n  if (w) w.style.display = \"none\";\n}\n\n// 用户点开「录像回放」时调用：需要才取，且 30 秒内复用。\nfunction ensureSaved() {\n  if (!cur) return;\n  const key = cur.id || cur.label;\n  const fresh = savedFetchedFor === key && (Date.now() - savedFetchedAt) < SAVED_TTL_MS;\n  if (fresh) return;\n  savedFetchedFor = key;\n  savedFetchedAt = Date.now();\n  loadSaved();\n}\n\nasync function loadSaved() {\n  try {\n    const r = await fetch('/api/list');\n    const d = await r.json();\n    const box = $('saved'); box.innerHTML = '';\n    const mine = (d.items || []).filter(it => !cur || it.user === cur.label);\n    if (!mine.length) {\n      box.innerHTML = '<div class=\"imt\" style=\"padding:8px 0\">还没有录像</div>';\n      return;\n    }\n    for (const it of mine) {\n      let trig = '', fps = 10;\n      try {\n        if (it.meta) { const m = JSON.parse(it.meta); trig = m.trigger || ''; fps = m.fps || 10; }\n      } catch (x) {}\n      const short = it.name.replace(/^[0-9]{4}-[0-9]{2}-[0-9]{2}-[0-9]{6}-?/, '');\n      const div = document.createElement('div');\n      div.className = 'item';\n      div.innerHTML = '<div style=\"flex:1\">'\n        + '<div class=\"inm\">' + esc(short || it.name)\n        + (trig ? '<span class=\"badge\">' + esc(trig) + '</span>' : '') + '</div>'\n        + '<div class=\"imt\">' + it.frames + ' 帧 · ' + fps + ' fps</div></div>'\n        + '<div style=\"color:#4fe3ff\">&#9656;</div>';\n      div.onclick = () => playVideo(it);\n      box.appendChild(div);\n    }\n  } catch (x) {}\n}\n\nfunction playVideo(it) {\n  const v = $('vid');\n  const url = '/file/' + encodeURI(it.path) + '/replay.mp4';\n  v.src = url; v.load();\n  $('player').style.display = 'block';\n  $('pinfo').textContent = it.frames + ' 帧';\n  $('dl').onclick = () => window.open(url, '_blank');\n  v.scrollIntoView({ behavior: 'smooth', block: 'center' });\n}\n$('pback').onclick = () => {\n  const v = $('vid'); v.pause(); v.removeAttribute('src'); v.load();\n  $('player').style.display = 'none';\n};\n\nfunction esc(s) {\n  return String(s == null ? '' : s).replace(/[&<>\"']/g,\n    c => ({'&':'&amp;','<':'&lt;','>':'&gt;','\"':'&quot;',\"'\":'&#39;'}[c]));\n}\n\n// ── hash 路由（2026-11-23 网页改版）────────────────────────────\n//   #/                总览（每账号一张折叠卡片）\n//   #/remote/:id      查看画面（同页跳转，取代旧 ?view= 独立窗口）\n//   #/ledger/:id      运行分析（顶部用户切换器；t13 改名不改路由）\n//   #/earnings/:id    摩拉收益（顶部用户切换器）\n//   三个功能页顶部都有「用户切换器」，页内直接切账号看对应数据，无需退回首页。\nlet curId = '';   // 功能页当前选中的账号 id（用户切换器回显用）\n\n// 拉取会话列表（token / 主题 / 录制参数 / 账号），供功能页复用。\nasync function ensureSessions() {\n  const r = await fetch('/api/sessions');\n  const d = await r.json();\n  window.__slToken = d.directToken || \"\";\n  if (typeof d.themeDark === 'boolean') { appIsDark = d.themeDark; applyTheme(); }\n  applySettings(d);\n  users = (d.sessions || []).filter(s => s.bound);\n  return users;\n}\n\nfunction hideRemoteUi() {\n  const v = $('viewer'); if (v) v.style.display = 'none';\n  const p = $('player'); if (p) p.style.display = 'none';\n  const vc = $('viewctrls'); if (vc) vc.style.display = 'none';\n  hideRemoteCursor();   // 离开画面页：远端光标回显跟着收掉\n}\nfunction hideFuncPages() {\n  const lp = $('ledgerPage'); if (lp) lp.style.display = 'none';\n  const ep = $('earningsPage'); if (ep) ep.style.display = 'none';\n}\n\n// 用户切换器：容器 + 当前账号 + 选中回调。无账号时隐藏。\nfunction buildUserSwitch(containerId, currentId, onPick) {\n  const box = $(containerId);\n  if (!box) return;\n  box.innerHTML = '';\n  const list = users.filter(u => u && u.id);\n  if (!list.length) { box.style.display = 'none'; return; }\n  box.style.display = 'flex';\n  for (const u of list) {\n    const chip = document.createElement('span');\n    chip.className = 'uchip' + (u.id === currentId ? ' on' : '');\n    chip.textContent = u.label || u.user || u.id;\n    chip.onclick = () => onPick(u);\n    box.appendChild(chip);\n  }\n}\n\n// ── 查看画面（#/remote/:id）──────────────────────────────────\nasync function showRemote(id) {\n  await ensureSessions();\n  const u = users.find(x => x.id === id) || users[0];\n  if (!u) { showOverview(); return; }\n  document.body.classList.add('viewonly');\n  document.documentElement.classList.add('viewonly-root');\n  const vc = $('viewctrls'); if (vc) vc.style.display = 'flex';\n  hideFuncPages();\n  const v = $('viewer'); if (v) v.style.display = 'block';\n  const p = $('player'); if (p) p.style.display = 'none';\n  applyRemoteUser(u);\n}\n\nfunction applyRemoteUser(u) {\n  cur = u;\n  curId = u.id;\n  document.title = (u.label || u.user) + ' · 实时画面';\n  const t = $('viewerTitle'); if (t) t.textContent = '实时画面 · ' + (u.label || u.user);\n  // ★ T34：切账号时先只显示分辨率；实测/配置 fps 由 refreshCaptureBackend() 补齐\n  __fpsInfo = { screen: u.screen || '', cfg: 0, act: 0 };\n  renderFpsInfo();\n  const cv = $('vframe'); if (cv) cv.classList.remove('show');\n  const ph = $('ph'); if (ph) ph.style.display = 'none';\n  hideRemoteCursor();   // 切账号：旧账号的光标回显清掉，等新回包再画\n  buildUserSwitch('remoteSwitch', u.id, applyRemoteUser);\n  resetSavedList();\n  syncCapture();\n  refreshCaptureBackend();\n  startLive();      // startLive 内部先 stopLive()，切账号时旧流会被收掉\n}\n\n// ══════════════════════════════════════════════════════════════════════\n// 运行分析（#/ledger/:id）— 3.3.47 / t13 重写\n// ══════════════════════════════════════════════════════════════════════\n//\n// ★ 为什么重写：t4 交付的 /api/bgi/runs 三个接口**页面一个都没用**，\n//   老 renderLedger 只渲染 /api/bgi/ledger 的 groups，连 records 都读了没渲染 ——\n//   用户看不到「每次运行的时间 / 时长 / 状态」。\n//\n// ★ 数据源（全部已存在，本次不新增后端查询逻辑，只加一个 picks 列表接口）：\n//   /api/bgi/runs         逐次运行（开始/结束/时长/状态/脚本数/跨天/收益/拾取）\n//   /api/bgi/runs/detail  单次运行 → 路线明细 + 报错完整栈（点开才拉，按需）\n//   /api/bgi/runs/groups  按配置组聚合\n//   /api/bgi/picks        三类拾取事实（picked/no_target/skipped），分页\n//   /api/bgi/ledger       老台账（保留：用户熟悉的「问题组」视图）\n//   /api/miyoushe/mora/*  米游社账目侧（分区展示，绝不与 BGI 侧混算）\n//\n// ★ 筛选分层（关键，容易做错）：\n//   · days 是**后端**参数（查询时按 cutoff 过滤）→ 改它必须重新请求；\n//   · 配置组 / 状态 / 排序 / 关键字 / 时长阈值 / kind 是**前端**内存过滤 → 不重请。\n//\n// ★ 分页：单页 50 条（§17.5 单次响应 < 1MB）。\n\nconst RA = {\n  user: null,\n  days: 7,\n  runs: [],\n  groups: [],\n  picks: [],\n  pickCounts: { picked: 0, noTarget: 0, skipped: 0 },\n  ledger: null,\n  loading: false,\n  err: '',\n  // 前端筛选状态\n  f: { group: '', state: '', kw: '', minDurMin: '', sort: 'start-desc' },\n  // 分页（三个表各一份）\n  page: { runs: 1, groups: 1, picks: 1 },\n  pageSize: 50,\n  // 展开的运行（runId → detail JSON 或 'loading'）\n  details: {},\n  // 按需展开的分区\n  open: { runs: true, groups: true, picks: false, problems: false, miyoushe: false },\n};\n\n// ── 人类可读时长（秒 → 中文，不显示裸毫秒）──────────────────────────\nfunction raDur(sec) {\n  if (sec == null || isNaN(sec) || sec < 0) return '—';\n  const t = Math.round(sec);\n  if (t < 60) return t + '秒';\n  const h = Math.floor(t / 3600), m = Math.floor((t % 3600) / 60), s = t % 60;\n  let out = '';\n  if (h > 0) out += h + '小时';\n  if (m > 0) out += m + '分';\n  if (s > 0 || out === '') out += s + '秒';\n  return out;\n}\n\n// ── 时刻：ISO → HH:MM:SS（跨天补上日期）────────────────────────────\nfunction raTime(iso, withDay) {\n  if (!iso) return '—';\n  const d = new Date(iso);\n  if (isNaN(d.getTime())) return esc(iso);\n  const p2 = n => String(n).padStart(2, '0');\n  const hm = p2(d.getHours()) + ':' + p2(d.getMinutes()) + ':' + p2(d.getSeconds());\n  return withDay\n    ? (p2(d.getMonth() + 1) + '-' + p2(d.getDate()) + ' ' + hm)\n    : hm;\n}\n\n// ── 状态 → 徽标（颜色 + 中文）───────────────────────────────────────\nfunction raState(st) {\n  const s = String(st == null ? '' : st);\n  const map = {\n    'Finished': ['成功', 'badge-ok'],\n    'Failed': ['失败', 'badge-fail'],\n    'Cancelled': ['取消', 'badge-warn'],\n    'Interrupted': ['中断', 'badge-warn'],\n    'Running': ['进行中', 'badge-run'],\n  };\n  const it = map[s] || [s || '未知', ''];\n  return '<span class=\"ra-badge ' + it[1] + '\">' + esc(it[0]) + '</span>';\n}\n\n// ── 数字格式化（大数字加千分位，便于读）─────────────────────────────\nfunction raNum(n) {\n  if (n == null || n === '' || isNaN(n)) return '0';\n  return Number(n).toLocaleString('en-US');\n}\n\n// ── 每秒摩拉（对齐参考实现的排序维度）───────────────────────────────\nfunction raMoraPerMin(run) {\n  const sec = Number(run.durSec) || 0;\n  const mora = Number(run.mora) || 0;\n  if (sec <= 0) return 0;\n  return (mora / sec) * 60;\n}\n\n// ── 拉数据（days 变了才重请；其余筛选不重请）────────────────────────\nasync function raLoad(u) {\n  RA.user = u;\n  RA.loading = true; RA.err = '';\n  RA.details = {}; RA.page = { runs: 1, groups: 1, picks: 1 };\n  const body = $('ledgerBody');\n  if (body) body.innerHTML = raLoadingHtml();\n\n  const q = 'user=' + encodeURIComponent(u.user) + '&days=' + RA.days;\n  const [runsR, groupsR, picksR, ledgerR] = await Promise.allSettled([\n    fetch('/api/bgi/runs?' + q).then(r => r.json()),\n    fetch('/api/bgi/runs/groups?' + q).then(r => r.json()),\n    fetch('/api/bgi/picks?' + q + '&limit=500').then(r => r.json()),\n    fetch('/api/bgi/ledger?' + q).then(r => r.json()),\n  ]);\n\n  RA.runs = (runsR.status === 'fulfilled' && runsR.value && runsR.value.ok) ? (runsR.value.items || []) : [];\n  RA.groups = (groupsR.status === 'fulfilled' && groupsR.value && groupsR.value.ok) ? (groupsR.value.items || []) : [];\n  const pk = (picksR.status === 'fulfilled') ? picksR.value : null;\n  RA.picks = (pk && pk.ok) ? (pk.items || []) : [];\n  RA.pickCounts = (pk && pk.counts) ? pk.counts : { picked: 0, noTarget: 0, skipped: 0 };\n  RA.ledger = (ledgerR.status === 'fulfilled') ? ledgerR.value : null;\n\n  // 全部失败才算错；部分失败降级显示（不整页白屏）\n  if (runsR.status === 'rejected' && groupsR.status === 'rejected' && picksR.status === 'rejected')\n    RA.err = (runsR.reason && runsR.reason.message) || '网络错误';\n\n  RA.loading = false;\n  raRender();\n}\n\n// ── 加载中骨架 ───────────────────────────────────────────────────────\nfunction raLoadingHtml() {\n  return '<div class=\"ra-skel\">'\n    + '<div class=\"ra-skel-row\"></div><div class=\"ra-skel-row\"></div><div class=\"ra-skel-row\"></div>'\n    + '</div><div class=\"pageempty\" style=\"text-align:center\">正在读取运行记录…</div>';\n}\n\n// ── 主渲染 ───────────────────────────────────────────────────────────\nfunction raRender() {\n  const body = $('ledgerBody');\n  if (!body) return;\n\n  if (RA.err) {\n    body.innerHTML = '<div class=\"pageempty\">加载失败：' + esc(RA.err) + '</div>'\n      + '<div style=\"text-align:center;margin-top:10px\">'\n      + '<button class=\"uact-btn\" onclick=\"raReload()\">重试</button></div>';\n    return;\n  }\n\n  const all = RA.runs || [];\n  const empty = all.length === 0 && (RA.groups || []).length === 0;\n\n  let html = raToolbarHtml();\n  html += raMetricsHtml();\n\n  if (empty) {\n    // ── 空态 ②：确实没有记录 ──\n    html += '<div class=\"pageempty\" style=\"text-align:center;padding:34px 16px\">'\n      + '近 ' + RA.days + ' 天没有配置组运行记录。'\n      + '<div style=\"margin-top:8px;font-size:11.5px;color:var(--txt4)\">'\n      + '运行记录由 BGI 日志采集而来；确认主程序已开启、账号目录下有当天的日志文件。</div></div>';\n    body.innerHTML = html;\n    return;\n  }\n\n  html += raRunsSectionHtml();\n  html += raGroupsSectionHtml();\n  html += raPicksSectionHtml();\n  html += raProblemsSectionHtml();\n  html += raMiyousheSectionHtml();\n\n  body.innerHTML = html;\n  raWire();\n}\n\n// ── 顶部工具条：时间范围 + 筛选 + 排序 ───────────────────────────────\nfunction raToolbarHtml() {\n  const f = RA.f;\n  const groups = [...new Set((RA.runs || []).map(r => r.group).filter(Boolean))].sort();\n  const states = [...new Set((RA.runs || []).map(r => r.state).filter(Boolean))];\n\n  let h = '<div class=\"ra-toolbar\">';\n  // 时间范围（★ 后端参数：改了要重请）\n  h += '<label class=\"ra-f\"><span>时间范围</span><select id=\"raDays\">';\n  [['1', '今天'], ['3', '近 3 天'], ['7', '近 7 天'], ['14', '近 14 天'], ['30', '近 30 天']]\n    .forEach(([v, t]) => { h += '<option value=\"' + v + '\"' + (String(RA.days) === v ? ' selected' : '') + '>' + t + '</option>'; });\n  h += '</select></label>';\n\n  // 配置组（前端）\n  h += '<label class=\"ra-f\"><span>配置组</span><select id=\"raGroup\"><option value=\"\">全部</option>';\n  groups.forEach(g => { h += '<option value=\"' + esc(g) + '\"' + (f.group === g ? ' selected' : '') + '>' + esc(g) + '</option>'; });\n  h += '</select></label>';\n\n  // 状态（前端）\n  h += '<label class=\"ra-f\"><span>状态</span><select id=\"raState\"><option value=\"\">全部</option>';\n  states.forEach(s => {\n    const label = { Finished: '成功', Failed: '失败', Cancelled: '取消', Interrupted: '中断', Running: '进行中' }[s] || s;\n    h += '<option value=\"' + esc(s) + '\"' + (f.state === s ? ' selected' : '') + '>' + esc(label) + '</option>';\n  });\n  h += '</select></label>';\n\n  // 时长阈值（前端）\n  h += '<label class=\"ra-f\"><span>时长 ≥</span><select id=\"raDur\">';\n  [['', '不限'], ['1', '1 分钟'], ['5', '5 分钟'], ['15', '15 分钟'], ['30', '30 分钟'], ['60', '1 小时']]\n    .forEach(([v, t]) => { h += '<option value=\"' + v + '\"' + (f.minDurMin === v ? ' selected' : '') + '>' + t + '</option>'; });\n  h += '</select></label>';\n\n  // 排序（前端）\n  h += '<label class=\"ra-f\"><span>排序</span><select id=\"raSort\">';\n  [['start-desc', '开始时间 ↓'], ['start-asc', '开始时间 ↑'],\n   ['dur-desc', '时长 ↓'], ['dur-asc', '时长 ↑'],\n   ['mpm-desc', '每分钟摩拉 ↓'], ['picks-desc', '拾取数 ↓']]\n    .forEach(([v, t]) => { h += '<option value=\"' + v + '\"' + (f.sort === v ? ' selected' : '') + '>' + t + '</option>'; });\n  h += '</select></label>';\n\n  // 关键字（前端，跨块）\n  h += '<label class=\"ra-f ra-f-kw\"><span>搜索</span>'\n    + '<input id=\"raKw\" type=\"search\" placeholder=\"配置组 / 路线 / 物品 / 报错\" value=\"' + esc(f.kw) + '\"></label>';\n\n  h += '<button class=\"uact-btn\" id=\"raReset\">重置</button>';\n  h += '</div>';\n\n  // 生效中的筛选提示\n  const bits = [];\n  if (f.group) bits.push('配置组=' + f.group);\n  if (f.state) bits.push('状态=' + f.state);\n  if (f.minDurMin) bits.push('时长≥' + f.minDurMin + '分');\n  if (f.kw) bits.push('搜索「' + f.kw + '」');\n  if (bits.length) {\n    h += '<div class=\"ra-filterbar\">已筛选：' + esc(bits.join(' · ')) + '</div>';\n  }\n  return h;\n}\n\n// ── 顶部指标卡 ───────────────────────────────────────────────────────\nfunction raMetricsHtml() {\n  const runs = raFilteredRuns();\n  const total = runs.length;\n  const failed = runs.filter(r => r.state === 'Failed').length;\n  const ok = runs.filter(r => r.state === 'Finished').length;\n  const durSum = runs.reduce((a, r) => a + (Number(r.durSec) || 0), 0);\n  const moraSum = runs.reduce((a, r) => a + (Number(r.mora) || 0), 0);\n  const picksSum = runs.reduce((a, r) => a + (Number(r.picks) || 0), 0);\n  const rate = total > 0 ? Math.round((ok / total) * 100) : 0;\n  return '<div class=\"metrics ra-metrics\">'\n    + '<div class=\"metric\"><div class=\"v num\">' + total + '</div><div class=\"k\">运行次数</div></div>'\n    + '<div class=\"metric\"><div class=\"v num\">' + rate + '%</div><div class=\"k\">成功率</div></div>'\n    + '<div class=\"metric\"><div class=\"v num ' + (failed > 0 ? 'badge-fail' : '') + '\">' + failed + '</div><div class=\"k\">失败次数</div></div>'\n    + '<div class=\"metric\"><div class=\"v\">' + raDur(durSum) + '</div><div class=\"k\">总时长</div></div>'\n    + '<div class=\"metric\"><div class=\"v num\">' + raNum(picksSum) + '</div><div class=\"k\">总拾取</div></div>'\n    + '<div class=\"metric\"><div class=\"v num\">' + raNum(moraSum) + '</div><div class=\"k\">总摩拉</div></div>'\n    + '</div>';\n}\n\n// ── 前端筛选（全部内存操作，不重新请求）──────────────────────────────\nfunction raFilteredRuns() {\n  const f = RA.f;\n  let list = (RA.runs || []).slice();\n\n  if (f.group) list = list.filter(r => r.group === f.group);\n  if (f.state) list = list.filter(r => r.state === f.state);\n\n  if (f.minDurMin) {\n    const min = Number(f.minDurMin) * 60;\n    list = list.filter(r => (Number(r.durSec) || 0) >= min);\n  }\n\n  if (f.kw) {\n    const k = f.kw.toLowerCase();\n    list = list.filter(r =>\n      String(r.group || '').toLowerCase().includes(k)\n      || String(r.detail || '').toLowerCase().includes(k)\n      || String(r.state || '').toLowerCase().includes(k));\n  }\n\n  const s = f.sort || 'start-desc';\n  list.sort((a, b) => {\n    if (s === 'start-asc') return (a.startAtMs || 0) - (b.startAtMs || 0);\n    if (s === 'dur-desc') return (b.durSec || 0) - (a.durSec || 0);\n    if (s === 'dur-asc') return (a.durSec || 0) - (b.durSec || 0);\n    if (s === 'mpm-desc') return raMoraPerMin(b) - raMoraPerMin(a);\n    if (s === 'picks-desc') return (b.picks || 0) - (a.picks || 0);\n    return (b.startAtMs || 0) - (a.startAtMs || 0);\n  });\n  return list;\n}\n\n// ── 通用分页条 ───────────────────────────────────────────────────────\nfunction raPagerHtml(kind, total) {\n  const pages = Math.max(1, Math.ceil(total / RA.pageSize));\n  const cur = Math.min(RA.page[kind] || 1, pages);\n  RA.page[kind] = cur;\n  if (pages <= 1) return '<div class=\"ra-pager\"><span class=\"ra-pg-info\">共 ' + total + ' 条</span></div>';\n  return '<div class=\"ra-pager\" data-pager=\"' + kind + '\">'\n    + '<button class=\"ra-pg\" data-pg=\"' + kind + '\" data-to=\"' + (cur - 1) + '\"' + (cur <= 1 ? ' disabled' : '') + '>‹ 上一页</button>'\n    + '<span class=\"ra-pg-info\">第 ' + cur + ' / ' + pages + ' 页 · 共 ' + total + ' 条</span>'\n    + '<button class=\"ra-pg\" data-pg=\"' + kind + '\" data-to=\"' + (cur + 1) + '\"' + (cur >= pages ? ' disabled' : '') + '>下一页 ›</button>'\n    + '</div>';\n}\n\nfunction raSlice(kind, list) {\n  const pages = Math.max(1, Math.ceil(list.length / RA.pageSize));\n  let cur = RA.page[kind] || 1;\n  if (cur > pages) cur = pages;\n  RA.page[kind] = cur;\n  const from = (cur - 1) * RA.pageSize;\n  return list.slice(from, from + RA.pageSize);\n}\n\n// ── 分区外壳（可折叠，按需加载）──────────────────────────────────────\nfunction raSection(id, title, badge, inner, opened) {\n  const on = opened ? ' open' : '';\n  return '<details class=\"card ra-sec\" id=\"ra-sec-' + id + '\"' + on + '>'\n    + '<summary>' + esc(title)\n    + (badge != null ? ' <span class=\"badge tag\">' + esc(String(badge)) + '</span>' : '')\n    + '</summary><div class=\"body\">' + inner + '</div></details>';\n}\n\n// ── ① 运行记录（核心）───────────────────────────────────────────────\nfunction raRunsSectionHtml() {\n  const list = raFilteredRuns();\n  const rawTotal = (RA.runs || []).length;\n\n  if (!list.length) {\n    // ── 空态 ③：筛出来是空 ──\n    return raSection('runs', '运行记录', '0 / ' + rawTotal,\n      '<div class=\"pageempty\" style=\"text-align:center;padding:26px 12px\">'\n      + '当前筛选条件没有匹配记录。'\n      + '<div style=\"margin-top:8px;font-size:11.5px;color:var(--txt4)\">'\n      + '共 ' + rawTotal + ' 条运行记录被筛选条件排除 · <a href=\"javascript:raResetFilter()\" style=\"color:var(--accent)\">重置筛选</a></div>'\n      + '</div>', true);\n  }\n\n  const page = raSlice('runs', list);\n  let h = '<div class=\"ra-scroll\"><table class=\"ldtable ra-table\"><thead><tr>'\n    + '<th style=\"width:26px\"></th><th>配置组</th><th>开始</th><th>结束</th>'\n    + '<th class=\"num\">时长</th><th>状态</th><th class=\"num\">脚本</th>'\n    + '<th class=\"num\">拾取</th><th class=\"num\">摩拉/分</th><th>跨天</th>'\n    + '</tr></thead><tbody>';\n\n  for (const r of page) {\n    const open = !!RA.open['run:' + r.runId];\n    const crossTag = r.crossDay\n      ? '<span class=\"ra-badge badge-warn\" title=\"该次运行跨 0 点，分属两个日志文件\">跨天</span>'\n      : '';\n    h += '<tr class=\"ra-runrow' + (open ? ' ra-open' : '') + '\" data-run=\"' + esc(r.runId) + '\">'\n      + '<td class=\"ra-exp\">' + (open ? '▾' : '▸') + '</td>'\n      + '<td>' + esc(r.group || '(未识别组名)') + '</td>'\n      + '<td class=\"ra-mono\">' + raTime(r.startAt, true) + '</td>'\n      + '<td class=\"ra-mono\">' + (r.endAt ? raTime(r.endAt, true) : '<span style=\"opacity:.5\">进行中</span>') + '</td>'\n      + '<td class=\"num\">' + raDur(r.durSec) + '</td>'\n      + '<td>' + raState(r.state) + '</td>'\n      + '<td class=\"num\">' + (r.scriptCount || 0) + '</td>'\n      + '<td class=\"num\">' + raNum(r.picks) + '</td>'\n      + '<td class=\"num\">' + Math.round(raMoraPerMin(r)) + '</td>'\n      + '<td>' + crossTag + '</td>'\n      + '</tr>';\n    if (open) {\n      h += '<tr class=\"ra-detailrow\"><td colspan=\"10\">' + raDetailHtml(r) + '</td></tr>';\n    }\n  }\n  h += '</tbody></table></div>';\n  h += raPagerHtml('runs', list.length);\n  return raSection('runs', '运行记录', page.length + ' / ' + list.length, h, RA.open.runs);\n}\n\n// ── 展开的详情：路线明细 + 报错完整栈 ────────────────────────────────\nfunction raDetailHtml(r) {\n  const d = RA.details[r.runId];\n  if (d === 'loading' || d === undefined)\n    return '<div class=\"ra-detail-loading\">正在读取路线与报错…</div>';\n  if (d === 'error')\n    return '<div class=\"ra-detail-loading badge-fail\">明细加载失败</div>';\n  if (!d || d.ok === false || d.found === false)\n    return '<div class=\"ra-detail-loading\">' + esc((d && d.error) || '没有明细数据（可能已被留存清理）') + '</div>';\n\n  const routes = d.routes || [];\n  const errors = d.errors || [];\n  let h = '<div class=\"ra-detail\">';\n\n  // 概要行\n  h += '<div class=\"ra-dsum\">'\n    + '<span>收益 <b class=\"num\">' + raNum(d.run && d.run.exp) + '</b> 经验 · '\n    + '<b class=\"num\">' + raNum(d.run && d.run.mora) + '</b> 摩拉</span>'\n    + '<span>拾取 <b class=\"num\">' + raNum(d.run && d.run.picks) + '</b></span>'\n    + '<span>路线 ' + routes.length + ' 条 · 报错 ' + errors.length + ' 条</span>'\n    + (d.run && d.run.srcFileStart ? '<span class=\"ra-mono\" style=\"color:var(--txt4)\">' + esc(d.run.srcFileStart) + '</span>' : '')\n    + '</div>';\n\n  // 路线表\n  if (routes.length) {\n    h += '<div class=\"ra-sub\">路线 / 脚本明细</div><div class=\"ra-scroll\"><table class=\"ldtable ra-table\"><thead><tr>'\n      + '<th>类型</th><th>名称</th><th>开始</th><th class=\"num\">时长</th><th>状态</th><th class=\"num\">拾取</th><th>拾取明细</th>'\n      + '</tr></thead><tbody>';\n    for (const rt of routes) {\n      const isScript = rt.kind === 'script';\n      h += '<tr>'\n        + '<td><span class=\"ra-badge ' + (isScript ? 'badge-js' : 'badge-path') + '\">'\n          + (isScript ? '脚本' : '路线') + '</span></td>'\n        + '<td class=\"ra-name\" title=\"' + esc(rt.name || '') + '\">' + esc(rt.name || '—') + '</td>'\n        + '<td class=\"ra-mono\">' + raTime(rt.startAt) + '</td>'\n        + '<td class=\"num\">' + raDur(rt.durSec) + '</td>'\n        + '<td>' + raState(rt.state) + '</td>'\n        + '<td class=\"num\">' + (rt.pickCount || 0) + '</td>'\n        + '<td class=\"ra-pickd\">' + raPickDetailHtml(rt.pickDetail) + '</td>'\n        + '</tr>';\n    }\n    h += '</tbody></table></div>';\n  } else {\n    h += '<div class=\"ra-sub\">路线 / 脚本明细</div><div class=\"pageempty\">该次运行没有路线记录。</div>';\n  }\n\n  // 报错（完整栈，可展开）\n  if (errors.length) {\n    h += '<div class=\"ra-sub\">报错详情 <span class=\"badge tag\">' + errors.length + '</span></div>';\n    for (let i = 0; i < errors.length; i++) {\n      const er = errors[i];\n      const routeName = (routes.find(x => x.routeId === er.routeId) || {}).name || '(未归属路线)';\n      h += '<details class=\"ra-err\">'\n        + '<summary><span class=\"ra-badge badge-fail\">' + esc(er.type || er.level || '错误') + '</span> '\n        + '<span class=\"ra-errmsg\">' + esc(er.message || '') + '</span>'\n        + '<span class=\"ra-errmeta\">' + raTime(er.ts) + ' · ' + esc(routeName) + '</span></summary>';\n      h += '<pre class=\"ra-stack\">' + esc(er.detail || er.message || '(无堆栈)') + '</pre>';\n      h += '</details>';\n    }\n  } else {\n    h += '<div class=\"ra-sub\">报错详情</div><div class=\"pageempty\">该次运行没有报错。</div>';\n  }\n\n  h += '</div>';\n  return h;\n}\n\n// ── 拾取明细（JSON 对象 → 「物品 ×N」可读串）─────────────────────────\nfunction raPickDetailHtml(pd) {\n  if (!pd) return '<span style=\"opacity:.4\">—</span>';\n  let obj = pd;\n  if (typeof pd === 'string') {\n    try { obj = JSON.parse(pd); } catch (e) { return esc(pd); }\n  }\n  if (!obj || typeof obj !== 'object') return esc(String(pd));\n  const keys = Object.keys(obj);\n  if (!keys.length) return '<span style=\"opacity:.4\">—</span>';\n  // 按数量降序，最多显示 6 项，其余折叠成 +N\n  const sorted = keys.map(k => [k, Number(obj[k]) || 0]).sort((a, b) => b[1] - a[1]);\n  const head = sorted.slice(0, 6).map(([k, v]) => esc(k) + '×' + v).join('、');\n  const more = sorted.length > 6\n    ? '<span title=\"' + esc(sorted.slice(6).map(([k, v]) => k + '×' + v).join('、')) + '\" style=\"opacity:.6\"> 等 ' + sorted.length + ' 种</span>'\n    : '';\n  return '<span class=\"ra-pickitems\">' + head + more + '</span>';\n}\n\n// ── ② 按配置组汇总 ──────────────────────────────────────────────────\nfunction raGroupsSectionHtml() {\n  const f = RA.f;\n  let list = (RA.groups || []).slice();\n  if (f.group) list = list.filter(g => g.group === f.group);\n  if (f.kw) {\n    const k = f.kw.toLowerCase();\n    list = list.filter(g => String(g.group || '').toLowerCase().includes(k));\n  }\n  list.sort((a, b) => (b.runs || 0) - (a.runs || 0));\n\n  if (!list.length) {\n    return raSection('groups', '按配置组汇总', '0',\n      '<div class=\"pageempty\" style=\"text-align:center;padding:22px 12px\">'\n      + (RA.groups.length ? '当前筛选条件没有匹配的配置组。' : '暂无汇总数据。')\n      + '</div>', RA.open.groups);\n  }\n\n  const page = raSlice('groups', list);\n  let h = '<div class=\"ra-scroll\"><table class=\"ldtable ra-table\"><thead><tr>'\n    + '<th>配置组</th><th class=\"num\">次数</th><th class=\"num\">失败</th>'\n    + '<th class=\"num\">总时长</th><th class=\"num\">均经验</th><th class=\"num\">均摩拉</th>'\n    + '<th class=\"num\">总拾取</th><th>最后结束</th>'\n    + '</tr></thead><tbody>';\n  for (const g of page) {\n    const failCls = (g.failures || 0) > 0 ? 'badge-fail' : 'badge-ok';\n    h += '<tr>'\n      + '<td>' + esc(g.group || '(未识别)') + '</td>'\n      + '<td class=\"num\">' + (g.runs || 0) + '</td>'\n      + '<td class=\"num ' + failCls + '\">' + (g.failures || 0) + '</td>'\n      + '<td class=\"num\">' + raDur(g.totalDurSec) + '</td>'\n      + '<td class=\"num\">' + raNum(g.avgExp) + '</td>'\n      + '<td class=\"num\">' + raNum(g.avgMora) + '</td>'\n      + '<td class=\"num\">' + raNum(g.totalPicks) + '</td>'\n      + '<td class=\"ra-mono\">' + raTime(g.lastEndAt, true) + '</td>'\n      + '</tr>';\n  }\n  h += '</tbody></table></div>' + raPagerHtml('groups', list.length);\n  return raSection('groups', '按配置组汇总', page.length + ' / ' + list.length, h, RA.open.groups);\n}\n\n// ── ③ 拾取事实（三类）───────────────────────────────────────────────\nfunction raPicksSectionHtml() {\n  const c = RA.pickCounts || {};\n  const total = (c.picked || 0) + (c.noTarget || 0) + (c.skipped || 0);\n  const badge = total + ' 条';\n\n  if (!total) {\n    return raSection('picks', '拾取明细', '0',\n      '<div class=\"pageempty\" style=\"text-align:center;padding:22px 12px\">'\n      + '近 ' + RA.days + ' 天没有拾取事件记录。</div>', RA.open.picks);\n  }\n\n  // 三类计数条\n  let h = '<div class=\"ra-pickcounts\">'\n    + '<span class=\"ra-pc ra-pc-picked\"><b>' + raNum(c.picked) + '</b> 显式拾取</span>'\n    + '<span class=\"ra-pc ra-pc-notarget\"><b>' + raNum(c.noTarget) + '</b> 无目标</span>'\n    + '<span class=\"ra-pc ra-pc-skipped\"><b>' + raNum(c.skipped) + '</b> 跳过</span>'\n    + '</div>';\n\n  // kind 过滤\n  h += '<div class=\"ra-toolbar\" style=\"margin:10px 0 6px\">'\n    + '<label class=\"ra-f\"><span>类型</span><select id=\"raKind\">'\n    + '<option value=\"\">全部</option>'\n    + '<option value=\"picked\"' + (RA.f.kind === 'picked' ? ' selected' : '') + '>显式拾取</option>'\n    + '<option value=\"no_target\"' + (RA.f.kind === 'no_target' ? ' selected' : '') + '>无目标</option>'\n    + '<option value=\"skipped\"' + (RA.f.kind === 'skipped' ? ' selected' : '') + '>跳过</option>'\n    + '</select></label></div>';\n\n  let list = (RA.picks || []).slice();\n  if (RA.f.kind) list = list.filter(p => p.kind === RA.f.kind);\n  if (RA.f.kw) {\n    const k = RA.f.kw.toLowerCase();\n    list = list.filter(p => String(p.item || '').toLowerCase().includes(k));\n  }\n\n  if (!list.length) {\n    h += '<div class=\"pageempty\" style=\"text-align:center;padding:18px 12px\">'\n      + '当前类型/搜索条件下没有拾取事件。</div>';\n    return raSection('picks', '拾取明细', badge, h, RA.open.picks);\n  }\n\n  const page = raSlice('picks', list);\n  h += '<div class=\"ra-scroll\"><table class=\"ldtable ra-table\"><thead><tr>'\n    + '<th>时刻</th><th>类型</th><th>物品 / 原因（原话）</th><th>所属运行</th>'\n    + '</tr></thead><tbody>';\n  for (const p of page) {\n    const kindLabel = { picked: '拾取', no_target: '无目标', skipped: '跳过' }[p.kind] || p.kind;\n    const kindCls = { picked: 'badge-ok', no_target: 'badge-warn', skipped: 'badge-fail' }[p.kind] || '';\n    h += '<tr>'\n      + '<td class=\"ra-mono\">' + raTime(p.ts, true) + '</td>'\n      + '<td><span class=\"ra-badge ' + kindCls + '\">' + esc(kindLabel) + '</span></td>'\n      + '<td class=\"ra-name\" title=\"' + esc(p.item || '') + '\">' + esc(p.item || '—') + '</td>'\n      + '<td class=\"ra-mono\" style=\"font-size:10.5px;opacity:.65\">' + esc(String(p.runId || '').slice(0, 8)) + '</td>'\n      + '</tr>';\n  }\n  h += '</tbody></table></div>';\n  if ((RA.picks || []).length >= 500) {\n    h += '<div class=\"desc\" style=\"margin-top:6px;color:var(--txt4)\">'\n      + '已显示最近 500 条（服务端分页上限）。可用类型/搜索缩小范围。</div>';\n  }\n  h += raPagerHtml('picks', list.length);\n  return raSection('picks', '拾取明细', badge, h, RA.open.picks);\n}\n\n// ── ④ 问题组（沿用老台账视图，用户熟悉）─────────────────────────────\nfunction raProblemsSectionHtml() {\n  const d = RA.ledger || {};\n  const problems = (d.zeroReward || []).concat(d.failed || []);\n  if (!problems.length) return '';\n  let h = '';\n  for (const rec of problems) {\n    h += '<div class=\"dline\"><span class=\"dk\">' + esc(rec.group || '') + '</span>'\n      + '<span class=\"dv\">' + esc(rec.state || '') + ' · ' + esc(rec.day || '') + ' ' + esc(rec.start || '')\n      + (rec.detail ? ' · ' + esc(rec.detail) : '') + '</span></div>';\n  }\n  return raSection('problems', '问题组（0 收益 / 失败）', problems.length, h, RA.open.problems);\n}\n\n// ── ⑤ 米游社摩拉（分区展示，与 BGI 侧不混算）────────────────────────\nfunction raMiyousheSectionHtml() {\n  let h = '<div id=\"raMiyousheBody\">'\n    + '<div class=\"pageempty\">展开本区后自动加载米游社账目数据…</div></div>';\n  h += '<div class=\"ra-source\">'\n    + '<b>口径说明</b>：本区为<b>米游社札记账目流水</b>（官方数据，延迟约 1 小时，无路线归属）；'\n    + '上方各区的摩拉来自 <b>BGI 日志侧</b>（该次挂机时段实际收益，含路线上下文）。'\n    + '<b>两者口径不同，分区展示，绝不混算。</b>'\n    + '</div>';\n  return raSection('miyoushe', '米游社摩拉（账目侧）', null, h, RA.open.miyoushe);\n}\n\n// ── 米游社数据加载（分区展开时才拉）─────────────────────────────────\nasync function raLoadMiyoushe() {\n  const host = $('raMiyousheBody');\n  if (!host || !RA.user) return;\n  host.innerHTML = '<div class=\"pageempty\">加载中…</div>';\n  try {\n    const u = encodeURIComponent(RA.user.user);\n    const [sumR, verR] = await Promise.allSettled([\n      fetch('/api/miyoushe/mora/summary?user=' + u + '&days=' + RA.days).then(r => r.json()),\n      fetch('/api/miyoushe/mora/versions?user=' + u).then(r => r.json()),\n    ]);\n    const sum = (sumR.status === 'fulfilled') ? sumR.value : null;\n    const ver = (verR.status === 'fulfilled') ? verR.value : null;\n\n    if (!sum || sum.ok === false) {\n      host.innerHTML = '<div class=\"pageempty\">米游社数据不可用：'\n        + esc((sum && sum.error) || '未登录或未采集') + '</div>';\n      return;\n    }\n    const items = sum.items || [];\n    if (!items.length) {\n      host.innerHTML = '<div class=\"pageempty\">近 ' + RA.days + ' 天没有米游社账目记录。'\n        + '<div style=\"margin-top:6px;font-size:11.5px;color:var(--txt4)\">'\n        + '米游社数据需在「米游社」页完成登录并等待采集。</div></div>';\n      return;\n    }\n    let h = '<div class=\"ra-scroll\"><table class=\"ldtable ra-table\"><thead><tr>'\n      + '<th>日期</th><th>配置组/来源</th><th class=\"num\">摩拉</th></tr></thead><tbody>';\n    for (const it of items.slice(0, 50)) {\n      h += '<tr><td class=\"ra-mono\">' + esc(it.day || '') + '</td>'\n        + '<td>' + esc(it.groupName || '-') + '</td>'\n        + '<td class=\"num\">' + raNum(it.totalMorale || it.totalMora || 0) + '</td></tr>';\n    }\n    h += '</tbody></table></div>';\n    const verName = (ver && ver.current) ? String(ver.current) : '';\n    if (verName) h += '<div class=\"ra-source\" style=\"margin-top:6px\">分类规则版本：' + esc(verName) + '</div>';\n    host.innerHTML = h;\n  } catch (e) {\n    host.innerHTML = '<div class=\"pageempty\">加载失败：' + esc(e.message) + '</div>';\n  }\n}\n\n// ── 事件绑定 ─────────────────────────────────────────────────────────\nfunction raWire() {\n  // 时间范围（后端参数 → 重请）\n  const days = $('raDays');\n  if (days) days.onchange = () => { RA.days = Number(days.value) || 7; raSaveFilter(); raLoad(RA.user); };\n\n  // 以下全部前端过滤（不重请）\n  const bind = (id, key, isNum) => {\n    const el = $(id);\n    if (!el) return;\n    el.onchange = () => {\n      RA.f[key] = isNum ? (el.value || '') : el.value;\n      RA.page = { runs: 1, groups: 1, picks: 1 };\n      raSaveFilter();\n      raRender();\n    };\n  };\n  bind('raGroup', 'group');\n  bind('raState', 'state');\n  bind('raDur', 'minDurMin');\n  bind('raSort', 'sort');\n  bind('raKind', 'kind');\n\n  // 关键字：输入防抖 260ms\n  const kw = $('raKw');\n  if (kw) {\n    let t = null;\n    kw.oninput = () => {\n      clearTimeout(t);\n      t = setTimeout(() => {\n        RA.f.kw = kw.value.trim();\n        RA.page = { runs: 1, groups: 1, picks: 1 };\n        raSaveFilter();\n        raRender();\n      }, 260);\n    };\n  }\n\n  const reset = $('raReset');\n  if (reset) reset.onclick = () => raResetFilter();\n\n  // 展开某次运行 → 拉详情\n  document.querySelectorAll('tr.ra-runrow').forEach(tr => {\n    tr.onclick = (ev) => {\n      if (ev.target.closest('a')) return;\n      const id = tr.getAttribute('data-run');\n      if (!id) return;\n      const key = 'run:' + id;\n      if (RA.open[key]) { delete RA.open[key]; raRender(); return; }\n      RA.open[key] = true;\n      RA.details[id] = 'loading';\n      raRender();\n      raFetchDetail(id);\n    };\n  });\n\n  // 分页\n  document.querySelectorAll('button.ra-pg').forEach(b => {\n    b.onclick = () => {\n      const kind = b.getAttribute('data-pg');\n      const to = Number(b.getAttribute('data-to'));\n      if (!kind || !to || to < 1) return;\n      RA.page[kind] = to;\n      raRender();\n      const sec = $('ra-sec-' + kind);\n      if (sec && sec.scrollIntoView) sec.scrollIntoView({ block: 'nearest' });\n    };\n  });\n\n  // 分区折叠状态记忆 + 米游社懒加载\n  document.querySelectorAll('details.ra-sec').forEach(det => {\n    const id = (det.id || '').replace('ra-sec-', '');\n    det.ontoggle = () => {\n      RA.open[id] = det.open;\n      if (id === 'miyoushe' && det.open) raLoadMiyoushe();\n    };\n  });\n}\n\n// 拉单次运行明细\nasync function raFetchDetail(runId) {\n  try {\n    const r = await fetch('/api/bgi/runs/detail?id=' + encodeURIComponent(runId));\n    RA.details[runId] = await r.json();\n  } catch (e) {\n    RA.details[runId] = 'error';\n  }\n  raRender();\n}\n\n// 重置筛选（保留时间范围）\nfunction raResetFilter() {\n  RA.f = { group: '', state: '', kw: '', minDurMin: '', sort: 'start-desc' };\n  RA.page = { runs: 1, groups: 1, picks: 1 };\n  raSaveFilter();\n  raRender();\n}\n\nfunction raReload() { if (RA.user) raLoad(RA.user); }\n\n// ── 筛选条件写进 URL hash（刷新 / 分享不丢）──────────────────────────\n//   #/ledger/:id?days=7&group=xxx&state=Failed&sort=dur-desc&kw=abc\nfunction raSaveFilter() {\n  try {\n    const p = new URLSearchParams();\n    p.set('days', String(RA.days));\n    if (RA.f.group) p.set('group', RA.f.group);\n    if (RA.f.state) p.set('state', RA.f.state);\n    if (RA.f.sort && RA.f.sort !== 'start-desc') p.set('sort', RA.f.sort);\n    if (RA.f.kw) p.set('kw', RA.f.kw);\n    if (RA.f.minDurMin) p.set('minDur', RA.f.minDurMin);\n    if (RA.f.kind) p.set('kind', RA.f.kind);\n    const base = location.hash.split('?')[0];\n    const qs = p.toString();\n    const next = base + (qs ? '?' + qs : '');\n    if (location.hash !== next) history.replaceState(null, '', next);\n  } catch (e) { /* 不支持 history 时忽略 */ }\n}\n\nfunction raLoadFilterFromHash() {\n  try {\n    const q = location.hash.split('?')[1] || '';\n    const p = new URLSearchParams(q);\n    RA.days = Number(p.get('days')) || 7;\n    RA.f = {\n      group: p.get('group') || '',\n      state: p.get('state') || '',\n      kw: p.get('kw') || '',\n      minDurMin: p.get('minDur') || '',\n      sort: p.get('sort') || 'start-desc',\n      kind: p.get('kind') || '',\n    };\n  } catch (e) { /* 解析失败用默认值 */ }\n}\n\n// ── 新入口：showLedger 的替代实现 ────────────────────────────────────\nasync function showRunAnalysis(id) {\n  await ensureSessions();\n  const u = users.find(x => x.id === id) || users[0];\n  if (!u) { showOverview(); return; }\n  document.body.classList.add('page-func');\n  hideRemoteUi();\n  const lp = $('ledgerPage'); if (lp) lp.style.display = 'block';\n  const ep = $('earningsPage'); if (ep) ep.style.display = 'none';\n  const mp = $('miyoushePage'); if (mp) mp.style.display = 'none';\n  raLoadFilterFromHash();\n  RA.user = u;\n  curId = u.id;\n  document.title = '运行分析 · ' + (u.label || u.user);\n  const t = $('ledgerTitle'); if (t) t.textContent = '运行分析';\n  buildUserSwitch('ledgerSwitch', u.id, (nu) => {\n    RA.user = nu; curId = nu.id;\n    document.title = '运行分析 · ' + (nu.label || nu.user);\n    buildUserSwitch('ledgerSwitch', nu.id, arguments.callee);\n    raLoad(nu);\n  });\n  raLoad(u);\n}\n// 兼容旧调用点：原 showLedger 语义已由 showRunAnalysis 承担（t13 改名不改路由）\nfunction showLedger(id) { return showRunAnalysis(id); }\n// ── 摩拉收益（#/earnings/:id）────────────────────────────────\nasync function showEarnings(id) {\n  await ensureSessions();\n  const u = users.find(x => x.id === id) || users[0];\n  if (!u) { showOverview(); return; }\n  document.body.classList.add('page-func');\n  hideRemoteUi();\n  const lp = $('ledgerPage'); if (lp) lp.style.display = 'none';\n  const ep = $('earningsPage'); if (ep) ep.style.display = 'block';\n  applyEarningsUser(u);\n}\nfunction applyEarningsUser(u) {\n  curId = u.id;\n  document.title = '摩拉收益 · ' + (u.label || u.user);\n  buildUserSwitch('earningsSwitch', u.id, applyEarningsUser);\n  loadEarnings(u);\n}\nasync function loadEarnings(u) {\n  const body = $('earningsBody');\n  body.innerHTML = '<div class=\"pageempty\">加载中…</div>';\n  try {\n    const r = await fetch('/api/bgi/stats?user=' + encodeURIComponent(u.user));\n    const d = await r.json();\n    body.innerHTML = renderEarnings(d);\n  } catch (e) {\n    body.innerHTML = '<div class=\"pageempty\">加载失败：' + esc(e.message) + '</div>';\n  }\n}\nfunction renderEarnings(d) {\n  if (!d) return '<div class=\"pageempty\">暂无收益数据。</div>';\n  const exp = d.exp || d.expGained || d.dogfoodExp || 0;\n  const mora = d.mora || d.moraGained || d.dogfoodMora || 0;\n  let picks = d.pickTotal || 0;\n  if (d.picks && typeof d.picks === 'object') {\n    picks = Object.values(d.picks).reduce((a, b) => a + (typeof b === 'number' ? b : 0), 0);\n  }\n  let html = '<div class=\"metrics\">'\n    + '<div class=\"metric\"><div class=\"v num\">' + exp + '</div><div class=\"k\">今日经验</div></div>'\n    + '<div class=\"metric\"><div class=\"v num\">' + mora + '</div><div class=\"k\">今日摩拉</div></div>'\n    + '<div class=\"metric\"><div class=\"v num\">' + picks + '</div><div class=\"k\">今日拾取</div></div>'\n    + '</div>';\n  if (d.lastRoute) html += '<div class=\"dline\" style=\"margin-top:12px\"><span class=\"dk\">最近路线</span><span class=\"dv\">' + esc(d.lastRoute) + '</span></div>';\n  return html;\n}\n\n// ── 总览（#/）────────────────────────────────────────────────\nfunction showOverview() {\n  hideRemoteUi();\n  hideFuncPages();\n  document.body.classList.remove('viewonly', 'page-func');\n  document.documentElement.classList.remove('viewonly-root');\n  document.title = 'SoulLink';\n  try { loadUsers(); } catch (e) { console.error('加载失败', e); }\n}\n\n// ── 路由分发 ─────────────────────────────────────────────────\nfunction route() {\n  stopLive();                              // 任何路由切换先停流\n  document.body.classList.remove('viewonly', 'page-func');\n  document.documentElement.classList.remove('viewonly-root');\n  const parts = (location.hash || '').replace(/^#\\/?/, '').split('/').filter(Boolean);\n  const page = parts[0] || '';\n  const id = parts.length > 1 ? decodeURIComponent(parts.slice(1).join('/')) : '';\n  if (page === 'remote' && id) { showRemote(id); return; }\n  if (page === 'ledger') { showLedger(id); return; }\n  if (page === 'earnings') { showEarnings(id); return; }\n  showOverview();\n}\n\nconst lb = $('ledgerBack'); if (lb) lb.onclick = () => { location.hash = '#/'; };\nconst eb = $('earningsBack'); if (eb) eb.onclick = () => { location.hash = '#/'; };\nwindow.addEventListener('hashchange', route);\n/* ══════════ Pointer Lock 真机验收清单（2026-11-23，部署后实测）══════════\n   对应 POINTER-LOCK-DESIGN-20261123.md §7.2 V1-V10：\n   V1 进入锁定：桌面左键点画面 → 光标隐藏、pointerLockElement===#scr、移动转视角\n   V2 相对位移正确：缓慢匀速移动鼠标 → 远端视角方向/幅度一致，无反向/漂移\n   V3 点击不粘连：锁定态点击 → 远端正确点击，无「进锁定那次被误点」\n   V4 ESC 一键开菜单：锁定态按 ESC → 光标恢复 + 远端弹出游戏菜单（补发一次 Esc）\n   V5 快捷键逃生门：命中 Ctrl+Alt+Shift(默认) → 光标恢复，远端不弹菜单（纯解锁）\n   V6 ESC 后重锁冷却：ESC 退出后立即点画面 → 500ms 内不重锁，无 error 抖动\n   V7 触屏回退：触屏打开 → 无锁定，单指滑动仍转视角（moverel），不触发锁定\n   V8 不支持浏览器降级：无 Pointer Lock → 走绝对模式 + 右键 look，无报错\n   V9 锁死兜底：锁定态断网/JS 抛异常 → 按 ESC 仍退出（浏览器层），光标恢复\n   V10 灵敏度：同机锁定态转 90° 视角 → 记录鼠标位移标定 sensitivity，二次进入一致\n══════════════════════════════════════════════════════════════════ */\n\nroute();\n</script>\n</body>\n</html>";
	}

	private static string PageVersion()
	{
		try
		{
			string text = LoadIndexHtml();
			byte[] array = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty));
			StringBuilder stringBuilder = new StringBuilder(12);
			for (int i = 0; i < 6; i++)
			{
				stringBuilder.Append(array[i].ToString("x2"));
			}
			return stringBuilder.ToString();
		}
		catch
		{
			return "";
		}
	}

	private static async Task WriteText(HttpListenerContext ctx, int code, string text)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(text);
		ctx.Response.StatusCode = code;
		ctx.Response.ContentType = "text/plain; charset=utf-8";
		ctx.Response.ContentLength64 = bytes.Length;
		await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(continueOnCapturedContext: false);
		ctx.Response.Close();
	}

	private static async Task WriteJson(HttpListenerContext ctx, object data)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data));
		ctx.Response.StatusCode = 200;
		ctx.Response.ContentType = "application/json; charset=utf-8";
		ctx.Response.ContentLength64 = bytes.Length;
		await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(continueOnCapturedContext: false);
		ctx.Response.Close();
	}

	private static async Task WriteHtml(HttpListenerContext ctx, string html)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(html);
		ctx.Response.StatusCode = 200;
		ctx.Response.ContentType = "text/html; charset=utf-8";
		ctx.Response.ContentLength64 = bytes.Length;
		await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(continueOnCapturedContext: false);
		ctx.Response.Close();
	}

	private static async Task WriteFile(HttpListenerContext ctx, string rel)
	{
		_ = 3;
		try
		{
			string fullPath = Path.GetFullPath(ReplayRoot);
			string fullPath2 = Path.GetFullPath(Path.Combine(fullPath, Uri.UnescapeDataString(rel)));
			if (!fullPath2.StartsWith(fullPath, StringComparison.OrdinalIgnoreCase))
			{
				await WriteText(ctx, 403, "禁止访问");
				return;
			}
			if (!File.Exists(fullPath2))
			{
				await WriteText(ctx, 404, "文件不存在");
				return;
			}
			FileInfo fileInfo = new FileInfo(fullPath2);
			string text = fileInfo.Extension.ToLowerInvariant();
			string text2;
			if (text != null)
			{
				int length = text.Length;
				if (length != 4)
				{
					if (length == 5)
					{
						char c = text[2];
						if (c != 'e')
						{
							if (c != 'p')
							{
								if (c == 's' && text == ".json")
								{
									text2 = "application/json; charset=utf-8";
									goto IL_0273;
								}
							}
							else if (text == ".jpeg")
							{
								goto IL_0236;
							}
						}
						else if (text == ".webm")
						{
							text2 = "video/webm";
							goto IL_0273;
						}
					}
				}
				else
				{
					char c = text[1];
					if ((uint)c > 109u)
					{
						if (c != 'p')
						{
							if (c != 't' || !(text == ".txt"))
							{
								goto IL_026c;
							}
							text2 = "text/plain; charset=utf-8";
						}
						else
						{
							if (!(text == ".png"))
							{
								goto IL_026c;
							}
							text2 = "image/png";
						}
						goto IL_0273;
					}
					if (c != 'j')
					{
						if (c == 'm' && text == ".mp4")
						{
							text2 = "video/mp4";
							goto IL_0273;
						}
					}
					else if (text == ".jpg")
					{
						goto IL_0236;
					}
				}
			}
			goto IL_026c;
			IL_0236:
			text2 = "image/jpeg";
			goto IL_0273;
			IL_026c:
			text2 = "application/octet-stream";
			goto IL_0273;
			IL_0273:
			string contentType = text2;
			if (text == ".mp4" || text == ".webm")
			{
				await WriteStreamWithRange(ctx, fullPath2, fileInfo.Length, contentType);
				return;
			}
			ctx.Response.ContentType = contentType;
			ctx.Response.ContentLength64 = fileInfo.Length;
			using FileStream fs = File.OpenRead(fullPath2);
			await fs.CopyToAsync(ctx.Response.OutputStream).ConfigureAwait(continueOnCapturedContext: false);
			ctx.Response.Close();
		}
		catch
		{
			try
			{
				ctx.Response.Abort();
			}
			catch
			{
			}
		}
	}

	private static async Task WriteStreamWithRange(HttpListenerContext ctx, string fullPath, long length, string contentType)
	{
		long num = 0L;
		long num2 = length - 1;
		string text = ctx.Request.Headers["Range"];
		if (!string.IsNullOrWhiteSpace(text) && text.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
		{
			string text2 = text.Substring(6);
			int num3 = text2.IndexOf('-');
			if (num3 >= 0)
			{
				string text3 = text2.Substring(0, num3).Trim();
				string text4 = text2.Substring(num3 + 1).Trim();
				if (text3.Length == 0)
				{
					if (long.TryParse(text4, out var result) && result > 0)
					{
						num = Math.Max(0L, length - result);
						num2 = length - 1;
					}
				}
				else
				{
					if (long.TryParse(text3, out var result2))
					{
						num = Math.Clamp(result2, 0L, Math.Max(0L, length - 1));
					}
					if (text4.Length > 0 && long.TryParse(text4, out var result3))
					{
						num2 = Math.Clamp(result3, num, length - 1);
					}
				}
			}
		}
		long num4 = num2 - num + 1;
		if (num4 <= 0)
		{
			await WriteText(ctx, 416, "范围无效");
			return;
		}
		bool flag = num > 0 || num2 < length - 1;
		ctx.Response.StatusCode = (flag ? 206 : 200);
		ctx.Response.ContentType = contentType;
		ctx.Response.ContentLength64 = num4;
		ctx.Response.Headers["Accept-Ranges"] = "bytes";
		if (flag)
		{
			ctx.Response.Headers["Content-Range"] = "bytes " + num + "-" + num2 + "/" + length;
		}
		using FileStream fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		fs.Seek(num, SeekOrigin.Begin);
		byte[] buffer = new byte[65536];
		long remaining = num4;
		while (remaining > 0)
		{
			int length2 = (int)Math.Min(buffer.Length, remaining);
			int got = await fs.ReadAsync(buffer.AsMemory(0, length2)).ConfigureAwait(continueOnCapturedContext: false);
			if (got <= 0)
			{
				break;
			}
			await ctx.Response.OutputStream.WriteAsync(buffer.AsMemory(0, got)).ConfigureAwait(continueOnCapturedContext: false);
			remaining -= got;
		}
		ctx.Response.Close();
	}

	private static object ApiBgiLedger(HttpListenerContext ctx)
	{
		string text = (ctx.Request.QueryString["user"] ?? "").Trim();
		if (text.Length == 0)
		{
			return new
			{
				ok = false,
				error = "缺少 user"
			};
		}
		int days = (int.TryParse(ctx.Request.QueryString["days"], out var result) ? Math.Clamp(result, 1, 365) : 7);
		return new
		{
			ok = true,
			user = text,
			days = days,
			records = RouteLedger.Query(text, days),
			groups = RouteLedger.GroupSummary(text, days),
			zeroReward = RouteLedger.QueryZeroReward(text, days),
			failed = RouteLedger.QueryFailed(text, days)
		};
	}

	private static async Task<object> ApiBgiRunsAsync(HttpListenerContext ctx)
	{
		string user = (ctx.Request.QueryString["user"] ?? "").Trim();
		if (user.Length == 0)
		{
			return new
			{
				ok = false,
				error = "缺少 user"
			};
		}
		int days = (int.TryParse(ctx.Request.QueryString["days"], out var result) ? Math.Clamp(result, 1, 365) : 7);
		IReadOnlyList<RunRecord> readOnlyList;
		try
		{
			readOnlyList = await RunRecordStore.QueryRunsAsync(user, days).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			return new
			{
				ok = false,
				error = "查询失败：" + ex.Message
			};
		}
		List<object> list = new List<object>(readOnlyList.Count);
		foreach (RunRecord item in readOnlyList)
		{
			list.Add(new
			{
				runId = item.RunId,
				account = item.Account,
				group = item.GroupName,
				startAt = Iso(item.StartAtMs),
				startAtMs = item.StartAtMs,
				endAt = (item.EndAtMs.HasValue ? Iso(item.EndAtMs.Value) : null),
				endAtMs = item.EndAtMs,
				durSec = (double)item.DurMs / 1000.0,
				state = item.State,
				scriptCount = item.ScriptCount,
				exp = item.Exp,
				mora = item.Mora,
				picks = item.Picks,
				startDay = item.StartDay,
				endDay = item.EndDay,
				crossDay = item.IsCrossDay,
				backfilled = item.IsBackfilled,
				detail = item.Detail,
				srcFile = Path.GetFileName(item.SrcFileStart)
			});
		}
		return new
		{
			ok = true,
			user = user,
			days = days,
			source = "run-db",
			retentionDays = RunRecordHost.RetentionDays,
			count = list.Count,
			items = list
		};
	}

	private static async Task<object> ApiBgiRunDetailAsync(HttpListenerContext ctx)
	{
		string runId = (ctx.Request.QueryString["id"] ?? "").Trim();
		if (runId.Length == 0)
		{
			string user = (ctx.Request.QueryString["user"] ?? "").Trim();
			if (user.Length == 0)
			{
				return new
				{
					ok = false,
					error = "缺少 id（或 user+latest=1）"
				};
			}
			int days = (int.TryParse(ctx.Request.QueryString["days"], out var result) ? Math.Clamp(result, 1, 365) : 7);
			IReadOnlyList<RunRecord> readOnlyList;
			try
			{
				readOnlyList = await RunRecordStore.QueryRunsAsync(user, days).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (Exception ex)
			{
				return new
				{
					ok = false,
					error = "查询失败：" + ex.Message
				};
			}
			if (readOnlyList.Count == 0)
			{
				return new
				{
					ok = true,
					user = user,
					found = false,
					error = "该账号没有运行记录"
				};
			}
			runId = readOnlyList[0].RunId;
		}
		RunRecord run;
		IReadOnlyList<RouteRecord> routes;
		IReadOnlyList<ErrorRecord> readOnlyList2;
		try
		{
			run = await RunRecordStore.FindRunAsync(runId).ConfigureAwait(continueOnCapturedContext: false);
			routes = await RunRecordStore.QueryRoutesAsync(runId).ConfigureAwait(continueOnCapturedContext: false);
			readOnlyList2 = await RunRecordStore.QueryErrorsAsync(runId).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex2)
		{
			return new
			{
				ok = false,
				error = "查询失败：" + ex2.Message
			};
		}
		if (run == null && routes.Count == 0 && readOnlyList2.Count == 0)
		{
			return new
			{
				ok = true,
				runId = runId,
				found = false,
				error = "没有该运行记录（可能已被留存清理）"
			};
		}
		List<object> list = new List<object>(routes.Count);
		foreach (RouteRecord item in routes)
		{
			list.Add(new
			{
				routeId = item.RouteId,
				kind = item.Kind,
				name = item.Name,
				parentRouteId = item.ParentRouteId,
				startAt = Iso(item.StartAtMs),
				startAtMs = item.StartAtMs,
				endAt = (item.EndAtMs.HasValue ? Iso(item.EndAtMs.Value) : null),
				endAtMs = item.EndAtMs,
				durSec = (double)item.DurMs / 1000.0,
				state = item.State,
				pickCount = item.PickCount,
				pickDetail = item.PickDetail,
				srcFile = Path.GetFileName(item.SrcFile)
			});
		}
		List<object> list2 = new List<object>(readOnlyList2.Count);
		foreach (ErrorRecord item2 in readOnlyList2)
		{
			list2.Add(new
			{
				errId = item2.ErrId,
				routeId = item2.RouteId,
				ts = Iso(item2.TsMs),
				tsMs = item2.TsMs,
				level = item2.Level,
				type = item2.ExceptionType,
				message = item2.Message,
				detail = item2.Detail,
				source = item2.Source,
				srcFile = Path.GetFileName(item2.SrcFile)
			});
		}
		return new
		{
			ok = true,
			runId = runId,
			found = true,
			run = ((run == null) ? null : new
			{
				runId = run.RunId,
				account = run.Account,
				group = run.GroupName,
				startAt = Iso(run.StartAtMs),
				startAtMs = run.StartAtMs,
				endAt = (run.EndAtMs.HasValue ? Iso(run.EndAtMs.Value) : null),
				endAtMs = run.EndAtMs,
				durSec = (double)run.DurMs / 1000.0,
				state = run.State,
				scriptCount = run.ScriptCount,
				exp = run.Exp,
				mora = run.Mora,
				picks = run.Picks,
				startDay = run.StartDay,
				endDay = run.EndDay,
				crossDay = run.IsCrossDay,
				backfilled = run.IsBackfilled,
				detail = run.Detail,
				srcFileStart = Path.GetFileName(run.SrcFileStart),
				srcFileEnd = Path.GetFileName(run.SrcFileEnd)
			}),
			routes = list,
			errors = list2,
			routeCount = list.Count,
			errorCount = list2.Count
		};
	}

	private static async Task<object> ApiBgiRunGroupsAsync(HttpListenerContext ctx)
	{
		string user = (ctx.Request.QueryString["user"] ?? "").Trim();
		if (user.Length == 0)
		{
			return new
			{
				ok = false,
				error = "缺少 user"
			};
		}
		int days = (int.TryParse(ctx.Request.QueryString["days"], out var result) ? Math.Clamp(result, 1, 365) : 7);
		IReadOnlyList<RunGroupSummary> readOnlyList;
		try
		{
			readOnlyList = await RunRecordStore.QueryGroupsAsync(user, days).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			return new
			{
				ok = false,
				error = "查询失败：" + ex.Message
			};
		}
		List<object> list = new List<object>(readOnlyList.Count);
		foreach (RunGroupSummary item in readOnlyList)
		{
			list.Add(new
			{
				group = item.Group,
				runs = item.Runs,
				failures = item.Failures,
				avgExp = item.AvgExp,
				avgMora = item.AvgMora,
				totalExp = item.TotalExp,
				totalMora = item.TotalMora,
				totalPicks = item.TotalPicks,
				totalDurSec = (double)item.TotalDurMs / 1000.0,
				lastEndAt = (item.LastEndAtMs.HasValue ? Iso(item.LastEndAtMs.Value) : null)
			});
		}
		return new
		{
			ok = true,
			user = user,
			days = days,
			count = list.Count,
			items = list
		};
	}

	private static async Task<object> ApiBgiPicksAsync(HttpListenerContext ctx)
	{
		string user = (ctx.Request.QueryString["user"] ?? "").Trim();
		if (user.Length == 0)
		{
			return new
			{
				ok = false,
				error = "缺少 user"
			};
		}
		int days = (int.TryParse(ctx.Request.QueryString["days"], out var result) ? Math.Clamp(result, 1, 365) : 7);
		int offset = (int.TryParse(ctx.Request.QueryString["offset"], out var result2) ? Math.Max(0, result2) : 0);
		int limit = (int.TryParse(ctx.Request.QueryString["limit"], out var result3) ? Math.Clamp(result3, 1, 500) : 100);
		string runId = (ctx.Request.QueryString["run"] ?? "").Trim();
		string text = runId;
		if (text != null && text.Length == 0)
		{
			runId = null;
		}
		string routeId = (ctx.Request.QueryString["route"] ?? "").Trim();
		string text2 = routeId;
		if (text2 != null && text2.Length == 0)
		{
			routeId = null;
		}
		string kind = (ctx.Request.QueryString["kind"] ?? "").Trim();
		if (kind != "picked" && kind != "no_target" && kind != "skipped")
		{
			kind = "";
		}
		IReadOnlyList<PickEvent> rows;
		RunRecordStore.PickFactCounts pickFactCounts;
		try
		{
			rows = await RunRecordStore.QueryPickFactsAsync(runId, routeId, (kind.Length > 0) ? kind : null, user, days, offset, limit).ConfigureAwait(continueOnCapturedContext: false);
			pickFactCounts = await RunRecordStore.QueryPickCountsAsync(runId, routeId, user, days).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			return new
			{
				ok = false,
				error = "查询失败：" + ex.Message
			};
		}
		List<object> list = new List<object>(rows.Count);
		foreach (PickEvent item in rows)
		{
			list.Add(new
			{
				runId = item.RunId,
				routeId = item.RouteId,
				kind = item.Kind,
				item = item.Item,
				ts = Iso(item.TsMs),
				tsMs = item.TsMs,
				lineNo = item.LineNo,
				srcFile = Path.GetFileName(item.SrcFile)
			});
		}
		return new
		{
			ok = true,
			user = user,
			days = days,
			offset = offset,
			limit = limit,
			kind = kind,
			count = list.Count,
			counts = new
			{
				picked = pickFactCounts.Picked,
				noTarget = pickFactCounts.NoTarget,
				skipped = pickFactCounts.Skipped
			},
			items = list
		};
	}

	private static async Task HandleMiyousheAsync(HttpListenerContext ctx, string path)
	{
		string account = (ctx.Request.QueryString["user"] ?? "").Trim();
		try
		{
			if (ctx.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
			{
				switch (path)
				{
				case "/api/miyoushe/login/status":
				{
					HttpListenerContext ctx2 = ctx;
					await WriteJson(ctx2, await MiyousheService.LoginStatusAsync(account, default));
					return;
				}
				// ★★ t50：原神角色列表 + 默认角色（GET）★★
				//   用户原话：「米游社那里显示一个17什么的账号，但是没有名字，分不清啊。
				//             而且如果有绑定多个原神账号，应该要可以切换，设为默认，
				//             不然要取哪个数据呢？」
				case "/api/miyoushe/roles":
					await HandleMiyousheRolesAsync(ctx, account).ConfigureAwait(continueOnCapturedContext: false);
					return;
				case "/api/miyoushe/archive":
				{
					int limit = (int.TryParse(ctx.Request.QueryString["limit"], out var result) ? Math.Clamp(result, 1, 50000) : 5000);
					MiyousheArchive miyousheArchive = await MiyousheStore.Shared.QueryAsync(account, limit).ConfigureAwait(continueOnCapturedContext: false);
					await WriteJson(ctx, new
					{
						ok = true,
						account = account,
						archive = miyousheArchive,
						entries = miyousheArchive.Entries.Count,
						source = "miyoushe.db",
						classification = "none; raw facts only"
					});
					return;
				}
				case "/api/miyoushe/catalog":
					await WriteJson(ctx, new
					{
						ok = true,
						missing = MiyousheCatalog.Validate(),
						regions = MiyousheCatalog.Regions.Values,
						games = MiyousheCatalog.Games.Values,
						community = MiyousheCatalog.Community.Values,
						forums = MiyousheCatalog.Forums
					});
					return;
				case "/api/miyoushe/sign/logs":
				{
					int limit2 = (int.TryParse(ctx.Request.QueryString["limit"], out var result4) ? result4 : 1000);
					HttpListenerContext ctx2 = ctx;
					string account2 = account;
					await WriteJson(ctx2, new
					{
						ok = true,
						account = account2,
						items = await MiyousheStore.Shared.QueryTasksAsync(account, limit2).ConfigureAwait(continueOnCapturedContext: false)
					});
					return;
				}
				case "/api/miyoushe/pull/status":
					await WriteJson(ctx, new
					{
						ok = true,
						items = MiyousheService.Statuses()
					});
					return;
				case "/api/miyoushe/mora/rows":
				{
					int days2 = (int.TryParse(ctx.Request.QueryString["days"], out var result5) ? Math.Clamp(result5, 1, 365) : 7);
					int rv = (int.TryParse(ctx.Request.QueryString["rule"], out var result6) ? result6 : 42);
					IReadOnlyList<MoraClassifier.MoraClassRow> readOnlyList2 = await MoraClassifier.QueryRowsAsync(account, days2, rv).ConfigureAwait(continueOnCapturedContext: false);
					await WriteJson(ctx, new
					{
						ok = true,
						account = account,
						rule_version = rv,
						items = readOnlyList2,
						count = readOnlyList2.Count,
						kill_count_semantics = "恒1（用户裁定，不按 details.count 乘算）"
					});
					return;
				}
				case "/api/miyoushe/mora/summary":
				{
					int days = (int.TryParse(ctx.Request.QueryString["days"], out var result2) ? Math.Clamp(result2, 1, 365) : 7);
					int rv = (int.TryParse(ctx.Request.QueryString["rule"], out var result3) ? result3 : 42);
					string groupName = ctx.Request.QueryString["group"];
					string routeName = ctx.Request.QueryString["route"];
					IReadOnlyList<MoraClassifier.MoraSummaryRow> readOnlyList = await MoraClassifier.QuerySummaryAsync(account, days, groupName, routeName, rv).ConfigureAwait(continueOnCapturedContext: false);
					await WriteJson(ctx, new
					{
						ok = true,
						account = account,
						rule_version = rv,
						items = readOnlyList,
						count = readOnlyList.Count,
						semantics = "四类均为记录条数(record_count)，非击杀数"
					});
					return;
				}
				case "/api/miyoushe/mora/versions":
					await WriteJson(ctx, new
					{
						ok = true,
						account = account,
						current = 42,
						versions = await MoraClassifier.QueryVersionsAsync(account).ConfigureAwait(continueOnCapturedContext: false)
					});
					return;
				case "/api/miyoushe/login/poll":
					await WriteJson(ctx, new
					{
						ok = true,
						result = await MiyousheService.PollQrAsync(ctx.Request.QueryString["session"] ?? "", default).ConfigureAwait(continueOnCapturedContext: false)
					});
					return;
				}
			}
			else
			{
				string json;
				using (StreamReader reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding, detectEncodingFromByteOrderMarks: false, 1024, leaveOpen: true))
				{
					char[] buffer = new char[8193];
					int num = await reader.ReadBlockAsync(buffer, 0, buffer.Length).ConfigureAwait(continueOnCapturedContext: false);
					if (num > 8192)
					{
						await WriteJson(ctx, new
						{
							ok = false,
							error = "请求过大"
						});
						return;
					}
					json = new string(buffer, 0, num);
				}
				MiyousheApiRequest miyousheApiRequest = JsonSerializer.Deserialize<MiyousheApiRequest>(json, new JsonSerializerOptions
				{
					PropertyNameCaseInsensitive = true
				});
				if (miyousheApiRequest == null)
				{
					await WriteJson(ctx, new
					{
						ok = false,
						error = "格式错误"
					});
					return;
				}
				account = miyousheApiRequest.account ?? account;
				switch (path)
				{
				// ★★ t50：设默认角色（POST body {gameUid:"..."}，空串=清除）★★
				case "/api/miyoushe/roles":
					await HandleMiyousheRolesAsync(ctx, account, miyousheApiRequest.gameUid).ConfigureAwait(continueOnCapturedContext: false);
					return;
				case "/api/miyoushe/login/begin":
				{
					HttpListenerContext ctx2 = ctx;
					await WriteJson(ctx2, new
					{
						ok = true,
						result = await MiyousheService.BeginQrAsync(account, miyousheApiRequest.region ?? "cn", default).ConfigureAwait(continueOnCapturedContext: false)
					});
					return;
				}
				case "/api/miyoushe/settings":
				{
					AppSettings appSettings2 = App.Config?.Settings;
					if (appSettings2 == null)
					{
						await WriteJson(ctx, new
						{
							ok = false,
							error = "配置未加载"
						});
						return;
					}
					if (miyousheApiRequest.enabled.HasValue)
					{
						appSettings2.MiyousheSignEnabled = miyousheApiRequest.enabled.Value;
					}
					if (miyousheApiRequest.hour.HasValue)
					{
						appSettings2.MiyousheSignHour = Math.Clamp(miyousheApiRequest.hour.Value, 0, 23);
					}
					if (miyousheApiRequest.salt != null)
					{
						appSettings2.MiyousheDsSalt = miyousheApiRequest.salt.Trim();
					}
					if (miyousheApiRequest.osSalt != null)
					{
						appSettings2.MiyousheOsDsSalt = miyousheApiRequest.osSalt.Trim();
					}
					if (miyousheApiRequest.region == "os" || miyousheApiRequest.region == "cn")
					{
						appSettings2.MiyousheAccountRegions[account] = miyousheApiRequest.region;
					}
					if (!string.IsNullOrWhiteSpace(miyousheApiRequest.task) && miyousheApiRequest.enabled.HasValue)
					{
						appSettings2.MiyousheTaskEnabled[miyousheApiRequest.task] = miyousheApiRequest.enabled.Value;
					}
					if (!string.IsNullOrWhiteSpace(miyousheApiRequest.task) && miyousheApiRequest.excluded.HasValue)
					{
						string item = account + "|" + miyousheApiRequest.task;
						if (miyousheApiRequest.excluded.Value)
						{
							appSettings2.MiyousheExcludedTasks.Add(item);
						}
						else
						{
							appSettings2.MiyousheExcludedTasks.Remove(item);
						}
					}
					if (miyousheApiRequest.forums != null)
					{
						appSettings2.MiyousheForumIds = miyousheApiRequest.forums.Where((string x) => MiyousheCatalog.Forums.ContainsKey(x)).Distinct().Take(5)
							.ToList();
					}
					if (miyousheApiRequest.delaySeconds.HasValue)
					{
						appSettings2.MiyousheCommunityDelaySeconds = Math.Clamp(miyousheApiRequest.delaySeconds.Value, 8, 120);
					}
					if (miyousheApiRequest.maxRetries.HasValue)
					{
						appSettings2.MiyousheCommunityMaxRetries = Math.Clamp(miyousheApiRequest.maxRetries.Value, 0, 3);
					}
					if (miyousheApiRequest.backoffSeconds.HasValue)
					{
						appSettings2.MiyousheCommunityBackoffBaseSeconds = Math.Clamp(miyousheApiRequest.backoffSeconds.Value, 5, 300);
					}
					if (!string.IsNullOrWhiteSpace(account) && miyousheApiRequest.enabled.HasValue)
					{
						appSettings2.MiyousheSignAccounts[account] = miyousheApiRequest.enabled.Value;
					}
					ConfigStore.Save(App.Config);
					await WriteJson(ctx, new
					{
						ok = true,
						enabled = appSettings2.MiyousheSignEnabled,
						hour = appSettings2.MiyousheSignHour,
						saltConfigured = (appSettings2.MiyousheDsSalt.Length > 0),
						osSaltConfigured = (appSettings2.MiyousheOsDsSalt.Length > 0)
					});
					return;
				}
				case "/api/miyoushe/import":
				{
					string account2 = miyousheApiRequest.path?.Trim() ?? "";
					if (account2.Length == 0)
					{
						account2 = App.Config?.Settings?.MiyousheImportPath ?? "";
					}
					if (miyousheApiRequest.probeOnly == true)
					{
						await WriteJson(ctx, MiyousheImporter.Probe(account, account2));
						return;
					}
					MiyousheImportOutcome miyousheImportOutcome = await MiyousheImporter.ImportAsync(account, account2, miyousheApiRequest.verify != false, default).ConfigureAwait(continueOnCapturedContext: false);
					if (miyousheImportOutcome.Ok && account2.Length > 0 && !account2.Equals("@bound", StringComparison.OrdinalIgnoreCase))
					{
						AppSettings appSettings = App.Config?.Settings;
						if (appSettings != null && appSettings.MiyousheImportPath != account2)
						{
							appSettings.MiyousheImportPath = account2;
							ConfigStore.Save(App.Config);
						}
					}
					if (miyousheImportOutcome.OkCount > 0)
					{
						LogBus.Ok("米游社凭据导入", "账号 " + account + " 导入 " + miyousheImportOutcome.OkCount + " 份（来源 " + miyousheImportOutcome.SourceKind + "，未记录凭据内容）");
					}
					await WriteJson(ctx, miyousheImportOutcome);
					return;
				}
				case "/api/miyoushe/sign/run":
				{
					HttpListenerContext ctx2 = ctx;
					await WriteJson(ctx2, new
					{
						ok = true,
						items = await MiyousheBatchService.RunAsync(account, scheduled: false, default).ConfigureAwait(continueOnCapturedContext: false)
					});
					return;
				}
				case "/api/miyoushe/run":
				{
					string action = miyousheApiRequest.action;
					bool flag = action == "sign" || action == "both";
					action = miyousheApiRequest.action;
					bool flag2 = action == "ledger" || action == "both";
					if (!flag && !flag2)
					{
						await WriteJson(ctx, new
						{
							ok = false,
							error = "action 须为 sign/ledger/both"
						});
						return;
					}
					HttpListenerContext ctx2 = ctx;
					await WriteJson(ctx2, await MiyousheService.RunForAccountAsync(account, flag, flag2, default).ConfigureAwait(continueOnCapturedContext: false));
					return;
				}
				case "/api/miyoushe/mora/classify":
				{
					if (account.Length == 0)
					{
						await WriteJson(ctx, new
						{
							ok = false,
							error = "缺少 user 参数"
						});
						return;
					}
					MoraClassifier.MoraClassRunResult moraClassRunResult = await MoraClassifier.ClassifyAsync(account).ConfigureAwait(continueOnCapturedContext: false);
					await WriteJson(ctx, new
					{
						ok = moraClassRunResult.Ok,
						account = account,
						rule_version = moraClassRunResult.RuleVersion,
						entries = moraClassRunResult.Entries,
						classified = moraClassRunResult.Classified,
						unknown = moraClassRunResult.Unknown,
						project_hits = moraClassRunResult.ProjectHits,
						route_hits = moraClassRunResult.RouteHits,
						unattributed = moraClassRunResult.Unattributed
					});
					return;
				}
				}
			}
			await WriteJson(ctx, new
			{
				ok = false,
				error = "未知米游社接口"
			});
		}
		catch (Exception ex) when (!(ex is OperationCanceledException))
		{
			await WriteJson(ctx, new
			{
				ok = false,
				error = ((ex is ArgumentException || ex is InvalidOperationException) ? ex.Message : "米游社请求失败，请稍后重试")
			});
		}
	}

	private static async Task<object> ApiBgiSpansAsync(HttpListenerContext ctx, string path)
	{
		string user = (ctx.Request.QueryString["user"] ?? "").Trim();
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		long start = (long.TryParse(ctx.Request.QueryString["start"], out var result) ? result : (num - 86400000));
		long end = (long.TryParse(ctx.Request.QueryString["end"], out var result2) ? result2 : num);
		if (end <= start)
		{
			end = start + 1;
		}
		if (path == "/api/bgi/spans")
		{
			return new
			{
				ok = true,
				account = user,
				startAtMs = start,
				endAtMs = end,
				items = (await RunRecordStore.QuerySpansRangeAsync(user, start, end).ConfigureAwait(continueOnCapturedContext: false)).Select((StateSpan x) => new { x.SpanId, x.Account, x.State, x.StartAtMs, x.EndAtMs, x.DurMs, x.DurMsSoFar, x.IsOngoing, x.SrcFile })
			};
		}
		if (path == "/api/bgi/spans/totals")
		{
			RunRecordStore.SpanFactTotals spanFactTotals = await RunRecordStore.QuerySpanTotalsAsync(user, start, end).ConfigureAwait(continueOnCapturedContext: false);
			return new
			{
				ok = true,
				account = user,
				startAtMs = start,
				endAtMs = end,
				IdleMs = spanFactTotals.IdleMs,
				NotRunningMs = spanFactTotals.NotRunningMs,
				view = "derived_facts_not_business_judgement"
			};
		}
		string day = ctx.Request.QueryString["day"] ?? DateTime.Now.ToString("yyyy-MM-dd");
		RunRecordStore.DaySpan daySpan = await RunRecordStore.QueryDaySpanAsync(user, day).ConfigureAwait(continueOnCapturedContext: false);
		return (daySpan == null) ? ((object)new
		{
			ok = true,
			account = user,
			day = day,
			hasRecord = false,
			message = "无记录"
		}) : ((object)new
		{
			ok = true,
			account = user,
			day = day,
			hasRecord = true,
			RunningMs = daySpan.RunningMs,
			IdleMs = daySpan.IdleMs,
			NotRunningMs = daySpan.NotRunningMs,
			UnknownMs = daySpan.UnknownMs,
			ComputedAtMs = daySpan.ComputedAtMs,
			IsFinal = daySpan.IsFinal
		});
	}

	private static string Iso(long ms)
	{
		try
		{
			return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
		}
		catch
		{
			return "";
		}
	}

	private static object ApiBgiIngestStatus()
	{
		IReadOnlyList<IngestCursorStatus> readOnlyList = IngestCursorStore.All();
		return new
		{
			ok = true,
			count = readOnlyList.Count,
			skippedRescan = new
			{
				accounts = BgiLogCenter.SkippedRescanCount,
				skippedBytes = BgiLogCenter.SkippedRescanTotalBytes
			},
			items = readOnlyList.Select((IngestCursorStatus s) => new
			{
				account = s.Account,
				srcFile = s.SrcFile,
				cursor = s.Cursor,
				fileLength = s.FileLength,
				pendingBytes = s.PendingBytes,
				updatedAtMs = s.UpdatedAtMs
			}).ToList()
		};
	}

	/// <summary>t52：手动全量重扫（清游标+重置内存状态）。默认不触发。</summary>
	private static object ApiBgiIngestRescan(HttpListenerContext ctx)
	{
		string text = (ctx.Request.QueryString["user"] ?? "").Trim();
		BgiLogCenter.ForceFullRescan(text.Length == 0 ? null : text);
		return new
		{
			ok = true,
			rescan = text.Length == 0 ? "all" : text
		};
	}

	private static object ApiBgiStats(HttpListenerContext ctx)
	{
		string text = (ctx.Request.QueryString["user"] ?? "").Trim();
		if (text.Length == 0)
		{
			return new
			{
				ok = false,
				error = "缺少 user"
			};
		}
		DailyStats dailyStats = DailyStatsCache.Get(text);
		int num = 0;
		foreach (int value in dailyStats.Picks.Values)
		{
			num += value;
		}
		return new
		{
			ok = true,
			user = text,
			day = dailyStats.Day,
			exp = dailyStats.DogfoodExp,
			mora = dailyStats.DogfoodMora,
			pickTotal = num,
			picks = new Dictionary<string, int>(dailyStats.Picks, StringComparer.OrdinalIgnoreCase),
			dogfoodRoutes = dailyStats.DogfoodRoutes,
			lastDogfoodRoute = dailyStats.LastDogfoodRoute,
			lastRoute = dailyStats.LastDogfoodRoute,
			groups = dailyStats.Groups.ToArray()
		};
	}

	private static object ApiList()
	{
		string replayRoot = ReplayRoot;
		List<object> list = new List<object>();
		try
		{
			if (Directory.Exists(replayRoot))
			{
				string[] directories = Directory.GetDirectories(replayRoot);
				foreach (string path in directories)
				{
					string[] directories2 = Directory.GetDirectories(path);
					foreach (string text in directories2)
					{
						string text2 = "";
						try
						{
							string path2 = Path.Combine(text, "meta.json");
							if (File.Exists(path2))
							{
								text2 = File.ReadAllText(path2);
							}
						}
						catch
						{
						}
						int num = 0;
						try
						{
							if (JsonSerializer.Deserialize<JsonElement>(text2).TryGetProperty("frames", out var value) && value.TryGetInt32(out var value2))
							{
								num = value2;
							}
						}
						catch
						{
						}
						if (num == 0)
						{
							try
							{
								num = Directory.GetFiles(text, "*.jpg").Length;
							}
							catch
							{
							}
						}
						bool hasVideo = File.Exists(Path.Combine(text, "replay.mp4"));
						list.Add(new
						{
							user = Path.GetFileName(path),
							name = Path.GetFileName(text),
							frames = num,
							hasVideo = hasVideo,
							path = Path.GetFileName(path) + "/" + Path.GetFileName(text),
							meta = text2
						});
					}
				}
			}
		}
		catch
		{
		}
		return new
		{
			ok = true,
			count = list.Count,
			items = list.OrderByDescending((object x) => x).ToList()
		};
	}

	private static object ApiStatus()
	{
		List<object> list = new List<object>();
		try
		{
			for (int i = 1; i <= 20; i++)
			{
				RelayClient relayClient = RelayClient.FindBySession(i);
				if (relayClient != null)
				{
					list.Add(new
					{
						sessionId = i,
						connected = relayClient.IsConnected,
						screen = relayClient.ScreenWidth + "x" + relayClient.ScreenHeight
					});
				}
			}
		}
		catch
		{
		}
		return new
		{
			ok = true,
			sessions = list
		};
	}

	private static async Task HandleInput(HttpListenerContext ctx)
	{
		try
		{
			string json;
			using (StreamReader sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
			{
				json = await sr.ReadToEndAsync().ConfigureAwait(continueOnCapturedContext: false);
			}
			InputRequest req = JsonSerializer.Deserialize<InputRequest>(json, new JsonSerializerOptions
			{
				PropertyNameCaseInsensitive = true
			});
			if (req == null)
			{
				await WriteJson(ctx, new
				{
					ok = false,
					error = "请求格式错误"
				});
				return;
			}
			int sid = 0;
			int.TryParse(ctx.Request.QueryString["sid"] ?? "0", out sid);
			if (sid <= 0)
			{
				await WriteJson(ctx, new
				{
					ok = false,
					error = "缺少 sid"
				});
				return;
			}
			RelayClient relay = RelayClient.FindBySession(sid);
			if (relay == null || !relay.IsConnected)
			{
				await WriteJson(ctx, new
				{
					ok = false,
					error = "会话 " + sid + " 未连接"
				});
				return;
			}
			int num = Math.Max(1, relay.ScreenWidth);
			int num2 = Math.Max(1, relay.ScreenHeight);
			int num3 = ((req.x >= 0.0) ? ((int)Math.Round(req.x * (double)num)) : (-1));
			int num4 = ((req.y >= 0.0) ? ((int)Math.Round(req.y * (double)num2)) : (-1));
			switch (req.action)
			{
			case "click":
			{
				int x = Math.Clamp(num3, 0, num - 1);
				int y = Math.Clamp(num4, 0, num2 - 1);
				await relay.ClickAtAsync(x, y, req.button);
				break;
			}
			case "down":
				if (num3 >= 0)
				{
					await relay.MoveAsync(num3, num4);
				}
				await relay.DownAsync(req.button);
				break;
			case "up":
				await relay.UpAsync(req.button);
				break;
			case "move":
				await relay.MoveAsync(Math.Clamp(num3, 0, num - 1), Math.Clamp(num4, 0, num2 - 1));
				break;
			case "wheel":
				await relay.WheelAsync(req.delta);
				break;
			case "key":
			{
				string text = (req.key ?? "").Trim();
				if (text.Length == 0)
				{
					await WriteJson(ctx, new
					{
						ok = false,
						error = "缺少按键"
					});
					return;
				}
				await relay.KeyAsync(text);
				break;
			}
			default:
				await WriteJson(ctx, new
				{
					ok = false,
					error = "未知动作 " + req.action
				});
				return;
			}
			await WriteJson(ctx, new
			{
				ok = true,
				sid = sid
			});
		}
		catch (Exception ex)
		{
			await WriteJson(ctx, new
			{
				ok = false,
				error = ex.Message
			});
		}
	}

	private static object ApiSessions()
	{
		List<object> list = new List<object>();
		try
		{
			AppConfig config = App.Config;
			List<string> list2 = config?.Settings?.RecordAccountIds ?? new List<string>();
			Dictionary<string, string> dictionary = config?.Settings?.BgiPaths ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (Account item in AccountScope.Accounts(config))
			{
				if (item == null || string.IsNullOrWhiteSpace(item.UserName) || !BgiPathResolver.IsPinned(item.UserName))
				{
					continue;
				}
				AccountState accountState = AccountStateCenter.Get(item.Id);
				int num = accountState?.SessionId ?? 0;
				RelayClient relayClient = ((num > 0) ? RelayClient.FindBySession(num) : null);
				bool flag = relayClient?.IsConnected ?? false;
				double bgiLogIdleMinutes = -1.0;
				try
				{
					if (accountState != null && accountState.BgiRunning && accountState.BgiLogWrite > DateTime.MinValue)
					{
						bgiLogIdleMinutes = Math.Round((DateTime.Now - accountState.BgiLogWrite).TotalMinutes, 1);
					}
				}
				catch
				{
				}
				string taskName = "";
				string configGroup = "";
				string routeFile = "";
				string round = "";
				string role = "";
				string eta = "";
				string action = "";
				int errorCount = 0;
				int pickTotal = 0;
				try
				{
					BgiLogSnapshot bgiLogSnapshot = BgiLogCenter.Snapshot(item.Id);
					if (bgiLogSnapshot != null && bgiLogSnapshot.Tail.Count > 0)
					{
						StringBuilder stringBuilder = new StringBuilder(bgiLogSnapshot.Tail.Count * 96);
						foreach (BgiLogLine item2 in bgiLogSnapshot.Tail)
						{
							stringBuilder.AppendLine(item2.Text);
						}
						BgiStatus bgiStatus = new BgiStatus();
						bgiStatus.Update(stringBuilder.ToString());
						taskName = bgiStatus.TaskName ?? "";
						configGroup = bgiStatus.ConfigGroup ?? "";
						routeFile = bgiStatus.RouteFile ?? "";
						round = bgiStatus.Round ?? "";
						role = bgiStatus.RoleName ?? "";
						eta = bgiStatus.RouteEta ?? "";
						action = bgiStatus.CurrentAction ?? "";
						pickTotal = bgiStatus.PickTotal;
					}
					if (accountState != null)
					{
						errorCount = accountState.ErrorCount;
					}
				}
				catch
				{
				}
				list.Add(new
				{
					id = item.Id,
					user = item.UserName,
					nick = (item.DisplayName ?? ""),
					label = (string.IsNullOrWhiteSpace(item.DisplayName) ? item.UserName : (item.UserName + "-" + item.DisplayName)),
					sessionId = num,
					bound = true,
					online = flag,
					bgiRunning = (accountState?.BgiRunning ?? false),
					bgiPid = (accountState?.BgiPid ?? 0),
					bgiLogIdleMinutes = bgiLogIdleMinutes,
					recording = list2.Contains(item.Id),
					bgiPath = (dictionary.TryGetValue(item.UserName, out var value) ? value : ""),
					screen = (flag ? (relayClient.ScreenWidth + "x" + relayClient.ScreenHeight) : ""),
					taskName = taskName,
					configGroup = configGroup,
					routeFile = routeFile,
					round = round,
					role = role,
					eta = eta,
					action = action,
					errorCount = errorCount,
					pickTotal = pickTotal,
					directPort = ResolveDirectPort(item.Id)
				});
			}
		}
		catch
		{
		}
		string version = "";
		try
		{
			Version version2 = typeof(WebServer).Assembly.GetName().Version;
			if (version2 != null)
			{
				version = version2.Major + "." + version2.Minor + "." + version2.Build;
			}
		}
		catch
		{
		}
		bool themeDark = true;
		try
		{
			themeDark = ThemeManager.IsDark;
		}
		catch
		{
		}
		int recordFps = 5;
		int recordQuality = 75;
		int recordKeepSeconds = 30;
		int streamFps = 30;
		int videoBitrate = 8000;
		int nvencPreset = 3;
		int vbvBufferIncreasePercent = 0;
		int captureMode = 0;
		try
		{
			AppSettings appSettings = App.Config?.Settings;
			if (appSettings != null)
			{
				recordFps = Math.Max(1, appSettings.RecordFps);
				recordQuality = Math.Clamp(appSettings.RecordQuality, 10, 100);
				recordKeepSeconds = Math.Clamp(appSettings.RecordKeepSeconds, 5, 600);
				streamFps = Math.Clamp(appSettings.StreamFps, 1, 120);
				videoBitrate = Math.Clamp(appSettings.VideoBitrateKbps, 1000, 500000);
				nvencPreset = Math.Clamp(appSettings.NvencPreset, 1, 7);
				vbvBufferIncreasePercent = Math.Clamp(appSettings.VbvBufferIncreasePercent, 0, 200);
				captureMode = Math.Clamp(appSettings.CaptureMode, 0, 3);
			}
		}
		catch
		{
		}
		string directToken = "";
		try
		{
			AppConfig config2 = App.Config;
			if (config2 != null && config2.Settings?.WebDirectToAgent == true)
			{
				directToken = App.Config.Settings.WebPassword ?? "";
			}
		}
		catch
		{
		}
		return new
		{
			ok = true,
			version = version,
			themeDark = themeDark,
			directToken = directToken,
			recordFps = recordFps,
			recordQuality = recordQuality,
			recordKeepSeconds = recordKeepSeconds,
			streamFps = streamFps,
			videoBitrate = videoBitrate,
			nvencPreset = nvencPreset,
			vbvBufferIncreasePercent = vbvBufferIncreasePercent,
			captureMode = captureMode,
			sessions = list
		};
	}

	private static async Task HandleSettings(HttpListenerContext ctx)
	{
		try
		{
			string json;
			using (StreamReader sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
			{
				json = await sr.ReadToEndAsync().ConfigureAwait(continueOnCapturedContext: false);
			}
			SettingsDto settingsDto = JsonSerializer.Deserialize<SettingsDto>(json, new JsonSerializerOptions
			{
				PropertyNameCaseInsensitive = true
			});
			if (settingsDto == null)
			{
				await WriteJson(ctx, new
				{
					ok = false,
					error = "格式错误"
				}).ConfigureAwait(continueOnCapturedContext: false);
				return;
			}
			AppSettings appSettings = App.Config?.Settings;
			if (appSettings == null)
			{
				await WriteJson(ctx, new
				{
					ok = false,
					error = "配置未加载"
				}).ConfigureAwait(continueOnCapturedContext: false);
				return;
			}
			if (settingsDto.fps > 0)
			{
				appSettings.RecordFps = Math.Clamp(settingsDto.fps, 1, 30);
			}
			if (settingsDto.quality > 0)
			{
				appSettings.RecordQuality = Math.Clamp(settingsDto.quality, 10, 100);
			}
			if (settingsDto.keepSeconds > 0)
			{
				appSettings.RecordKeepSeconds = Math.Clamp(settingsDto.keepSeconds, 5, 600);
			}
			if (settingsDto.streamFps > 0)
			{
				appSettings.StreamFps = Math.Clamp(settingsDto.streamFps, 1, 120);
			}
			if (settingsDto.videoBitrate > 0)
			{
				appSettings.VideoBitrateKbps = Math.Clamp(settingsDto.videoBitrate, 1000, 500000);
			}
			if (settingsDto.nvencPreset > 0)
			{
				appSettings.NvencPreset = Math.Clamp(settingsDto.nvencPreset, 1, 7);
			}
			if (settingsDto.vbvBufferIncreasePercent.HasValue)
			{
				appSettings.VbvBufferIncreasePercent = Math.Clamp(settingsDto.vbvBufferIncreasePercent.Value, 0, 200);
			}
			if (settingsDto.captureMode.HasValue)
			{
				appSettings.CaptureMode = Math.Clamp(settingsDto.captureMode.Value, 0, 3);
			}
			try
			{
				ConfigStore.Save(App.Config);
			}
			catch
			{
			}
			try
			{
				Application current = Application.Current;
				if (current != null)
				{
					((DispatcherObject)current).Dispatcher.BeginInvoke((Delegate)(Action)(() =>
					{
						App.NotifySettingsChanged();
					}), Array.Empty<object>());
				}
			}
			catch
			{
			}
			int num = 0;
			try
			{
				foreach (Account item in AccountScope.Accounts(App.Config))
				{
					if (item == null || !(appSettings.RecordAccountIds?.Contains(item.Id) ?? false))
					{
						continue;
					}
					int num2 = AccountStateCenter.Get(item.Id)?.SessionId ?? 0;
					if (num2 > 0)
					{
						RelayClient relayClient = RelayClient.FindBySession(num2);
						if (relayClient != null && relayClient.IsConnected)
						{
							relayClient.RecordStartAsync(appSettings.RecordFps, appSettings.RecordQuality, appSettings.RecordKeepSeconds);
							num++;
						}
					}
				}
			}
			catch
			{
			}
			RelayClient.PushVideoParametersToConnectedAgents();
			RelayClient.PushCaptureModeToConnectedAgents(appSettings.CaptureMode);
			await WriteJson(ctx, new
			{
				ok = true,
				fps = appSettings.RecordFps,
				quality = appSettings.RecordQuality,
				keepSeconds = appSettings.RecordKeepSeconds,
				streamFps = appSettings.StreamFps,
				videoBitrate = appSettings.VideoBitrateKbps,
				nvencPreset = appSettings.NvencPreset,
				vbvBufferIncreasePercent = appSettings.VbvBufferIncreasePercent,
				captureMode = appSettings.CaptureMode,
				agentsUpdated = num
			}).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			await WriteJson(ctx, new
			{
				ok = false,
				error = ex.Message
			}).ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	private static int ResolveDirectPort(string accountId)
	{
		try
		{
			AppSettings appSettings = App.Config?.Settings;
			if (appSettings == null || !appSettings.WebDirectToAgent || appSettings.WebPort <= 0)
			{
				return 0;
			}
			List<Account> list = App.Config?.Accounts;
			if (list == null || string.IsNullOrWhiteSpace(accountId))
			{
				return 0;
			}
			int num = list.FindIndex((Account a) => string.Equals(a.Id, accountId, StringComparison.OrdinalIgnoreCase));
			if (num < 0)
			{
				return 0;
			}
			return WebDirect.PortFor(appSettings.WebPort, num);
		}
		catch
		{
			return 0;
		}
	}

	private static async Task WriteLiveFrame(HttpListenerContext ctx)
	{
		_ = 5;
		try
		{
			if (!int.TryParse(ctx.Request.QueryString["sid"] ?? "", out var result) || result <= 0)
			{
				await WriteText(ctx, 400, "缺少 sid 参数");
				return;
			}
			string path = Path.GetDirectoryName(ReplayRoot) ?? "";
			string live = Path.Combine(path, "s" + result, "live", "latest.jpg");
			RelayClient relayClient = RelayClient.FindBySession(result);
			if (relayClient != null && relayClient.IsConnected)
			{
				DateTime before = (File.Exists(live) ? File.GetLastWriteTimeUtc(live) : DateTime.MinValue);
				relayClient.WriteLatestFrameAsync();
				int waited = 0;
				while (waited < 600)
				{
					await Task.Delay(10).ConfigureAwait(continueOnCapturedContext: false);
					waited += 10;
					if (File.Exists(live) && File.GetLastWriteTimeUtc(live) > before)
					{
						break;
					}
				}
			}
			else if (!File.Exists(live))
			{
				await WriteText(ctx, 404, "会话未连接");
				return;
			}
			if (!File.Exists(live))
			{
				await WriteText(ctx, 404, "还没有实时帧");
				return;
			}
			byte[] array;
			using (FileStream fs = new FileStream(live, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
			{
				using MemoryStream ms = new MemoryStream();
				await fs.CopyToAsync(ms).ConfigureAwait(continueOnCapturedContext: false);
				array = ms.ToArray();
			}
			ctx.Response.StatusCode = 200;
			ctx.Response.ContentType = "image/jpeg";
			ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
			ctx.Response.ContentLength64 = array.Length;
			await ctx.Response.OutputStream.WriteAsync(array).ConfigureAwait(continueOnCapturedContext: false);
			ctx.Response.Close();
		}
		catch
		{
			try
			{
				ctx.Response.Abort();
			}
			catch
			{
			}
		}
	}

	private static string LoginHtml()
	{
		return "<!DOCTYPE html>\r\n<html lang=\"zh-CN\">\r\n<head>\r\n<meta charset=\"utf-8\">\r\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\r\n<title>SoulLink 登录</title>\r\n<style>\r\n  *{box-sizing:border-box}\r\n  body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;\r\n       background:#080b16;color:#e6ecff;\r\n       font-family:-apple-system,BlinkMacSystemFont,\"Microsoft YaHei\",sans-serif}\r\n  .card{width:300px;padding:26px 22px;background:#111729;border:1px solid #232d4a;\r\n        border-radius:14px}\r\n  h1{font-size:16px;margin:0 0 4px;font-weight:600}\r\n  .sub{color:#6f7fa8;font-size:11.5px;margin-bottom:18px}\r\n  input{width:100%;height:40px;padding:0 12px;border-radius:9px;border:1px solid #232d4a;\r\n        background:#0b0f1e;color:#e6ecff;font-size:13px;outline:none}\r\n  input:focus{border-color:#4fe3ff}\r\n  button{width:100%;height:40px;margin-top:12px;border:none;border-radius:9px;\r\n         background:linear-gradient(135deg,#6c5ce7,#4fe3ff);color:#fff;\r\n         font-size:13px;font-weight:600;cursor:pointer}\r\n  button:disabled{opacity:.6}\r\n  #err{color:#ff8fa3;font-size:11.5px;margin-top:10px;min-height:16px;text-align:center}\r\n</style>\r\n</head>\r\n<body>\r\n<div class=\"card\">\r\n  <h1>SoulLink</h1>\r\n  <div class=\"sub\">请输入访问口令</div>\r\n  <input id=\"pw\" type=\"password\" placeholder=\"口令\" autocomplete=\"current-password\">\r\n  <button id=\"go\">登录</button>\r\n  <div id=\"err\"></div>\r\n</div>\r\n<script>\r\nconst $ = id => document.getElementById(id);\r\nasync function login() {\r\n  const pw = $('pw').value;\r\n  if (!pw) { $('err').textContent = '请输入口令'; return; }\r\n  $('go').disabled = true; $('err').textContent = '';\r\n  try {\r\n    const r = await fetch('/api/login?pw=' + encodeURIComponent(pw));\r\n    const d = await r.json();\r\n    if (d.ok) { location.href = '/'; }\r\n    else { $('err').textContent = d.error || '登录失败'; $('go').disabled = false; }\r\n  } catch (e) { $('err').textContent = '网络错误'; $('go').disabled = false; }\r\n}\r\n$('go').onclick = login;\r\n$('pw').addEventListener('keydown', e => { if (e.key === 'Enter') login(); });\r\n$('pw').focus();\r\n</script>\r\n</body>\r\n</html>";
	}
}
