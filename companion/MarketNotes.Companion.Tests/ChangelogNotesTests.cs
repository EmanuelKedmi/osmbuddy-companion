using MarketNotes.Companion;
using Xunit;

namespace MarketNotes.Companion.Tests;

public class ChangelogNotesTests
{
    [Fact]
    public void Parses_changelog_sections_into_version_headlines()
    {
        var text = """
            # Changelog

            ## 1.0.6
            - Drop the name and loot bands below the chat field.

            ## 1.0.5
            - Accept the gray battle analysis title bar.
            """;

        var entries = ChangelogNotes.Parse(text);
        Assert.Equal(2, entries.Count);
        Assert.Equal("1.0.6", entries[0].Version);
        Assert.Equal(["Drop the name and loot bands below the chat field."], entries[0].Headlines);
        Assert.Equal("1.0.5", entries[1].Version);
        Assert.Equal(["Accept the gray battle analysis title bar."], entries[1].Headlines);
    }

    [Fact]
    public void Parses_semantic_release_github_bodies()
    {
        var body = """
            ## [1.0.6](https://github.com/EmanuelKedmi/osmbuddy-companion/compare/v1.0.5...v1.0.6) (2026-11-21)


            ### Bug Fixes

            * drop the name and loot bands below the chat field. ([53b52f0](https://github.com/EmanuelKedmi/osmbuddy-companion/commit/53b52f02f34a14ce56d971f8dc1b17c9fce3fca9))
            """;

        var entries = ChangelogNotes.Parse(body);
        Assert.Single(entries);
        Assert.Equal("1.0.6", entries[0].Version);
        Assert.Equal(["Drop the name and loot bands below the chat field."], entries[0].Headlines);
    }

    [Fact]
    public void Merge_prefers_remote_headlines_and_keeps_fallback_for_empty()
    {
        var remote = new[]
        {
            new ChangelogEntry("1.0.6", ["Live note."]),
            new ChangelogEntry("1.0.3", Array.Empty<string>()),
        };
        var fallback = new[]
        {
            new ChangelogEntry("1.0.6", ["Embedded note."]),
            new ChangelogEntry("1.0.3", ["Ship the ngm hijack button."]),
            new ChangelogEntry("1.0.2", ["Publish from GitHub."]),
        };

        var merged = ChangelogNotes.Merge(remote, fallback);
        Assert.Equal(["1.0.6", "1.0.3", "1.0.2"], merged.Select(e => e.Version).ToArray());
        Assert.Equal(["Live note."], merged[0].Headlines);
        Assert.Equal(["Ship the ngm hijack button."], merged[1].Headlines);
        Assert.Equal(["Publish from GitHub."], merged[2].Headlines);
    }

    [Fact]
    public void Format_plain_lists_each_version()
    {
        var text = ChangelogNotes.FormatPlain(
        [
            new ChangelogEntry("1.0.6", ["Drop the chat bands."]),
            new ChangelogEntry("1.0.5", ["Accept gray title bar."]),
        ]);

        Assert.Contains("1.0.6", text);
        Assert.Contains("· Drop the chat bands.", text);
        Assert.Contains("1.0.5", text);
    }
}
