using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CloudLink.Tests;

/// <summary>A local HTTP server whose routes the tests script, including misbehaviour.</summary>
public sealed class FakeServer : IDisposable
{
    readonly HttpListener _listener = new();
    readonly ConcurrentDictionary<string, Func<HttpListenerContext, Task>> _routes = new();

    public string Base { get; }
    public ConcurrentQueue<string> Requests { get; } = new();

    public FakeServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Base = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(Base + "/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public void Route(string pathAndQuery, Func<HttpListenerContext, Task> handler) => _routes[pathAndQuery] = handler;

    public void Json(string pathAndQuery, string json) => Route(pathAndQuery, ctx => WriteAsync(ctx, 200, json, "application/json"));

    /// <summary>
    /// Serves <paramref name="content"/> with Range support. <paramref name="misbehave"/> gets the
    /// request number (1-based) and may return a status to fail with, or a byte count after which
    /// the connection is cut.
    /// </summary>
    public void File(string path, byte[] content, Func<int, Fault?>? misbehave = null, bool ranges = true)
    {
        int count = 0;
        Route(path, async ctx =>
        {
            var fault = misbehave?.Invoke(Interlocked.Increment(ref count));
            if (fault?.Status is int status)
            {
                if (fault.RetryAfterSeconds is int ra) ctx.Response.Headers["Retry-After"] = ra.ToString();
                await WriteAsync(ctx, status, "{\"error\":{\"message\":\"scripted failure\"}}", "application/json");
                return;
            }

            long from = 0;
            string? range = ranges ? ctx.Request.Headers["Range"] : null;
            if (range is not null)
            {
                from = long.Parse(range["bytes=".Length..].TrimEnd('-'));
                if (from >= content.Length)
                {
                    ctx.Response.StatusCode = 416;
                    ctx.Response.Headers["Content-Range"] = $"bytes */{content.Length}";
                    ctx.Response.Close();
                    return;
                }
                ctx.Response.StatusCode = 206;
                ctx.Response.Headers["Content-Range"] = $"bytes {from}-{content.Length - 1}/{content.Length}";
            }

            byte[] body = fault?.Corrupt == true ? content.Select(b => (byte)(b ^ 0x5A)).ToArray() : content;
            ctx.Response.ContentLength64 = content.Length - from;
            int toSend = (int)(content.Length - from);
            if (fault?.CutAfter is int cut && cut < toSend)
            {
                await ctx.Response.OutputStream.WriteAsync(body.AsMemory((int)from, cut));
                await ctx.Response.OutputStream.FlushAsync();
                if (fault.Hang) await Task.Delay(TimeSpan.FromSeconds(8));
                ctx.Response.Abort();
                return;
            }
            await ctx.Response.OutputStream.WriteAsync(body.AsMemory((int)from, toSend));
            ctx.Response.Close();
        });
    }

    public static async Task WriteAsync(HttpListenerContext ctx, int status, string body, string type)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = type;
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = Task.Run(async () =>
            {
                string key = ctx.Request.Url!.PathAndQuery;
                Requests.Enqueue($"{key} range={ctx.Request.Headers["Range"]} auth={ctx.Request.Headers["Authorization"]}");
                try
                {
                    if (_routes.TryGetValue(key, out var handler) || _routes.TryGetValue(ctx.Request.Url.AbsolutePath, out handler))
                        await handler(ctx);
                    else
                        await WriteAsync(ctx, 404, "{\"error\":{\"message\":\"no route " + key + "\"}}", "application/json");
                }
                catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException) { }
            });
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
    }
}

public sealed record Fault(int? Status = null, int? CutAfter = null, bool Corrupt = false, int? RetryAfterSeconds = null, bool Hang = false);
