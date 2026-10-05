using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CloudLink.Core;

namespace CloudLink.Providers;

/// <summary>OneDrive and SharePoint sharing links, through Microsoft Graph's shares API.</summary>
public sealed class OneDriveProvider(HttpClient api, ITokenSource auth, string graphBase = "https://graph.microsoft.com/v1.0")
    : ICloudProvider
{
    const string Select = "$select=id,name,size,file,folder,package,remoteItem,parentReference,fileSystemInfo,lastModifiedDateTime";

    // Set when a shared drive refuses direct addressing and items must be reached through the share.
    volatile bool _viaShareOnly;

    public Func<int, TimeSpan?, TimeSpan>? Backoff { get; set; }

    public string Name => "OneDrive";

    public bool Matches(Uri link)
    {
        string h = link.Host.ToLowerInvariant();
        return h == "1drv.ms" || h == "onedrive.com" || h.EndsWith(".onedrive.com")
            || h == "onedrive.live.com" || h.EndsWith(".live.com") && h.Contains("onedrive")
            || h.EndsWith(".sharepoint.com") || h.EndsWith(".sharepoint.cn") || h.EndsWith(".sharepoint.us");
    }

    public static string EncodeShareLink(string link) =>
        "u!" + Convert.ToBase64String(Encoding.UTF8.GetBytes(link)).TrimEnd('=').Replace('/', '_').Replace('+', '-');

    public void OnUnauthorized() => auth.Invalidate();

    public async Task<RemoteEntry> ResolveAsync(Uri link, CancellationToken ct)
    {
        string token = EncodeShareLink(link.OriginalString);
        // redeemSharingLink is what opening the link in a browser does: it gives the
        // signed-in account lasting access, which the folder listing below relies on.
        using var doc = await GetAsync($"{graphBase}/shares/{token}/driveItem?{Select}", ct, "redeemSharingLink");
        return Parse(doc.RootElement, token) with { IsShareRoot = true };
    }

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(RemoteEntry folder, CancellationToken ct)
    {
        string token = folder.ShareToken!;
        if (folder.IsShareRoot || _viaShareOnly || folder.DriveId is null)
            return await ListPagesAsync(ChildrenUrl(folder, viaShare: true), token, ct);
        try
        {
            return await ListPagesAsync(ChildrenUrl(folder, viaShare: false), token, ct);
        }
        catch (CloudException ex) when (ex.StatusCode is 403 or 404)
        {
            var result = await ListPagesAsync(ChildrenUrl(folder, viaShare: true), token, ct);
            _viaShareOnly = true;
            return result;
        }
    }

    string ChildrenUrl(RemoteEntry f, bool viaShare) => ItemUrl(f, viaShare) + "/children?$top=200&" + Select;

    string ItemUrl(RemoteEntry e, bool viaShare)
    {
        if (e.IsShareRoot) return $"{graphBase}/shares/{e.ShareToken}/driveItem";
        return viaShare || e.DriveId is null
            ? $"{graphBase}/shares/{e.ShareToken}/items/{Uri.EscapeDataString(e.Id)}"
            : $"{graphBase}/drives/{Uri.EscapeDataString(e.DriveId)}/items/{Uri.EscapeDataString(e.Id)}";
    }

    async Task<IReadOnlyList<RemoteEntry>> ListPagesAsync(string url, string token, CancellationToken ct)
    {
        var result = new List<RemoteEntry>();
        string? next = url;
        while (next is not null)
        {
            using var doc = await GetAsync(next, ct);
            if (doc.RootElement.TryGetProperty("value", out var items))
                foreach (var item in items.EnumerateArray())
                    result.Add(Parse(item, token));
            next = doc.RootElement.TryGetProperty("@odata.nextLink", out var n) ? n.GetString() : null;
            // The next page must be on the same host; the access token is not sent anywhere else.
            if (next is not null && !(Uri.TryCreate(next, UriKind.Absolute, out var nextUri)
                && nextUri.Scheme == new Uri(graphBase).Scheme && nextUri.Authority == new Uri(graphBase).Authority))
                throw new CloudException("OneDrive pointed to an unexpected address for the next page of the listing.", false);
        }
        return result;
    }

    public async Task<HttpRequestMessage> CreateDownloadRequestAsync(RemoteEntry file, CancellationToken ct)
    {
        // Graph answers with a redirect to a short-lived download address; asking again
        // on every attempt means an expired address never matters.
        var req = new HttpRequestMessage(HttpMethod.Get, ItemUrl(file, _viaShareOnly) + "/content");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await auth.GetAccessTokenAsync(ct));
        return req;
    }

    public async Task<string> GetAccountNameAsync(CancellationToken ct)
    {
        using var doc = await GetAsync($"{graphBase}/me?$select=userPrincipalName,mail,displayName", ct);
        var r = doc.RootElement;
        return Str(r, "mail") ?? Str(r, "userPrincipalName") ?? Str(r, "displayName") ?? "signed in";
    }

    Task<JsonDocument> GetAsync(string url, CancellationToken ct, string? prefer = null) =>
        Api.GetJsonAsync(api, async c =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await auth.GetAccessTokenAsync(c));
            if (prefer is not null) req.Headers.TryAddWithoutValidation("Prefer", prefer);
            return req;
        }, auth.Invalidate, ct, Backoff);

    internal static RemoteEntry Parse(JsonElement item, string shareToken)
    {
        // An item that only points into someone else's drive carries the real identity in remoteItem.
        var src = item.TryGetProperty("remoteItem", out var remote) && remote.ValueKind == JsonValueKind.Object ? remote : item;

        string id = Str(src, "id") ?? Str(item, "id") ?? throw new CloudException("OneDrive returned an item without an id.", false);
        string name = Str(item, "name") ?? Str(src, "name") ?? id;
        bool isFile = src.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.Object;
        bool isFolder = !isFile && (src.TryGetProperty("folder", out _) || src.TryGetProperty("package", out _));

        string? driveId = src.TryGetProperty("parentReference", out var parent) ? Str(parent, "driveId") : null;
        long? size = src.TryGetProperty("size", out var sz) && sz.TryGetInt64(out long l) ? l : null;

        DateTimeOffset? modified = null;
        string? stamp = (src.TryGetProperty("fileSystemInfo", out var fsi) ? Str(fsi, "lastModifiedDateTime") : null)
            ?? Str(src, "lastModifiedDateTime");
        if (stamp is not null && DateTimeOffset.TryParse(stamp, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            modified = parsed;

        var kind = HashKind.None;
        byte[]? hash = null;
        if (isFile && file.TryGetProperty("hashes", out var hashes) && hashes.ValueKind == JsonValueKind.Object)
        {
            try
            {
                if (Str(hashes, "sha256Hash") is { Length: 64 } sha256) (kind, hash) = (HashKind.Sha256, Convert.FromHexString(sha256));
                else if (Str(hashes, "quickXorHash") is { Length: > 0 } quick) (kind, hash) = (HashKind.QuickXor, Convert.FromBase64String(quick));
                else if (Str(hashes, "sha1Hash") is { Length: 40 } sha1) (kind, hash) = (HashKind.Sha1, Convert.FromHexString(sha1));
            }
            catch (FormatException)
            {
                (kind, hash) = (HashKind.None, null);
            }
        }

        return new RemoteEntry
        {
            Id = id,
            Name = name,
            IsFolder = isFolder,
            Size = isFolder ? null : size,
            Modified = modified,
            HashKind = kind,
            Hash = hash,
            DriveId = driveId,
            ShareToken = shareToken,
            SkipReason = isFile || isFolder ? null : "This kind of item has no file content",
        };
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
