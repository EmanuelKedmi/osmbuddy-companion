using System.Globalization;

namespace MarketNotes.Companion.Farm;

/// <summary>
/// Presentation for a drop-rate column. The stored figure stays quantity per kill.
/// N is kills per one unit. Smaller N is a more common drop, so sort by <see cref="Value"/>.
/// N at least 10 is an integer. Below 10 it keeps one decimal.
/// When quantity exceeds kills, the label stays units per kill so mesos do not read as "1 / 0.5".
/// </summary>
public static class KillsPerDrop
{
    public static double? Value(double quantity, double kills)
    {
        if (!(quantity > 0) || !(kills > 0) || !double.IsFinite(quantity) || !double.IsFinite(kills)) return null;
        var n = kills / quantity;
        return double.IsFinite(n) && n > 0 ? n : null;
    }

    public static string Format(long quantity, int kills, bool withUnit = false)
    {
        return Format((double)quantity, kills, withUnit);
    }

    public static string Format(double quantity, double kills, bool withUnit = false)
    {
        var n = Value(quantity, kills);
        if (n == null) return "—";
        if (n.Value < 1) return Count(quantity / kills) + " / kill";
        var text = "1 / " + Count(n.Value);
        return withUnit ? text + " kills" : text;
    }

    public static string FromPerKill(double? dropsPerKill, bool withUnit = false)
    {
        if (dropsPerKill == null) return "—";
        return Format(dropsPerKill.Value, 1, withUnit);
    }

    private static string Count(double value)
    {
        if (!double.IsFinite(value) || value <= 0) return "—";
        var culture = CultureInfo.InvariantCulture;
        if (value >= 10)
        {
            return Math.Round(value, MidpointRounding.AwayFromZero).ToString("N0", culture);
        }

        var tenths = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        if (tenths >= 10) return tenths.ToString("N0", culture);
        return tenths.ToString("0.0", culture);
    }
}
