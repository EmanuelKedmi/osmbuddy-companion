using System.Text.Json;

namespace MarketNotes.Companion.Scanning;

/// <summary>
/// Maps an OCR character name onto a name already saved on the account.
/// Exact match wins; otherwise a one-or-two letter OCR miss (MonkeDKof /
/// MonkeyDKof) reuses the ledger spelling so stats do not create a twin.
/// </summary>
public static class LedgerNameMatch
{
    public static string? Prefer(string? ocrName, IEnumerable<string> ledgerNames)
    {
        var key = ocrName?.Trim() ?? "";
        if (key.Length == 0) return null;

        // Among exact + near-miss ledger names, keep the longest spelling so an
        // OCR drop (MonkeDKof) does not stick when MonkeyDKof is already saved —
        // even if a short twin was created earlier.
        string? best = null;
        foreach (var raw in ledgerNames)
        {
            var name = raw?.Trim() ?? "";
            if (name.Length == 0) continue;
            if (!name.Equals(key, StringComparison.OrdinalIgnoreCase)
                && !ScreenLabel.NamesClose(name, key))
            {
                continue;
            }

            if (best == null
                || name.Length > best.Length
                || (name.Length == best.Length
                    && name.Equals(key, StringComparison.OrdinalIgnoreCase)))
            {
                best = name;
            }
        }

        return best ?? key;
    }

    public static IReadOnlyList<string> NamesFromProfileJson(string json)
    {
        var names = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("save", out var save)) return names;
            if (!save.TryGetProperty("characters", out var characters)
                || characters.ValueKind != JsonValueKind.Array)
            {
                return names;
            }

            foreach (var row in characters.EnumerateArray())
            {
                if (row.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    var text = name.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) names.Add(text);
                }
            }
        }
        catch (JsonException)
        {
            return names;
        }

        return names;
    }
}
