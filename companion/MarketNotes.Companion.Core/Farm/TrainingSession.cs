using System.Globalization;
using System.Text.RegularExpressions;
using MarketNotes.Companion.Scanning;

namespace MarketNotes.Companion.Farm;

public sealed class LootStack
{
    public string ItemName { get; set; } = "";
    public long Quantity { get; set; }
    public int Notices { get; set; }
}

/// <summary>
/// One training sitting inside the companion. Kills and minutes come from the
/// battle analysis text. Loot lines come from the corner. A repeated region
/// does not add the lines again.
/// </summary>
public sealed class TrainingSession
{
    private string? _regionHash;
    private List<string> _lines = [];
    private List<string> _accepted = [];
    private int? _parsedMapId;
    private List<NoticeTrack> _tracks = [];
    private NoticeFrame? _lastTracked;

    private int _kills;
    private long _durationMs;
    private long? _exp;
    private long? _damage;
    private long? _attacks;
    private long? _meso;
    private int _baseKills;
    private long _baseDurationMs;
    private long _baseExp;
    private long _baseDamage;
    private long _baseAttacks;
    private long _baseMeso;
    private long _startedAtMs;
    private long _lastAtMs;
    private bool _anchorKills;
    /// <summary>
    /// The mob cell was still 0 while damage or EXP was already on the panel.
    /// The next positive mob total is the backlog from before this map, not a new kill.
    /// </summary>
    private bool _anchorKillsOnly;
    /// <summary>
    /// No trusted mob total was available when this visit opened. The next good
    /// frame stamps <see cref="_baseKills"/> once. Later reads are not baselines.
    /// </summary>
    private bool _pendingKillStamp;

    public string CharacterName { get; private set; } = "";
    public int? MapId { get; private set; }
    public string MapName { get; private set; } = "";
    public int Generation { get; private set; }
    public string FeedKey { get; private set; } = "";
    public bool Open { get; private set; }
    /// <summary>True when this visit began while CANCEL was already showing.</summary>
    public bool JoinedMidAnalysis { get; private set; }
    public int Kills => Math.Max(0, _kills - _baseKills);
    public long DurationMs
    {
        get
        {
            if (_durationMs > 0 || _baseDurationMs > 0) return Math.Max(0, _durationMs - _baseDurationMs);
            if (_startedAtMs > 0 && _lastAtMs > _startedAtMs) return _lastAtMs - _startedAtMs;
            return 0;
        }
    }
    public long? Exp => _exp == null ? null : Math.Max(0, _exp.Value - _baseExp);
    public long? Damage => _damage == null ? null : Math.Max(0, _damage.Value - _baseDamage);
    public long? Attacks => _attacks == null ? null : Math.Max(0, _attacks.Value - _baseAttacks);
    public long? Meso => _meso == null ? null : Math.Max(0, _meso.Value - _baseMeso);
    public bool Ocr { get; private set; }
    public bool Sample { get; private set; }
    public string BattleTextRaw { get; private set; } = "";
    public int IgnoredLines { get; private set; }
    public int ParseErrors { get; private set; }
    public string? LastError { get; private set; }
    public List<LootStack> Drops { get; } = [];

    public void Clear()
    {
        CharacterName = "";
        MapId = null;
        MapName = "";
        Generation = 0;
        FeedKey = "";
        Open = false;
        JoinedMidAnalysis = false;
        _kills = 0;
        _durationMs = 0;
        _exp = null;
        _damage = null;
        _attacks = null;
        _meso = null;
        _baseKills = 0;
        _baseDurationMs = 0;
        _baseExp = 0;
        _baseDamage = 0;
        _baseAttacks = 0;
        _baseMeso = 0;
        _startedAtMs = 0;
        _lastAtMs = 0;
        _anchorKills = false;
        _anchorKillsOnly = false;
        _pendingKillStamp = false;
        Ocr = false;
        Sample = false;
        BattleTextRaw = "";
        IgnoredLines = 0;
        ParseErrors = 0;
        LastError = null;
        _regionHash = null;
        _lines = [];
        _accepted = [];
        _parsedMapId = null;
        _tracks = [];
        _lastTracked = null;
        Drops.Clear();
    }

    public void ApplySample(TrainingFixtureFile fixture, string characterName)
    {
        Clear();
        Sample = true;
        Ocr = false;
        CharacterName = characterName;
        MapId = fixture.MapId;
        MapName = fixture.MapName;
        ApplyBattle(BattleText.Parse(fixture.Battle.Text));
        Ingest(fixture.Frames);
    }

