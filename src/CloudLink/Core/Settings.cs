using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudLink.Core;

public static class AppPaths
{
    public static string Root { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudLink");

    public static string File(string name)
    {
        Directory.CreateDirectory(Root);
        return Path.Combine(Root, name);
    }
}

public sealed class Settings
{
    /// <summary>
    /// The application (client) ID of the Microsoft Entra app registration this build signs in through.
    /// It is set at build time by the CloudLinkMicrosoftClientId property (Directory.Build.props) and is
    /// empty in a build that has none; OneDrive then needs an ID entered in Settings.
    /// </summary>
    public static string BuiltInMicrosoftClientId { get; } = CanonicalId(typeof(Settings).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "MicrosoftClientId")?.Value);

    /// <summary>The usual lower-case, hyphenated form of an application ID; empty for anything that is not one.</summary>
    public static string CanonicalId(string? id) => Guid.TryParse(id?.Trim(), out var guid) ? guid.ToString("D") : "";

    public string MicrosoftClientId { get; set; } = "";
    public string GoogleClientId { get; set; } = "";
    [JsonIgnore] public string GoogleClientSecret { get; set; } = "";
    [JsonIgnore] public string GoogleApiKey { get; set; } = "";

    // The two Google credentials are written encrypted for the current Windows user.
    [JsonPropertyName("GoogleClientSecret")]
    public string StoredGoogleClientSecret { get => Protect(GoogleClientSecret); set => GoogleClientSecret = Unprotect(value); }
    [JsonPropertyName("GoogleApiKey")]
    public string StoredGoogleApiKey { get => Protect(GoogleApiKey); set => GoogleApiKey = Unprotect(value); }
    public int ParallelDownloads { get; set; } = 4;
    public string LastFolder { get; set; } = "";
    public string MicrosoftAccount { get; set; } = "";
    public string GoogleAccount { get; set; } = "";

    [JsonIgnore]
    public string EffectiveMicrosoftClientId =>
        CanonicalId(MicrosoftClientId) is { Length: > 0 } own ? own : BuiltInMicrosoftClientId;

    /// <summary>False until there is an application ID to sign in to Microsoft with.</summary>
    [JsonIgnore]
    public bool HasMicrosoftClientId => EffectiveMicrosoftClientId.Length > 0;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    const string Sealed = "dpapi:";

    static string Protect(string value) => value.Length == 0 ? "" : Sealed + Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

    static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(Sealed, StringComparison.Ordinal)) return stored;   // written by 1.0, in the clear
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(stored[Sealed.Length..]), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return "";
        }
    }

    public static Settings Load()
    {
        try
        {
            string path = AppPaths.File("settings.json");
            if (System.IO.File.Exists(path))
            {
                string text = System.IO.File.ReadAllText(path);
                var settings = JsonSerializer.Deserialize<Settings>(text) ?? new Settings();
                // Versions before 1.2 also wrote the application ID in use; rewrite the file without it.
                if (text.Contains("\"EffectiveMicrosoftClientId\"", StringComparison.Ordinal)) settings.Save();
                return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Write("Settings could not be read: " + ex.Message);
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            string path = AppPaths.File("settings.json");
            string tmp = path + ".tmp";
            System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            System.IO.File.Move(tmp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Settings could not be saved: " + ex.Message);
        }
    }
}

/// <summary>The version comes from Directory.Build.props; a git commit, when there is one, is appended by the SDK.</summary>
public static class AppInfo
{
    static readonly string Informational = typeof(AppInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static string Version => Informational.Split('+')[0];
    public static string Commit => Informational.Contains('+') ? new string(Informational.Split('+')[1].Take(7).ToArray()) : "";
    public static bool ShowCommit { get; set; } = true;
    public static string Display => ShowCommit && Commit.Length > 0 ? $"{Version} ({Commit})" : Version;
}

public static class Log
{
    static readonly object Gate = new();

    public static string FilePath => AppPaths.File("cloudlink.log");

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                string path = FilePath;
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 5 * 1024 * 1024)
                    System.IO.File.Move(path, path + ".old", true);
                System.IO.File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
