using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarketNotes.Companion.Farm;

public sealed class FixtureFile
{
    public int MapId { get; set; }
    public string MapName { get; set; } = "";
    public int Kills { get; set; }
    public long DurationMs { get; set; }
    public List<FixtureFrame> Frames { get; set; } = [];
}

public sealed class FixtureFrame
{
    public string? RegionHash { get; set; }
    public List<string>? Lines { get; set; }
    public string? Error { get; set; }
}

public static class FixtureFarm
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string? Locate()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "fixtures", "drop-notices.json"),
            Path.Combine(Environment.CurrentDirectory, "data", "fixtures", "drop-notices.json"),
            Path.Combine(Environment.CurrentDirectory, "..", "data", "fixtures", "drop-notices.json"),
            Path.Combine(Environment.CurrentDirectory, "..", "..", "data", "fixtures", "drop-notices.json"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static FixtureFile Load()
    {
        var path = Locate();
        if (path == null) throw new FileNotFoundException("data/fixtures/drop-notices.json was not found.");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<FixtureFile>(json, ReadOptions)
            ?? throw new InvalidOperationException("The drop-notice fixture was empty.");
    }
}

/// <summary>
/// Posts raw notice lines or a heartbeat. The site in lib/farm.ts parses lines.
/// This client does not decide drop rates.
/// </summary>
public sealed class FarmFeedClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<(bool Ok, string Message)> PostAsync(string url, object body, string? token = null, CancellationToken cancel = default)
    {
        var posted = await PostForBodyAsync(url, body, token, cancel);
        return (posted.Ok, posted.Message);
    }

    public async Task<(bool Ok, string Message, string Body)> GetAsync(string url, string? token = null, CancellationToken cancel = default)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return (false, "The feed URL has to be an http or https address.", "");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim());
            }

            using var response = await Http.SendAsync(request, cancel);
            var text = await response.Content.ReadAsStringAsync(cancel);
            if (!response.IsSuccessStatusCode)
            {
                var detail = text.Length > 180 ? text[..180] : text;
                return (false, $"The feed returned {(int)response.StatusCode}. {detail}", text);
            }

            return (true, "Read from the feed.", text);
        }
        catch (Exception ex)
        {
            return (false, $"The feed is down ({ex.GetType().Name}).", "");
        }
    }

    public async Task<(bool Ok, string Message, string Body)> PostForBodyAsync(string url, object body, string? token = null, CancellationToken cancel = default)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return (false, "The feed URL has to be an http or https address.", "");
        }

        try
        {
            var json = JsonSerializer.Serialize(body, WriteOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim());
            using var response = await Http.SendAsync(request, cancel);
            var text = await response.Content.ReadAsStringAsync(cancel);
            if (!response.IsSuccessStatusCode)
            {
                var detail = text.Length > 180 ? text[..180] : text;
                return (false, $"The feed returned {(int)response.StatusCode}. {detail}", text);
            }

            return (true, "Posted to the feed.", text);
        }
        catch (Exception ex)
        {
            return (false, $"The feed is down ({ex.GetType().Name}).", "");
        }
    }
}