    public void BindLive(string characterName, int? mapId, string mapName)
    {
        Sample = false;
        Ocr = true;
        CharacterName = characterName;
        MapId = mapId;
        MapName = mapName;
    }

    public void BeginLive(string characterName, int? mapId, string mapName, int generation, bool joinedMid, BattleSnapshot baseline, long atMs)
    {
        Clear();
        Open = true;
        Ocr = true;
        Sample = false;
        JoinedMidAnalysis = joinedMid;
        Generation = generation;
        CharacterName = characterName ?? "";
        MapId = mapId;
        MapName = mapName ?? "";
        FeedKey = $"live:{generation}:{Slug(CharacterName)}:{Slug(MapName)}";
        SetBaseline(baseline);
        // The panel mob total is cumulative. A count already in hand is this
        // visit's stamp. Otherwise the first good mob frame stamps once.
        // Keeping the wait flag up after that stamp made every later read a
        // new baseline, so the counter stayed at 0.
        _pendingKillStamp = baseline.Kills == null;
        _anchorKills = false;
        NoteClock(atMs);
    }

    /// <summary>
    /// Call after <see cref="BeginLive"/> when this map was entered mid-analysis
    /// and the mob total was still unread. A later reading of the whole panel
    /// must not be stored as kills on the map just entered.
    /// </summary>
    public void HoldUncountedKills()
    {
        _anchorKills = true;
        _anchorKillsOnly = true;
    }

    public void Close() => Open = false;

    private static void Anchor(int? now, int previousRaw, int shown, ref int raw, ref int baseline)
    {
        if (now == null) return;
        raw = now.Value;
        baseline = now.Value < previousRaw ? -shown : now.Value - shown;
    }

    private static void Anchor(long? now, long previousRaw, long shown, ref long raw, ref long baseline)
    {
        if (now == null) return;
        raw = now.Value;
        baseline = now.Value < previousRaw ? -shown : now.Value - shown;
    }

    private static void Anchor(long? now, long? previousRaw, long? shown, ref long? raw, ref long baseline)
    {
        if (now == null) return;
        var kept = shown ?? 0;
        var previous = previousRaw ?? now.Value;
        raw = now.Value;
        baseline = now.Value < previous ? -kept : now.Value - kept;
    }

    public void RememberCharacter(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        CharacterName = name.Trim();
    }

    public void RememberMap(string name, int? mapId)
    {
        if (!string.IsNullOrWhiteSpace(name)) MapName = name.Trim();
        if (mapId != null) MapId = mapId;
        ReconsiderDrops();
    }

    public long LastActiveAtMs { get; private set; }

    public void NoteClock(long atMs)
    {
        if (atMs <= 0) return;
        if (_startedAtMs == 0) _startedAtMs = atMs;
        _lastAtMs = atMs;
        LastActiveAtMs = atMs;
    }

    /// <summary>
    /// The battle panel kept counting while this map was parked, or it was
    /// reset. Keep the kills and drops already shown, and count only what
    /// happens after the return.
    /// </summary>
    public void ResumeFrom(BattleSnapshot now)
    {
        Open = true;
        var shownKills = Kills;
        var previousKills = _kills;
        var shownDuration = DurationMs;
        var previousDuration = _durationMs;
        var shownExp = Exp;
        var previousExp = _exp;
        var shownDamage = Damage;
        var previousDamage = _damage;
        var shownAttacks = Attacks;
        var previousAttacks = _attacks;
        var shownMeso = Meso;
        var previousMeso = _meso;
        ApplyBattle(now);
        Anchor(now.Kills, previousKills, shownKills, ref _kills, ref _baseKills);
        Anchor(now.DurationMs, previousDuration, shownDuration, ref _durationMs, ref _baseDurationMs);
        Anchor(now.Exp, previousExp, shownExp, ref _exp, ref _baseExp);
        Anchor(now.Damage, previousDamage, shownDamage, ref _damage, ref _baseDamage);
        Anchor(now.Attacks, previousAttacks, shownAttacks, ref _attacks, ref _baseAttacks);
        Anchor(now.Meso, previousMeso, shownMeso, ref _meso, ref _baseMeso);
    }

    public void SetBaseline(BattleSnapshot baseline)
    {
        _baseKills = baseline.Kills ?? 0;
        _baseDurationMs = baseline.DurationMs ?? 0;
        _baseExp = baseline.Exp ?? 0;
        _baseDamage = baseline.Damage ?? 0;
        _baseAttacks = baseline.Attacks ?? 0;
        _baseMeso = baseline.Meso ?? 0;
    }

