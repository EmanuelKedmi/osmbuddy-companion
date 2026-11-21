namespace MarketNotes.Companion.Scanning;

public enum ScannerMode
{
    Fm,
    Training,
}

/// <summary>
/// The share has no FM / training switch. Each map check picks the scanner.
/// A Free Market room is the FM scanner. Every other map is training.
/// </summary>
public static class ScannerModeChoice
{
    public static ScannerMode ForMap(string? mapName)
    {
        return FmMapGate.Classify(mapName).Ok ? ScannerMode.Fm : ScannerMode.Training;
    }
}

/// <summary>
/// The map name that chooses the scanner. A Free Market line wins on this
/// check, including when the training label is still locked to a hunting map.
/// A catalog hunting map wins on this check. Any other name waits until the
/// hold accepts it. A weak read keeps the previous choice.
/// </summary>
public static class ModeMapPicker
{
    public static string Resolve(string previous, IReadOnlyList<string>? lines, string heldBefore, string heldAfter)
    {
        var picked = Pick(lines, heldBefore, heldAfter);
        if (string.IsNullOrWhiteSpace(picked)) return previous;
        // Keep FM mode when the new pick is OCR junk (e.g. "GoC") rather than
        // a catalog hunting map or another Free Market room.
        if (FmMapGate.Classify(previous).Ok
            && !FmMapGate.Classify(picked).Ok
            && MapTitle.Choose([picked]) is not { FromCatalog: true, StreetOnly: false })
        {
            return previous;
        }

        return picked;
    }

    private static string? Pick(IReadOnlyList<string>? lines, string heldBefore, string heldAfter)
    {
        if (lines == null) return null;
        var fm = FreeMarketName(lines);
        if (fm != null) return fm;
        var choice = MapTitle.Choose(lines);
        if (choice != null && choice.Value.FromCatalog) return choice.Value.Name;
        if (heldAfter.Length > 0 && !string.Equals(heldBefore, heldAfter, StringComparison.Ordinal))
        {
            return heldAfter;
        }

        return null;
    }

    private static string? FreeMarketName(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            var gate = FmMapGate.Classify(line);
            if (gate.Ok) return gate.MatchedName ?? line.Trim();
        }

        if (lines.Count > 1)
        {
            var joined = string.Join(" ", lines);
            var gate = FmMapGate.Classify(joined);
            if (gate.Ok) return gate.MatchedName ?? joined.Trim();
        }

        return null;
    }
}
