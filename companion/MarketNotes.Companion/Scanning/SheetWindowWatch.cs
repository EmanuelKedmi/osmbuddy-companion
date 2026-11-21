using System.Text.RegularExpressions;
using MarketNotes.Companion.Capture;
using MarketNotes.Companion.Farm;
using MarketNotes.Companion.Modes;

namespace MarketNotes.Companion.Scanning;

public sealed class SheetWatchResult
{
    public required string Status { get; init; }
    public required bool CaptureOk { get; init; }
    public StatSheet? Stat { get; init; }
    public SkillSheet? Skill { get; init; }
}

/// <summary>
/// Finds framed windows on the share and reads them only when Windows OCR
/// returns text. A frame that cannot be parsed does not become a character.
/// </summary>
public sealed class SheetWindowWatch
{
    private readonly Dictionary<string, string> _hashes = new();
    private readonly Dictionary<string, StatSheet> _statByKey = new();
    private readonly Dictionary<string, SkillSheet> _skillByKey = new();

    public void Reset()
    {
        _hashes.Clear();
        _statByKey.Clear();
        _skillByKey.Clear();
    }

    public SheetWatchResult Inspect(CapturedFrame? frame)
    {
        if (frame == null)
        {
            return new SheetWatchResult
            {
                Status = "The grab returned no pixels.",
                CaptureOk = false,
            };
        }

        if (frame.Bgra.Length == 0 || frame.Height <= 0 || frame.Bgra.Length % frame.Height != 0)
        {
            return new SheetWatchResult
            {
                Status = "The stat and skill crop was empty.",
                CaptureOk = false,
            };
        }

        var stride = frame.Bgra.Length / frame.Height;
        var frames = UiChromeFinder.Frames(frame.Bgra, frame.Width, frame.Height, stride);
        if (!WindowsOcr.IsAvailable)
        {
            return new SheetWatchResult
            {
                Status = frames.Count == 0
                    ? "The stat window and the skill window are not on this frame."
                    : $"A framed window is on the share. {WindowsOcr.Status} Stats and skills were not read.",
                CaptureOk = true,
            };
        }

        StatSheet? stat = null;
        SkillSheet? skill = null;
        var notes = new List<string>();
        var seen = new HashSet<string>();
        foreach (var rect in frames)
        {
            var crop = RegionCrop.CropRect(frame, rect.X, rect.Y, rect.Width, rect.Height);
            if (!crop.Ok) continue;
            var key = $"{rect.X},{rect.Y},{rect.Width},{rect.Height}";
            seen.Add(key);
            var hash = DropRegion.Hash(crop.Bgra);
            if (_hashes.TryGetValue(key, out var previous) && previous == hash)
            {
                // Keep the last parse for this frame. Skipping OCR used to drop
                // Stat entirely, so CharacterLive thought S was closed every tick
                // after the first read and cleared the feed lock.
                notes.Add("A window is unchanged. Skipped reading.");
                if (_statByKey.TryGetValue(key, out var heldStat) && BetterStat(heldStat, stat))
                {
                    stat = heldStat;
                }

                if (_skillByKey.TryGetValue(key, out var heldSkill) && skill == null)
                {
                    skill = heldSkill;
                }

                continue;
            }

            _hashes[key] = hash;
            var lines = WindowsOcr.ReadLines(crop.Bgra, crop.Width, crop.Height);
            // Worlds CHARACTER STAT often needs a 2× pass to pick up ATTACK/ACCURACY.
            var scaled = WindowsOcr.ReadLines(
                GameFrame.PointScale(crop.Bgra, crop.Width, crop.Height, crop.Width * 2, crop.Height * 2),
                crop.Width * 2,
                crop.Height * 2);
            if (scaled.Count > lines.Count) lines = scaled;
            else if (lines.Count > 0 && scaled.Count > 0)
            {
                // Keep duplicate "N (base+bonus)" lines: INT and LUK are often
                // equal, and Distinct would collapse them to three totals.
                lines = MergeOcrPasses(lines, scaled);
            }

            if (lines.Count == 0)
            {
                notes.Add("Windows OCR read no text in a window.");
                _statByKey.Remove(key);
                _skillByKey.Remove(key);
                continue;
            }

            var text = string.Join("\n", lines);
            var flat = text.Replace("\r", " ").Replace("\n", " | ");
            if (flat.Length > 240) flat = flat[..240];
            TrainingSessionLog.Append([$"sheet ocr '{flat}'"]);
            if (DebugLog.Enabled)
            {
                InkSampleDump.Save(
                    "sheet",
                    crop.Bgra,
                    crop.Width,
                    crop.Height,
                    $"rect={rect.X},{rect.Y} {rect.Width}x{rect.Height}; " + text);
            }

            var parsedStat = StatWindowText.Parse(text);
            var parsedSkill = SkillWindowText.Parse(text);
            if (parsedStat.Succeeded)
            {
                _statByKey[key] = parsedStat;
                if (BetterStat(parsedStat, stat)) stat = parsedStat;
            }
            else
            {
                _statByKey.Remove(key);
            }

            if (parsedSkill.Succeeded)
            {
                _skillByKey[key] = parsedSkill;
                skill ??= parsedSkill;
            }
            else
            {
                _skillByKey.Remove(key);
            }

            if (!parsedStat.Succeeded && !parsedSkill.Succeeded)
            {
                notes.Add("A window was read and it is not a stat or skill window.");
            }
            else if (parsedStat.Succeeded && parsedStat.Level == null)
            {
                notes.Add("Stat window read, but level was not read.");
            }
        }

        foreach (var key in _hashes.Keys.Where(key => !seen.Contains(key)).ToList())
        {
            _hashes.Remove(key);
            _statByKey.Remove(key);
            _skillByKey.Remove(key);
        }

        if (stat != null) notes.Add("Stat window read.");
        if (skill != null) notes.Add("Skill window read.");
        if (notes.Count == 0)
        {
            notes.Add(frames.Count == 0
                ? "The stat window and the skill window are not on this frame."
                : "No readable stat or skill window.");
        }

        return new SheetWatchResult
        {
            Status = string.Join(" ", notes),
            CaptureOk = true,
            Stat = stat,
            Skill = skill,
        };
    }

