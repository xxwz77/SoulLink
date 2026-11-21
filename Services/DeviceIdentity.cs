using System;
using System.IO;
using System.Text.Json;

namespace SoulLink.Services
{
    /// <summary>
    /// 设备身份：本机唯一标识（设备码）。
    ///
    /// ★ 用户要求：不需要注册登录，用软件生成的设备码识别用户。
    ///   设备码首次运行时随机生成，存到本地，之后一直不变。
    /// </summary>
    public static class DeviceIdentity
    {
        private static string? _cached;

        private static string FilePath
        {
            get
            {
                // ★ 2.21.1：跟随数据目录。device.json 是设备码，
                //   和数据一起搬走才对——换了目录但设备码变了的体验很怪。
                string dir = AppIdentity.DataDir;
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "device.json");
            }
        }

        /// <summary>本机设备码。格式：DEV-XXXXXXXXXXXX（12 位十六进制）</summary>
        public static string Code
        {
            get
            {
                if (_cached != null) return _cached;

                try
                {
                    if (File.Exists(FilePath))
                    {
                        var d = JsonSerializer.Deserialize<DeviceFile>(File.ReadAllText(FilePath));
                        if (d != null && !string.IsNullOrWhiteSpace(d.Code) && d.Code.Length >= 8)
                        {
                            _cached = d.Code;
                            return _cached;
                        }
                    }
                }
                catch { }

                // 首次运行：生成新设备码
                string code = "DEV-" + Guid.NewGuid().ToString("N").Substring(0, 12).ToUpperInvariant();

                try
                {
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(new DeviceFile
                    {
                        Code = code,
                        CreatedAt = DateTime.Now
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch { }

                _cached = code;
                LogBus.Info("已生成设备码：" + code);
                return _cached;
            }
        }

        /// <summary>本机的显示名（默认用 Windows 用户名）。</summary>
        public static string DisplayName
        {
            get
            {
                try
                {
                    if (File.Exists(FilePath))
                    {
                        var d = JsonSerializer.Deserialize<DeviceFile>(File.ReadAllText(FilePath));
                        if (d != null && !string.IsNullOrWhiteSpace(d.Name)) return d.Name;
                    }
                }
                catch { }
                return Environment.UserName;
            }
        }

        /// <summary>修改显示名。</summary>
        public static void SetDisplayName(string name)
        {
            try
            {
                var d = new DeviceFile { Code = Code, Name = name ?? "", CreatedAt = DateTime.Now };
                if (File.Exists(FilePath))
                {
                    try
                    {
                        var old = JsonSerializer.Deserialize<DeviceFile>(File.ReadAllText(FilePath));
                        if (old != null && old.CreatedAt > DateTime.MinValue) d.CreatedAt = old.CreatedAt;
                    }
                    catch { }
                }
                File.WriteAllText(FilePath, JsonSerializer.Serialize(d,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private sealed class DeviceFile
        {
            public string Code { get; set; } = "";
            public string Name { get; set; } = "";
            public DateTime CreatedAt { get; set; }
        }
    }
}
