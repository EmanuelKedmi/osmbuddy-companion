using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using MarketNotes.Companion.Farm;

namespace MarketNotes.Companion;

/// <summary>
/// A release build asks GitHub which companion is published. A newer release is
/// downloaded from GitHub and swapped in after this process exits. A loopback
/// feed still asks that local site.
/// </summary>
public static class CompanionUpdate
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public static async Task<string?> NewerVersionAsync(string? feedUrl, string current)
    {
        var localSite = WebsiteAddress.IsLoopback(feedUrl);
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                localSite ? WebsiteAddress.VersionUrl(feedUrl) : WebsiteAddress.ReleaseApiUrl);
            if (!localSite)
            {
                request.Headers.UserAgent.ParseAdd("MarketNotes.Companion");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
            }

            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var remote = localSite ? ReadString(doc, "version") : WebsiteAddress.VersionFromReleaseTag(ReadString(doc, "tag_name"));
            if (!AppVersion.IsNewer(remote, current)) return null;
            return AppVersion.Release(remote);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadString(JsonDocument doc, string name)
    {
        if (!doc.RootElement.TryGetProperty(name, out var property)) return null;
        return property.GetString();
    }

    public static async Task<(bool Ok, string Message)> StageAsync(string? feedUrl)
    {
        var current = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(current) || current.Contains('"'))
            return (false, "This build cannot replace itself.");

        var localSite = WebsiteAddress.IsLoopback(feedUrl);
        var downloaded = Path.Combine(Path.GetTempPath(), "MarketNotes.Companion-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            var downloadUrl = localSite ? WebsiteAddress.DownloadUrl(feedUrl) : WebsiteAddress.ReleaseDownloadUrl;
            using var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
                return (false, localSite ? "The website did not offer a companion download." : "GitHub did not offer a companion download.");
            await using (var input = await response.Content.ReadAsStreamAsync())
            await using (var output = File.Create(downloaded))
            {
                await input.CopyToAsync(output);
            }
        }
        catch
        {
            TryDelete(downloaded);
            return (false, "The update could not be downloaded.");
        }

        if (!LooksLikeExe(downloaded))
        {
            TryDelete(downloaded);
            return (false, "The download was not a companion program.");
        }

        if (!StartReplacer(downloaded, current))
        {
            TryDelete(downloaded);
            return (false, "The update could not be staged.");
        }

        return (true, "The new companion will open in a moment.");
    }

    private static bool LooksLikeExe(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length < 100_000) return false;
            using var stream = File.OpenRead(path);
            return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch
        {
            return false;
        }
    }

    private static bool StartReplacer(string downloaded, string current)
    {
        if (downloaded.Contains('"') || current.Contains('"')) return false;
        var script = Path.Combine(Path.GetTempPath(), "MarketNotes.Companion-update-" + Guid.NewGuid().ToString("N") + ".cmd");
        var body = "@echo off\r\n"
            + "set N=0\r\n"
            + ":retry\r\n"
            + "set /a N+=1\r\n"
            + $"copy /Y \"{downloaded}\" \"{current}\" >nul\r\n"
            + "if not errorlevel 1 goto launch\r\n"
            + "if %N% LSS 30 (\r\n"
            + "  ping 127.0.0.1 -n 2 >nul\r\n"
            + "  goto retry\r\n"
            + ")\r\n"
            + "exit /b 1\r\n"
            + ":launch\r\n"
            + $"start \"\" \"{current}\"\r\n"
            + $"del \"{downloaded}\"\r\n"
            + "del \"%~f0\"\r\n";
        try
        {
            File.WriteAllText(script, body);
            var start = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(script);
            return Process.Start(start) != null;
        }
        catch
        {
            TryDelete(script);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A leftover temp file is not the running companion.
        }
    }
}