    /// <summary>
    /// Merge 1× and 2× OCR. Non-bonus lines stay unique; bonus totals may
    /// repeat (INT and LUK are often the same number on Worlds sheets).
    /// </summary>
    private static List<string> MergeOcrPasses(IReadOnlyList<string> first, IReadOnlyList<string> second)
    {
        var merged = first.ToList();
        var seen = new HashSet<string>(first, StringComparer.OrdinalIgnoreCase);
        var bonusCount = first.Count(IsBonusTotal);
        foreach (var line in second)
        {
            if (IsBonusTotal(line))
            {
                if (bonusCount >= 4) continue;
                merged.Add(line);
                bonusCount++;
                continue;
            }

            if (seen.Add(line)) merged.Add(line);
        }

        return merged;
    }

    private static bool IsBonusTotal(string line)
    {
        var key = ScreenLabel.Normalize(line);
        return Regex.IsMatch(key, @"^\d{1,4}\s*\(\s*\d+\s*\+\s*\d+\s*\)");
    }

    /// <summary>
    /// Prefer a named sheet, then one with level, then more primary stats.
    /// A partial combat crop must not win over the full CHARACTER STAT frame.
    /// </summary>
    private static bool BetterStat(StatSheet candidate, StatSheet? current)
    {
        if (current == null) return true;
        var candName = candidate.Name != null;
        var curName = current.Name != null;
        if (candName != curName) return candName;
        if (candName && curName && ScreenLabel.NamesClose(candidate.Name, current.Name)
            && candidate.Name!.Length != current.Name!.Length)
        {
            return candidate.Name.Length > current.Name.Length;
        }

        var candLevel = candidate.Level != null;
        var curLevel = current.Level != null;
        if (candLevel != curLevel) return candLevel;

        var candPrimaries = CountPrimaries(candidate);
        var curPrimaries = CountPrimaries(current);
        if (candPrimaries != curPrimaries) return candPrimaries > curPrimaries;

        if ((candidate.Accuracy != null) != (current.Accuracy != null)) return candidate.Accuracy != null;
        return false;
    }

    private static int CountPrimaries(StatSheet sheet) =>
        (sheet.Str != null ? 1 : 0)
        + (sheet.Dex != null ? 1 : 0)
        + (sheet.Int != null ? 1 : 0)
        + (sheet.Luk != null ? 1 : 0);
}