    public void ApplyBattle(BattleSnapshot battle)
    {
        if (_anchorKills && battle.Kills != null)
        {
            if (_anchorKillsOnly)
            {
                if (battle.Kills.Value > 0)
                {
                    _baseKills = battle.Kills.Value;
                    _anchorKills = false;
                    _anchorKillsOnly = false;
                    _pendingKillStamp = false;
                }
            }
            else
            {
                SetBaseline(battle);
                _anchorKills = false;
                _pendingKillStamp = false;
            }
        }
        else if (_pendingKillStamp && battle.FrameGood && battle.Kills != null)
        {
            _baseKills = battle.Kills.Value;
            _pendingKillStamp = false;
        }

        if (!string.IsNullOrWhiteSpace(battle.Text)) BattleTextRaw = battle.Text;
        if (battle.Kills != null) _kills = battle.Kills.Value;
        if (battle.DurationMs != null) _durationMs = battle.DurationMs.Value;
        if (battle.Exp != null) _exp = battle.Exp;
        if (battle.Damage != null) _damage = battle.Damage;
        if (battle.Attacks != null) _attacks = battle.Attacks;
        if (battle.Meso != null) _meso = battle.Meso;
    }

    public IngestResult Ingest(IReadOnlyList<NoticeFrame> frames)
    {
        var result = DropLines.Ingest(Cursor(), frames, MapId, MapName);
        Absorb(result);
        var tracked = frames.LastOrDefault(frame => frame.Tops != null && frame.AtMs != null && frame.Lines != null);
        if (tracked != null) _lastTracked = tracked;
        return result;
    }

    /// <summary>
    /// A clipped line can sit on screen before the minimap title is known.
    /// Once the map resolves, confirm it from the lines already read.
    /// </summary>
    private void ReconsiderDrops()
    {
        if (_lines.Count == 0) return;
        var map = MapDrops.Resolve(MapId, MapName);
        if (map == _parsedMapId) return;
        var again = _lastTracked != null && _lastTracked.Lines!.SequenceEqual(_lines)
            ? new NoticeFrame
            {
                RegionHash = _regionHash,
                Lines = _lines.ToList(),
                Tops = _lastTracked.Tops!.ToList(),
                CropHeight = _lastTracked.CropHeight,
                AtMs = _lastTracked.AtMs,
            }
            : new NoticeFrame { RegionHash = _regionHash, Lines = _lines.ToList() };
        var result = DropLines.Ingest(
            new NoticeCursor
            {
                RegionHash = null,
                Lines = _lines.ToList(),
                AcceptedKeys = _accepted.ToList(),
                ParsedMapId = _parsedMapId,
                Tracks = _tracks.ToList(),
            },
            [again],
            MapId,
            MapName);
        Absorb(result);
    }

    private NoticeCursor Cursor()
    {
        return new NoticeCursor
        {
            RegionHash = _regionHash,
            Lines = _lines.ToList(),
            AcceptedKeys = _accepted.ToList(),
            ParsedMapId = _parsedMapId,
            Tracks = _tracks.ToList(),
        };
    }

    private void Absorb(IngestResult result)
    {
        _regionHash = result.Cursor.RegionHash;
        _lines = result.Cursor.Lines;
        _accepted = result.Cursor.AcceptedKeys ?? [];
        _parsedMapId = result.Cursor.ParsedMapId;
        _tracks = result.Cursor.Tracks ?? [];
        IgnoredLines += result.Ignored;
        ParseErrors += result.ParseErrors;
        if (result.LastError != null) LastError = result.LastError;
        foreach (var drop in result.Drops)
        {
            var name = PriceCatalog.CanonicalItemName(drop.ItemName);
            var existing = Drops.FirstOrDefault(row => row.ItemName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Quantity += drop.Quantity;
                existing.Notices += 1;
                continue;
            }

            Drops.Add(new LootStack { ItemName = name, Quantity = drop.Quantity, Notices = 1 });
        }
    }

    public string SessionId()
    {
        if (FeedKey.Length > 0) return FeedKey;
        var slug = Slug(CharacterName);
        if (slug.Length == 0) slug = "hunter";
        return $"sample:{(Sample ? "fixture" : "live")}:{slug}:{MapId?.ToString() ?? "map"}";
    }

    private static string Slug(string value)
    {
        return Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
    }
}

