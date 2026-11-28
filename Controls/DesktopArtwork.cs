using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SoulLink.Models;
using SoulLink.Services;
using SoulLink.Theme;

namespace SoulLink.Controls;

public sealed class DesktopArtwork : FrameworkElement
{
    private Window? _window;
    private IDisposable? _renderClock;
    private int _framesPerSecond;
    private DesktopPalette? _palette;
    private DesktopThemeArt.Clip? _clip;
    private BitmapSource? _sceneStill;
    private int _stillWidth;
    private SceneFrameKey? _requested;
    private SceneFrame? _presented;
    private bool _decoding;
    private bool _sceneFailed;
    private BitmapSource? _portrait;
    private DesktopPortraitArt? _portraitScene;
    private double _seconds;
    private bool _pageActive = true;

    public DesktopArtwork()
    {
        IsHitTestVisible = false;
        Focusable = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => SyncAnimation();
    }

    public string Region { get; set; } = "Header";
    public string? ThemeId { get; set; }

    public bool PageActive
    {
        get => _pageActive;
        set
        {
            if (_pageActive == value) return;
            _pageActive = value;
            if (!value) DropArtwork();
            Refresh();
        }
    }

    private DesktopPalette Palette => DesktopPalette.Find(ThemeId) ?? ThemeManager.Palette;

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (Region == "Base")
        {
            ThemeManager.AppearanceChanged += Refresh;
            SystemParameters.StaticPropertyChanged += SystemEffectChanged;
            _window = Window.GetWindow(this);
            if (_window != null)
            {
                _window.Activated += WindowActivityChanged;
                _window.Deactivated += WindowActivityChanged;
                _window.StateChanged += WindowActivityChanged;
            }
        }
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        ThemeManager.AppearanceChanged -= Refresh;
        SystemParameters.StaticPropertyChanged -= SystemEffectChanged;
        if (_window != null)
        {
            _window.Activated -= WindowActivityChanged;
            _window.Deactivated -= WindowActivityChanged;
            _window.StateChanged -= WindowActivityChanged;
        }
        _window = null;
        SyncAnimation();
        DropArtwork();
    }

    private void WindowActivityChanged(object? sender, EventArgs args) => SyncAnimation();

    private void SystemEffectChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) SyncAnimation();
    }

    private void Refresh()
    {
        DesktopPalette palette = Palette;
        if (_palette != palette)
        {
            DropArtwork();
            _palette = palette;
            _seconds = 0;
        }
        InvalidateVisual();
        SyncAnimation();
    }

    private void DropArtwork()
    {
        _clip = null;
        _requested = null;
        _presented = null;
        _sceneFailed = false;
        _sceneStill = null;
        _stillWidth = 0;
        _portrait = null;
        _portraitScene = null;
    }

    private void SyncAnimation()
    {
        int frames = 0;
        if (Region == "Base" && PageActive && IsLoaded && IsVisible && !Palette.IsPlain && !_sceneFailed &&
            _window?.IsActive == true && _window.WindowState != WindowState.Minimized &&
            !ThemeManager.AnimationsDisabled && SystemParameters.ClientAreaAnimation)
        {
            frames = (App.Config?.Settings.EffectTier, Palette.Id) switch
            {
                (AppSettings.EffectTierLevel.High, _) => 20,
                (_, "P3") => 15,
                _ => 8,
            };
        }
        if (frames == _framesPerSecond) return;
        _renderClock?.Dispose();
        _framesPerSecond = frames;
        InvalidateVisual();
        _renderClock = frames == 0 ? null : TickHub.RegisterRenderFrames(frames, elapsed =>
        {
            _seconds += Math.Clamp(elapsed.TotalSeconds, 0, .19);
            InvalidateVisual();
        });
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (Region is not ("Base" or "Preview") || RenderSize.Width < 1 || RenderSize.Height < 1) return;
        var bounds = new Rect(RenderSize);
        DesktopPalette palette = Palette;
        if (palette.Id == DesktopCustomArtwork.Id)
        {
            DesktopCustomArtwork.DrawCurrent(drawing, bounds, palette, Region == "Preview" || PageActive);
            return;
        }
        drawing.PushClip(new RectangleGeometry(bounds));
        drawing.DrawRectangle(DesktopPortraitArt.Ink(palette.Base), null, bounds);

        if (Region == "Preview")
        {
            BitmapSource? preview = DesktopThemeArt.Preview(palette);
            if (preview != null)
                drawing.DrawImage(preview, Cover(bounds, (double)preview.PixelWidth / preview.PixelHeight,
                    palette.IsPortrait ? .5 : .64));
        }
        else if (PageActive && !palette.IsPlain)
        {
            if (palette.IsPortrait) DrawPortrait(drawing, palette, bounds);
            else DrawClip(drawing, palette, bounds);
        }
        drawing.Pop();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual();
    }

    private void DrawPortrait(DrawingContext drawing, DesktopPalette palette, Rect bounds)
    {
        _portrait ??= DesktopThemeArt.Portrait(palette.Id);
        if (_portrait == null) return;
        if (_portraitScene == null || _portraitScene.Size != bounds.Size)
            _portraitScene = new DesktopPortraitArt(palette.Id, _portrait, bounds.Size);
        _portraitScene.Draw(drawing, _seconds + 2.65);
    }

    private void DrawClip(DrawingContext drawing, DesktopPalette palette, Rect bounds)
    {
        if (ThemeManager.AnimationsDisabled || _sceneFailed)
        {
            _clip = null;
            _requested = null;
            _presented = null;
            DrawStill(drawing, palette, bounds);
            return;
        }

        var clip = _clip ??= DesktopThemeArt.Open(palette.Id);
        if (clip == null)
        {
            _sceneFailed = true;
            SyncAnimation();
            DrawStill(drawing, palette, bounds);
            return;
        }
        Rect artwork = Cover(bounds, clip.Aspect, .64);
        int pixels = DecodeWidth(artwork.Width, clip.Width);

        // Use real frames during playback. A short dissolve is confined to the loop seam.
        int overlap = Math.Min(4, clip.FrameCount / 2);
        int blendStart = clip.FrameCount - overlap;
        double frame = _seconds * clip.FrameRate;
        if (frame >= clip.FrameCount) frame = overlap + (frame - overlap) % blendStart;
        int incoming = frame > blendStart ? (int)(frame - blendStart) : -1;
        _requested = new SceneFrameKey(clip, pixels, (int)frame, incoming);
        if (!_decoding && _presented?.Key != _requested)
            _ = DecodeSceneAsync();
        if (_presented is not SceneFrame presented || presented.Key.Clip != clip)
        {
            DrawStill(drawing, palette, bounds);
            return;
        }
        _sceneStill = null;
        _stillWidth = 0;
        drawing.DrawImage(presented.Primary, artwork);
        if (incoming >= 0 && presented.Incoming != null)
        {
            double ratio = (frame - blendStart) / overlap;
            double opacity = ratio * ratio * (3 - 2 * ratio);
            drawing.PushOpacity(opacity);
            drawing.DrawImage(presented.Incoming, artwork);
            drawing.Pop();
        }
    }

    private async Task DecodeSceneAsync()
    {
        _decoding = true;
        try
        {
            while (_requested is SceneFrameKey key && _presented?.Key != key)
            {
                SceneFrame loaded;
                try
                {
                    loaded = await Task.Run(() => new SceneFrame(key, key.Clip.Frame(key.Primary, key.Width),
                        key.Incoming < 0 ? null : key.Clip.Frame(key.Incoming, key.Width)));
                }
                catch (Exception error) when (error is IOException or FileFormatException or NotSupportedException)
                {
                    if (_requested?.Clip != key.Clip) continue;
                    _sceneFailed = true;
                    _requested = null;
                    SyncAnimation();
                    InvalidateVisual();
                    return;
                }
                if (_clip != key.Clip || _requested?.Width != key.Width) continue;
                _presented = loaded;
                InvalidateVisual();
            }
        }
        finally
        {
            _decoding = false;
        }
    }

    private void DrawStill(DrawingContext drawing, DesktopPalette palette, Rect bounds)
    {
        const int stillSourceWidth = 2880;
        var artwork = Cover(bounds, 1240d / 771, .64);
        int pixels = DecodeWidth(artwork.Width, stillSourceWidth);
        if (_sceneStill == null || _stillWidth != pixels)
        {
            _sceneStill = DesktopThemeArt.Still(palette.Id, pixels < stillSourceWidth ? pixels : 0);
            _stillWidth = pixels;
        }
        if (_sceneStill != null) drawing.DrawImage(_sceneStill, artwork);
    }

    private int DecodeWidth(double logicalWidth, int sourceWidth)
    {
        // Snap the decode request up to a bucket so resizing does not rebuild the cache at every pixel.
        double physicalWidth = logicalWidth * VisualTreeHelper.GetDpi(this).DpiScaleX;
        return Math.Min(sourceWidth, Math.Max(320, (int)Math.Ceiling(physicalWidth / 320) * 320));
    }

    private static Rect Cover(Rect bounds, double aspect, double anchorX)
    {
        double width = Math.Max(bounds.Width, bounds.Height * aspect);
        double height = width / aspect;
        return new Rect(bounds.X + (bounds.Width - width) * anchorX,
            bounds.Y + (bounds.Height - height) * .5, width, height);
    }

    private readonly record struct SceneFrameKey(DesktopThemeArt.Clip Clip, int Width, int Primary, int Incoming);

    private sealed record SceneFrame(SceneFrameKey Key, BitmapSource Primary, BitmapSource? Incoming);
}
