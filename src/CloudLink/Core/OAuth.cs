using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloudLink.Core;

public sealed record OAuthConfig(
    string Service,
    string AuthorizeUrl,
    string TokenUrl,
    string ClientId,
    string? ClientSecret,
    string Scope,
    string RedirectHost,
    IReadOnlyDictionary<string, string> ExtraAuthorizeParams);

/// <summary>
/// Sign-in through the system browser (authorization code + PKCE, loopback redirect).
/// What is kept is the refresh token and the application ID it was issued to, encrypted for the
/// current Windows user.
/// </summary>
public sealed class OAuthSession(Func<OAuthConfig> config, string tokenFileName, HttpClient http) : ITokenSource
{
    /// <summary>What is kept on disk: the refresh token and the application it was issued to.</summary>
    sealed record Grant(string? ClientId, string RefreshToken);

    readonly SemaphoreSlim _gate = new(1, 1);
    readonly object _storeLock = new();
    volatile string? _access;
    DateTimeOffset _expires;
    Grant? _grant;
    bool _grantLoaded;

    string TokenFile => AppPaths.File(tokenFileName);

    /// <summary>True when there is a stored sign-in made with the application ID in use now.</summary>
    public bool IsSignedIn => UsableGrant(config()) is not null;

    public void Invalidate() => _access = null;