public static class RateBlend
{
    public const int PoolMinKills = 100;
    public const int PoolFullTrustKills = 1000;

    public static (double PerKill, string Source, double Trust) Decide(double libraryPerKill, int poolKills, long poolQuantity)
    {
        var library = double.IsFinite(libraryPerKill) ? Math.Max(0, libraryPerKill) : 0;
        if (poolKills < PoolMinKills || poolQuantity <= 0)
        {
            return (library, "library", 0);
        }

        var trust = Math.Min(1d, poolKills / (double)PoolFullTrustKills);
        var pooled = poolQuantity / (double)poolKills;
        return (library * (1 - trust) + pooled * trust, "blend", trust);
    }
}

public static class LibraryChances
{
    private static readonly object Gate = new();
    private static Dictionary<string, double>? _rates;

    public static double? PerKill(int? mapId, string itemName)
    {
        if (mapId == null) return null;
        var key = Key(mapId.Value, itemName);
        return Rates().TryGetValue(key, out var rate) ? rate : null;
    }

    private static Dictionary<string, double> Rates()
    {
        if (_rates != null) return _rates;
        lock (Gate)
        {
            if (_rates != null) return _rates;
            var found = new Dictionary<string, double>(StringComparer.Ordinal);
            try
            {
                var path = Data.DataFiles.Find("library-chances.json");
                if (path != null)
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                    if (doc.RootElement.TryGetProperty("maps", out var maps))
                    {
                        foreach (var map in maps.EnumerateArray())
                        {
                            if (!map.TryGetProperty("mapId", out var idElement) || !idElement.TryGetInt32(out var mapId)) continue;
                            if (!map.TryGetProperty("items", out var items)) continue;
                            foreach (var item in items.EnumerateArray())
                            {
                                var name = item.TryGetProperty("itemName", out var nameElement) ? nameElement.GetString() ?? "" : "";
                                if (!item.TryGetProperty("perKill", out var rateElement) || !rateElement.TryGetDouble(out var rate)) continue;
                                if (name.Length == 0) continue;
                                found[Key(mapId, name)] = rate;
                            }
                        }
                    }
                }
            }
            catch
            {
                // No library figure. The report says the blend needs the catalog chance.
            }

            _rates = found;
            return found;
        }
    }

    private static string Key(int mapId, string itemName)
    {
        return mapId + "|" + DropLines.NormalizeKey(itemName);
    }
}

public sealed class LootPoolSnapshot
{
    public int Kills { get; init; }
    public long DurationMs { get; init; }
    public Dictionary<string, long> QuantityByItem { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public long Quantity(string itemName)
    {
        return QuantityByItem.TryGetValue(itemName, out var quantity) ? quantity : 0;
    }

    public static LootPoolSnapshot FromSession(TrainingSession session)
    {
        return new LootPoolSnapshot
        {
            Kills = session.Kills,
            DurationMs = session.DurationMs,
            QuantityByItem = session.Drops.ToDictionary(drop => drop.ItemName, drop => drop.Quantity, StringComparer.OrdinalIgnoreCase),
        };
    }
}

public static class TrainingReport
{
    public static string Format(TrainingSession session, LootPoolSnapshot pool)
    {
        var culture = CultureInfo.InvariantCulture;
        var lines = new List<string>();
        if (session.Sample) lines.Add("Sample. OCR did not run (ocr false). These lines were not read from a game window.");
        else if (!session.Ocr) lines.Add("OCR did not run.");
        var who = string.IsNullOrWhiteSpace(session.CharacterName) ? "This character" : session.CharacterName;
        var where = string.IsNullOrWhiteSpace(session.MapName) ? "this map" : session.MapName;
        lines.Add($"{who} on {where}.");
        if (!session.Sample) lines.Add(session.Open ? "This visit is open." : "This visit is closed.");
        if (session.JoinedMidAnalysis)
        {
            lines.Add("Battle analysis was already running, so these totals start when this map was entered.");
        }

        lines.Add($"Kills {session.Kills.ToString("N0", culture)} · {TimeText(session.DurationMs)} · EXP {Num(session.Exp)} · Damage {Num(session.Damage)} · Attacks {Num(session.Attacks)} · Meso {Num(session.Meso)}");
        lines.Add("Confirmed drops");
        if (session.Drops.Count == 0)
        {
            lines.Add("No confirmed drops yet.");
        }
        else
        {
            lines.Add("Item\tCount");
        }

        foreach (var drop in session.Drops.OrderByDescending(row => row.Quantity).ThenBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase))
        {
            var perKill = session.Kills > 0 ? drop.Quantity / (double)session.Kills : (double?)null;
            var perHour = session.DurationMs > 0 ? drop.Quantity / (session.DurationMs / 3_600_000d) : (double?)null;
            lines.Add($"{drop.ItemName}\t{drop.Quantity.ToString("N0", culture)}");
            lines.Add($"{KillsPerDrop.FromPerKill(perKill, withUnit: true)} · {Rate(perHour)} per hour");
            lines.Add(ExpectedLine(session, pool, drop.ItemName));
        }

