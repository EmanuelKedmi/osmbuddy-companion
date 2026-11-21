using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MarketNotes.Companion.Modes;

/// <summary>
/// The website address and companion token are shared by both scanners and kept on this PC.
/// The token is encrypted for the current Windows user.
/// </summary>
public static class WebsiteSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public readonly record struct Loaded(bool Found, string Url, string Token);

    public static Loaded Load()
    {
        try
        {
            var path = Path();
            if (!File.Exists(path)) return new Loaded(false, "", "");
            var saved = JsonSerializer.Deserialize<FileBody>(File.ReadAllText(path), JsonOptions);
            if (saved == null) return new Loaded(false, "", "");
            return new Loaded(true, saved.Url?.Trim() ?? "", Unprotect(saved.Token));
        }
        catch
        {
            return new Loaded(false, "", "");
        }
    }

    public static void Save(string url, string token)
    {
        try
        {
            var path = Path();
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var body = new FileBody
            {
                Url = url.Trim(),
                Token = Protect(token.Trim()),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(body, JsonOptions));
        }
        catch
        {
            // A blocked settings folder still leaves the values in effect for this run.
        }
    }

    private static string Protect(string token)
    {
        if (token.Length == 0) return "";
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string Unprotect(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return "";
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    private sealed class FileBody
    {
        public string? Url { get; set; }
        public string? Token { get; set; }
    }

    private static string Path()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
        return System.IO.Path.Combine(root, "MarketNotes.Companion", "website.json");
    }
}