    public void SignOut()
    {
        _access = null;
        lock (_storeLock)
        {
            try
            {
                File.Delete(TokenFile);
                _grant = null;
                _grantLoaded = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still on disk: read it again next time rather than claim it is gone.
                _grantLoaded = false;
                Log.Write($"The stored {config().Service} sign-in could not be deleted: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Deletes a stored sign-in that was made through a different application ID than the one in use.
    /// It can never work again, so it should not stay on disk.
    /// </summary>
    public void ForgetIfForeign()
    {
        var cfg = config();
        if (cfg.ClientId.Length > 0 && LoadGrant() is { ClientId: { } owner } && !SameId(owner, cfg.ClientId)) SignOut();
    }

    static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // A sign-in made through one application is of no use to another, so a change of ID means signing in again.
    Grant? UsableGrant(OAuthConfig cfg)
    {
        var grant = LoadGrant();
        if (grant is null || cfg.ClientId.Length == 0) return null;
        return grant.ClientId is null || SameId(grant.ClientId, cfg.ClientId) ? grant : null;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_access is { } cached && DateTimeOffset.UtcNow < _expires - TimeSpan.FromMinutes(2)) return cached;
            var cfg = config();
            var grant = UsableGrant(cfg) ?? throw new SignInRequiredException(cfg.Service);
            var form = new Dictionary<string, string>
            {
                ["client_id"] = cfg.ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = grant.RefreshToken,
            };
            return await RedeemAsync(cfg, form, grant, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Opens the browser and waits for the user to finish signing in.</summary>
    public async Task SignInAsync(CancellationToken ct)
    {
        var cfg = config();
        if (cfg.ClientId.Length == 0)
            throw new CloudException($"{cfg.Service} sign-in is not set up yet. Open Settings to enter the application ID.", false);
        using var loopback = new LoopbackReceiver();
        string redirect = $"http://{cfg.RedirectHost}:{loopback.Port}";
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var query = new Dictionary<string, string>(cfg.ExtraAuthorizeParams)
        {
            ["client_id"] = cfg.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirect,
            ["scope"] = cfg.Scope,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
        };
        string url = cfg.AuthorizeUrl + "?" + string.Join("&",
            query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        Dictionary<string, string> reply;
        try
        {
            reply = await loopback.WaitAsync(state, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CloudException($"The {cfg.Service} sign-in was not finished in time.", false);
        }

        if (reply.TryGetValue("error", out string? error))
        {
            reply.TryGetValue("error_description", out string? description);
            throw new CloudException(DescribeSignInError(cfg.Service, error, description), false);
        }

        var form = new Dictionary<string, string>
        {
            ["client_id"] = cfg.ClientId,
            ["grant_type"] = "authorization_code",
            ["code"] = reply["code"],
            ["redirect_uri"] = redirect,
            ["code_verifier"] = verifier,
        };
        await _gate.WaitAsync(ct);
        try { await RedeemAsync(cfg, form, null, ct); }
        finally { _gate.Release(); }
        if (!IsSignedIn)
            throw new CloudException($"{cfg.Service} did not grant offline access; sign in again.", false);
    }

    /// <param name="refreshing">The stored sign-in being renewed, or null when redeeming a fresh sign-in.</param>
    async Task<string> RedeemAsync(OAuthConfig cfg, Dictionary<string, string> form, Grant? refreshing, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(cfg.ClientSecret)) form["client_secret"] = cfg.ClientSecret;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        using var resp = await http.PostAsync(cfg.TokenUrl, new FormUrlEncodedContent(form), cts.Token);
        string body = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var failure = Errors.FromResponse(resp, body);
            int status = (int)resp.StatusCode;
            if (refreshing is not null && NeedsNewSignIn(body, status, refreshing.ClientId is not null))
            {
                // The stored sign-in was revoked, expired, or belongs to another application; only a new sign-in fixes it.
                Log.Write($"{cfg.Service} refresh refused: {failure.Message}");
                SignOut();
                throw new SignInRequiredException(cfg.Service);
            }
            throw new CloudException(failure.Message, status is 408 or 429 or >= 500, failure.RetryAfter);
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        string access = root.GetProperty("access_token").GetString()!;
        int seconds = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out int s) ? s : 3600;
        if (root.TryGetProperty("refresh_token", out var r) && r.GetString() is { Length: > 0 } refresh)
            SaveGrant(new Grant(cfg.ClientId, refresh));
        else if (refreshing is { ClientId: null })
            SaveGrant(refreshing with { ClientId = cfg.ClientId });   // it worked, so it belongs to this application
        _expires = DateTimeOffset.UtcNow.AddSeconds(seconds);
        _access = access;
        return access;
    }

    /// <summary>
    /// Turns the error a sign-in page sends back into something a person can act on. The codes are the ones
    /// Microsoft uses when an organisation does not let its users approve an app themselves.
    /// </summary>
    internal static string DescribeSignInError(string service, string error, string? description)
    {
        string text = description ?? "";
        string[] needsAdmin = ["AADSTS90094", "AADSTS900941", "AADSTS90093", "AADSTS65001", "AADSTS900981"];
        if (needsAdmin.Any(code => text.Contains(code, StringComparison.OrdinalIgnoreCase)))
            return "Your organisation requires an administrator to approve CloudLink before this account can use it. " +
                "Ask your IT administrator to approve it. A link from a personal OneDrive that is open to anyone can also be opened with a personal Microsoft account.";
        if (error == "access_denied")
            return $"The {service} sign-in was cancelled, or the permission was not granted.";
        int detail = text.IndexOf("Trace ID", StringComparison.OrdinalIgnoreCase);
        if (detail > 0) text = text[..detail].Trim();   // the trace and correlation IDs mean nothing to the reader
        return $"{service} sign-in failed: {(text.Length > 0 ? text : error)}";
    }

    /// <summary>
    /// Whether a refused refresh means the stored sign-in itself is finished. For one known to belong to this
    /// application, only the answers that are about the sign-in count; an answer about the application (wrong
    /// secret, registration changed) leaves it alone, because it works again once that is put right. A sign-in
    /// stored by an older version carries no application ID, so any definite refusal ends it.
    /// </summary>
    static bool NeedsNewSignIn(string body, int status, bool boundToThisApplication)
    {
        string? error = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                error = e.GetString();
        }
        catch (JsonException) { }

        if (error is "invalid_grant" or "interaction_required") return true;
        return !boundToThisApplication && status is >= 400 and < 500 and not 408 and not 429;
    }

    Grant? LoadGrant()
    {
        lock (_storeLock)
        {
            if (_grantLoaded) return _grant;
            _grantLoaded = true;
            try
            {
                if (!File.Exists(TokenFile)) return _grant = null;
                string text = Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(File.ReadAllBytes(TokenFile), null, DataProtectionScope.CurrentUser));
                if (!text.StartsWith('{')) return _grant = new Grant(null, text);   // written before 1.2: the token alone
                using var doc = JsonDocument.Parse(text);
                string? token = doc.RootElement.TryGetProperty("refresh_token", out var t) ? t.GetString() : null;
                string? client = doc.RootElement.TryGetProperty("client_id", out var c) ? c.GetString() : null;
                return _grant = string.IsNullOrEmpty(token) ? null : new Grant(client, token);
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidOperationException)
            {
                return _grant = null;   // not something this program wrote, or written for another Windows user
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _grantLoaded = false;   // could not be read this time; try again on the next call
                return _grant = null;
            }
        }
    }

    void SaveGrant(Grant grant)
    {
        string json = JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["client_id"] = grant.ClientId,
            ["refresh_token"] = grant.RefreshToken,
        });
        byte[] secret = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
        lock (_storeLock)
        {
            string tmp = TokenFile + ".tmp";
            File.WriteAllBytes(tmp, secret);
            File.Move(tmp, TokenFile, true);
            _grant = grant;
            _grantLoaded = true;
        }
    }

    static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Catches the browser's redirect on a free loopback port, on both 127.0.0.1 and ::1.</summary>
sealed class LoopbackReceiver : IDisposable
{
    readonly List<TcpListener> _listeners = [];

    public int Port { get; }

    public LoopbackReceiver()
    {
        var v4 = new TcpListener(IPAddress.Loopback, 0);
        v4.Start();
        _listeners.Add(v4);
        Port = ((IPEndPoint)v4.LocalEndpoint).Port;
        try
        {
            var v6 = new TcpListener(IPAddress.IPv6Loopback, Port);
            v6.Start();
            _listeners.Add(v6);
        }
        catch (SocketException) { }
    }

    public async Task<Dictionary<string, string>> WaitAsync(string expectedState, CancellationToken ct)
    {
        var done = new TaskCompletionSource<Dictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => done.TrySetCanceled(ct));
        foreach (var listener in _listeners) _ = AcceptLoopAsync(listener, expectedState, done, ct);
        return await done.Task;
    }

    static async Task AcceptLoopAsync(TcpListener listener, string expectedState,
        TaskCompletionSource<Dictionary<string, string>> done, CancellationToken ct)
    {
        try
        {
            while (!done.Task.IsCompleted)
            {
                // Each connection gets its own task and a short deadline, so one that connects
                // and says nothing (a browser warming up, another program) cannot hold up the real one.
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = ServeAsync(client, expectedState, done, ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
        {
        }
    }

    static async Task ServeAsync(TcpClient client, string expectedState,
        TaskCompletionSource<Dictionary<string, string>> done, CancellationToken outer)
    {
        try
        {
            using (client)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(outer);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                var ct = deadline.Token;
                await using var stream = client.GetStream();
                var query = ParseQuery(await ReadRequestLineAsync(stream, ct));
                bool ours = (query.ContainsKey("code") || query.ContainsKey("error"))
                    && query.TryGetValue("state", out string? state) && state == expectedState;
                string page = ours
                    ? (query.ContainsKey("code")
                        ? "<h2>Signed in</h2><p>You can close this tab and go back to CloudLink.</p>"
                        : "<h2>Sign-in did not complete</h2><p>Go back to CloudLink for details.</p>")
                    : "";
                byte[] bodyBytes = Encoding.UTF8.GetBytes(
                    $"<!doctype html><meta charset=utf-8><title>CloudLink</title><body style=\"font-family:Segoe UI,sans-serif;margin:3em\">{page}</body>");
                string head = $"HTTP/1.1 {(ours ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\n" +
                    $"Content-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
                await stream.WriteAsync(bodyBytes, ct);
                await stream.FlushAsync(ct);
                if (ours) done.TrySetResult(query);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
        {
        }
    }

    static async Task<string> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        int used = 0;
        while (used < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(used), ct);
            if (n == 0) break;
            used += n;
            string text = Encoding.ASCII.GetString(buffer, 0, used);
            int eol = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (eol >= 0) return text[..eol];
        }
        return "";
    }

    static Dictionary<string, string> ParseQuery(string requestLine)
    {
        var result = new Dictionary<string, string>();
        string[] parts = requestLine.Split(' ');
        if (parts.Length < 2) return result;
        int q = parts[1].IndexOf('?');
        if (q < 0) return result;
        foreach (string pair in parts[1][(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = eq < 0 ? pair : pair[..eq];
            string value = eq < 0 ? "" : pair[(eq + 1)..];
            result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        return result;
    }

    public void Dispose()
    {
        foreach (var listener in _listeners) listener.Stop();
    }
}
