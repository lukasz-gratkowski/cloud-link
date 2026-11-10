using System.Net;
using System.Net.Http;
using System.Text.Json;
using CloudLink.Core;

namespace CloudLink.Providers;

/// <summary>
/// What a private browser window gets when it opens an "Anyone with the link" address on OneDrive for
/// work or school or SharePoint: a guest visit, held in a cookie, that the OneDrive API on the same
/// host accepts for listing the shared folder and for handing out download addresses. No account and
/// no sign-in are involved, so this works whoever runs CloudLink.
/// </summary>
sealed class SharePointGuest : IDisposable
{
    const int MaxHops = 10;
    const string NoLongerOpens =
        "The link no longer opens without an account. It may have been switched off or limited to certain people.";
    const string Unknown = "SharePoint does not know this link. It may have been removed or have expired (HTTP 404)";

    readonly HttpClient _http;
    readonly CookieContainer _cookies = new();
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly Uri _link;
    readonly Func<Uri, bool> _allowedHost;
    volatile bool _stale;
    // Set once the visit cannot be renewed; every later request then fails at once instead of trying again.
    volatile CloudException? _closed;

    /// <summary>The OneDrive API on the host the link landed on, such as https://contoso-my.sharepoint.com/_api/v2.0.</summary>
    public string ApiBase { get; private set; } = "";

    /// <summary>
    /// Whether the visit left a guest session behind. A link SharePoint lets a guest into always does;
    /// a link it no longer knows gets a page of SharePoint's own, with status 200 and no cookie.
    /// </summary>
    bool HasSession => _cookies.GetAllCookies().Any(c => !c.Expired);

    SharePointGuest(Uri link, string userAgent, Func<Uri, bool> allowedHost)
    {
        _link = link;
        _allowedHost = allowedHost;
        _http = new HttpClient(new SocketsHttpHandler
        {
            CookieContainer = _cookies,
            // Redirects are followed by hand in VisitAsync, so that no host other than SharePoint is asked anything.
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        }) { Timeout = Timeout.InfiniteTimeSpan };
        // SharePoint answers a guest's API calls only from something that announces itself as a
        // Windows browser; the rest of the string still says what is asking.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    /// <summary>
    /// Opens the link as a guest and checks that the API lets the guest see the shared item. Returns
    /// the visit and the item, or null when the link does not open without an account.
    /// </summary>
    public static async Task<(SharePointGuest Guest, JsonDocument Item)?> TryOpenAsync(
        Uri link, string shareToken, string select, string userAgent, Func<Uri, bool> allowedHost,
        Func<int, TimeSpan?, TimeSpan>? backoff, CancellationToken ct)
    {
        var guest = new SharePointGuest(link, userAgent, allowedHost);
        try
        {
            if (await guest.VisitAsync(backoff, ct) is not { } apiBase) { guest.Dispose(); return null; }
            guest.ApiBase = apiBase;
            using var resp = await guest.SendOnceAsync($"{apiBase}/shares/{shareToken}/driveItem?{select}", backoff, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            int code = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode)
            {
                try
                {
                    var item = JsonDocument.Parse(body);
                    if (item.RootElement.ValueKind == JsonValueKind.Object) return (guest, item);
                    item.Dispose();
                    throw new JsonException("not an object");
                }
                catch (JsonException)
                {
                    Log.Write($"The link did not open as a guest: the OneDrive API on {resp.RequestMessage?.RequestUri?.Host} answered with something other than the shared item.");
                    throw new CloudException("SharePoint answered with a page instead of the shared item.", false) { WithoutAccount = true };
                }
            }
            bool session = guest.HasSession;
            guest.Dispose();
            Log.Write($"The link did not open as a guest: HTTP {code} from the OneDrive API on {resp.RequestMessage?.RequestUri?.Host}"
                + (session ? "." : ", and the visit left no guest session."));
            if (code != 401) Log.Write(Errors.Diagnostics(resp));
            // SharePoint showed a page of its own instead of letting the guest in, and its API says the
            // link is not there (a link that exists but needs an account gets 401). No account finds it either.
            if (code == 404 && !session)
                throw new CloudException(Unknown, false) { StatusCode = 404, WithoutAccount = true };
            // Refused, unknown to a guest, or sent elsewhere: the signed-in route may still open it.
            if (code is 401 or 403 or 404 or >= 300 and < 400) return null;
            throw AsGuest(Errors.FromResponse(resp, body));
        }
        catch
        {
            guest.Dispose();
            throw;
        }
    }

    /// <summary>GET a JSON document as the guest. A visit that has expired is renewed on the way.</summary>
    public async Task<JsonDocument> GetAsync(string url, Func<int, TimeSpan?, TimeSpan>? backoff, CancellationToken ct)
    {
        bool renewed = false;
        try
        {
            return await Api.GetJsonAsync(_http, async c =>
            {
                if (_closed is not null) throw Closed();
                if (_stale)
                {
                    await RenewAsync(backoff, c);
                    renewed = true;
                }
                return Request(url);
            }, () => _stale = true, ct, backoff);
        }
        catch (CloudException ex) when (ex.StatusCode == 401 && renewed && !ct.IsCancellationRequested)
        {
            // Renewed visits were turned away as well, so the rest of the run is spared the same wait.
            // (A 401 with no renewal behind it, the last of a row of other failures, is left for another try.)
            _closed = new CloudException(
                "SharePoint stopped letting CloudLink in as a guest. The link may have been switched off or limited to certain people.",
                false) { StatusCode = 401 };
            throw Closed();
        }
        catch (CloudException ex) when (!ex.WithoutAccount)
        {
            throw AsGuest(ex);
        }
        catch (JsonException)
        {
            throw new CloudException("SharePoint answered with a page instead of the item.", false) { WithoutAccount = true };
        }
    }

    /// <summary>
    /// Follows the link the way a browser does, which leaves the guest cookie behind, but only as far as
    /// SharePoint goes: an address on any other host, such as the Microsoft sign-in page that a link for
    /// an organisation's own people leads to, is not requested. Returns the API address on the host the
    /// link landed on, or null when the link does not open without an account.
    /// </summary>
    async Task<string?> VisitAsync(Func<int, TimeSpan?, TimeSpan>? backoff, CancellationToken ct)
    {
        backoff ??= Errors.Backoff;
        if (!_allowedHost(_link)) return null;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(100));
                var at = _link;
                for (int hop = 0; ; hop++)
                {
                    using var resp = await _http.GetAsync(at, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    int code = (int)resp.StatusCode;
                    if (code is 408 or 429 || code >= 500) throw Errors.FromResponse(resp, "");
                    if (code is >= 300 and < 400 && resp.Headers.Location is { } location)
                    {
                        var next = location.IsAbsoluteUri ? location : new Uri(at, location);
                        if (!_allowedHost(next) || hop >= MaxHops)
                        {
                            Log.Write($"The link did not open as a guest: it leads on to {next.Host}.");
                            return null;
                        }
                        at = next;
                        continue;
                    }
                    if (!resp.IsSuccessStatusCode)
                    {
                        Log.Write($"The link did not open as a guest: {at.Host} answered HTTP {code}.");
                        if (code == 404)
                            throw new CloudException("Not found, or the link no longer works (HTTP 404)", false) { StatusCode = 404, WithoutAccount = true };
                        return null;
                    }
                    return at.GetLeftPart(UriPartial.Authority) + "/_api/v2.0";
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt < 4 && Errors.IsTransient(ex))
            {
                await Task.Delay(backoff(attempt, (ex as CloudException)?.RetryAfter), ct);
            }
        }
    }

