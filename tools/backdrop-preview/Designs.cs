using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using static SoulLink.BackdropPreview.Pal;

namespace SoulLink.BackdropPreview
{
    // ════════════════════════════════════════════════════════════════
    //  方案 A ── 童话星轨
    //  深靛紫夜空 + 柔性极光绸带 + 呼吸星海 + 上浮爱心 + 流星
    // ════════════════════════════════════════════════════════════════
    internal sealed class DesignA : BackdropBase
    {
        public override string Name => "A";

        private struct Star { public double X, Y, R, Phase, Rate; public int Col; }
        private struct HeartC { public double X0, Speed, PhaseY, Size, Rot0, RotSpd; public int Col; }
        private struct Orb { public double X, Y, R, Phase, Spd, Amp; public int Col; public double A; }

        private readonly Star[] _stars = new Star[220];
        private readonly HeartC[] _hearts = new HeartC[8];
        private readonly Orb[] _orbs = new Orb[6];

        public DesignA()
        {
            for (int i = 0; i < _stars.Length; i++)
                _stars[i] = new Star
                {
                    X = Fx.Hash(i, 11), Y = Fx.Hash(i, 23),
                    R = 0.5 + Math.Pow(Fx.Hash(i, 37), 2.6) * 1.9,
                    Phase = Fx.Hash(i, 41) * Math.PI * 2,
                    Rate = 0.5 + Fx.Hash(i, 53) * 2.0,
                    Col = (int)(Fx.Hash(i, 67) * 4)
                };
            for (int i = 0; i < _hearts.Length; i++)
                _hearts[i] = new HeartC
                {
                    X0 = 0.07 + Fx.Hash(i, 71) * 0.86,
                    Speed = 0.014 + Fx.Hash(i, 83) * 0.020,
                    PhaseY = Fx.Hash(i, 103),
                    Size = 5.0 + Fx.Hash(i, 101) * 8.0,
                    Rot0 = (Fx.Hash(i, 107) - 0.5) * 40,
                    RotSpd = (Fx.Hash(i, 109) - 0.5) * 14,
                    Col = (int)(Fx.Hash(i, 113) * 3)
                };
            for (int i = 0; i < _orbs.Length; i++)
                _orbs[i] = new Orb
                {
                    X = 0.08 + Fx.Hash(i, 127) * 0.84,
                    Y = 0.12 + Fx.Hash(i, 131) * 0.76,
                    R = 0.10 + Fx.Hash(i, 137) * 0.16,
                    Phase = Fx.Hash(i, 139) * Math.PI * 2,
                    Spd = 0.05 + Fx.Hash(i, 149) * 0.10,
                    Amp = 0.02 + Fx.Hash(i, 151) * 0.04,
                    Col = (int)(Fx.Hash(i, 157) * 4),
                    A = 0.09 + Fx.Hash(i, 163) * 0.11
                };
        }

        private static readonly Color[] Tones = { Lilac, Pink, Ice, Mint };

