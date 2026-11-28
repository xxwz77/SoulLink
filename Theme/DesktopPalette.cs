using System;
using System.Linq;
using System.Windows.Media;

namespace SoulLink.Theme;

internal sealed record DesktopPalette(string Id, string Name, bool Dark, Color Base, Color Tint,
    Color Surface, Color Text, Color Secondary, Color Accent, Color Motif, Color Highlight,
    double Radius, Color MaterialStart, Color MaterialEnd, byte MaterialStartAlpha, byte MaterialEndAlpha,
    bool IsPlain)
{
    internal bool IsPortrait => Id.StartsWith('P');
    internal string Label => $"{Id}  {Name}";

    internal static readonly DesktopPalette[] Light =
    {
        Create("L1", "草莓兔邮局", false, 0xfaeae9, 0x492b37, 0x714b58, 0x993752, 0xd891a0, 20, 0xfff8f3, 0xf4ced7, 230, 208),
        Create("L2", "布丁海豹", false, 0xfff0d5, 0x403125, 0x705540, 0x85512a, 0xd5a061, 23, 0xfffbea, 0xedc28a, 236, 222),
        Create("L3", "霜蓝芯片", false, 0xe6eff4, 0x203444, 0x446072, 0x215b77, 0x86a9bf, 6, 0xf2f7fa, 0xdbe6ef, 240, 225),
        Create("L4", "薄荷掌机", false, 0xe5f2e6, 0x233c30, 0x47624f, 0x366741, 0x88b799, 13, 0xf6f9e9, 0xdcebd3, 238, 227),
        Create("L5", "泡泡实验室", false, 0xe5f0f5, 0x2c334a, 0x535a76, 0x51528a, 0xb5a9ce, 20, 0xf4f8ff, 0xdfedf3, 237, 218),
        Create("L6", "云端风洞", false, 0xe6eefb, 0x26354c, 0x4d607b, 0x355984, 0x91a8d4, 9, 0xf2f7ff, 0xd4e3f4, 239, 225),
        Create("L7", "樱吹雪", false, 0xf7e9e5, 0x4a2936, 0x75515c, 0x963d5d, 0xd889a3, 18, 0xfff9f1, 0xe8b6c7, 239, 207),
        Create("L8", "奶酪星球", false, 0xfff1d8, 0x3b322a, 0x695849, 0x845023, 0xd8b565, 18, 0xfff5dc, 0xeee2bf, 238, 219),
        Plain("L9", "雪白", false, 0xf7f8fa, 0x222831, 0x536070, 0x45617f, 0xafbdca, 9),
        Plain("L10", "燕麦", false, 0xf4efe5, 0x322a22, 0x655747, 0x78583c, 0xc6ad8e, 9),
        Plain("L11", "雾青", false, 0xeaf3f0, 0x283e39, 0x4f6860, 0x416d60, 0xa1bdb4, 9),
    };

    internal static readonly DesktopPalette[] DarkThemes =
    {
        Create("D1", "紫绡月弧", true, 0x24192e, 0xf7eaf7, 0xcdb2ce, 0xe5b9e5, 0xaa84b8, 22, 0x3f2b4d, 0x4a355a, 234, 200),
        Create("D2", "晚安团熊", true, 0x28212d, 0xf6e9eb, 0xcdbaCA, 0xebc6a3, 0xac8c9a, 23, 0x554455, 0x322c35, 228, 235),
        Create("D3", "光子回廊", true, 0x0d202e, 0xe6f4fc, 0xb0c6d6, 0x83deed, 0x598eaa, 8, 0x10283c, 0x155368, 237, 185),
        Create("D4", "星云摇篮", true, 0x16172d, 0xf0eafb, 0xc4bada, 0xe4b1e9, 0x9477bc, 18, 0x2a2543, 0x493450, 233, 206),
        Create("D5", "量子示波", true, 0x1b2725, 0xedf5dd, 0xbccdb8, 0xd1e990, 0x9ab781, 6, 0x23332b, 0x35453a, 237, 213),
        Create("D6", "霓彩磁流", true, 0x211a2c, 0xf6eafc, 0xc9b9d3, 0xf1b3db, 0xa582ba, 13, 0x33283d, 0x4b3549, 233, 214),
        Create("D7", "火星漫游车", true, 0x291e20, 0xf7eddc, 0xcbb9ad, 0xefb47c, 0xb48670, 9, 0x4c3732, 0x392a2a, 232, 228),
        Create("D8", "萤灯竹径", true, 0x182820, 0xeef5df, 0xbacab2, 0xe5d293, 0x87a586, 17, 0x253b2b, 0x303f2b, 232, 206),
        Plain("D9", "炭黑", true, 0x1c1e22, 0xf0f2f6, 0xb5bfca, 0xaec3d9, 0x596575, 9),
        Plain("D10", "午夜蓝", true, 0x182332, 0xeef4fc, 0xb5c7db, 0x9ecaf0, 0x567697, 9),
        Plain("D11", "深松绿", true, 0x192924, 0xedf6ef, 0xb5cdbd, 0x9bd5b5, 0x5b8774, 9),
    };

    internal static readonly DesktopPalette[] Portraits =
    {
        Create("P1", "星羽来信", true, 0x24233b, 0xf7eef9, 0xd2c4de, 0xe7c1ed, 0xb499c9, 20, 0x302740, 0x41354e, 234, 202),
        Create("P2", "莓糖月牙", false, 0xf9e5e9, 0x492d3a, 0x755462, 0x90395a, 0xe6a0b4, 22, 0xfff6f4, 0xf3cbd6, 238, 222),
        Create("P3", "光绫回声", true, 0x1b304b, 0xeef6ff, 0xc0d4ed, 0xabddec, 0x859bc8, 12, 0x203650, 0x2e4c68, 231, 202),
        Create("P4", "鎏金花序", true, 0x28242b, 0xfcf0e2, 0xd2c3bd, 0xf3c799, 0xb7957c, 14, 0x34292e, 0x41312d, 235, 209),
        Create("P5", "羽音唱片", false, 0xede3e8, 0x3e3142, 0x675569, 0x7f4d72, 0xc6a4be, 9, 0xf8f1f5, 0xe8dbe4, 239, 214),
    };

    private static readonly DesktopPalette[] All = Light.Concat(DarkThemes).Concat(Portraits).ToArray();

    internal static DesktopPalette? Find(string? id)
    {
        string? current = id?.ToLowerInvariant() switch
        {
            "rose" => "L7", "porcelain" => "L3", "apricot" => "L2",
            "graphite" => "D3", "prism" => "D1", "amber" => "D7",
            _ => id
        };
        return All.FirstOrDefault(choice => string.Equals(choice.Id, current, StringComparison.OrdinalIgnoreCase));
    }

    internal static DesktopPalette Resolve(bool dark, string? id)
    {
        DesktopPalette? selected = Find(id);
        return selected?.Dark == dark ? selected : (dark ? DarkThemes[0] : Light[0]);
    }

    internal static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    internal static Color Mix(Color first, Color second, double amount)
    {
        byte Channel(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        return Color.FromRgb(Channel(first.R, second.R), Channel(first.G, second.G), Channel(first.B, second.B));
    }

    private static Color Rgb(int color) => Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color);

    private static DesktopPalette Create(string id, string name, bool dark, int basis, int text, int secondary,
        int accent, int motif, double radius, int coat, int trim, byte coatAlpha, byte trimAlpha)
    {
        Color bg = Rgb(basis);
        Color tint = Mix(bg, Rgb(motif), dark ? .074 : .097);
        Color surface = Mix(bg, Colors.White, dark ? .072 : .46);
        return new(id, name, dark, bg, tint, surface, Rgb(text), Rgb(secondary), Rgb(accent), Rgb(motif),
            Mix(Rgb(motif), Colors.White, .64), radius, Rgb(coat), Rgb(trim), coatAlpha, trimAlpha, false);
    }

    private static DesktopPalette Plain(string id, string name, bool dark, int basis, int text, int secondary,
        int accent, int motif, double radius)
    {
        Color bg = Rgb(basis);
        Color surface = Mix(bg, Colors.White, dark ? .066 : .76);
        Color chrome = Mix(bg, dark ? Colors.Black : Rgb(motif), dark ? .07 : .068);
        return new(id, name, dark, bg, chrome, surface, Rgb(text), Rgb(secondary), Rgb(accent), Rgb(motif),
            Mix(Rgb(motif), Colors.White, .64), radius, surface, surface, 255, 255, true);
    }
}
