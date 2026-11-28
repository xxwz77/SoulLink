using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SoulLink.Models;
using SoulLink.Services;
using SoulLink.Theme;
using Forms = System.Windows.Forms;

namespace SoulLink.Views;

public partial class CustomArtworkWindow : Window
{
    private readonly bool _dark;
    private readonly int _transparency;
    private BitmapSource? _image;
    private ArtworkCrop _crop;
    private Point? _lastDrag;
    private Color _accent;
    private Color _surface;
    private bool _ready;
    private bool _colorValid;
    private bool _loading;
    private bool _applying;
    private bool _closed;
    private DesktopPalette? _previewPalette;

    internal CustomDesktopArtwork? Result { get; private set; }

    internal CustomArtworkWindow(bool dark, CustomDesktopArtwork? existing, DesktopPalette palette, int transparency, double aspect)
    {
        _dark = dark;
        _transparency = Math.Clamp(transparency, 0, 100);
        _crop = DesktopCustomArtwork.Crop(existing);
        _accent = DesktopCustomArtwork.TryColor(existing?.Accent, out var accent) ? accent : palette.Accent;
        _surface = DesktopCustomArtwork.TryColor(existing?.Surface, out var surface) ? surface : palette.Surface;
        InitializeComponent();

        Width = Math.Min(Width, Math.Max(MinWidth, SystemParameters.WorkArea.Width));
        Height = Math.Min(Height, Math.Max(MinHeight, SystemParameters.WorkArea.Height));
        // Preserve the reference viewport's aspect, scaling both dimensions to fit into the editor.
        double ratio = double.IsFinite(aspect) && aspect > 0 ? aspect : 16.0 / 9;
        PreviewHost.Width = ratio >= 1 ? 680 : 680 * ratio;
        PreviewHost.Height = ratio >= 1 ? 680 / ratio : 680;
        PreviewCards.Margin = new Thickness(14, 0, 0, 12);
        double cardScale = Math.Min(1, Math.Min(PreviewHost.Height / 240, PreviewHost.Width / 160));
        PreviewCards.RenderTransform = new ScaleTransform(cardScale, cardScale);
        ModeLabel.Text = dark ? "自定义原图 · 深色" : "自定义原图 · 浅色";
        AccentText.Text = DesktopCustomArtwork.ColorText(_accent);
        SurfaceText.Text = DesktopCustomArtwork.ColorText(_surface);
        _ready = true;
        _colorValid = true;
        RefreshPreview();
        ImageStatus.Text = "尚未选择图片";
        if (existing != null) Loaded += async (_, _) => await LoadExistingAsync(existing.ImageKey);
    }

    private async Task LoadExistingAsync(string key)
    {
        if (_closed || _loading || _image != null) return;
        _loading = true;
        ImageStatus.Text = "正在读取已保存的图片…";
        RefreshControls();
        try
        {
            var image = await Task.Run(() => CustomArtworkStore.TryLoad(key));
            if (_closed) return;
            _image = image;
            ImageStatus.Text = image == null ? "已保存的图片无法读取，请重新选择。" : Dimensions(image);
            RefreshPreview();
        }
        finally
        {
            _loading = false;
            if (!_closed) RefreshControls();
        }
    }