        return string.Join("\n", lines);
    }

    private static string ExpectedLine(TrainingSession session, LootPoolSnapshot pool, string itemName)
    {
        var library = LibraryChances.PerKill(session.MapId, itemName);
        if (library == null)
        {
            return "The library blend needs the catalog chance. The observed rate above is this sitting.";
        }

        var decision = RateBlend.Decide(library.Value, pool.Kills, pool.Quantity(itemName));
        if (decision.Source == "library")
        {
            return $"Expected stays on the library rate of {KillsPerDrop.FromPerKill(library, withUnit: true)} until this character has {RateBlend.PoolMinKills} pooled kills on this map and the item has been seen ({pool.Kills.ToString("N0", CultureInfo.InvariantCulture)} kills pooled).";
        }

        var percent = (decision.Trust * 100).ToString("0", CultureInfo.InvariantCulture);
        return $"Expected {KillsPerDrop.FromPerKill(decision.PerKill, withUnit: true)}. Library {KillsPerDrop.FromPerKill(library, withUnit: true)}, pool weight {percent}% of {pool.Kills.ToString("N0", CultureInfo.InvariantCulture)} kills, full pool weight at {RateBlend.PoolFullTrustKills:N0}.";
    }

    private static string Num(long? value)
    {
        return value == null ? "—" : value.Value.ToString("N0", CultureInfo.InvariantCulture);
    }

    private static string Rate(double? value)
    {
        if (value == null || !double.IsFinite(value.Value)) return "—";
        var abs = Math.Abs(value.Value);
        if (abs >= 100) return value.Value.ToString("N0", CultureInfo.InvariantCulture);
        if (abs >= 10) return value.Value.ToString("0.##", CultureInfo.InvariantCulture);
        return value.Value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static string TimeText(long durationMs)
    {
        if (durationMs <= 0) return "0 min";
        var totalSeconds = durationMs / 1000;
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        return seconds == 0 ? $"{minutes} min" : $"{minutes} min {seconds} sec";
    }
}

public enum AnalysisSignal
{
    /// <summary>A held rectangle or a frame that never showed the panel. Not a stop.</summary>
    Uncertain,
    /// <summary>Orange START. Analysis is idle.</summary>
    Idle,
    /// <summary>Green CANCEL. Analysis is running.</summary>
    Running,
    /// <summary>The panel stayed missing until the hold expired.</summary>
    Absent,
}

public sealed class TrainingSignal
{
    public string? MapName { get; init; }
    public int? MapId { get; init; }
    public string? CharacterName { get; init; }
    public AnalysisSignal Analysis { get; init; }
    public BattleSnapshot? Battle { get; init; }
    public IReadOnlyList<NoticeFrame>? Notices { get; init; }
    public long AtMs { get; init; }
}

public readonly record struct TrainingUpdate(TrainingSession? Shown, TrainingSession? JustClosed);

/// <summary>
/// Opens a visit when analysis starts on a map, or when a map is entered
/// while analysis is already running. A brief idle is held open so the
/// button can flicker without splitting the visit. The visit stops after
/// that hold, or when the map changes. One uncertain frame does not close it.
/// </summary>
public sealed class TrainingWatch
{
    /// <summary>
    /// Analysis can read idle or absent for this long before the visit stops.
    /// Shorter gaps stay on the same session.
    /// </summary>
    public const long IdleGraceMs = 30 * 1000;

    /// <summary>
    /// A map's visit stays available this long after it leaves the screen.
    /// Coming back inside the window shows that map's drops again.
    /// </summary>
    public const long IdleKeepMs = 15 * 60 * 1000;

    private readonly Totals _raw = new();
    private readonly List<TrainingSession> _parked = [];
    private readonly List<string> _trace = [];
    private AnalysisSignal _phase = AnalysisSignal.Uncertain;
    private bool _freshStart;
    private int _generation;
    private string? _ignoredMap;
    private IReadOnlyList<NoticeFrame>? _unapplied;
    private long? _idleSince;

