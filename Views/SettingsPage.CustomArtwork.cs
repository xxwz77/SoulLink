using System.IO;
using System.Windows;
using SoulLink.Models;
using SoulLink.Services;
using SoulLink.Theme;

namespace SoulLink.Views;

public partial class SettingsPage
{
    private void RefreshCustomArtwork(AppSettings settings)
    {
        bool dark = ThemeManager.IsDark;
        var stored = DesktopCustomArtwork.Setting(settings, dark);
        string? selected = dark ? settings.DarkThemeId : settings.LightThemeId;
        CustomArtworkButton.Content = stored == null ? "自定义原图…" : "编辑自定义原图…";
        CustomArtworkHint.Text = ThemeManager.Palette.Id == DesktopCustomArtwork.Id
            ? "当前使用 · " + (dark ? "深色" : "浅色")
            : selected == DesktopCustomArtwork.Id || (stored != null && !File.Exists(CustomArtworkStore.PathFor(stored.ImageKey)))
                ? "原图不可用，打开后可重新选择图片。"
                : "可使用自己的图片和配色。";
    }

    private void CustomArtwork_Click(object sender, RoutedEventArgs e)
    {
        var settings = App.Config.Settings;
        bool dark = ThemeManager.IsDark;
        var owner = Window.GetWindow(this);
        var backdrop = owner?.FindName("Backdrop") as FrameworkElement;
        double aspect = backdrop is { ActualWidth: > 1, ActualHeight: > 1 }
            ? backdrop.ActualWidth / backdrop.ActualHeight : 16.0 / 9;
        var stored = DesktopCustomArtwork.Setting(settings, dark);
        var editor = new CustomArtworkWindow(dark, stored, ThemeManager.Palette, settings.CardTransparency, aspect) { Owner = owner };
        if (editor.ShowDialog() != true || editor.Result is not { } artwork) return;
        if (dark)
        {
            settings.DarkCustomArtwork = artwork;
            settings.DarkThemeId = DesktopCustomArtwork.Id;
        }
        else
        {
            settings.LightCustomArtwork = artwork;
            settings.LightThemeId = DesktopCustomArtwork.Id;
        }
        SaveAppearance();
    }
}
