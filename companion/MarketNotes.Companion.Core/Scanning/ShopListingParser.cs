using System.Text.RegularExpressions;

namespace MarketNotes.Companion.Scanning;

/// <summary>
/// Turns the shop title and the item lines Windows OCR read inside the shop
/// frame into owner, shop name, and priced rows.
/// </summary>
public static class ShopListingParser
{
    private static readonly Regex OwnShop = new(
        @"^(?<owner>.+?)'s\s+(?<shop>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Priced = new(
        @"^(?<name>.*?)(?:\s+x\s*(?<bundle>\d{1,5}))?(?:\s+(?<qty>\d{1,5}))?\s+(?<price>\d{1,3}(?:[,.]\d{3})+|\d{2,10})\s*(?:mesos?)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BareNumber = new(
        @"^(?<price>\d{1,3}(?:[,.]\d{3})+|\d{1,10})\s*(?:mesos?)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// A stack count, then the meso coin (0 or the letter o), then the price.
    /// The coin is often read as o. Any of those pieces can be missing.
    /// </summary>
    private static readonly Regex QtyPrice = new(
        @"^(?:(?<lead>\d{1,4})\s+)?(?:(?<coin>[0oO])\s+)?(?:(?<mid>\d{1,4})\s+)?(?<price>\d{1,3}(?:[,.]\d{3})+|\d{4,10})\s*(?:mesos?)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TrailingCount = new(
        @"^(?<base>.+?)\s+(?:x\s*)?(?<count>\d{1,4})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MesoWord = new(
        @"\b(?:mesos?|esos?|eos)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> Chrome = new(StringComparer.OrdinalIgnoreCase)
    {
        "buy", "sell", "close", "exit", "ok", "cancel", "item", "price", "qty", "quantity",
        "meso", "mesos", "shop", "enter", "cash", "next", "back", "confirm", "per",
    };

    public static FmFrameInput Parse(
        string? mapName,
        int? channel,
        double? scrollPosition,
        IReadOnlyList<string> titleLines,
        IReadOnlyList<string> rowLines)
    {
        var (owner, shop) = ParseTitle(titleLines);
        var rows = ReadRows(rowLines, out var unreadable);
        var visible = Math.Max(rows.Count, 1);
        return new FmFrameInput
        {
            MapName = string.IsNullOrWhiteSpace(mapName) ? "" : mapName.Trim(),
            Owner = owner,
            ShopName = shop,
            Channel = channel,
            Scrollbar = new FmScrollbar
            {
                Position = scrollPosition is >= 0 and <= 1 ? scrollPosition.Value : 0,
                First = 0,
                Visible = visible,
            },
            Rows = rows,
            Unreadable = unreadable,
        };
    }

    public static List<FmVisibleRow> ParseRows(IReadOnlyList<string> lines) => ReadRows(lines, out _);

    private static List<FmVisibleRow> ReadRows(IReadOnlyList<string> lines, out int unreadable)
    {
        var rows = new List<FmVisibleRow>();
        string? pendingName = null;
        int pendingQty = 1;
        var qtySet = false;
        var spoil = false;
        unreadable = 0;
        foreach (var raw in lines)
        {
            var line = StripCoin(Clean(raw));
            if (line.Length == 0 || IsChromeLine(line) || IsCoin(line) || IsMesoNoise(line)) continue;

            if (TryQtyThenPrice(line, out var lineQty, out var linePrice))
            {
                if (spoil || pendingName == null)
                {
                    spoil = false;
                    pendingName = null;
                    qtySet = false;
                    continue;
                }

                var qty = lineQty ?? (qtySet ? pendingQty : 1);
                if (!Add(rows, pendingName, qty, linePrice)) unreadable++;
                pendingName = null;
                qtySet = false;
                continue;
            }

            if (TryPriced(line, out var name, out var qtyOnLine, out var price))
            {
                if (spoil)
                {
                    spoil = false;
                    pendingName = null;
                    qtySet = false;
                    continue;
                }

                if (!Add(rows, name, qtyOnLine, price)) unreadable++;
                pendingName = null;
                qtySet = false;
                continue;
            }

            if (TryBare(line, out var number))
            {
                if (number == 0) continue;
                if (pendingName == null) continue;
                if (!qtySet && number is >= 1 and <= 9999 && number < 1000)
                {
                    pendingQty = (int)number;
                    qtySet = true;
                    continue;
                }

                if (spoil)
                {
                    spoil = false;
                    pendingName = null;
                    qtySet = false;
                    continue;
                }

                if (number >= 1)
                {
                    if (!Add(rows, pendingName, qtySet ? pendingQty : 1, number)) unreadable++;
                    pendingName = null;
                    qtySet = false;
                }

                continue;
            }

            if (IsUnreadable(line))
            {
                pendingName = null;
                qtySet = false;
                spoil = true;
                unreadable++;
                continue;
            }

            if (pendingName != null && !HasDigit(line) && !IsJustNumber(pendingName))
            {
                var joined = pendingName + " " + line;
                if (IsUnreadable(joined))
                {
                    pendingName = null;
                    qtySet = false;
                    spoil = true;
                    unreadable++;
                    continue;
                }

                pendingName = joined;
                continue;
            }

            var peeled = PeelCount(line, out var peeledQty);
            pendingName = peeled;
            pendingQty = peeledQty ?? 1;
            qtySet = peeledQty != null;
        }

        return rows;
    }

    /// <summary>
    /// Keeps a priced row the catalog knows, including a chopped name that
    /// fits one catalog item, and a clean name the catalog does not have.
    /// A price bled into the name, or two items in one line, is returned in
    /// <paramref name="hidden"/> for the debug table only.
    /// </summary>
    public static List<FmVisibleRow> WithCatalogNames(IReadOnlyList<FmVisibleRow> rows) =>
        WithCatalogNames(rows, out _);

    public static List<FmVisibleRow> WithCatalogNames(IReadOnlyList<FmVisibleRow> rows, out List<FmVisibleRow> hidden)
    {
        var kept = new List<FmVisibleRow>();
        hidden = [];
        foreach (var row in rows)
        {
            if (PriceCatalog.TryRecover(row.Item, out var canonical))
            {
                row.Item = canonical;
            }
            else if (IsJunk(row.Item))
            {
                hidden.Add(row);
                continue;
            }

            row.Row = kept.Count;
            row.Slot = kept.Count;
            kept.Add(row);
        }

        return kept;
    }

    /// <summary>
    /// While a cursor or a bled line is on the list, the pale fill is the
    /// pointer, not a sale. The clean rows stay; their sold flag waits.
    /// </summary>
    public static void HoldSalesWhileCovered(IList<FmVisibleRow> listed, IReadOnlyList<FmVisibleRow> hidden)
    {
        if (hidden.Count == 0 && !listed.Any(row => row.Covered)) return;
        foreach (var row in listed) row.Sold = false;
    }

    private static (string Owner, string Shop) ParseTitle(IReadOnlyList<string> lines)
    {
        var useful = new List<string>();
        foreach (var raw in lines)
        {
            var line = Clean(raw);
            if (line.Length == 0 || Chrome.Contains(line)) continue;
            useful.Add(line);
        }

        if (useful.Count == 0) return ("", "");
        var owned = OwnShop.Match(useful[0]);
        if (owned.Success)
        {
            return (owned.Groups["owner"].Value.Trim(), owned.Groups["shop"].Value.Trim());
        }

        if (useful.Count == 1) return ("", useful[0]);
        return (useful[0], useful[1]);
    }

    private static bool IsChromeLine(string line)
    {
        if (Chrome.Contains(line)) return true;
        if (line.Contains("hired merchant", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("buy item", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("leave store", StringComparison.OrdinalIgnoreCase)) return true;
        return Regex.IsMatch(line, @"^\d{1,2}:\d{2}$");
    }

    private static bool TryQtyThenPrice(string line, out int? qty, out long price)
    {
        qty = null;
        price = 0;
        var match = QtyPrice.Match(line);
        if (!match.Success) return false;
        if (!TryAmount(match.Groups["price"].Value, out price) || price < 1) return false;
        if (match.Groups["mid"].Success && int.TryParse(match.Groups["mid"].Value, out var mid) && mid is >= 1 and <= 9999)
        {
            qty = mid;
        }
        else if (match.Groups["lead"].Success && int.TryParse(match.Groups["lead"].Value, out var lead) && lead is >= 1 and <= 9999)
        {
            qty = lead;
        }

        return true;
    }

    private static bool TryPriced(string line, out string name, out int qty, out long price)
    {
        name = "";
        qty = 1;
        price = 0;
        var match = Priced.Match(line);
        if (!match.Success) return false;
        name = Clean(match.Groups["name"].Value);
        if (name.Length < 2 || Chrome.Contains(name)) return false;
        if (!TryAmount(match.Groups["price"].Value, out price) || price < 1) return false;
        var token = match.Groups["price"].Value;
        var grouped = token.Contains(',') || token.Contains('.');
        var saysMeso = line.Contains("meso", StringComparison.OrdinalIgnoreCase);
        if (!grouped && !saysMeso && price < 1000) return false;
        if (match.Groups["bundle"].Success && int.TryParse(match.Groups["bundle"].Value, out var bundle) && bundle is >= 1 and <= 100_000)
        {
            qty = bundle;
        }
        else if (match.Groups["qty"].Success && int.TryParse(match.Groups["qty"].Value, out var parsed) && parsed is >= 1 and <= 100_000)
        {
            qty = parsed;
        }

        return true;
    }

    private static bool TryBare(string line, out long number)
    {
        number = 0;
        var match = BareNumber.Match(line);
        if (!match.Success) return false;
        return TryAmount(match.Groups["price"].Value, out number);
    }

    private static bool TryAmount(string raw, out long number)
    {
        var digits = Regex.Replace(raw, @"[^\d]", "");
        if (!long.TryParse(digits, out number) || number < 0 || number > 50_000_000_000L) 
        {
            number = 0;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns false when the name is two listings run together and was left out.
    /// </summary>
    private static bool Add(List<FmVisibleRow> rows, string name, int qty, long price)
    {
        var item = Clean(name);
        if (item.Length < 2 || price < 1 || qty < 1 || IsUnreadable(item)) return false;
        rows.Add(new FmVisibleRow
        {
            Item = item,
            Quantity = qty,
            PriceEach = price,
            Slot = rows.Count,
            Row = rows.Count,
        });
        return true;
    }

    /// <summary>
    /// A trailing number is a stack when the name without it is a real item
    /// and the full text is not. Mastery-book levels 1–30 stay on the name.
    /// </summary>
    private static string PeelCount(string name, out int? qty)
    {
        qty = null;
        var match = TrailingCount.Match(name);
        if (!match.Success) return name;
        if (!int.TryParse(match.Groups["count"].Value, out var count) || count is < 1 or > 9999) return name;
        var basis = match.Groups["base"].Value.Trim();
        if (basis.Length < 2) return name;
        if (PriceCatalog.TryMatch(name, out _)) return name;
        if (!PriceCatalog.TryMatch(basis, out _)) return name;
        if (count <= 30 && basis.Contains("Mastery Book", StringComparison.OrdinalIgnoreCase)) return name;
        qty = count;
        return basis;
    }

    private static bool IsUnreadable(string name)
    {
        if (MesoWord.IsMatch(name)) return true;
        var words = name.Split(' ');
        if (words.Length < 4) return false;
        for (var i = 1; i < words.Length; i++)
        {
            var left = string.Join(' ', words.Take(i));
            var right = string.Join(' ', words.Skip(i));
            if (left.Length < 4 || right.Length < 4) continue;
            if (!PriceCatalog.TryMatch(left, out var first)) continue;
            if (!PriceCatalog.TryMatch(right, out var second)) continue;
            if (!first.Equals(second, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// A shop line that should not become a listing. A real name the catalog
    /// does not have, such as Yellow Gaia Cape, is not junk.
    /// </summary>
    private static bool IsJunk(string name)
    {
        if (PriceCatalog.TryMatch(name, out _)) return false;
        var letters = 0;
        foreach (var ch in name)
        {
            if (char.IsLetter(ch)) letters++;
        }

        if (letters < 3) return true;
        if (Regex.IsMatch(name, @"\d{1,3}[,.]\d{3}")) return true;
        if (Regex.IsMatch(name, @"\b(?:mesos?|esos?|eos|mes)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return true;
        if (Regex.IsMatch(name, @"\b\p{L}\b", RegexOptions.CultureInvariant)) return true;
        return GluedNames(name);
    }

    private static bool GluedNames(string name)
    {
        var words = name.Split(' ');
        if (words.Length < 4) return false;
        for (var i = 1; i < words.Length; i++)
        {
            var left = string.Join(' ', words.Take(i));
            var right = string.Join(' ', words.Skip(i));
            if (left.Length < 4 || right.Length < 4) continue;
            var leftMatch = PriceCatalog.TryMatch(left, out var first);
            var rightMatch = PriceCatalog.TryMatch(right, out var second);
            if (leftMatch && rightMatch && !first.Equals(second, StringComparison.OrdinalIgnoreCase)) return true;
            if (leftMatch && !rightMatch && NameWords(words.Skip(i)) >= 2) return true;
            if (rightMatch && !leftMatch && NameWords(words.Take(i)) >= 2) return true;
        }

        return false;
    }

    private static int NameWords(IEnumerable<string> words)
    {
        var count = 0;
        foreach (var word in words)
        {
            var letters = 0;
            foreach (var ch in word)
            {
                if (char.IsLetter(ch)) letters++;
            }

            if (letters >= 3) count++;
        }

        return count;
    }

    private static bool IsCoin(string line) => line is "o" or "O";

    private static bool IsMesoNoise(string line) =>
        Regex.IsMatch(line, @"^(?:mesos?|esos?|eos)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The meso coin beside a name is often read as a trailing 0 or o.
    /// </summary>
    private static string StripCoin(string line)
    {
        if (line.Length < 4 || !Regex.IsMatch(line, @"\s+[0oO]$")) return line;
        if (!line.Any(char.IsLetter)) return line;
        return line[..^2].TrimEnd();
    }

    private static bool HasDigit(string line) => line.Any(char.IsDigit);

    private static bool IsJustNumber(string line)
    {
        foreach (var ch in line)
        {
            if (char.IsDigit(ch) || ch is ' ' or ',' or '.') continue;
            return false;
        }

        return line.Any(char.IsDigit);
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return Regex.Replace(value.Trim(), @"\s+", " ");
    }
}