    public TrainingSession? OpenSession { get; private set; }
    public TrainingSession? ClosedSession { get; private set; }
    public TrainingSession? Shown => OpenSession ?? ClosedSession;
    /// <summary>How the latest battle panel's kill cell was applied: kept, corrected, or ignored.</summary>
    public string BattleKillUse => _raw.KillUse;

    /// <summary>
    /// Closes the open visit without starting another. The share calls this
    /// when the map is a Free Market room and the training scanner stops.
    /// </summary>
    public TrainingSession? Stop()
    {
        _freshStart = false;
        return Finalize("free market");
    }

    public TrainingUpdate Observe(TrainingSignal signal)
    {
        TrainingSession? justClosed = null;
        var analysis = signal.Analysis;
        if (analysis == AnalysisSignal.Uncertain)
        {
            if (signal.Battle != null) _raw.Merge(signal.Battle);
            Touch(signal, true);
            return new TrainingUpdate(Shown, null);
        }

        if (analysis is AnalysisSignal.Idle or AnalysisSignal.Absent)
        {
            if (signal.Battle != null) _raw.Merge(signal.Battle);
            if (OpenSession == null)
            {
                _idleSince = null;
                Touch(signal, true);
                _phase = analysis;
                _freshStart = false;
                return new TrainingUpdate(Shown, null);
            }

            var began = _idleSince == null;
            _idleSince ??= signal.AtMs;
            Touch(signal, true);
            if (signal.AtMs - _idleSince.Value < IdleGraceMs)
            {
                if (began) Note($"holding {OpenSession.MapName} idle; closes after 30s");
                return new TrainingUpdate(Shown, null);
            }

            justClosed = Finalize(analysis == AnalysisSignal.Idle ? "analysis idle" : "analysis absent");
            _idleSince = null;
            _phase = analysis;
            _freshStart = false;
            return new TrainingUpdate(Shown, justClosed);
        }

        if (_idleSince != null)
        {
            var reset = PanelWentBackwards(signal.Battle);
            _idleSince = null;
            if (reset)
            {
                _raw.Replace(signal.Battle);
                _freshStart = true;
                justClosed = Finalize("analysis restarted");
            }
            else if (OpenSession != null)
            {
                Note($"kept {OpenSession.MapName} open; idle was under 30s");
            }
        }

        var startedNow = _phase == AnalysisSignal.Idle;
        _phase = AnalysisSignal.Running;
        if (startedNow)
        {
            _raw.Replace(signal.Battle);
            _freshStart = true;
            justClosed = Finalize("analysis restarted");
        }
        else if (signal.Battle != null)
        {
            _raw.Merge(signal.Battle);
        }

        var map = signal.MapName?.Trim() ?? "";
        if (map.Length == 0)
        {
            Touch(signal, KnownMap(OpenSession?.MapId, OpenSession?.MapName));
            return new TrainingUpdate(Shown, justClosed);
        }

        var known = KnownMap(signal.MapId, map);
        if (OpenSession != null && !known && !ScreenLabel.SamePlace(OpenSession.MapName, map))
        {
            if (!string.Equals(_ignoredMap, map, StringComparison.OrdinalIgnoreCase))
            {
                _ignoredMap = map;
                Note($"ignored misread '{map}' while on {OpenSession.MapName}; kept {DropSummary(OpenSession)}");
            }

            Touch(signal, KnownMap(OpenSession.MapId, OpenSession.MapName));
            return new TrainingUpdate(Shown, justClosed);
        }

        _ignoredMap = null;
        if (OpenSession != null && !ScreenLabel.SamePlace(OpenSession.MapName, map))
        {
            Touch(signal, false);
            var closed = Finalize("map changed");
            justClosed = closed ?? justClosed;
            if (TryResume(signal, map)) return new TrainingUpdate(Shown, justClosed);
            OpenNew(signal, map, joinedMid: true, known, holdUncountedKills: UncountedKills(_raw.ToSnapshot()));
            return new TrainingUpdate(Shown, justClosed);
        }

        if (OpenSession == null)
        {
            if (TryResume(signal, map))
            {
                _freshStart = false;
                return new TrainingUpdate(Shown, justClosed);
            }

            OpenNew(signal, map, joinedMid: !_freshStart, known);
            _freshStart = false;
            return new TrainingUpdate(Shown, justClosed);
        }

        _freshStart = false;
        if (signal.MapId != null || !string.Equals(OpenSession.MapName, map, StringComparison.OrdinalIgnoreCase))
        {
            OpenSession.RememberMap(map, signal.MapId ?? OpenSession.MapId);
        }

        Touch(signal, known || KnownMap(OpenSession?.MapId, OpenSession?.MapName));
        return new TrainingUpdate(Shown, justClosed);
    }