        public override void Render(Ctx c)
        {
            double w = c.W, h = c.H, t = c.T;
            var dc = c.Dc;
            double minR = Math.Min(w, h);

            Base(c, Fx.C("#08051C"), Fx.C("#160C3A"), Fx.C("#1E0A31"), 0.50);

            // ── 月晕（右上），给画面一个光源 ──
            double mx = w * 0.80, my = h * 0.13;
            Fx.Blob(dc, mx, my, minR * 0.62, Violet, 0.130, 0.30);
            Fx.Blob(dc, mx, my, minR * 0.26, Lilac, 0.190, 0.26);
            Fx.Blob(dc, mx, my, minR * 0.095, Fx.C("#FFF2FC"), 0.400, 0.20);
            Fx.Blob(dc, mx, my, minR * 0.032, White, 0.560, 0.18);

            // ── 柔性极光绸带（三条，错相慢移）──
            var r1 = Fx.BuildRibbon(w, h * 0.265, h * 0.078, 0.95, t * 0.14, h * 0.180);
            Fx.SoftRibbon(dc, r1.mid, r1.halfW, Violet, Lilac, Pink, 0.72);

            var r2 = Fx.BuildRibbon(w, h * 0.475, h * 0.095, 0.72, t * 0.115 + 2.3, h * 0.235);
            Fx.SoftRibbon(dc, r2.mid, r2.halfW, Ice, Cyan, Violet, 0.58);

            var r3 = Fx.BuildRibbon(w, h * 0.700, h * 0.072, 1.28, t * 0.175 + 4.6, h * 0.165);
            Fx.SoftRibbon(dc, r3.mid, r3.halfW, Pink, HotPink, Violet, 0.46);

            // 绸带亮芯划线
            var core1 = new List<Point>(r1.mid);
            Fx.GlowPolyline(dc, core1, Fx.Lerp(Lilac, White, 0.55), 1.3, 0.30);
            var core2 = new List<Point>(r2.mid);
            Fx.GlowPolyline(dc, core2, Fx.Lerp(Ice, White, 0.55), 1.2, 0.24);

            // ── 星海 ──
            foreach (var s in _stars)
            {
                double px = s.X * w, py = s.Y * h;
                double tw = 0.5 + 0.5 * Math.Sin(t * s.Rate + s.Phase);
                double a = 0.20 + 0.72 * tw;
                var col = Tones[s.Col];
                if (s.R > 1.5) Fx.Blob(dc, px, py, s.R * 6.0, col, a * 0.26, 0.28);
                Fx.Dot(dc, px, py, s.R, Fx.Lerp(col, White, 0.55), a);
                if (s.R > 1.75) Fx.Star(dc, px, py, s.R * 4.0, s.Phase * 57, Fx.Lerp(col, White, 0.30), a * 0.62);
            }

            // ── 定点闪耀 ──
            for (int i = 0; i < 7; i++)
            {
                double x = (0.08 + Fx.Hash(i, 211) * 0.84) * w;
                double y = (0.06 + Fx.Hash(i, 223) * 0.66) * h;
                double p = Math.Pow(0.5 + 0.5 * Math.Sin(t * (0.85 + Fx.Hash(i, 227) * 0.9) + Fx.Hash(i, 229) * 6.28), 2.4);
                Fx.Sparkle(dc, x, y, 3.0 + 6.4 * p, t * 20, i % 2 == 0 ? Lilac : Pink, 0.18 + 0.82 * p);
            }

            // ── 流星（9s 周期，划 1.7s）──
            {
                double period = 9.0, life = 1.7;
                double ph = (t + 2.4) % period;
                if (ph < life)
                {
                    double u = ph / life;
                    double p = Math.Sin(u * Math.PI);
                    double sx = w * (0.10 + 0.66 * u), sy = h * (0.08 + 0.36 * u);
                    var tail = new List<Point>();
                    for (int i = 0; i <= 10; i++)
                    {
                        double q = i / 10.0;
                        tail.Add(new Point(sx - 190 * q, sy - 68 * q));
                    }
                    Fx.GlowPolyline(dc, tail, Fx.Lerp(Ice, White, 0.45), 2.0, p * 0.95);
                    Fx.Blob(dc, sx, sy, 20, White, p * 0.60, 0.22);
                    Fx.Sparkle(dc, sx, sy, 9 * p, t * 30, Ice, p * 0.9);
                }
            }

            // ── 爱心上浮 ──
            foreach (var hp in _hearts)
            {
                double prog = (t * hp.Speed + hp.PhaseY) % 1.0;
                double y = h * (1.06 - prog * 1.18);
                double x = w * hp.X0 + Math.Sin(t * 0.52 + hp.PhaseY * 6.28) * w * 0.032;
                double fade = Math.Sin(prog * Math.PI);
                double a = 0.16 + 0.56 * fade;
                var col = hp.Col == 0 ? Pink : hp.Col == 1 ? HotPink : Lilac;
                Fx.Blob(dc, x, y, hp.Size * 3.2, col, a * 0.30, 0.24);
                double sz = hp.Size * (0.45 + 0.55 * fade);
                Fx.Heart(dc, x, y, sz, hp.Rot0 + Math.Sin(t * 0.75 + hp.PhaseY * 6.28) * hp.RotSpd
                        + 10 * Math.Sin(t * 0.33 + hp.PhaseY * 4.0), Fx.Lerp(col, White, 0.25), a);
            }

            // ── 散景光球 ──
            foreach (var o in _orbs)
            {
                double x = (o.X + Math.Sin(t * o.Spd + o.Phase) * o.Amp) * w;
                double y = (o.Y + Math.Cos(t * o.Spd * 0.83 + o.Phase) * o.Amp * 0.8) * h;
                Fx.Blob(dc, x, y, o.R * minR * 0.62, Tones[o.Col],
                        o.A * (0.72 + 0.28 * Math.Sin(t * 0.65 + o.Phase)));
            }

            Fx.Vignette(dc, w, h, 0.25);
            Fx.Grain(dc, w, h, t, 0.060);
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  方案 B ── 星云之瞳
    //  极深底色 + 旋涡星云 + 中心能量核呼吸 + 轨道光弧 + 星尘
    // ════════════════════════════════════════════════════════════════
    internal sealed class DesignB : BackdropBase
    {
        public override string Name => "B";

        private struct Cloud { public double Ang, Rad, R, Phase, Spd; public int Col; public double A; }
        private struct Dust { public double Ang, Rad, R, Spd, Phase; public int Col; }
        private struct Orbiter { public double Rx, Ry, Tilt, Phase, Spd, Arc; public int Col; }

        private readonly Cloud[] _clouds = new Cloud[12];
        private readonly Dust[] _dust = new Dust[190];
        private readonly Orbiter[] _orbiters = new Orbiter[3];

        public DesignB()
        {
            for (int i = 0; i < _clouds.Length; i++)
                _clouds[i] = new Cloud
                {
                    Ang = Fx.Hash(i, 311) * Math.PI * 2,
                    Rad = 0.05 + Fx.Hash(i, 313) * 0.34,
                    R = 0.14 + Fx.Hash(i, 317) * 0.24,
                    Phase = Fx.Hash(i, 331) * Math.PI * 2,
                    Spd = 0.045 + Fx.Hash(i, 337) * 0.085,
                    Col = i % 4,
                    A = 0.13 + Fx.Hash(i, 347) * 0.20
                };
            for (int i = 0; i < _dust.Length; i++)
                _dust[i] = new Dust
                {
                    Ang = Fx.Hash(i, 401) * Math.PI * 2,
                    Rad = Math.Sqrt(Fx.Hash(i, 409)),
                    R = 0.45 + Fx.Hash(i, 419) * 1.7,
                    Spd = 0.04 + Fx.Hash(i, 421) * 0.15,
                    Phase = Fx.Hash(i, 431) * Math.PI * 2,
                    Col = i % 4
                };
            for (int i = 0; i < _orbiters.Length; i++)
                _orbiters[i] = new Orbiter
                {
                    Rx = 0.28 + i * 0.13,
                    Ry = 0.15 + i * 0.075,
                    Tilt = -14 + i * 13,
                    Phase = i * 2.1,
                    Spd = 0.19 - i * 0.045,
                    Arc = 0.60 + i * 0.24,
                    Col = i % 3
                };
        }

        private static readonly Color[] Tones = { HotPink, Ice, Violet, Pink };

        public override void Render(Ctx c)
        {
            double w = c.W, h = c.H, t = c.T;
            var dc = c.Dc;
            double minR = Math.Min(w, h);
            double cx = w * 0.50, cy = h * 0.46;

            Base(c, Fx.C("#05040E"), Fx.C("#0E0724"), Fx.C("#0A0518"), 0.55);

            // ── 旋涡星云（绕中心缓慢公转）──
            foreach (var cl in _clouds)
            {
                double ang = cl.Ang + t * cl.Spd;
                double rad = cl.Rad * minR * (1 + 0.08 * Math.Sin(t * 0.29 + cl.Phase));
                double x = cx + Math.Cos(ang) * rad;
                double y = cy + Math.Sin(ang) * rad * 0.70;
                double pulse = 0.78 + 0.22 * Math.Sin(t * 0.50 + cl.Phase);
                Fx.Blob(dc, x, y, cl.R * minR * 0.95, Tones[cl.Col], cl.A * pulse, 0.34);
            }

            // ── 中心能量核（呼吸）──
            double beat = 0.5 + 0.5 * Math.Sin(t * 0.80);
            double coreR = minR * (0.080 + 0.022 * beat);
            Fx.Blob(dc, cx, cy, coreR * 8.5, Violet, 0.115 + 0.055 * beat, 0.36);
            Fx.Blob(dc, cx, cy, coreR * 4.0, Pink, 0.150 + 0.080 * beat, 0.30);
            Fx.Blob(dc, cx, cy, coreR * 1.60, Fx.C("#FFE4FA"), 0.330 + 0.170 * beat, 0.24);
            Fx.Blob(dc, cx, cy, coreR * 0.60, White, 0.680 + 0.240 * beat, 0.26);

            // ── 中心星芒（收敛成细长十字，不做大块几何）──
            double flare = 0.5 + 0.5 * beat;
            Fx.Star(dc, cx, cy, minR * 0.155 * flare, 0, Fx.C("#D6BEFF"), 0.085 * flare);
            Fx.Star(dc, cx, cy, minR * 0.062 * flare, 0, Fx.C("#FFEAFB"), 0.13 * flare);
            Fx.Star(dc, cx, cy, minR * 0.075 * flare, 45, Fx.C("#E9DCFF"), 0.07 * flare);

            // ── 核外光环 ──
            dc.PushTransform(new RotateTransform(-8, cx, cy));
            for (int k = 0; k < 2; k++)
            {
                double rr = coreR * (2.4 + k * 1.05) * (1 + 0.045 * Math.Sin(t * 0.85 + k * 1.7));
                var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(64 - k * 20), 218, 188, 255)), 0.9 + k * 0.4);
                pen.Freeze();
                dc.DrawEllipse(null, pen, new Point(cx, cy), rr, rr * 0.60);
            }
            dc.Pop();

