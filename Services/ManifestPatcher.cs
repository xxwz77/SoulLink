using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SoulLink.Services
{
    /// <summary>
    /// 不依赖 Windows SDK：直接改 PE 内嵌清单里的 requestedExecutionLevel。
    /// ★ 为什么需要它：主程序必须 requireAdministrator 才能投送，
    ///   但 SYSTEM 计划任务启动带该清单的 exe 会返回 ERROR_ELEVATION_REQUIRED(740)。
    ///   引导副本只把清单里的等级改成 asInvoker，其余字节完全不动。
    /// </summary>
    public static class ManifestPatcher
    {
        /// <summary>返回是否已成功改成 asInvoker。失败时调用方应放弃该副本。</summary>
        public static bool MakeAsInvoker(string exePath)
        {
            try
            {
                byte[] data = File.ReadAllBytes(exePath);
                int resDir = FindResourceDirectory(data);
                if (resDir < 0) return false;
                int manifestData = FindManifestEntry(data, resDir);
                if (manifestData < 0) return false;

                // ★ 清单里含中文注释：必须按字节定位，不能用字符串索引（UTF-8 下字节偏移更大）。
                // ★ 属性值必须干净：曾经把填充空格写进引号内（level="asInvoker   "），
                //   Windows 解析 XML 失败，直接返回 ERROR_SXS_CANT_GEN_ACTCTX(14001)，
                //   进程根本起不来。填充只能放在引号之外，且不能破坏标签语法。
                byte[] needle = Encoding.ASCII.GetBytes("level=\"requireAdministrator\"");
                byte[] replacement = Encoding.ASCII.GetBytes("level=\"asInvoker\"           ");
                if (needle.Length != replacement.Length) return false;

                int at = -1;
                for (int i = manifestData; i + needle.Length <= data.Length; i++)
                {
                    bool hit = true;
                    for (int j = 0; j < needle.Length; j++)
                        if (data[i + j] != needle[j]) { hit = false; break; }
                    if (hit) { at = i; break; }
                }
                if (at < 0) return false;

                Array.Copy(replacement, 0, data, at, replacement.Length);
                File.WriteAllBytes(exePath, data);
                return true;
            }
            catch { return false; }
        }

        private static int FindResourceDirectory(byte[] d)
        {
            if (d.Length < 0x40 || d[0] != (byte)'M' || d[1] != (byte)'Z') return -1;
            int pe = BitConverter.ToInt32(d, 0x3C);
            if (pe <= 0 || pe + 24 >= d.Length) return -1;
            if (BitConverter.ToUInt32(d, pe) != 0x00004550) return -1; // "PE"
            int optSize = BitConverter.ToUInt16(d, pe + 20);
            int opt = pe + 24;
            int magic = BitConverter.ToUInt16(d, opt);
            int dirOff = magic == 0x20B ? opt + 112 : opt + 96; // PE32+ / PE32
            int resRva = BitConverter.ToInt32(d, dirOff + 16); // 资源目录是第 2 项
            if (resRva <= 0) return -1;
            return RvaToOffset(d, pe, optSize, resRva);
        }

        private static int FindManifestEntry(byte[] d, int resDir)
        {
            // 资源目录：shar[0] = 类型，shar[1] = 名称，第 24 类型即 RT_MANIFEST
            int count = BitConverter.ToUInt16(d, resDir + 12) + BitConverter.ToUInt16(d, resDir + 14);
            for (int i = 0; i < count; i++)
            {
                int e = resDir + 16 + i * 8;
                if (e + 8 > d.Length) return -1;
                uint name = BitConverter.ToUInt32(d, e);
                uint off = BitConverter.ToUInt32(d, e + 4);
                if ((name & 0x80000000) != 0 || name != 24) continue;
                int nameDir = resDir + (int)(off & 0x7FFFFFFF);
                if (nameDir + 16 > d.Length) continue;
                int n2 = BitConverter.ToUInt16(d, nameDir + 12) + BitConverter.ToUInt16(d, nameDir + 14);
                for (int j = 0; j < n2; j++)
                {
                    int e2 = nameDir + 16 + j * 8;
                    uint off2 = BitConverter.ToUInt32(d, e2 + 4);
                    if ((off2 & 0x80000000) == 0) continue; // 必须是子目录
                    int langDir = resDir + (int)(off2 & 0x7FFFFFFF);
                    if (langDir + 16 > d.Length) continue;
                    int n3 = BitConverter.ToUInt16(d, langDir + 12) + BitConverter.ToUInt16(d, langDir + 14);
                    for (int k = 0; k < n3; k++)
                    {
                        int e3 = langDir + 16 + k * 8;
                        uint dataOff = BitConverter.ToUInt32(d, e3 + 4);
                        if ((dataOff & 0x80000000) != 0) continue; // 必须指向数据项，不是子目录
                        int pe2 = BitConverter.ToInt32(d, 0x3C);
                        int optSize2 = BitConverter.ToUInt16(d, pe2 + 20);
                        // ★ 语言项偏移在高位未置位时仍相对资源目录起点；
                        //   少数链接器写绝对偏移，两种都试，取真正落在清单上的那个。
                        foreach (int dataEntry in new[] { resDir + (int)(dataOff & 0x7FFFFFFF), (int)(dataOff & 0x7FFFFFFF) })
                        {
                            if (dataEntry <= 0 || dataEntry + 8 > d.Length) continue;
                            int rva = BitConverter.ToInt32(d, dataEntry);
                            if (rva <= 0) continue;
                            int size = BitConverter.ToInt32(d, dataEntry + 4);
                            int mfOff = RvaToOffset(d, pe2, optSize2, rva);
                            if (mfOff <= 0 || size <= 0 || mfOff + size > d.Length) continue;
                            string probe = Encoding.UTF8.GetString(d, mfOff, Math.Min(size, 512));
                            if (probe.Contains("assembly") || probe.Contains("requestedExecutionLevel")) return mfOff;
                        }
                    }
                }
            }
            return -1;
        }

        private static int RvaToOffset(byte[] d, int pe, int optSize, int rva)
        {
            int opt = pe + 24;
            int sections = BitConverter.ToUInt16(d, pe + 6);
            int sectionStart = opt + optSize;
            for (int i = 0; i < sections; i++)
            {
                int s = sectionStart + i * 40;
                uint va = BitConverter.ToUInt32(d, s + 12);
                uint size = BitConverter.ToUInt32(d, s + 16);
                uint raw = BitConverter.ToUInt32(d, s + 20);
                if ((uint)rva >= va && (uint)rva < va + Math.Max(size, 1u)) return (int)(raw + (uint)(rva - va));
            }
            return -1;
        }
    }
}