    /// <summary>Starts the visit again with no cookies, as a fresh private window would.</summary>
    async Task RenewAsync(Func<int, TimeSpan?, TimeSpan>? backoff, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_closed is not null) throw Closed();   // another request has just found the link closed
            if (!_stale) return;                       // another request has just renewed it
            foreach (Cookie c in _cookies.GetAllCookies()) c.Expired = true;
            string? landed;
            try
            {
                landed = await VisitAsync(backoff, ct);
            }
            catch (CloudException ex) when (!ex.Transient)
            {
                _closed = ex;
                throw;
            }
            // The cookie belongs to the host the link first landed on; a visit that ends anywhere else,
            // or leaves no session (a link removed in the meantime gets a page and no cookie), is not a renewal.
            if (landed != ApiBase || !HasSession)
            {
                _closed = new CloudException(NoLongerOpens, false) { StatusCode = 403 };
                throw Closed();
            }
            Log.Write("Renewed the guest visit to SharePoint.");
            _stale = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A new copy each time: several downloads may be told at once.</summary>
    CloudException Closed() => AsGuest(_closed!);

    /// <summary>The same failure, marked as having happened with no account involved.</summary>
    static CloudException AsGuest(CloudException ex) =>
        new(ex.Message, ex.Transient, ex.RetryAfter) { StatusCode = ex.StatusCode, WithoutAccount = true };

    async Task<HttpResponseMessage> SendOnceAsync(string url, Func<int, TimeSpan?, TimeSpan>? backoff, CancellationToken ct)
    {
        backoff ??= Errors.Backoff;
        for (int attempt = 1; ; attempt++)
        {
            HttpResponseMessage? resp = null;
            try
            {
                using var req = Request(url);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(100));
                resp = await _http.SendAsync(req, cts.Token);
                await resp.Content.LoadIntoBufferAsync(cts.Token);
                int code = (int)resp.StatusCode;
                if (code is 408 or 429 || code >= 500)
                    throw Errors.FromResponse(resp, await resp.Content.ReadAsStringAsync(cts.Token));
                return resp;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt < 6 && Errors.IsTransient(ex))
            {
                resp?.Dispose();
                await Task.Delay(backoff(attempt, (ex as CloudException)?.RetryAfter), ct);
            }
            catch
            {
                resp?.Dispose();
                throw;
            }
        }
    }

    static HttpRequestMessage Request(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/json");
        return req;
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
