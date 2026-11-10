using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CloudLink.Core;

public class CloudException(string message, bool transient, TimeSpan? retryAfter = null) : Exception(message)
{
    public bool Transient { get; } = transient;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public int StatusCode { get; init; }

    /// <summary>The request was made as a guest, so no signed-in account had a part in the answer.</summary>
    public bool WithoutAccount { get; init; }

    /// <summary>The failure is about the sign-in itself (getting or renewing a token), not about a link or a file.</summary>
    public bool AtSignIn { get; init; }
}

public sealed class SignInRequiredException(string service)
    : Exception($"Sign in to {service} to continue.")
{
    public string Service { get; } = service;
}

public static class Errors
{
    public static CloudException FromResponse(HttpResponseMessage resp, string body)
    {
        int code = (int)resp.StatusCode;
        string detail = ExtractMessage(body) ?? resp.ReasonPhrase ?? "request failed";
        bool throttled = body.Contains("rateLimitExceeded", StringComparison.OrdinalIgnoreCase)
            || body.Contains("activityLimitReached", StringComparison.OrdinalIgnoreCase)
            || body.Contains("throttled", StringComparison.OrdinalIgnoreCase);
        bool transient = code is 401 or 408 or 429 || code >= 500 || (code == 403 && throttled);

        TimeSpan? retryAfter = resp.Headers.RetryAfter?.Delta;
        if (retryAfter is null && resp.Headers.RetryAfter?.Date is DateTimeOffset when)
            retryAfter = when - DateTimeOffset.UtcNow;
        if (retryAfter is { } r && (r < TimeSpan.Zero || r > TimeSpan.FromMinutes(10)))
            retryAfter = r < TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromMinutes(10);

        string lead = code switch
        {
            401 => "The service did not accept the sign-in",
            403 => "Access denied",
            404 => "Not found, or the link no longer works",
            429 => "The service asked to slow down",
            >= 300 and < 400 => "The service sent the request elsewhere instead of answering",
            _ => "The service reported an error",
        };
        return new CloudException($"{lead}: {detail} (HTTP {code})", transient, retryAfter) { StatusCode = code };
    }

    /// <summary>
    /// One log line with what Microsoft's and Google's support ask for when a request goes wrong.
    /// Addresses are cut to host and path, and a share's encoded link is left out: a query can carry a
    /// short-lived key, and a link shared with anyone is a key in itself.
    /// </summary>
    public static string Diagnostics(HttpResponseMessage resp)
    {
        static string? Header(HttpResponseMessage r, string name) =>
            r.Headers.TryGetValues(name, out var v) ? string.Join(",", v) : null;
        var parts = new List<string> { $"HTTP {(int)resp.StatusCode} from {resp.RequestMessage?.RequestUri?.Host}" };
        if (resp.Headers.Location is { } loc)
        {
            string where = loc.IsAbsoluteUri ? loc.Host + loc.AbsolutePath : loc.OriginalString.Split('?', '#')[0];
            parts.Add("location " + Regex.Replace(where, "u(?:!|%21)[A-Za-z0-9_-]+", "u!..."));
        }
        foreach (string name in new[] { "request-id", "client-request-id", "x-ms-ags-diagnostic", "SPRequestGuid", "x-msedge-ref" })
            if (Header(resp, name) is { } value) parts.Add($"{name} {value}");
        return string.Join("; ", parts);
    }

    static string? ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String)
                return FirstLine(d.GetString());
            if (!root.TryGetProperty("error", out var err) && !root.TryGetProperty("odata.error", out err)) return null;
            if (err.ValueKind == JsonValueKind.String) return err.GetString();
            if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m))
            {
                // SharePoint's older answers carry the text one level down: {"lang":"en-US","value":"..."}.
                if (m.ValueKind == JsonValueKind.Object && m.TryGetProperty("value", out var value)) m = value;
                if (m.ValueKind == JsonValueKind.String) return FirstLine(m.GetString());
            }
        }
        catch (JsonException) { }
        return null;
    }

    static string? FirstLine(string? s)
    {
        if (s is null) return null;
        int i = s.IndexOfAny(['\r', '\n']);
        return i < 0 ? s : s[..i];
    }

    public static bool IsDiskFull(Exception ex) =>
        ex is IOException io && (io.HResult & 0xFFFF) is 112 or 39;

    /// <summary>The service could not be reached at all: no route, no DNS, connection refused.</summary>
    public static bool IsUnreachable(Exception ex) => ex is HttpRequestException
        { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError };

    /// <summary>Whether trying again can help. The caller has already ruled out a user cancel.</summary>
    public static bool IsTransient(Exception ex) => ex switch
    {
        CloudException c => c.Transient,
        SignInRequiredException => false,
        HttpRequestException => true,
        IOException io => !IsDiskFull(io),
        OperationCanceledException => true,
        TimeoutException => true,
        _ => false,
    };

    public static string Describe(Exception ex) => ex switch
    {
        CloudException or SignInRequiredException => ex.Message,
        OperationCanceledException or TimeoutException => "The connection stalled",
        HttpRequestException h => "Network problem: " + (h.InnerException?.Message ?? h.Message),
        IOException io when IsDiskFull(io) => "The disk is full",
        IOException io => "Connection or file problem: " + io.Message,
        UnauthorizedAccessException => "Windows denied access to the destination file",
        _ => ex.Message,
    };

    public static TimeSpan Backoff(int failures, TimeSpan? retryAfter)
    {
        if (retryAfter is { } r) return r + TimeSpan.FromMilliseconds(Random.Shared.Next(200, 1200));
        double seconds = Math.Min(60, Math.Pow(2, Math.Min(failures, 6)));
        return TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
    }
}

public static class Api
{
    /// <summary>GET a JSON document, retrying what can be retried.</summary>
    public static async Task<JsonDocument> GetJsonAsync(
        HttpClient http,
        Func<CancellationToken, Task<HttpRequestMessage>> build,
        Action onUnauthorized,
        CancellationToken ct,
        Func<int, TimeSpan?, TimeSpan>? backoff = null)
    {
        backoff ??= Errors.Backoff;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var req = await build(ct);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(100));
                using var resp = await http.SendAsync(req, cts.Token);
                string body = await resp.Content.ReadAsStringAsync(cts.Token);
                if (resp.IsSuccessStatusCode) return JsonDocument.Parse(body);
                if ((int)resp.StatusCode == 401) onUnauthorized();
                else if ((int)resp.StatusCode != 429) Log.Write(Errors.Diagnostics(resp));
                throw Errors.FromResponse(resp, body);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt < 6 && Errors.IsTransient(ex))
            {
                await Task.Delay(backoff(attempt, (ex as CloudException)?.RetryAfter), ct);
            }
        }
    }
}
