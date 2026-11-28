using System.Windows;
using System.Windows.Media;

namespace SoulLink.Controls;

/// <summary>Static theme surface for the independent windows that already host this element.</summary>
public sealed class AuroraBackdrop : FrameworkElement
{
    private static readonly DependencyProperty SurfaceBrushProperty = DependencyProperty.Register(
        "SurfaceBrush", typeof(Brush), typeof(AuroraBackdrop),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public AuroraBackdrop()
    {
        IsHitTestVisible = false;
        Focusable = false;
        SnapsToDevicePixels = true;
        SetResourceReference(SurfaceBrushProperty, "DialogUnderlay");
    }

    public void RefreshAnimationState() => InvalidateVisual();

    public void InvalidateStaticFrame() => InvalidateVisual();

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (GetValue(SurfaceBrushProperty) is Brush brush && RenderSize.Width > 0 && RenderSize.Height > 0)
            drawing.DrawRectangle(brush, null, new Rect(RenderSize));
    }
}
