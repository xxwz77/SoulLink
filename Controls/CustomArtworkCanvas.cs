using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SoulLink.Theme;

namespace SoulLink.Controls;

public sealed class CustomArtworkCanvas : FrameworkElement
{
    private BitmapSource? _image;
    private ArtworkCrop _crop;
    private DesktopPalette? _palette;

    internal void Update(BitmapSource? image, ArtworkCrop crop, DesktopPalette palette)
    {
        _image = image;
        _crop = crop;
        _palette = palette;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (_palette != null && ActualWidth > 0 && ActualHeight > 0)
            DesktopCustomArtwork.Draw(drawing, new Rect(RenderSize), _image, _crop, _palette);
    }
}