    private bool PanelWentBackwards(BattleSnapshot? next)
    {
        if (next == null || (next.FrameChecked && !next.FrameGood)) return false;
        if (next.Kills != null && _raw.Kills != null && next.Kills.Value < _raw.Kills.Value) return true;
        if (next.DurationMs != null && _raw.DurationMs != null && next.DurationMs.Value < _raw.DurationMs.Value) return true;
        return false;
    }

    /// <summary>
    /// Loot is only counted once the title is a catalog map. A misread such
    /// as "Monkey ForeWll" cannot be tied to the mobs that spawn there.
    /// </summary>
    private static bool KnownMap(int? mapId, string? mapName)
    {
        if (MapDrops.Resolve(mapId, mapName) != null) return true;
        return MapCatalog.Match(mapName) != null;
    }

    private static bool UncountedKills(BattleSnapshot panel)
    {
        return panel.Kills is null or 0 && (panel.Damage is > 0 || panel.Exp is > 0);
    }

    private void OpenNew(TrainingSignal signal, string map, bool joinedMid, bool ingestLoot, bool holdUncountedKills = false)
    {
        _generation++;
        var session = new TrainingSession();
        var baseline = joinedMid ? _raw.ToSnapshot() : new BattleSnapshot();
        session.BeginLive(signal.CharacterName ?? "", signal.MapId, map, _generation, joinedMid, baseline, signal.AtMs);
        if (holdUncountedKills) session.HoldUncountedKills();
        session.ApplyBattle(_raw.ToSnapshot());
        var frames = signal.Notices ?? _unapplied;
        if (ingestLoot && frames != null)
        {
            session.Ingest(frames);
            _unapplied = null;
        }
        else if (signal.Notices != null)
        {
            _unapplied = signal.Notices;
        }

        OpenSession = session;
        Note($"opened {session.MapName} joinedMid={joinedMid} drops={session.Drops.Count}");
    }

    private void Touch(TrainingSignal signal, bool ingest)
    {
        var session = OpenSession;
        if (session == null || !session.Open)
        {
            if (signal.Notices != null) _unapplied = signal.Notices;
            return;
        }

        if (!string.IsNullOrWhiteSpace(signal.CharacterName)) session.RememberCharacter(signal.CharacterName);
        session.NoteClock(signal.AtMs);
        session.ApplyBattle(_raw.ToSnapshot());
        if (!ingest)
        {
            if (signal.Notices != null) _unapplied = signal.Notices;
            return;
        }

        if (signal.Notices != null)
        {
            session.Ingest(signal.Notices);
            _unapplied = null;
        }
    }

    private bool TryResume(TrainingSignal signal, string map)
    {
        Expire(signal.AtMs);
        TrainingSession? kept = null;
        foreach (var session in _parked)
        {
            var sameId = signal.MapId != null && session.MapId != null && signal.MapId == session.MapId;
            if (!sameId && !ScreenLabel.SamePlace(session.MapName, map)) continue;
            kept = session;
        }

        if (kept == null) return false;
        _parked.Remove(kept);
        var before = DropSummary(kept);
        kept.ResumeFrom(signal.Battle ?? _raw.ToSnapshot());
        if (!string.IsNullOrWhiteSpace(signal.CharacterName)) kept.RememberCharacter(signal.CharacterName);
        if (signal.MapId != null || !string.Equals(kept.MapName, map, StringComparison.OrdinalIgnoreCase))
        {
            kept.RememberMap(map, signal.MapId ?? kept.MapId);
        }

        kept.NoteClock(signal.AtMs);
        if (signal.Notices != null)
        {
            kept.Ingest(signal.Notices);
            _unapplied = null;
        }

        OpenSession = kept;
        _freshStart = false;
        Note($"resumed {kept.MapName} kept {before}; now {DropSummary(kept)} kills={kept.Kills}");
        return true;
    }

