namespace SoulLink.Models;

public sealed record CustomDesktopArtwork
{
    public string ImageKey { get; init; } = "";
    public string Accent { get; init; } = "#4C6F93";
    public string Surface { get; init; } = "#E7EEF8";
    public double Zoom { get; init; } = 1;
    public double CenterX { get; init; } = .5;
    public double CenterY { get; init; } = .5;
}