            // ── 轨道光弧 ──
            for (int i = 0; i < _orbiters.Length; i++)
            {
                var o = _orbiters[i];
                dc.PushTransform(new RotateTransform(o.Tilt, cx, cy));
                double rx = o.Rx * minR, ry = o.Ry * minR;
                var faint = new Pen(new SolidColorBrush(Color.FromArgb(22, 208, 186, 255)), 0.75);
                faint.Freeze();
                dc.DrawEllipse(null, faint, new Point(cx, cy), rx, ry);

                double a0 = t * o.Spd + o.Phase;
                var pts = new List<Point>();
                for (int s = 0; s <= 26; s++)
                {
                    double u = s / 26.0;
                    double a = a0 + u * o.Arc;
                    pts.Add(new Point(cx + Math.Cos(a) * rx, cy + Math.Sin(a) * ry));
                }
                Fx.GlowPolyline(dc, pts, Tones[o.Col], 1.9, 0.85);
                var head = pts[pts.Count - 1];
                Fx.Blob(dc, head.X, head.Y, 13, Tones[o.Col], 0.72, 0.22);
                Fx.Sparkle(dc, head.X, head.Y, 5, -t * 24, Fx.Lerp(Tones[o.Col], White, 0.5), 0.85);
                dc.Pop();
            }

