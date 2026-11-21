using MarketNotes.Companion.Capture;

namespace MarketNotes.Companion.Scanning;

/// <summary>
/// Shop UI regions for the FM scanner. The fractions are the intended layout
/// (map name along the top, shop dialog in the middle, scrollbar on its right
/// edge). They are not a measurement of a live client.
/// </summary>
public readonly record struct FmCropReport(int MapBytes, int HeaderBytes, int ScrollBytes, int RowBytes, bool CaptureOk);

public static class FmRegions
{
    public static FmCropReport Measure(CapturedFrame? frame)
    {
        if (frame == null) return new FmCropReport(0, 0, 0, 0, false);
        var map = RegionCrop.Crop(frame, 0, 0, 1, 0.08);
        var header = RegionCrop.Crop(frame, 0.18, 0.16, 0.82, 0.28);
        var scroll = RegionCrop.Crop(frame, 0.78, 0.28, 0.86, 0.78);
        var rows = RegionCrop.Crop(frame, 0.18, 0.28, 0.78, 0.78);
        return new FmCropReport(map.Length, header.Length, scroll.Length, rows.Length, map.Length + header.Length + scroll.Length + rows.Length > 0);
    }
}
