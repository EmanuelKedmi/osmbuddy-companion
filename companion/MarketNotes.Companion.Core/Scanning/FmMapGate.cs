using System.Text.Json;
using System.Text.RegularExpressions;
using MarketNotes.Companion.Data;

namespace MarketNotes.Companion.Scanning;

public sealed class FmMapClassification
{
    public required bool Ok { get; init; }
    public required string Detected { get; init; }
    public string? MatchedName { get; init; }
    /// <summary>Set only when the alias itself names a channel. A room number is not a channel.</summary>
    public int? ChannelFromName { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// A frame is Free Market only when the map name is in data/fm-maps.json or
/// one of the aliases used in this repo. A hunting map is not an FM map.
/// The room number in Free Market&lt;1&gt; is not a channel.
/// </summary>
public static class FmMapGate
{
    private static readonly Regex ChannelAlias = new(@"^fm channel (\d{1,2})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RoomAlias = new(@"^fm (\d{1,2})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly object Gate = new();
    private static Dictionary<string, MapEntry>? _entries;

    private sealed record MapEntry(string Name, int Id);

    public static bool IsFreeMarket(string? mapName, out string detected, out int? channelFromName)
    {
        var gate = Classify(mapName);
        detected = gate.Detected;
        channelFromName = gate.ChannelFromName;
        return gate.Ok;
    }

    public static FmMapClassification Classify(string? mapName)
    {
        var raw = Regex.Replace((mapName ?? "").Trim(), @"\s+", " ");
        var detected = raw.Length == 0 ? "unknown" : raw;
        if (raw.Length == 0 || raw.Equals("unknown", StringComparison.OrdinalIgnoreCase) || raw.Equals("unknown map", StringComparison.OrdinalIgnoreCase))
        {
            return Reject(detected);
        }

        var key = Normalize(raw);
        var matched = MatchKey(detected, key);
        if (matched != null) return matched;

        // OCR title bands often keep the street name: "Hidden Street Free Market<l>".
        var freeMarketAt = key.LastIndexOf("free market", StringComparison.Ordinal);
        if (freeMarketAt > 0)
        {
            matched = MatchKey(detected, key[freeMarketAt..]);
            if (matched != null) return matched;
        }

        var fmAt = key.LastIndexOf("fm ", StringComparison.Ordinal);
        if (fmAt > 0)
        {
            matched = MatchKey(detected, key[fmAt..]);
            if (matched != null) return matched;
        }
        else if (key.EndsWith(" fm", StringComparison.Ordinal) || key == "fm")
        {
            matched = MatchKey(detected, "fm");
            if (matched != null) return matched;
        }

        return Reject(detected);
    }

    private static FmMapClassification? MatchKey(string detected, string key)
    {
        var channel = ChannelAlias.Match(key);
        if (channel.Success && int.TryParse(channel.Groups[1].Value, out var channelNumber) && channelNumber is >= 1 and <= 30)
        {
            return Accept(detected, "Free Market", channelNumber);
        }

        var room = RoomAlias.Match(key);
        if (room.Success && int.TryParse(room.Groups[1].Value, out var roomNumber) && roomNumber is >= 1 and <= 22)
        {
            return Accept(detected, $"Free Market<{roomNumber}>", null);
        }

        if (key is "fm" or "free market") return Accept(detected, "Free Market", null);
        if (key is "fm entrance" or "free market entrance") return Accept(detected, "Free Market Entrance", null);

        if (Entries().TryGetValue(key, out var hit)) return Accept(detected, hit.Name, null);

        var freeRoom = Regex.Match(key, @"^free market (\d{1,2})$", RegexOptions.CultureInvariant);
        if (freeRoom.Success && int.TryParse(freeRoom.Groups[1].Value, out roomNumber) && roomNumber is >= 1 and <= 22)
        {
            return Accept(detected, $"Free Market<{roomNumber}>", null);
        }

        return null;
    }

    public static string Normalize(string name)
    {
        var lower = name.ToLowerInvariant();
        lower = Regex.Replace(lower, @"[<>\[\]()]", " ");
        // | and ! are stripped next; treat them as 1 beside a Free Market / FM label.
        lower = Regex.Replace(lower, @"\b((?:henesys |perion |el nath |ludibrium )?(?:free market|fm)(?: channel)?)\s*[|!](?=\s|$)", "$1 1", RegexOptions.CultureInvariant);
        lower = Regex.Replace(lower, @"[^a-z0-9]+", " ").Trim();
        lower = Regex.Replace(lower, @"\s+", " ");
        // Windows OCR often reads the room digit 1 as l / I / | / !.
        return RewriteLookalikeRoomDigits(lower);
    }

    /// <summary>
    /// Rewrite a short Free Market / FM room or channel token made of OCR
    /// digit lookalikes so <c>Free Market&lt;l&gt;</c> matches room 1.
    /// </summary>
    private static string RewriteLookalikeRoomDigits(string key)
    {
        return Regex.Replace(
            key,
            @"\b((?:henesys |perion |el nath |ludibrium )?(?:free market|fm)(?: channel)?) ((?:[0-9]|[li]){1,2})\b",
            match =>
            {
                var head = match.Groups[1].Value;
                var token = match.Groups[2].Value;
                if (token.All(char.IsDigit)) return match.Value;
                var digits = new string(token.Select(ch => ch is 'l' or 'i' ? '1' : ch).ToArray());
                if (!int.TryParse(digits, out var number) || number < 1 || number > 30) return match.Value;
                return head + " " + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            },
            RegexOptions.CultureInvariant);
    }

    private static FmMapClassification Reject(string detected)
    {
        return new FmMapClassification
        {
            Ok = false,
            Detected = detected,
            Reason = "not an FM map",
        };
    }

    private static FmMapClassification Accept(string detected, string matchedName, int? channelFromName)
    {
        return new FmMapClassification
        {
            Ok = true,
            Detected = detected,
            MatchedName = matchedName,
            ChannelFromName = channelFromName,
        };
    }

    private static Dictionary<string, MapEntry> Entries()
    {
        if (_entries != null) return _entries;
        lock (Gate)
        {
            if (_entries != null) return _entries;
            var found = new Dictionary<string, MapEntry>(StringComparer.Ordinal);
            try
            {
                var path = DataFiles.Find("fm-maps.json");
                if (path != null)
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    if (doc.RootElement.TryGetProperty("maps", out var maps))
                    {
                        foreach (var map in maps.EnumerateArray())
                        {
                            if (!map.TryGetProperty("name", out var nameElement)) continue;
                            var name = nameElement.GetString();
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            var id = map.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var parsed) ? parsed : 0;
                            Add(found, name, id);
                        }
                    }
                }
            }
            catch
            {
                // Aliases above still answer when the map list cannot be read.
            }

            _entries = found;
            return found;
        }
    }

    private static void Add(Dictionary<string, MapEntry> found, string name, int id)
    {
        var key = Normalize(name);
        if (key.Length == 0) return;
        if (!found.TryGetValue(key, out var existing))
        {
            found[key] = new MapEntry(name, id);
            return;
        }

        var existingFm = existing.Id is >= 910000000 and < 920000000;
        var nextFm = id is >= 910000000 and < 920000000;
        if (!existingFm && nextFm) found[key] = new MapEntry(name, id);
    }
}
