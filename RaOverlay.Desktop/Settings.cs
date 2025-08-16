using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RaOverlay.Desktop;

public sealed class OverlaySettings
{
    public string Username { get; set; } = "";
    public string ApiKeyCipher { get; set; } = "";           // DPAPI-protected
    public int    Port { get; set; } = 4050;

    public string Position { get; set; } = "tl";              // tl|tr|bl|br
    public double Opacity  { get; set; } = 0.75;              // 0..1
    public int    Blur     { get; set; } = 8;                 // px
    public double Scale    { get; set; } = 1.0;               // 0.6..1.4
    public int    MinWidth { get; set; } = 560;               // px
    public string BackgroundRgb { get; set; } = "32,34,38";   // "r,g,b"
    public string NextSort { get; set; } = "list";            // list|points-asc|points-desc

    public string? SoundPath { get; set; }                    // original mp3 path user picked
}

public static class SettingsStore
{
    public static string GetFolder()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RaOverlay");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string GetPath() => Path.Combine(GetFolder(), "settings.json");

    public static OverlaySettings Load()
    {
        try
        {
            var path = GetPath();
            if (!File.Exists(path)) return new OverlaySettings();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<OverlaySettings>(json) ?? new OverlaySettings();
        }
        catch
        {
            return new OverlaySettings();
        }
    }

    public static void Save(OverlaySettings s)
    {
        var json = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(GetPath(), json);
    }

    // DPAPI (per-current-user) protect/unprotect for the API key
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var bytes = Encoding.UTF8.GetBytes(plain);
        var cipher = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(cipher);
    }

    public static string Unprotect(string cipher)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cipher)) return "";
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(cipher), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return ""; }
    }
}
