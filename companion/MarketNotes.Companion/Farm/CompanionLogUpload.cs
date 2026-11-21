using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MarketNotes.Companion.Farm;

/// <summary>
/// The tail of the debug logs, posted to the website with the companion token.
/// </summary>
public static class CompanionLogUpload
{
    public const int MaxChars = 1_000_000;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static string Tail(string path, int maxChars = MaxChars)
    {
        try
        {
            if (!File.Exists(path)) return "";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length == 0) return "";
            var clipped = false;
            var maxBytes = (long)maxChars * 4;
            if (stream.Length > maxBytes)
            {
                stream.Seek(-maxBytes, SeekOrigin.End);
                clipped = true;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            if (clipped)
            {
                var line = text.IndexOf('\n');
                if (line >= 0) text = text[(line + 1)..];
            }

            if (text.Length > maxChars) text = text[^maxChars..];
            return text;
        }
        catch
        {
            return "";
        }
    }

    public static async Task<(bool Ok, string Message)> PostAsync(string url, string lootLog, string trainingLog, string? token)
    {
        if (lootLog.Length == 0 && trainingLog.Length == 0)
        {
            return (false, "No debug logs yet. Share the game with debug on, then upload.");
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return (false, "The website address has to be an http or https URL.");
        }

        try
        {
            var json = JsonSerializer.Serialize(new { lootLog, trainingLog });
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim());
            }

            using var response = await Http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return (false, "The website refused the logs. Paste a companion token from the account page.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var detail = text.Length > 180 ? text[..180] : text;
                return (false, $"The website returned {(int)response.StatusCode}. {detail}");
            }

            return (true, "Logs were sent to the website.");
        }
        catch (Exception ex)
        {
            return (false, $"The website is down ({ex.GetType().Name}).");
        }
    }
}
