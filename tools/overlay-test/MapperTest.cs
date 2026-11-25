using System;
using SoulLink.Services;

internal static class MapperTest
{
    private static void Check(bool ok, string text)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + text);
        if (!ok) throw new Exception(text);
    }
    public static int Main()
    {
        var fit = new CoordinateMapper { KeepAspect = true };
        fit.Update(1920, 1080, 960, 720);
        Check(fit.OffsetY > 80 && fit.OffsetY < 100, "fit viewport centers vertical letterbox");
        Check(fit.ToRemote(480, 360) == (960, 540), "fit viewport maps center exactly");
        Check(fit.ToUi(960,540).x == 480 && fit.ToUi(960,540).y == 360, "fit UI round trip");

        var stretch = new CoordinateMapper { KeepAspect = false };
        stretch.Update(1920,1080,480,270);
        Check(stretch.ToRemote(240,135) == (960,540), "stretch viewport maps center exactly");
        Check(stretch.ToRemote(480,270) == (1919,1079), "remote coordinates clamp to desktop");

        var actual = new CoordinateMapper { KeepAspect = true };
        actual.Update(1280,720,1280,720);
        Check(actual.OffsetX == 0 && actual.OffsetY == 0, "actual size has no letterbox");
        Check(actual.ToRemote(637,431) == (637,431), "actual size maps 1:1");
        Console.WriteLine("ALL COORDINATE MAPPING TESTS PASSED");
        return 0;
    }
}