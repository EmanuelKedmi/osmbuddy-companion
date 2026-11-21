using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarketNotes.Companion.Farm;

public sealed class PoolDrop
{
    public string ItemName { get; set; } = "";
    public long Quantity { get; set; }
}

public sealed class PoolSession
{
    public string Id { get; set; } = "";
    public string CharacterName { get; set; } = "";
    public int? MapId { get; set; }
    public string MapName { get; set; } = "";
    public int Kills { get; set; }
    public long DurationMs { get; set; }
    public bool Sample { get; set; }
    public bool Ocr { get; set; }
    public List<PoolDrop> Drops { get; set; } = [];
}

public sealed class PoolBook
{
    public int Version { get; set; } = 1;
    public List<PoolSession> Sessions { get; set; } = [];
}

/// <summary>
/// Per-character loot observations on this PC. The file lives under
/// %LocalAppData%/MarketNotes.Companion and is not the git repo. Library
/// chances are never written here.
/// </summary>
public sealed class LootPoolStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    public LootPoolStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? DefaultPath() : path;
    }

    public string Path => _path;

    public static string DefaultPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
        return System.IO.Path.Combine(root, "MarketNotes.Companion", "loot-pool.json");
    }

    public void ReplaceSession(TrainingSession session)
    {
        var book = Load();
        var id = session.SessionId();
        var stored = new PoolSession
        {
            Id = id,
            CharacterName = session.CharacterName,
            MapId = session.MapId,
            MapName = session.MapName,
            Kills = session.Kills,
            DurationMs = session.DurationMs,
            Sample = session.Sample,
            Ocr = session.Ocr,
            Drops = session.Drops.Select(drop => new PoolDrop { ItemName = drop.ItemName, Quantity = drop.Quantity }).ToList(),
        };
        var index = book.Sessions.FindIndex(row => row.Id == id);
        if (index >= 0) book.Sessions[index] = stored;
        else book.Sessions.Add(stored);
        Save(book);
    }

    public LootPoolSnapshot Snapshot(string characterName, int? mapId)
    {
        return Combined(characterName, mapId, null);
    }

    /// <summary>
    /// Totals for one character on one map. The live visit replaces any
    /// stored copy of the same session id, so a hunt is not counted twice.
    /// Another character on the same map stays in its own rows.
    /// </summary>
    public LootPoolSnapshot Combined(string characterName, int? mapId, TrainingSession? current)
    {
        var book = Load();
        var name = characterName.Trim();
        var quantity = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var kills = 0;
        long duration = 0;
        var skip = current?.SessionId() ?? "";
        foreach (var session in book.Sessions)
        {
            if (skip.Length > 0 && session.Id == skip) continue;
            if (!SameCharacterMap(session.CharacterName, session.MapId, name, mapId)) continue;
            Add(session.Kills, session.DurationMs, session.Drops.Select(drop => (drop.ItemName, drop.Quantity)));
        }

        if (current != null && (mapId == null || current.MapId == mapId))
        {
            Add(current.Kills, current.DurationMs, current.Drops.Select(drop => (drop.ItemName, drop.Quantity)));
        }

        return new LootPoolSnapshot { Kills = kills, DurationMs = duration, QuantityByItem = quantity };

        void Add(int addKills, long addDuration, IEnumerable<(string ItemName, long Quantity)> drops)
        {
            kills += addKills;
            duration += addDuration;
            foreach (var drop in drops)
            {
                if (drop.ItemName.Length == 0 || drop.Quantity <= 0) continue;
                quantity[drop.ItemName] = (quantity.TryGetValue(drop.ItemName, out var have) ? have : 0) + drop.Quantity;
            }
        }
    }

    private static bool SameCharacterMap(string sessionName, int? sessionMapId, string characterName, int? mapId)
    {
        if (mapId == null || sessionMapId != mapId) return false;
        return sessionName.Trim().Equals(characterName, StringComparison.OrdinalIgnoreCase);
    }

    private PoolBook Load()
    {
        try
        {
            if (!File.Exists(_path)) return new PoolBook();
            var book = JsonSerializer.Deserialize<PoolBook>(File.ReadAllText(_path), Options);
            if (book == null) return new PoolBook();
            book.Sessions ??= [];
            foreach (var session in book.Sessions) session.Drops ??= [];
            return book;
        }
        catch
        {
            return new PoolBook();
        }
    }

    private void Save(PoolBook book)
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(_path, JsonSerializer.Serialize(book, Options));
    }
}