    private void Expire(long atMs)
    {
        if (atMs <= 0) return;
        for (var i = _parked.Count - 1; i >= 0; i--)
        {
            var session = _parked[i];
            if (session.LastActiveAtMs <= 0 || atMs - session.LastActiveAtMs <= IdleKeepMs) continue;
            _parked.RemoveAt(i);
            Note($"expired {session.MapName} after 15 idle minutes; dropped {DropSummary(session)}");
        }
    }

    public IReadOnlyList<string> DrainTrace()
    {
        if (_trace.Count == 0) return [];
        var copy = _trace.ToList();
        _trace.Clear();
        return copy;
    }

    private TrainingSession? Finalize(string reason)
    {
        if (OpenSession == null) return null;
        var session = OpenSession;
        session.Close();
        OpenSession = null;
        ClosedSession = session;
        _parked.RemoveAll(other => other != session && ScreenLabel.SamePlace(other.MapName, session.MapName));
        if (!_parked.Contains(session)) _parked.Add(session);
        _unapplied = null;
        Note($"parked {session.MapName} ({reason}); kept {DropSummary(session)} kills={session.Kills}");
        return session;
    }

    private void Note(string message) => _trace.Add(message);

    private static string DropSummary(TrainingSession session)
    {
        if (session.Drops.Count == 0) return "no drops";
        return string.Join(", ", session.Drops.Select(drop => $"{drop.ItemName} x{drop.Quantity}"));
    }

    private sealed class Totals
    {
        public int? Kills { get; private set; }
        public long? DurationMs { get; private set; }
        public long? Exp { get; private set; }
        public long? Damage { get; private set; }
        public long? Attacks { get; private set; }
        public long? Meso { get; private set; }
        public string Text { get; private set; } = "";
        /// <summary>
        /// The stored kill count was bound to the Mob cell. A later mob cell
        /// replaces a stuck total that was not.
        /// </summary>
        private bool _killsFromMob;
        /// <summary>The stored kill count came from a frame that passed the gate.</summary>
        private bool _goodFrame;
        /// <summary>
        /// How the last merged panel treated its kill cell. Recording this does not change the count.
        /// </summary>
        public string KillUse { get; private set; } = "ignored";

        public void Clear()
        {
            Kills = null;
            DurationMs = null;
            Exp = null;
            Damage = null;
            Attacks = null;
            Meso = null;
            Text = "";
            _killsFromMob = false;
            _goodFrame = false;
            KillUse = "ignored";
        }

        public void Replace(BattleSnapshot? next)
        {
            Clear();
            if (next != null) Merge(next);
        }

        public void Merge(BattleSnapshot next)
        {
            // A frame that fails the gate is dropped whole. It cannot raise the
            // kill total or freeze a later good Mob cell out. A good Mob cell
            // may replace a total that never passed the gate, such as 1858 or
            // 87392. After a good total is stored, a small dip does not lower it.
            if (next.FrameChecked && !next.FrameGood)
            {
                KillUse = "rejected";
                return;
            }

            var fromMob = next.KillsFromMob && next.Kills != null;
            var replacesStuck = fromMob && next.FrameGood && !_goodFrame
                && Kills != null && next.Kills!.Value < Kills.Value;
            var ahead = Ahead(next.Kills, Kills);
            var acceptKills = replacesStuck || ahead;
            if (acceptKills) Kills = next.Kills;
            if (Ahead(next.DurationMs, DurationMs)) DurationMs = next.DurationMs;
            if (Ahead(next.Exp, Exp)) Exp = next.Exp;
            if (Ahead(next.Damage, Damage)) Damage = next.Damage;
            if (Ahead(next.Attacks, Attacks)) Attacks = next.Attacks;
            if (Ahead(next.Meso, Meso)) Meso = next.Meso;
            if (!string.IsNullOrWhiteSpace(next.Text)) Text = next.Text;
            if (acceptKills)
            {
                _killsFromMob = fromMob;
                if (next.FrameGood) _goodFrame = true;
            }

            KillUse = replacesStuck ? "corrected" : ahead ? "kept" : "ignored";
        }

        private static bool Ahead(long? next, long? held)
        {
            return next != null && (held == null || next.Value >= held.Value);
        }

        public BattleSnapshot ToSnapshot()
        {
            return new BattleSnapshot
            {
                Kills = Kills,
                KillsFromMob = _killsFromMob,
                DurationMs = DurationMs,
                Exp = Exp,
                Damage = Damage,
                Attacks = Attacks,
                Meso = Meso,
                Text = Text,
                FrameChecked = _goodFrame,
                FrameGood = _goodFrame,
            };
        }
    }
}
