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
		public string? user { get; set; }
		public bool? globalSignEnabled { get; set; }
		public bool? accountSignEnabled { get; set; }
		public string? signTime { get; set; }
		public List<string>? signProjects { get; set; }

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

		public int dx { get; set; }

		public int dy { get; set; }

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

	internal static string EnsureAuthToken()
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
			if (text is "/api/bgi/editor/status" or "/api/bgi/editor/start" or "/api/bgi/editor/stop")
			{
				if (IsAuthorized(ctx)) await HandleBgiEditor(ctx, text);
				else await WriteJson(ctx, new { ok = false, state = "unknown", error = "未登录" }, 401);
				return;
			}
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
				string indexHtml = LoadIndexHtml();
				await WriteHtml(ctx, indexHtml.Replace("__SOULLINK_PAGE_VERSION__", PageVersion(indexHtml), StringComparison.Ordinal));
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
			case "/api/bgi/runs/log":
				await WriteJson(ctx, await ApiBgiRunLogAsync(ctx));
				return;
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
					pageVer = PageVersion(LoadIndexHtml())
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
		return WebPages.Index;
	}

	private static string PageVersion(string html)
	{
		byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(html));
		return Convert.ToHexStringLower(hash.AsSpan(0, 6));
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

	private static async Task WriteJson(HttpListenerContext ctx, object data, int status = 200)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data));
		ctx.Response.StatusCode = status;
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
		ctx.Response.Headers["Cache-Control"] = "no-store";
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
		var records = RouteLedger.Query(text, days);
		return new
		{
			ok = true,
			user = text,
			days = days,
			records,
			groups = RouteLedger.GroupSummary(records),
			zeroReward = records.Where(r => r.ZeroReward).ToList(),
			failed = records.Where(r => r.IsFailure).ToList()
		};
	}

	private static bool TryLedgerDate(HttpListenerContext ctx, out DateTime? day)
	{
		day = null;
		string? value = ctx.Request.QueryString["date"];
		if (value == null) return true;
		if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
			|| parsed.Date == DateTime.MaxValue.Date) return false;
		day = parsed;
		return true;
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
		if (!TryLedgerDate(ctx, out var day)) return new { ok = false, error = "date 须为 yyyy-MM-dd" };
		IReadOnlyList<RunRecord> readOnlyList;
		try
		{
			readOnlyList = day.HasValue ? await RunRecordStore.QueryRunDayAsync(user, day.Value).ConfigureAwait(false)
				: await RunRecordStore.QueryRunsAsync(user, days).ConfigureAwait(false);
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
		if (!TryLedgerDate(ctx, out var day)) return new { ok = false, error = "date 须为 yyyy-MM-dd" };
		IReadOnlyList<PickEvent> rows;
		RunRecordStore.PickFactCounts pickFactCounts;
		try
		{
			rows = await RunRecordStore.QueryPickFactsAsync(runId, routeId, (kind.Length > 0) ? kind : null, user, days, offset, limit, day: day).ConfigureAwait(continueOnCapturedContext: false);
			pickFactCounts = await RunRecordStore.QueryPickCountsAsync(runId, routeId, user, days, day: day).ConfigureAwait(continueOnCapturedContext: false);
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
				case "/api/miyoushe/sign/settings":
					await HandleMiyousheSignSettingsAsync(ctx, account).ConfigureAwait(false);
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
					if (!TryLedgerDate(ctx, out var day)) { await WriteJson(ctx, new { ok = false, error = "date 须为 yyyy-MM-dd" }); return; }
					if (day.HasValue)
					{
						if (account.Length == 0) { await WriteJson(ctx, new { ok = false, error = "缺少 user" }); return; }
						var bins = await DailyMoraLedger.QueryAsync(account, day.Value).ConfigureAwait(false);
						var items = bins.Where(b => (ctx.Request.QueryString["group"] is not string g || b.GroupName == g)
							&& (ctx.Request.QueryString["route"] is not string r || b.RouteName == r)).ToList();
						await WriteJson(ctx, new { ok = true, account, source = "my_ledger_entry", items, count = items.Count });
						return;
					}
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
				account = (miyousheApiRequest.account ?? miyousheApiRequest.user ?? account).Trim();
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
						saltConfigured = (MiyousheService.EffectiveDsSalt(appSettings2, "cn").Length > 0),
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
				case "/api/miyoushe/sign/settings":
					await HandleMiyousheSignSettingsAsync(ctx, account, miyousheApiRequest).ConfigureAwait(false);
					return;
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
					await WriteJson(ctx2, await MiyousheService.RunForAccountAsync(account, flag, flag2, default, retryLogin: flag2 && !flag).ConfigureAwait(continueOnCapturedContext: false));
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
		DailyStats dailyStats = BgiLogCenter.DailyStatsForWeb(text);
		return new
		{
			ok = true,
			user = text,
			day = dailyStats.Day,
			exp = dailyStats.DogfoodExp,
			mora = dailyStats.DogfoodMora,
			pickTotal = dailyStats.PickTotal,
			picks = dailyStats.Picks,
			dogfoodRoutes = dailyStats.DogfoodRoutes,
			lastDogfoodRoute = dailyStats.LastDogfoodRoute,
			lastRoute = dailyStats.LastDogfoodRoute,
			groups = dailyStats.Groups.ToArray()
		};
	}

	private static object ApiList()
	{
		string replayRoot = ReplayRoot;
		var list = new List<(string Name, object Item)>();
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
						int num = 0, fps = 10;
						string trigger = "";
						if (!string.IsNullOrWhiteSpace(text2))
						{
							try
							{
								using var meta = JsonDocument.Parse(text2);
								var root = meta.RootElement;
								if (root.TryGetProperty("frames", out var frames) && frames.ValueKind == JsonValueKind.Number)
									frames.TryGetInt32(out num);
								if (root.TryGetProperty("fps", out var rate) && rate.ValueKind == JsonValueKind.Number
									&& rate.TryGetInt32(out var parsedFps) && parsedFps > 0)
									fps = parsedFps;
								if (root.TryGetProperty("trigger", out var kind) && kind.ValueKind == JsonValueKind.String)
									trigger = kind.GetString() ?? "";
							}
							catch (JsonException) { }
							catch (InvalidOperationException) { }
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
						string name = Path.GetFileName(text);
						list.Add((name, new
						{
							user = Path.GetFileName(path),
							name,
							frames = num,
							hasVideo = hasVideo,
							path = Path.GetFileName(path) + "/" + Path.GetFileName(text),
							meta = text2,
							trigger,
							fps
						}));
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
			items = list.OrderByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(x => x.Item).ToList()
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
			if (!relay.IsRemoteControlEnabled)
			{
				await WriteText(ctx, 403, "该账号的网页远控已关闭");
				return;
			}
			int num = Math.Max(1, relay.ScreenWidth);
			int num2 = Math.Max(1, relay.ScreenHeight);
			int num3 = ((req.x >= 0.0) ? ((int)Math.Round(req.x * (double)num)) : (-1));
			int num4 = ((req.y >= 0.0) ? ((int)Math.Round(req.y * (double)num2)) : (-1));
			RelayClient.RelayResponse? inputResult;
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
			case "up":
			case "keydown":
			case "keyup":
			case "moverel":
			case "releaseall":
			case "noop":
				bool mouseButton = req.action is "down" or "up";
				inputResult = await relay.SendAsync(new
				{
					Cmd = req.action, WebInput = true, Button = mouseButton ? req.button : (req.key ?? "").Trim(),
					Move = mouseButton && num3 >= 0 && num4 >= 0 ? 1 : 0,
					X = Math.Clamp(num3, 0, num - 1), Y = Math.Clamp(num4, 0, num2 - 1),
					Dx = req.dx, Dy = req.dy
				});
				await WriteJson(ctx, new { ok = inputResult?.Ok == true, error = inputResult?.Ok == true ? null : inputResult?.Message ?? "代理未响应" });
				return;
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
		bool anyRemoteControlEnabled = false;
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
				bool remoteControlEnabled = config?.Settings?.IsRemoteControlEnabledFor(item.Id) == true;
				anyRemoteControlEnabled |= remoteControlEnabled;
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
				var progress = accountState?.BgiRunning == true
					? BgiLogCenter.SessionProgressFor(item.Id)
					: null;
				if (progress is { ProcessId: > 0 } && accountState?.BgiPid is > 0
					&& progress.ProcessId != accountState.BgiPid)
					progress = null;
				var activity = progress?.Activity.Filter(DateTime.Now);
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
					remoteControlEnabled,
					bgiPath = (dictionary.TryGetValue(item.UserName, out var value) ? value : ""),
					screen = (flag ? (relayClient.ScreenWidth + "x" + relayClient.ScreenHeight) : ""),
					taskName = progress?.Task ?? "",
					configGroup = progress?.Group ?? "",
					routeFile = progress?.Route ?? "",
					round = progress?.Round ?? "",
					role = progress?.RunRole ?? "",
					eta = progress?.Eta ?? "",
					action = activity?.Action?.Text ?? "",
					actionAt = activity?.Action?.At.ToString("HH:mm:ss") ?? "",
					activeCharacter = activity?.Character?.Text ?? "",
					activeCharacterAt = activity?.Character?.At.ToString("HH:mm:ss") ?? "",
					recentSkill = activity?.Skill?.Text ?? "",
					recentSkillOutcome = activity?.Skill?.Outcome ?? "",
					recentSkillAt = activity?.Skill?.At.ToString("HH:mm:ss") ?? "",
					recentFood = activity?.Food?.Text ?? "",
					recentFoodOutcome = activity?.Food?.Outcome ?? "",
					recentFoodAt = activity?.Food?.At.ToString("HH:mm:ss") ?? "",
					errorCount = accountState?.ErrorCount ?? 0,
					pickTotal = progress?.PickTotal ?? 0,
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
			remoteControlEnabled = anyRemoteControlEnabled,
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
			if (appSettings == null || !appSettings.IsRemoteControlEnabledFor(accountId) || !appSettings.WebDirectToAgent || appSettings.WebPort <= 0)
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
		try
		{
			if (!int.TryParse(ctx.Request.QueryString["sid"] ?? "", out var result) || result <= 0)
			{
				await WriteText(ctx, 400, "缺少 sid 参数");
				return;
			}
			RelayClient relayClient = RelayClient.FindBySession(result);
			if (relayClient == null || !relayClient.IsConnected)
			{
				await WriteText(ctx, 404, "会话未连接");
				return;
			}
			if (!relayClient.IsRemoteControlEnabled)
			{
				await WriteText(ctx, 403, "该账号的网页远控已关闭");
				return;
			}
			string path = Path.GetDirectoryName(ReplayRoot) ?? "";
			string live = Path.Combine(path, "s" + result, "live", "latest.jpg");
			DateTime before = (File.Exists(live) ? File.GetLastWriteTimeUtc(live) : DateTime.MinValue);
			_ = relayClient.WriteLatestFrameAsync();
			int waited = 0;
			bool hasFreshFrame = false;
			while (waited < 600)
			{
				await Task.Delay(10).ConfigureAwait(continueOnCapturedContext: false);
				waited += 10;
				if (File.Exists(live) && File.GetLastWriteTimeUtc(live) > before)
				{
					hasFreshFrame = true;
					break;
				}
			}
			if (!hasFreshFrame)
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
			if (!relayClient.IsRemoteControlEnabled)
			{
				await WriteText(ctx, 403, "该账号的网页远控已关闭");
				return;
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
		return "<!DOCTYPE html>\r\n<html lang=\"zh-CN\">\r\n<head>\r\n<meta charset=\"utf-8\">\r\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\r\n<title>SoulLink 登录</title>\r\n" + WebFavicon.Link + "\r\n<style>\r\n  *{box-sizing:border-box}\r\n  body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;\r\n       background:#080b16;color:#e6ecff;\r\n       font-family:-apple-system,BlinkMacSystemFont,\"Microsoft YaHei\",sans-serif}\r\n  .card{width:300px;padding:26px 22px;background:#111729;border:1px solid #232d4a;\r\n        border-radius:14px}\r\n  h1{font-size:16px;margin:0 0 4px;font-weight:600}\r\n  .sub{color:#6f7fa8;font-size:11.5px;margin-bottom:18px}\r\n  input{width:100%;height:40px;padding:0 12px;border-radius:9px;border:1px solid #232d4a;\r\n        background:#0b0f1e;color:#e6ecff;font-size:13px;outline:none}\r\n  input:focus{border-color:#4fe3ff}\r\n  button{width:100%;height:40px;margin-top:12px;border:none;border-radius:9px;\r\n         background:linear-gradient(135deg,#6c5ce7,#4fe3ff);color:#fff;\r\n         font-size:13px;font-weight:600;cursor:pointer}\r\n  button:disabled{opacity:.6}\r\n  #err{color:#ff8fa3;font-size:11.5px;margin-top:10px;min-height:16px;text-align:center}\r\n</style>\r\n</head>\r\n<body>\r\n<div class=\"card\">\r\n  <h1>SoulLink</h1>\r\n  <div class=\"sub\">请输入访问口令</div>\r\n  <input id=\"pw\" type=\"password\" placeholder=\"口令\" autocomplete=\"current-password\">\r\n  <button id=\"go\">登录</button>\r\n  <div id=\"err\"></div>\r\n</div>\r\n<script>\r\nconst $ = id => document.getElementById(id);\r\nasync function login() {\r\n  const pw = $('pw').value;\r\n  if (!pw) { $('err').textContent = '请输入口令'; return; }\r\n  $('go').disabled = true; $('err').textContent = '';\r\n  try {\r\n    const r = await fetch('/api/login?pw=' + encodeURIComponent(pw));\r\n    const d = await r.json();\r\n    if (d.ok) { location.href = '/'; }\r\n    else { $('err').textContent = d.error || '登录失败'; $('go').disabled = false; }\r\n  } catch (e) { $('err').textContent = '网络错误'; $('go').disabled = false; }\r\n}\r\n$('go').onclick = login;\r\n$('pw').addEventListener('keydown', e => { if (e.key === 'Enter') login(); });\r\n$('pw').focus();\r\n</script>\r\n</body>\r\n</html>";
	}
}
