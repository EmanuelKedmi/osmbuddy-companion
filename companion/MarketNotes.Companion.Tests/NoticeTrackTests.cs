using MarketNotes.Companion.Farm;
using Xunit;

namespace MarketNotes.Companion.Tests;

/// <summary>
/// Many reads a second, positions in a 201-pixel crop. The stack is
/// bottom-anchored: the newest line sits at 140, each older one 18 higher.
/// </summary>
public sealed class NoticeTrackTests
{
    private const string Bear = "You have gained an item in the Etc tab (Bear Foot)";
    private const string Full = "Your Etc inventory is full.";
    private long _at = 1_000_000;

    private NoticeFrame Frame(params (string Line, int Top)[] lines)
    {
        _at += 100;
        return new NoticeFrame
        {
            RegionHash = _at.ToString(),
            Lines = lines.Select(line => line.Line).ToList(),
            Tops = lines.Select(line => line.Top).ToList(),
            CropHeight = 201,
            AtMs = _at,
        };
    }

    private static List<ParsedDrop> Run(params NoticeFrame[] frames)
    {
        var cursor = new NoticeCursor();
        var drops = new List<ParsedDrop>();
        foreach (var frame in frames)
        {
            var result = DropLines.Ingest(cursor, [frame]);
            cursor = result.Cursor;
            drops.AddRange(result.Drops);
        }

        return drops;
    }

    [Fact]
    public void A_line_read_garbled_in_between_counts_once()
    {
        var drops = Run(
            Frame((Bear, 140)),
            Frame(("You hays gained an item in th", 140)),
            Frame((Bear, 140)),
            Frame((Bear, 122), (Full, 140)),
            Frame(("ar Foot)", 104), (Full, 122), (Full, 140)),
            Frame((Bear, 86), (Full, 104), (Full, 122), (Full, 140)));
        Assert.Single(drops);
    }

    [Fact]
    public void The_same_item_again_below_counts_again()
    {
        var drops = Run(
            Frame((Bear, 140)),
            Frame((Bear, 122), (Bear, 140)),
            Frame((Bear, 104), (Bear, 122), (Full, 140)));
        Assert.Equal(2, drops.Count);
    }

    [Fact]
    public void A_misread_amount_on_the_same_line_counts_once()
    {
        var drops = Run(
            Frame(("You have gained mesos (+329)", 140)),
            Frame(("You have gained mesos (+328)", 140)),
            Frame(("You have gained mesos (+329)", 122), (Full, 140)));
        Assert.Single(drops);
        Assert.Equal(329, drops[0].Quantity);
    }

    [Fact]
    public void After_the_corner_empties_the_same_item_counts_again()
    {
        var drops = Run(
            Frame((Bear, 140)),
            Frame(),
            Frame(),
            Frame(),
            Frame(),
            Frame(),
            Frame((Bear, 140)));
        Assert.Equal(2, drops.Count);
    }

    [Fact]
    public void One_sparse_read_mid_transition_does_not_recount_the_stack()
    {
        var drops = Run(
            Frame((Full, 104), (Bear, 122), ("You have gained mesos (+227)", 140)),
            Frame(("ar", 60), ("ull.", 140)),
            Frame((Full, 104), (Bear, 122), ("You have gained mesos (+227)", 140)));
        Assert.Equal(2, drops.Count);
    }

    [Fact]
    public void A_pickup_that_scrolls_through_spam_counts_once()
    {
        var drops = Run(
            Frame((Full, 104), (Full, 122), (Bear, 140)),
            Frame((Full, 86), (Bear, 104), (Full, 122), (Full, 140)),
            Frame((Bear, 50), (Full, 68), (Full, 86), (Full, 104), (Full, 122), (Full, 140)),
            Frame((Full, 50), (Full, 68), (Full, 86), (Full, 104), (Full, 122), (Full, 140)));
        Assert.Single(drops);
    }

    [Fact]
    public void Two_meso_lines_keep_their_own_tracks()
    {
        var drops = Run(
            Frame(("You have gained mesos (+94)", 96), ("You have gained mesos (+101)", 114)),
            Frame(("have gaindarkon", 78), ("You have gained mesos (+101)", 96), (Bear, 114)),
            Frame(("You have gained mesos (+94)", 78), ("You have gained mesos (+101)", 96), (Bear, 114)));
        Assert.Equal([94, 101, 1], drops.Select(drop => drop.Quantity));
    }
}