            // ── 星尘（由中心向外扩散）──
            foreach (var d in _dust)
            {
                double rad = ((d.Rad + t * d.Spd * 0.13) % 1.0) * Math.Sqrt(w * w + h * h) * 0.56;
                double ang = d.Ang + rad * 0.0017 + Math.Sin(t * 0.38 + d.Phase) * 0.05;
                double x = cx + Math.Cos(ang) * rad * 1.06;
                double y = cy + Math.Sin(ang) * rad * 0.70;
                if (x < -20 || x > w + 20 || y < -20 || y > h + 20) continue;
                double tw = 0.35 + 0.65 * (0.5 + 0.5 * Math.Sin(t * (1.0 + d.Rad) + d.Phase));
                double edge = 1.0 - Math.Abs(rad / (Math.Sqrt(w * w + h * h) * 0.56));
                Fx.Dot(dc, x, y, d.R, Fx.Lerp(Tones[d.Col], White, 0.50), 0.10 + 0.58 * tw * Math.Max(0, edge));
            }

            // ── 闪耀 ──
            for (int i = 0; i < 8; i++)
            {
                double x = (0.05 + Fx.Hash(i, 503) * 0.90) * w;
                double y = (0.05 + Fx.Hash(i, 509) * 0.74) * h;
                double p = Math.Pow(0.5 + 0.5 * Math.Sin(t * (0.65 + Fx.Hash(i, 521)) + Fx.Hash(i, 523) * 6.28), 2.6);
                Fx.Sparkle(dc, x, y, 2.4 + 5.6 * p, -t * 14 + i * 11, i % 2 == 0 ? Ice : Pink, 0.16 + 0.84 * p);
            }