    private async void ChooseImage_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "选择背景图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|所有文件|*.*",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != true) return;
        _loading = true;
        Status.Text = "正在读取图片…";
        RefreshControls();
        try
        {
            var image = await Task.Run(() => CustomArtworkStore.LoadImage(picker.FileName));
            if (_closed) return;
            _image = image;
            _crop = ArtworkCrop.Centered;
            ImageStatus.Text = Dimensions(image);
            Status.Text = "";
            RefreshPreview();
        }
        catch (Exception ex) when (CustomArtworkStore.IsFileError(ex))
        {
            if (!_closed) Status.Text = ex is InvalidDataException ? ex.Message : "无法读取这张图片，请选择其他图片。";
        }
        finally
        {
            _loading = false;
            if (!_closed) RefreshControls();
        }
    }

    private static string Dimensions(BitmapSource image) => $"{image.PixelWidth} × {image.PixelHeight} 像素";

    private void Colors_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        bool accentValid = DesktopCustomArtwork.TryColor(AccentText.Text, out var accent);
        bool surfaceValid = DesktopCustomArtwork.TryColor(SurfaceText.Text, out var surface);
        if (accentValid) _accent = accent;
        if (surfaceValid) _surface = surface;
        _colorValid = accentValid && surfaceValid;
        RefreshPreview();
    }

    private void Color_Click(object sender, RoutedEventArgs e)
    {
        bool isAccent = ReferenceEquals(sender, AccentPicker);
        Color color = isAccent ? _accent : _surface;
        using var dialog = new Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(color.R, color.G, color.B),
            FullOpen = true
        };
        if (dialog.ShowDialog(new ColorDialogOwner(new WindowInteropHelper(this).Handle)) != Forms.DialogResult.OK) return;
        (isAccent ? AccentText : SurfaceText).Text = DesktopCustomArtwork.ColorText(Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B));
    }

    private sealed class ColorDialogOwner(IntPtr handle) : Forms.IWin32Window
    {
        public IntPtr Handle => handle;
    }

    private void Canvas_DragStarted(object sender, MouseButtonEventArgs e)
    {
        if (_image == null || _applying || !Canvas.CaptureMouse()) return;
        _lastDrag = e.GetPosition(Canvas);
        Canvas.Focus();
        e.Handled = true;
    }

    private void Canvas_Dragged(object sender, MouseEventArgs e)
    {
        if (_lastDrag is not Point last || !Canvas.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        Point position = e.GetPosition(Canvas);
        MoveCrop(position - last);
        _lastDrag = position;
    }

    private void Canvas_DragEnded(object sender, MouseButtonEventArgs e)
    {
        _lastDrag = null;
        if (Canvas.IsMouseCaptured) Canvas.ReleaseMouseCapture();
    }

    private void Canvas_KeyDown(object sender, KeyEventArgs e)
    {
        if (_image == null || _applying) return;
        double speed = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 20 : 5;
        Vector movement = e.Key switch
        {
            Key.Left => new Vector(-speed, 0),
            Key.Right => new Vector(speed, 0),
            Key.Up => new Vector(0, -speed),
            Key.Down => new Vector(0, speed),
            _ => default
        };
        if (movement == default) return;
        MoveCrop(movement);
        e.Handled = true;
    }

    private void MoveCrop(Vector movement)
    {
        if (_image == null) return;
        _crop = _crop.Drag(Canvas.RenderSize, DesktopCustomArtwork.PixelSize(_image), movement);
        RenderCrop();
    }

    private void Canvas_Zoomed(object sender, MouseWheelEventArgs e)
    {
        if (_image == null || _applying || e.Delta == 0) return;
        SetZoom(e.GetPosition(Canvas), _crop.Zoom * Math.Pow(1.12, e.Delta / 120.0));
        e.Handled = true;
    }

    private void ZoomSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || e.NewValue == _crop.Zoom) return;
        SetZoom(new Point(Canvas.RenderSize.Width / 2, Canvas.RenderSize.Height / 2), e.NewValue);
    }

    private void SetZoom(Point cursor, double scale)
    {
        if (_image == null) return;
        _crop = _crop.ZoomAt(Canvas.RenderSize, DesktopCustomArtwork.PixelSize(_image), cursor, scale);
        RenderCrop();
    }

    private void ResetCrop_Click(object sender, RoutedEventArgs e)
    {
        _crop = ArtworkCrop.Centered;
        RenderCrop();
    }

    private void RefreshPreview()
    {
        DesktopPalette palette = DesktopCustomArtwork.Palette(_dark, _accent, _surface);
        _previewPalette = palette;
        TextElement.SetForeground(PreviewCards, new SolidColorBrush(palette.Text));
        PreviewHost.Resources["CustomPreviewCard"] = new SolidColorBrush(palette.Surface) { Opacity = 1 - _transparency / 100.0 };
        PreviewHost.Resources["CustomPreviewStroke"] = new SolidColorBrush(DesktopPalette.WithAlpha(palette.Accent, 68));
        PreviewHost.Resources["CustomPreviewAccent"] = new SolidColorBrush(palette.Accent);
        PreviewHost.Resources["CustomPreviewOnAccent"] = new SolidColorBrush(DesktopCustomArtwork.TextOnAccent(palette.Accent));
        PreviewHost.Resources["CustomPreviewSecondary"] = new SolidColorBrush(palette.Secondary);
        AccentSwatch.Background = new SolidColorBrush(_accent);
        SurfaceSwatch.Background = new SolidColorBrush(_surface);
        ColorStatus.Text = !_colorValid ? "输入六位十六进制颜色，例如 #285E86。"
            : palette.Accent != _accent || palette.Surface != _surface ? "已调整预览色值以保持文字可读。" : "";
        RenderCrop();
        RefreshControls();
    }

    private void RenderCrop()
    {
        ZoomSlider.Value = _crop.Zoom;
        ZoomLabel.Text = $"缩放 {_crop.Zoom:P0}";
        if (_previewPalette != null) Canvas.Update(_image, _crop, _previewPalette);
    }

    private void RefreshControls()
    {
        bool canEdit = !_loading && !_applying;
        ChooseImageButton.IsEnabled = canEdit;
        ZoomSlider.IsEnabled = ResetButton.IsEnabled = canEdit && _image != null;
        AccentText.IsEnabled = SurfaceText.IsEnabled = AccentPicker.IsEnabled = SurfacePicker.IsEnabled = !_applying;
        ApplyButton.IsEnabled = canEdit && _colorValid && _image != null;
        CancelButton.IsEnabled = !_applying;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_image is not { } image || !_colorValid || _loading || _applying) return;
        _applying = true;
        Status.Text = "正在保存主题…";
        RefreshControls();
        try
        {
            string key = await Task.Run(() => CustomArtworkStore.Save(image));
            Result = new CustomDesktopArtwork
            {
                ImageKey = key, Accent = DesktopCustomArtwork.ColorText(_accent), Surface = DesktopCustomArtwork.ColorText(_surface),
                Zoom = _crop.Zoom, CenterX = _crop.CenterX, CenterY = _crop.CenterY
            };
        }
        catch (Exception ex) when (CustomArtworkStore.IsFileError(ex))
        {
            Status.Text = "无法保存主题图片，请检查数据目录权限和可用空间。";
        }
        finally
        {
            _applying = false;
            RefreshControls();
        }
        if (Result != null) DialogResult = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_applying) e.Cancel = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }
}
