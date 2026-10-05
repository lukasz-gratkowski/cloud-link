using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CloudLink.Core;

namespace CloudLink.Providers;

/// <summary>
/// Google Drive links through the Drive v3 API. Uses the signed-in account when there is
/// one, otherwise an API key, which is enough for "anyone with the link" items.
/// </summary>
public sealed class GoogleDriveProvider(HttpClient api, ITokenSource auth, Func<string> apiKey,
    string driveBase = "https://www.googleapis.com/drive/v3") : ICloudProvider
{
    const string FolderMime = "application/vnd.google-apps.folder";
    const string ShortcutMime = "application/vnd.google-apps.shortcut";
    const string ItemFields = "id,name,mimeType,size,md5Checksum,modifiedTime,resourceKey,exportLinks,shortcutDetails";

    // Google's own document types have no file content; they are exported to these formats.
    static readonly Dictionary<string, (string Mime, string Ext)> Exports = new()
    {
        ["application/vnd.google-apps.document"] = ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx"),
        ["application/vnd.google-apps.spreadsheet"] = ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx"),
        ["application/vnd.google-apps.presentation"] = ("application/vnd.openxmlformats-officedocument.presentationml.presentation", ".pptx"),
        ["application/vnd.google-apps.drawing"] = ("image/png", ".png"),
        ["application/vnd.google-apps.script"] = ("application/vnd.google-apps.script+json", ".json"),
    };

    public Func<int, TimeSpan?, TimeSpan>? Backoff { get; set; }

    public string Name => "Google Drive";

    public bool CanWork => auth.IsSignedIn || !string.IsNullOrWhiteSpace(apiKey());

    public bool Matches(Uri link)
    {
        string h = link.Host.ToLowerInvariant();
        return h is "drive.google.com" or "docs.google.com" or "drive.usercontent.google.com";
    }

    public void OnUnauthorized() => auth.Invalidate();

    /// <summary>Pulls the item id (and the resource key some older links need) out of a Drive link.</summary>
    public static (string Id, string? ResourceKey)? ParseLink(Uri link)
    {
        string? id = null, key = null;
        foreach (string pair in link.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq < 0) continue;
            string name = pair[..eq], value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            if (name == "id") id = value;
            else if (name == "resourcekey") key = value;
        }

        string[] seg = link.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < seg.Length - 1; i++)
            if (seg[i] is "folders" or "d")
                id = seg[i + 1];

        if (id is null || id.Length < 10 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return null;
        return (id, key);
    }

    public async Task<RemoteEntry> ResolveAsync(Uri link, CancellationToken ct)
    {
        var parsed = ParseLink(link)
            ?? throw new CloudException("This does not look like a Google Drive file or folder link.", false);
        return await GetItemAsync(parsed.Id, parsed.ResourceKey, ct);
    }

    async Task<RemoteEntry> GetItemAsync(string id, string? resourceKey, CancellationToken ct)
    {
        using var doc = await GetAsync(
            $"{driveBase}/files/{Uri.EscapeDataString(id)}?supportsAllDrives=true&fields={ItemFields}", id, resourceKey, ct);
        return await ParseAsync(doc.RootElement, resourceKey, ct);
    }

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(RemoteEntry folder, CancellationToken ct)
    {
        var result = new List<RemoteEntry>();
        string query = Uri.EscapeDataString($"'{folder.Id}' in parents and trashed = false");
        string? page = null;
        do
        {
            string url = $"{driveBase}/files?q={query}&pageSize=1000&supportsAllDrives=true&includeItemsFromAllDrives=true" +
                $"&fields=nextPageToken,files({ItemFields})" + (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page));
            using var doc = await GetAsync(url, folder.Id, folder.ResourceKey, ct);
            if (doc.RootElement.TryGetProperty("files", out var files))
                foreach (var f in files.EnumerateArray())
                    result.Add(await ParseAsync(f, null, ct));
            page = Str(doc.RootElement, "nextPageToken");
        } while (page is not null);
        return result;
    }

    public async Task<HttpRequestMessage> CreateDownloadRequestAsync(RemoteEntry file, CancellationToken ct)
    {
        string id = Uri.EscapeDataString(file.Id);
        if (file.ExportMime is null)
            return await RequestAsync($"{driveBase}/files/{id}?alt=media&supportsAllDrives=true", file.Id, file.ResourceKey, ct);

        // The export endpoint stops at 10 MB; the document's own export link does not, but needs a sign-in.
        if (file.ExportUrl is not null && auth.IsSignedIn && IsGoogleHost(file.ExportUrl))
            return await RequestAsync(file.ExportUrl, file.Id, file.ResourceKey, ct);
        return await RequestAsync($"{driveBase}/files/{id}/export?mimeType={Uri.EscapeDataString(file.ExportMime)}",
            file.Id, file.ResourceKey, ct);
    }

    public async Task<string> GetAccountNameAsync(CancellationToken ct)
    {
        using var doc = await GetAsync($"{driveBase}/about?fields=user(emailAddress,displayName)", null, null, ct);
        return doc.RootElement.TryGetProperty("user", out var u)
            ? Str(u, "emailAddress") ?? Str(u, "displayName") ?? "signed in"
            : "signed in";
    }

    Task<JsonDocument> GetAsync(string url, string? id, string? resourceKey, CancellationToken ct) =>
        Api.GetJsonAsync(api, c => RequestAsync(url, id, resourceKey, c), auth.Invalidate, ct, Backoff);

    async Task<HttpRequestMessage> RequestAsync(string url, string? id, string? resourceKey, CancellationToken ct)
    {
        HttpRequestMessage req;
        if (auth.IsSignedIn)
        {
            req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await auth.GetAccessTokenAsync(ct));
        }
        else
        {
            string key = apiKey().Trim();
            if (key.Length == 0) throw new SignInRequiredException(Name);
            req = new HttpRequestMessage(HttpMethod.Get, url + (url.Contains('?') ? "&" : "?") + "key=" + Uri.EscapeDataString(key));
        }
        if (id is not null && resourceKey is not null)
            req.Headers.TryAddWithoutValidation("X-Goog-Drive-Resource-Keys", $"{id}/{resourceKey}");
        return req;
    }

    async Task<RemoteEntry> ParseAsync(JsonElement f, string? knownResourceKey, CancellationToken ct)
    {
        string id = Str(f, "id") ?? throw new CloudException("Google Drive returned an item without an id.", false);
        string name = Str(f, "name") ?? id;
        string mime = Str(f, "mimeType") ?? "";
        string? resourceKey = Str(f, "resourceKey") ?? knownResourceKey;

        if (mime == ShortcutMime)
        {
            string? target = f.TryGetProperty("shortcutDetails", out var sd) ? Str(sd, "targetId") : null;
            if (target is null) return Unavailable(id, name, "The shortcut has no target");
            try
            {
                string? targetKey = Str(sd, "targetResourceKey");
                return await GetItemAsync(target, targetKey, ct) with { Name = name };
            }
            catch (CloudException ex) when (!ex.Transient)
            {
                return Unavailable(id, name, "The shortcut's target cannot be opened");
            }
        }

        DateTimeOffset? modified = Str(f, "modifiedTime") is { } stamp
            && DateTimeOffset.TryParse(stamp, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

        if (mime == FolderMime)
            return new RemoteEntry { Id = id, Name = name, IsFolder = true, Modified = modified, ResourceKey = resourceKey };

        if (mime.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal))
        {
            if (!Exports.TryGetValue(mime, out var export))
                return Unavailable(id, name, "Google " + mime[(mime.LastIndexOf('.') + 1)..] + " items cannot be downloaded as files");
            string? exportUrl = f.TryGetProperty("exportLinks", out var links) ? Str(links, export.Mime) : null;
            return new RemoteEntry
            {
                Id = id,
                Name = name.EndsWith(export.Ext, StringComparison.OrdinalIgnoreCase) ? name : name + export.Ext,
                Modified = modified,
                Resumable = false,
                ResourceKey = resourceKey,
                ExportMime = export.Mime,
                ExportUrl = exportUrl,
            };
        }

        byte[]? md5 = null;
        if (Str(f, "md5Checksum") is { Length: 32 } hex)
        {
            try { md5 = Convert.FromHexString(hex); } catch (FormatException) { }
        }

        return new RemoteEntry
        {
            Id = id,
            Name = name,
            Size = long.TryParse(Str(f, "size"), out long size) ? size : null,
            Modified = modified,
            HashKind = md5 is null ? HashKind.None : HashKind.Md5,
            Hash = md5,
            ResourceKey = resourceKey,
        };
    }

    static bool IsGoogleHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && (u.Host is "docs.google.com" or "drive.google.com" || u.Host.EndsWith(".googleapis.com", StringComparison.Ordinal));

    static RemoteEntry Unavailable(string id, string name, string reason) =>
        new() { Id = id, Name = name, SkipReason = reason };

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