            Fx.Vignette(dc, w, h, 0.55);
            Fx.Grain(dc, w, h, t, 0.055);
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  方案 C ── 樱色脉冲
    //  紫粉夜空 + 底部樱花绽放 + 斜向光带 + 落樱花瓣 + 地平线网格
    // ════════════════════════════════════════════════════════════════
    internal sealed class DesignC : BackdropBase
    {
        public override string Name => "C";

        private struct PetalC { public double X0, Speed, Phase, Size, Sway, Rot0, RotSpd, Par; public int Col; }
        private struct Streak { public double Y, Len, Speed, Phase, Thick, A; public int Col; }

        private readonly PetalC[] _petals = new PetalC[26];
        private readonly Streak[] _streaks = new Streak[10];

        public DesignC()
        {
            for (int i = 0; i < _petals.Length; i++)
                _petals[i] = new PetalC
                {
                    X0 = Fx.Hash(i, 601),
                    Speed = 0.022 + Fx.Hash(i, 607) * 0.034,
                    Phase = Fx.Hash(i, 613) * Math.PI * 2,
                    Size = 7.0 + Fx.Hash(i, 617) * 10.0,
                    Sway = 0.020 + Fx.Hash(i, 619) * 0.052,
                    Rot0 = Fx.Hash(i, 631) * 360,
                    RotSpd = 14 + Fx.Hash(i, 641) * 46,
                    Par = 0.55 + Fx.Hash(i, 643) * 0.55,
                    Col = i % 3
                };
            for (int i = 0; i < _streaks.Length; i++)
                _streaks[i] = new Streak
                {
                    Y = 0.04 + Fx.Hash(i, 901) * 0.86,
                    Len = 0.10 + Fx.Hash(i, 907) * 0.24,
                    Speed = 0.022 + Fx.Hash(i, 911) * 0.048,
                    Phase = Fx.Hash(i, 919),
                    Thick = 0.8 + Fx.Hash(i, 923) * 1.5,
                    A = 0.20 + Fx.Hash(i, 929) * 0.42,
                    Col = i % 3
                };
        }

