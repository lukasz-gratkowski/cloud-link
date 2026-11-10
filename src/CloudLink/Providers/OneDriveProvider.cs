using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CloudLink.Core;

namespace CloudLink.Providers;

/// <summary>
/// OneDrive and SharePoint sharing links. A work or school link shared with anyone who has it is opened
/// as a guest, the way a private browser window opens it, with no sign-in; every other link goes through
/// Microsoft Graph's shares API with the signed-in account.
/// </summary>
public sealed class OneDriveProvider(HttpClient api, ITokenSource auth, string graphBase = "https://graph.microsoft.com/v1.0")
    : ICloudProvider
{
    const string Select = "$select=id,name,size,file,folder,package,remoteItem,parentReference,fileSystemInfo,lastModifiedDateTime";

    // Set when a shared drive refuses direct addressing and items must be reached through the share.
    volatile bool _viaShareOnly;

    // Guest visits, by share token, for the links that opened without an account.
    readonly ConcurrentDictionary<string, SharePointGuest> _guests = new();

    public Func<int, TimeSpan?, TimeSpan>? Backoff { get; set; }

    /// <summary>Sent on guest visits. SharePoint wants it to begin like a Windows browser's.</summary>
    public string GuestUserAgent { get; init; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CloudLink";

    /// <summary>Hosts whose links may open without an account: OneDrive for work or school and SharePoint.</summary>
    public Func<Uri, bool> GuestHosts { get; init; } = IsWorkOrSchoolHost;

    public static bool IsWorkOrSchoolHost(Uri link)
    {
        string h = link.Host.ToLowerInvariant();
        return link.Scheme == Uri.UriSchemeHttps
            && (h.EndsWith(".sharepoint.com") || h.EndsWith(".sharepoint.cn") || h.EndsWith(".sharepoint.us"));
    }

    /// <summary>Whether the link is worth trying without a sign-in before asking for one.</summary>
    public bool MayOpenWithoutSignIn(Uri link) => GuestHosts(link);

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
        if (_guests.TryRemove(token, out var earlier)) earlier.Dispose();

        if (GuestHosts(link))
        {
            (SharePointGuest Guest, JsonDocument Item)? opened;
            try
            {
                opened = await SharePointGuest.TryOpenAsync(link, token, Select, GuestUserAgent, GuestHosts, Backoff, ct);
            }
            catch (CloudException ex) when (auth.IsSignedIn && !ex.Transient && ex.StatusCode != 404)
            {
                // An answer that says nothing about the link itself. With nobody signed in it is the
                // only word there is; with an account, the signed-in route may still open the link.
                Log.Write("Opening the link as a guest failed, so the signed-in account is tried: " + ex.Message);
                opened = null;
            }
            if (opened is { Guest: var guest, Item: var item })
            {
                using (item)
                {
                    RemoteEntry root;
                    try
                    {
                        root = Parse(item.RootElement, token) with { IsShareRoot = true };
                    }
                    catch
                    {
                        guest.Dispose();
                        throw;
                    }
                    _guests[token] = guest;
                    Log.Write("Opened the link as a guest, without a sign-in.");
                    return root;
                }
            }
        }

        try
        {
            // redeemSharingLink is what opening the link in a browser does: it gives the
            // signed-in account lasting access, which the folder listing below relies on.
            using var doc = await GetAsync($"{graphBase}/shares/{token}/driveItem?{Select}", ct, "redeemSharingLink");
            return Parse(doc.RootElement, token) with { IsShareRoot = true };
        }
        catch (CloudException ex) when (ex.StatusCode is >= 300 and < 400 && GuestHosts(link))
        {
            // What Graph does with a work or school link it will not open for this account: it sends
            // the request on to SharePoint, often without saying where. By now the link has also been
            // tried as a guest, so both statements below are known, and why is not.
            throw new CloudException(
                $"Microsoft would not open this work or school link for the signed-in account (HTTP {ex.StatusCode}). "
                + "It did not open without an account either, so it may be limited to certain people, need a password, or have expired. "
                + "Through a sign-in, an organisation's link opens only for accounts that organisation allows, "
                + "which a personal Microsoft account usually is not.",
                false) { StatusCode = ex.StatusCode };
        }
    }

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(RemoteEntry folder, CancellationToken ct)
    {
        string token = folder.ShareToken!;
        if (_guests.TryGetValue(token, out var guest))
            return await ListPagesAsync(GuestItemUrl(guest, folder) + "/children?$top=200&" + Select, token,
                guest.ApiBase, (url, c) => guest.GetAsync(url, Backoff, c), ct);
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

    /// <summary>A guest reaches items through the drive that holds them; the share's item path is closed to it.</summary>
    static string GuestItemUrl(SharePointGuest guest, RemoteEntry e, bool rootViaShare = true)
    {
        if (e.IsShareRoot && (rootViaShare || e.DriveId is null)) return $"{guest.ApiBase}/shares/{e.ShareToken}/driveItem";
        if (e.DriveId is null) throw new CloudException($"SharePoint did not say which drive holds \"{e.Name}\".", false);
        return $"{guest.ApiBase}/drives/{Uri.EscapeDataString(e.DriveId)}/items/{Uri.EscapeDataString(e.Id)}";
    }

    Task<IReadOnlyList<RemoteEntry>> ListPagesAsync(string url, string token, CancellationToken ct) =>
        ListPagesAsync(url, token, graphBase, (u, c) => GetAsync(u, c), ct);

    static async Task<IReadOnlyList<RemoteEntry>> ListPagesAsync(string url, string token, string apiBase,
        Func<string, CancellationToken, Task<JsonDocument>> get, CancellationToken ct)
    {
        var result = new List<RemoteEntry>();
        var expected = new Uri(apiBase);
        string? next = url;
        while (next is not null)
        {
            using var doc = await get(next, ct);
            if (doc.RootElement.TryGetProperty("value", out var items))
                foreach (var item in items.EnumerateArray())
                    result.Add(Parse(item, token));
            next = doc.RootElement.TryGetProperty("@odata.nextLink", out var n) ? n.GetString() : null;
            // The next page must be on the same host; the access token or guest cookie is not sent anywhere else.
            if (next is not null && !(Uri.TryCreate(next, UriKind.Absolute, out var nextUri)
                && nextUri.Scheme == expected.Scheme && nextUri.Authority == expected.Authority))
                throw new CloudException("OneDrive pointed to an unexpected address for the next page of the listing.", false);
        }
        return result;
    }

    public async Task<HttpRequestMessage> CreateDownloadRequestAsync(RemoteEntry file, CancellationToken ct)
    {
        if (file.ShareToken is { } token && _guests.TryGetValue(token, out var guest))
        {
            // The guest asks for a short-lived download address, which needs no cookie and takes a Range;
            // a new one on every attempt means an expired address never matters. A link to a single file
            // can ask through its drive or through the share; the second is the fallback.
            const string ask = "?$select=id,@content.downloadUrl";
            string? address;
            try
            {
                using var doc = await guest.GetAsync(GuestItemUrl(guest, file, rootViaShare: false) + ask, Backoff, ct);
                address = Str(doc.RootElement, "@content.downloadUrl");
            }
            catch (CloudException ex) when (file.IsShareRoot && file.DriveId is not null && ex.StatusCode is 403 or 404)
            {
                address = null;
            }
            if (address is null && file.IsShareRoot && file.DriveId is not null)
            {
                using var doc = await guest.GetAsync(GuestItemUrl(guest, file) + ask, Backoff, ct);
                address = Str(doc.RootElement, "@content.downloadUrl");
            }
            // Not worth another try: a link that lets people look but not download (or an item that is
            // not a file) is answered like this every time.
            if (!Uri.TryCreate(address, UriKind.Absolute, out var url)
                || url.Scheme != Uri.UriSchemeHttps && url.Scheme != new Uri(guest.ApiBase).Scheme)
                throw new CloudException(
                    "SharePoint did not give a download address for this file. The link may not allow downloads.",
                    false) { WithoutAccount = true };
            return new HttpRequestMessage(HttpMethod.Get, url);
        }

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