        public override void Render(Ctx c)
        {
            double w = c.W, h = c.H, t = c.T;
            var dc = c.Dc;
            double minR = Math.Min(w, h);

            Base(c, Fx.C("#0B0518"), Fx.C("#1D0C2E"), Fx.C("#120722"), 0.46);

            // ── 上部斜向柔光带（科技感，压得很淡，不挡内容）──
            dc.PushTransform(new RotateTransform(-13, w * 0.5, h * 0.5));
            var s1 = Fx.BuildRibbon(w * 1.8, h * 0.05, h * 0.085, 0.45, t * 0.075, h * 0.30);
            Fx.SoftRibbon(dc, s1.mid, s1.halfW, Ice, Lilac, Pink, 0.34);
            var s2 = Fx.BuildRibbon(w * 1.8, h * 0.94, h * 0.075, 0.60, t * 0.095 + 3.1, h * 0.34);
            Fx.SoftRibbon(dc, s2.mid, s2.halfW, Violet, HotPink, Pink, 0.30);
            dc.Pop();

            // ── 底部樱花绽放（主光源）──
            double bloomPulse = 0.82 + 0.18 * Math.Sin(t * 0.44);
            Fx.Blob(dc, w * 0.50, h * 1.00, minR * 0.95, HotPink, 0.150 * bloomPulse, 0.28);
            Fx.Blob(dc, w * 0.32, h * 1.02, minR * 0.66, Violet, 0.120 * bloomPulse, 0.28);
            Fx.Blob(dc, w * 0.70, h * 1.02, minR * 0.62, Lilac, 0.105 * bloomPulse, 0.28);
            Fx.Blob(dc, w * 0.50, h * 0.93, minR * 0.30, Fx.C("#FFD8EC"), 0.110 * bloomPulse, 0.24);

            // ── 右上冷光，与暖粉对冲 ──
            Fx.Blob(dc, w * 0.90, h * 0.08, minR * 0.52, Ice, 0.085, 0.32);
            Fx.Blob(dc, w * 0.12, h * 0.06, minR * 0.44, Violet, 0.075, 0.32);

            // ── 扩散涟漪 ──
            double rx0 = w * 0.50, ry0 = h * 0.94;
            for (int i = 0; i < 5; i++)
            {
                double u = (t * 0.10 + i / 5.0) % 1.0;
                double rad = u * minR * 1.30;
                double a = Math.Pow(Math.Sin(u * Math.PI), 1.3) * 0.28;
                var col = i % 2 == 0 ? Pink : Ice;
                var hard = new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * a), col.R, col.G, col.B)), 0.9);
                hard.Freeze();
                dc.DrawEllipse(null, hard, new Point(rx0, ry0), rad, rad * 0.60);
                var soft = new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * a * 0.26), col.R, col.G, col.B)), 3.6);
                soft.Freeze();
                dc.DrawEllipse(null, soft, new Point(rx0, ry0), rad, rad * 0.60);
            }

            // ── 细长光轨 ──
            foreach (var s in _streaks)
            {
                double span = 1.0 + s.Len;
                double u = ((t * s.Speed + s.Phase) % 1.0) * span - s.Len;
                double x0 = u * w, x1 = (u + s.Len) * w;
                double y = s.Y * h + Math.Sin(t * 0.30 + s.Phase * 6.28) * h * 0.012;
                var col = s.Col == 0 ? Ice : s.Col == 1 ? Pink : Lilac;
                var bright = Fx.Lerp(col, White, 0.6);
                var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
                b.GradientStops.Add(new GradientStop(Color.FromArgb(0, col.R, col.G, col.B), 0));
                b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * s.A), col.R, col.G, col.B), 0.55));
                b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * s.A * 0.75), bright.R, bright.G, bright.B), 0.93));
                b.GradientStops.Add(new GradientStop(Color.FromArgb(0, col.R, col.G, col.B), 1));
                b.Freeze();
                var pen = new Pen(b, s.Thick) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                pen.Freeze();
                dc.DrawLine(pen, new Point(x0, y), new Point(x1, y));
                Fx.Blob(dc, x1, y, s.Thick * 7.5, col, s.A * 0.55, 0.22);
            }

            // ── 星点 ──
            for (int i = 0; i < 120; i++)
            {
                double x = Fx.Hash(i, 701) * w;
                double y = Fx.Hash(i, 709) * h * 0.90;
                double tw = 0.5 + 0.5 * Math.Sin(t * (0.75 + Fx.Hash(i, 719) * 2.0) + Fx.Hash(i, 727) * 6.28);
                double r = 0.40 + Fx.Hash(i, 733) * 1.45;
                var col = i % 3 == 0 ? Pink : i % 3 == 1 ? Ice : Lilac;
                Fx.Dot(dc, x, y, r, Fx.Lerp(col, White, 0.40), 0.08 + 0.58 * tw);
            }

            // ── 落樱（主角）──
            foreach (var pt in _petals)
            {
                double prog = (t * pt.Speed + pt.Phase / 6.28) % 1.0;
                double y = -h * 0.14 + prog * h * 1.34;
                double x = w * pt.X0 + Math.Sin(t * 0.62 * pt.Par + pt.Phase) * w * pt.Sway
                           + Math.Sin(t * 1.8 + pt.Phase * 2) * w * 0.006;
                double fade = Math.Pow(Math.Sin(prog * Math.PI), 0.5);
                double a = 0.28 + 0.66 * fade;
                var innerCol = pt.Col == 0 ? Fx.C("#FFF4FA") : pt.Col == 1 ? Fx.C("#FFDAEC") : Fx.C("#F2E6FF");
                var outerCol = pt.Col == 0 ? Pink : pt.Col == 1 ? HotPink : Lilac;
                double rot = pt.Rot0 + t * pt.RotSpd;
                double sz = pt.Size * (0.72 + 0.28 * fade);
                Fx.Blob(dc, x, y, sz * 2.6, outerCol, a * 0.18, 0.26);
                Fx.Petal(dc, x, y, sz, rot, innerCol, outerCol, a,
                         0.55 + 0.45 * Math.Abs(Math.Cos(t * pt.Par * 1.1 + pt.Phase)));
            }

            // ── 底部地平线网格 ──
            double horizon = h * 0.86;
            var gridBrush = new LinearGradientBrush(Color.FromArgb(0, 176, 214, 255), Color.FromArgb(48, 200, 172, 255), 90);
            gridBrush.Freeze();
            var gridPen = new Pen(gridBrush, 0.75);
            gridPen.Freeze();
            for (int i = 0; i <= 8; i++)
            {
                double p = i / 8.0;
                double y = horizon + Math.Pow(p, 1.9) * (h - horizon) + Math.Sin(t * 0.85 + p * 5) * 0.8;
                if (y > h) continue;
                dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
            }
            for (int i = -15; i <= 15; i++)
                dc.DrawLine(gridPen, new Point(w * 0.5 + i * w * 0.019, horizon),
                                    new Point(w * 0.5 + i * w * 0.076, h));

            // ── 闪光 ──
            for (int i = 0; i < 6; i++)
            {
                double x = (0.10 + Fx.Hash(i, 801) * 0.80) * w;
                double y = (0.06 + Fx.Hash(i, 809) * 0.56) * h;
                double p = Math.Pow(0.5 + 0.5 * Math.Sin(t * (1.0 + Fx.Hash(i, 811)) + Fx.Hash(i, 821) * 6.28), 2.2);
                Fx.Sparkle(dc, x, y, 2.8 + 5.6 * p, t * 17, i % 2 == 0 ? Pink : Lilac, 0.16 + 0.84 * p);
            }

            Fx.Vignette(dc, w, h, 0.40);
            Fx.Grain(dc, w, h, t, 0.055);
        }
    }
}
