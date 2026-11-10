using System.Security.Cryptography;
using System.Text;
using CloudLink.Core;
using CloudLink.Providers;

namespace CloudLink.Tests;

sealed class FakeTokens : ITokenSource
{
    public int Version = 1;
    public bool SignedIn = true;
    public bool IsSignedIn => SignedIn;
    public Task<string> GetAccessTokenAsync(CancellationToken ct) => Task.FromResult("token" + Version);
    public void Invalidate() => Version++;
}

// AppPaths.Root is one value for the whole process, and every test here points it at a folder of its own
// that is deleted afterwards. Classes that do so share a collection, so that they do not run side by side.
[Collection("AppPaths.Root")]
public sealed class ProviderTests : IDisposable
{
    readonly FakeServer _server = new();
    readonly HttpClient _http = new();
    readonly string _dir = Path.Combine(Path.GetTempPath(), "cloudlink-tests-" + Guid.NewGuid().ToString("N"));
    static readonly Func<int, TimeSpan?, TimeSpan> Fast = (_, _) => TimeSpan.FromMilliseconds(10);

    public ProviderTests()
    {
        Directory.CreateDirectory(_dir);
        AppPaths.Root = Path.Combine(_dir, "appdata");
    }

    public void Dispose()
    {
        _server.Dispose();
        _http.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("", "AAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("Sg==", "SgAAAAAAAAAAAAAAAQAAAAAAAAA=")]
    [InlineData("tbQ=", "taAFAAAAAAAAAAAAAgAAAAAAAAA=")]
    [InlineData("0pZP", "0rDEEwAAAAAAAAAAAwAAAAAAAAA=")]
    [InlineData("jRRDVA==", "jaDAEKgAAAAAAAAABAAAAAAAAAA=")]
    [InlineData("eAV52qE=", "eChAHrQRCgAAAAAABQAAAAAAAAA=")]
    public void QuickXorHash_matches_the_rclone_test_vectors(string input, string expected)
    {
        var hash = new QuickXorHash();
        hash.Append(Convert.FromBase64String(input));
        Assert.Equal(expected, Convert.ToBase64String(hash.Finish()));
    }

    [Fact]
    public void QuickXorHash_wraps_around_and_does_not_depend_on_chunking()
    {
        // Reference: the published block algorithm, written out bit by bit.
        byte[] data = RandomNumberGenerator.GetBytes(100_003);
        var bits = new bool[160];
        for (int i = 0; i < data.Length; i++)
            for (int b = 0; b < 8; b++)
                if ((data[i] >> b & 1) != 0) bits[(i * 11L % 160 + b) % 160] ^= true;
        byte[] expected = new byte[20];
        for (int i = 0; i < 160; i++) if (bits[i]) expected[i / 8] |= (byte)(1 << i % 8);
        byte[] length = BitConverter.GetBytes((long)data.Length);
        for (int i = 0; i < 8; i++) expected[12 + i] ^= length[i];

        var hash = new QuickXorHash();
        for (int at = 0; at < data.Length;)
        {
            int n = Math.Min(data.Length - at, 1 + at % 977);
            hash.Append(data.AsSpan(at, n));
            at += n;
        }
        Assert.Equal(expected, hash.Finish());
    }

    [Theory]
    [InlineData("a<b>c:d\"e/f\\g|h?i*j", "a_b_c_d_e_f_g_h_i_j")]
    [InlineData("trailing. . ", "trailing")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("COM1", "_COM1")]
    [InlineData("console.log", "console.log")]
    [InlineData("...", "_")]
    [InlineData("..", "_")]
    [InlineData("..\\..\\evil.exe", ".._.._evil.exe")]
    [InlineData("", "_")]
    public void Names_are_made_safe_for_Windows(string name, string expected) =>
        Assert.Equal(expected, PathSafety.SanitizeName(name));

    [Fact]
    public void Long_names_are_shortened_but_keep_the_extension()
    {
        string safe = PathSafety.SanitizeName(new string('x', 400) + ".jpeg");
        Assert.Equal(200, safe.Length);
        Assert.EndsWith(".jpeg", safe);
    }

    [Theory]
    [InlineData("https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOpQrStUvWxYz012345?usp=sharing", "1AbCdEfGhIjKlMnOpQrStUvWxYz012345", null)]
    [InlineData("https://drive.google.com/drive/u/0/folders/1AbCdEfGhIjKlMnOpQrStUvWxYz012345", "1AbCdEfGhIjKlMnOpQrStUvWxYz012345", null)]
    [InlineData("https://drive.google.com/file/d/1AbCdEfGhIjKl-nOpQrSt_vWxYz012345/view?usp=drive_link", "1AbCdEfGhIjKl-nOpQrSt_vWxYz012345", null)]
    [InlineData("https://drive.google.com/open?id=1AbCdEfGhIjKlMnOpQrStUvWxYz012345", "1AbCdEfGhIjKlMnOpQrStUvWxYz012345", null)]
    [InlineData("https://drive.google.com/uc?export=download&id=1AbCdEfGhIjKlMnOpQrStUvWxYz012345", "1AbCdEfGhIjKlMnOpQrStUvWxYz012345", null)]
    [InlineData("https://docs.google.com/spreadsheets/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/edit#gid=0", "1AbCdEfGhIjKlMnOpQrStUvWxYz012345", null)]
    [InlineData("https://drive.google.com/drive/folders/0B1AbCdEfGhIjKlMnOp?resourcekey=0-abcDEF123&usp=sharing", "0B1AbCdEfGhIjKlMnOp", "0-abcDEF123")]
    public void Google_links_are_understood(string link, string id, string? key)
    {
        var parsed = GoogleDriveProvider.ParseLink(new Uri(link));
        Assert.Equal((id, key), parsed);
    }

    [Fact]
    public void Google_link_without_an_id_is_rejected() =>
        Assert.Null(GoogleDriveProvider.ParseLink(new Uri("https://drive.google.com/drive/my-drive")));

    [Fact]
    public void OneDrive_share_link_is_encoded_as_Graph_expects()
    {
        // The example from Microsoft's "Access shared items" page.
        string token = OneDriveProvider.EncodeShareLink("https://onedrive.live.com/redir?resid=1231244193912!12&authKey=1201919!12921!1");
        Assert.Equal("u!aHR0cHM6Ly9vbmVkcml2ZS5saXZlLmNvbS9yZWRpcj9yZXNpZD0xMjMxMjQ0MTkzOTEyITEyJmF1dGhLZXk9MTIwMTkxOSExMjkyMSEx", token);
    }

    [Theory]
    [InlineData("https://1drv.ms/f/c/0123456789abcdef/EXAMPLEexampleEXAMPLEexample0123456789ABCDEFab?e=abcdef", true)]
    [InlineData("https://onedrive.live.com/?id=ABC&cid=DEF", true)]
    [InlineData("https://contoso-my.sharepoint.com/:f:/g/personal/x/abc", true)]
    [InlineData("https://drive.google.com/drive/folders/abc", false)]
    public void OneDrive_links_are_recognised(string link, bool expected) =>
        Assert.Equal(expected, new OneDriveProvider(_http, new FakeTokens()).Matches(new Uri(link)));

    [Fact]
    public async Task OneDrive_folder_is_listed_across_pages_and_downloaded_through_the_redirect()
    {
        const string link = "https://1drv.ms/f/c/0123456789abcdef/Example?e=abc";
        string token = OneDriveProvider.EncodeShareLink(link);
        const string select = "$select=id,name,size,file,folder,package,remoteItem,parentReference,fileSystemInfo,lastModifiedDateTime";
        byte[] one = RandomNumberGenerator.GetBytes(300_000), two = RandomNumberGenerator.GetBytes(200_000), three = Encoding.UTF8.GetBytes("deep");
        var quick = new QuickXorHash();
        quick.Append(two);

        int rootCalls = 0;
        _server.Route($"/shares/{token}/driveItem?{select}", ctx =>
        {
            // The first answer is a throttle, the second an expired token: both must be ridden out.
            int n = Interlocked.Increment(ref rootCalls);
            if (n == 1) { ctx.Response.Headers["Retry-After"] = "0"; return FakeServer.WriteAsync(ctx, 429, "{}", "application/json"); }
            if (n == 2) return FakeServer.WriteAsync(ctx, 401, "{\"error\":{\"message\":\"expired\"}}", "application/json");
            Assert.Equal("redeemSharingLink", ctx.Request.Headers["Prefer"]);
            Assert.Equal("Bearer token2", ctx.Request.Headers["Authorization"]);
            return FakeServer.WriteAsync(ctx, 200, """{"id":"ROOT!1","name":"Shared stuff","folder":{"childCount":3},"parentReference":{"driveId":"DRV"}}""", "application/json");
        });
        _server.Json($"/shares/{token}/driveItem/children?$top=200&{select}", $$$"""
            {"value":[
              {"id":"F!1","name":"one.bin","size":{{{one.Length}}},"file":{"hashes":{"sha256Hash":"{{{Convert.ToHexString(SHA256.HashData(one))}}}"}},
               "parentReference":{"driveId":"DRV"},"fileSystemInfo":{"lastModifiedDateTime":"2023-01-02T03:04:05Z"}}],
             "@odata.nextLink":"{{{_server.Base}}}/page2"}
            """);
        _server.Json("/page2", $$$"""
            {"value":[
              {"id":"F!2","name":"two.bin","size":{{{two.Length}}},"file":{"hashes":{"quickXorHash":"{{{Convert.ToBase64String(quick.Finish())}}}"}},"parentReference":{"driveId":"DRV"}},
              {"id":"D!1","name":"Inner","folder":{},"parentReference":{"driveId":"DRV"}}]}
            """);
        _server.Json($"/drives/DRV/items/D%211/children?$top=200&{select}", $$$"""
            {"value":[{"id":"F!3","name":"three.txt","size":{{{three.Length}}},"file":{},"parentReference":{"driveId":"DRV"}}]}
            """);
        void Content(string path, string blob, byte[] data)
        {
            _server.Route(path, ctx =>
            {
                ctx.Response.StatusCode = 302;
                ctx.Response.Headers["Location"] = _server.Base + blob;
                ctx.Response.Close();
                return Task.CompletedTask;
            });
            _server.File(blob, data, n => n == 1 ? new Fault(CutAfter: data.Length / 2) : null);
        }
        Content("/drives/DRV/items/F%211/content", "/blob/1", one);
        Content("/drives/DRV/items/F%212/content", "/blob/2", two);
        Content("/drives/DRV/items/F%213/content", "/blob/3", three);

        var provider = new OneDriveProvider(_http, new FakeTokens(), _server.Base) { Backoff = Fast };
        var engine = new DownloadEngine(_http) { Backoff = Fast };
        var items = new List<FileItem>();
        await engine.ScanAsync(provider, new Uri(link), _dir, f => { lock (items) items.Add(f); }, default);
        await engine.DownloadAllAsync(items, default);

        Assert.All(items, i => Assert.Equal(FileState.Done, i.State));
        Assert.Equal(one, File.ReadAllBytes(Path.Combine(_dir, "Shared stuff", "one.bin")));
        Assert.Equal(two, File.ReadAllBytes(Path.Combine(_dir, "Shared stuff", "two.bin")));
        Assert.Equal(three, File.ReadAllBytes(Path.Combine(_dir, "Shared stuff", "Inner", "three.txt")));
        Assert.Equal(new DateTime(2023, 1, 2, 3, 4, 5, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Path.Combine(_dir, "Shared stuff", "one.bin")));
        // The storage address behind the redirect must never see the Graph token.
        Assert.DoesNotContain(_server.Requests, r => r.StartsWith("/blob/") && r.Contains("Bearer"));
    }

    [Fact]
    public async Task OneDrive_falls_back_to_the_share_path_when_the_drive_refuses()
    {
        const string link = "https://1drv.ms/f/c/1/Example";
        string token = OneDriveProvider.EncodeShareLink(link);
        const string select = "$select=id,name,size,file,folder,package,remoteItem,parentReference,fileSystemInfo,lastModifiedDateTime";
        _server.Json($"/shares/{token}/driveItem?{select}", """{"id":"R","name":"Top","folder":{},"parentReference":{"driveId":"DRV"}}""");
        _server.Json($"/shares/{token}/driveItem/children?$top=200&{select}", """{"value":[{"id":"D","name":"Inner","folder":{},"parentReference":{"driveId":"DRV"}}]}""");
        _server.Route($"/drives/DRV/items/D/children?$top=200&{select}", ctx => FakeServer.WriteAsync(ctx, 403, "{\"error\":{\"message\":\"accessDenied\"}}", "application/json"));
        _server.Json($"/shares/{token}/items/D/children?$top=200&{select}", """{"value":[{"id":"F","name":"f.txt","size":2,"file":{},"parentReference":{"driveId":"DRV"}}]}""");
        _server.File($"/shares/{token}/items/F/content", "hi"u8.ToArray());

        var provider = new OneDriveProvider(_http, new FakeTokens(), _server.Base) { Backoff = Fast };
        var engine = new DownloadEngine(_http) { Backoff = Fast };
        var items = new List<FileItem>();
        await engine.ScanAsync(provider, new Uri(link), _dir, items.Add, default);
        await engine.DownloadAllAsync(items, default);

        Assert.Equal("hi", File.ReadAllText(Path.Combine(_dir, "Top", "Inner", "f.txt")));
    }

    [Fact]
    public async Task OneDrive_single_file_link_downloads_the_file()
    {
        const string link = "https://1drv.ms/u/c/1/File";
        string token = OneDriveProvider.EncodeShareLink(link);
        const string select = "$select=id,name,size,file,folder,package,remoteItem,parentReference,fileSystemInfo,lastModifiedDateTime";
        _server.Json($"/shares/{token}/driveItem?{select}", """{"id":"F","name":"solo.txt","size":4,"file":{},"parentReference":{"driveId":"DRV"}}""");
        _server.File($"/shares/{token}/driveItem/content", "solo"u8.ToArray());

        var provider = new OneDriveProvider(_http, new FakeTokens(), _server.Base) { Backoff = Fast };
        var engine = new DownloadEngine(_http) { Backoff = Fast };
        var items = new List<FileItem>();
        await engine.ScanAsync(provider, new Uri(link), _dir, items.Add, default);
        await engine.DownloadAllAsync(items, default);

        Assert.Equal("solo", File.ReadAllText(Path.Combine(_dir, "solo.txt")));
    }

    /// <summary>
    /// A fake SharePoint host: the link leaves a guest cookie, and the API answers only to that cookie
    /// and to a browser-like User-Agent, as the real one does. <c>Expire</c> ends the current visit.
    /// </summary>
    sealed class FakeSharePoint
    {
        public const string Agent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CloudLink/test";
        const string Select = "$select=id,name,size,file,folder,package,remoteItem,parentReference,fileSystemInfo,lastModifiedDateTime";
        readonly FakeServer _server;
        int _visits;
        volatile int _valid = 1;

        public string Link { get; }
        public string Token { get; }
        public int Visits => _visits;

        public FakeSharePoint(FakeServer server, string linkPath)
        {
            _server = server;
            Link = server.Base + linkPath;
            Token = OneDriveProvider.EncodeShareLink(Link);
            server.Route(linkPath, ctx =>
            {
                int visit = Interlocked.Increment(ref _visits);
                ctx.Response.AppendCookie(new System.Net.Cookie("FedAuth", "guest" + visit) { Path = "/", HttpOnly = true });
                ctx.Response.Redirect(server.Base + "/personal/someone/_layouts/15/onedrive.aspx?id=%2Fpersonal%2Fsomeone%2FDocuments%2FShared");
                ctx.Response.Close();
                return Task.CompletedTask;
            });
            server.Route("/personal/someone/_layouts/15/onedrive.aspx", ctx => FakeServer.WriteAsync(ctx, 200, "<html>shared folder</html>", "text/html"));
        }

        /// <summary>Ends the current visit: its cookie stops working and only the next visit's cookie will do.</summary>
        public void Expire() => _valid = _visits + 1;

        /// <summary>No visit works any more, however often it is renewed.</summary>
        public void Refuse() => _valid = int.MaxValue;

        public void Api(string pathAndQuery, Func<string> json) => _server.Route("/_api/v2.0" + pathAndQuery, ctx =>
        {
            bool guest = ctx.Request.Cookies["FedAuth"]?.Value == "guest" + _valid;
            bool browser = ctx.Request.UserAgent?.StartsWith("Mozilla/5.0 (Windows NT 10.0; Win64; x64)") == true;
            if (ctx.Request.Headers["Authorization"] is not null)
                return FakeServer.WriteAsync(ctx, 400, "{\"error\":{\"message\":\"a guest sends no token\"}}", "application/json");
            if (!guest) return FakeServer.WriteAsync(ctx, 401, "{\"error\":{\"code\":\"unauthenticated\",\"message\":\"no session\"}}", "application/json");
            if (!browser) return FakeServer.WriteAsync(ctx, 403, "{\"error\":{\"code\":\"accessDenied\",\"message\":\"Access denied\"}}", "application/json");
            return FakeServer.WriteAsync(ctx, 200, json(), "application/json");
        });

        public void Root(string json) => Api($"/shares/{Token}/driveItem?{Select}", () => json);
        public void RootChildren(string json) => Api($"/shares/{Token}/driveItem/children?$top=200&{Select}", () => json);
        public void Children(string drive, string id, string json) =>
            Api($"/drives/{Uri.EscapeDataString(drive)}/items/{Uri.EscapeDataString(id)}/children?$top=200&{Select}", () => json);
        public void DownloadAddress(string drive, string id, Func<string> address) =>
            Api($"/drives/{Uri.EscapeDataString(drive)}/items/{Uri.EscapeDataString(id)}?$select=id,@content.downloadUrl",
                () => $$"""{"id":"{{id}}","@content.downloadUrl":"{{address()}}"}""");
    }

    sealed class NoTokens : ITokenSource
    {
        public bool IsSignedIn => false;
        public Task<string> GetAccessTokenAsync(CancellationToken ct) => throw new SignInRequiredException("Microsoft");
        public void Invalidate() { }
    }

    // 127.0.0.1 stands for SharePoint. The Microsoft sign-in page is on a host of its own, the server's other name.
    OneDriveProvider GuestCapable(ITokenSource tokens) => new(_http, tokens, _server.Base + "/graph")
    {
        Backoff = Fast,
        GuestHosts = u => u.Host == "127.0.0.1",
        GuestUserAgent = FakeSharePoint.Agent,
    };

    void SendsToSignIn(string linkPath)
    {
        _server.Route(linkPath, ctx =>
        {
            ctx.Response.Redirect(_server.Alias + "/login/common/oauth2/authorize");
            ctx.Response.Close();
            return Task.CompletedTask;
        });
        _server.Route("/login/common/oauth2/authorize", ctx => FakeServer.WriteAsync(ctx, 200, "<html>sign in</html>", "text/html"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharePoint_anyone_link_opens_as_a_guest_without_a_sign_in(bool signedIn)
    {
        var sp = new FakeSharePoint(_server, "/:f:/p/someone/Example?e=abc");
        byte[] one = RandomNumberGenerator.GetBytes(300_000), two = RandomNumberGenerator.GetBytes(200_000), three = Encoding.UTF8.GetBytes("deep");
        var quick = new QuickXorHash();
        quick.Append(one);
        sp.Root("""{"id":"ROOT","name":"Shared","folder":{"childCount":2},"parentReference":{"driveId":"b!DRV"}}""");
        sp.RootChildren($$$"""
            {"value":[
              {"id":"F1","name":"one.bin","size":{{{one.Length}}},"file":{"hashes":{"quickXorHash":"{{{Convert.ToBase64String(quick.Finish())}}}"}},
               "parentReference":{"driveId":"b!DRV"},"fileSystemInfo":{"lastModifiedDateTime":"2024-05-06T07:08:09Z"}}],
             "@odata.nextLink":"{{{_server.Base}}}/_api/v2.0/page2"}
            """);
        sp.Api("/page2", () => """
            {"value":[
              {"id":"F2","name":"two.bin","size":200000,"file":{},"parentReference":{"driveId":"b!DRV"}},
              {"id":"D1","name":"Inner","folder":{},"parentReference":{"driveId":"b!DRV"}}]}
            """);
        sp.Children("b!DRV", "D1", """{"value":[{"id":"F3","name":"three.txt","size":4,"file":{},"parentReference":{"driveId":"b!DRV"}}]}""");

        // Every request for an address gets a new one, as SharePoint's are short-lived.
        int issued = 0;
        string Address(string blob) => $"{_server.Base}{blob}?tempauth=t{Interlocked.Increment(ref issued)}";
        sp.DownloadAddress("b!DRV", "F1", () => Address("/dl/1"));
        sp.DownloadAddress("b!DRV", "F2", () => Address("/dl/2"));
        sp.DownloadAddress("b!DRV", "F3", () => Address("/dl/3"));
        // The first transfer of one.bin breaks off halfway; the resume must ask for a new address.
        _server.File("/dl/1", one, n => n == 1 ? new Fault(CutAfter: one.Length / 2) : null);
        _server.File("/dl/2", two);
        _server.File("/dl/3", three);

        // With an account signed in the link is opened the same way, and the account's token goes nowhere.
        var provider = GuestCapable(signedIn ? new FakeTokens() : new NoTokens());
        Assert.True(provider.MayOpenWithoutSignIn(new Uri(sp.Link)));
        var engine = new DownloadEngine(_http) { Backoff = Fast, Parallelism = 1 };
        var items = new List<FileItem>();
        await engine.ScanAsync(provider, new Uri(sp.Link), _dir, f => { lock (items) items.Add(f); }, default);
        Assert.Equal(3, items.Count);

        // The visit runs out between the listing and the downloads; it must be renewed, not reported.
        sp.Expire();
        await engine.DownloadAllAsync(items, default);

        Assert.All(items, i => Assert.Equal(FileState.Done, i.State));
        Assert.Equal(one, File.ReadAllBytes(Path.Combine(_dir, "Shared", "one.bin")));
        Assert.Equal(two, File.ReadAllBytes(Path.Combine(_dir, "Shared", "two.bin")));
        Assert.Equal(three, File.ReadAllBytes(Path.Combine(_dir, "Shared", "Inner", "three.txt")));
        Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Path.Combine(_dir, "Shared", "one.bin")));
        Assert.Equal(2, sp.Visits);
        // Both visits start with no cookie, the renewal included: it must look like a fresh private window.
        Assert.All(_server.Seen.Where(r => r.PathAndQuery == "/:f:/p/someone/Example?e=abc"), r => Assert.Equal("", r.Cookies));
        Assert.True(issued >= 4, $"expected a fresh address for the resume, got {issued} addresses");
        Assert.DoesNotContain(_server.Requests, r => r.StartsWith("/graph/"));
        Assert.DoesNotContain(_server.Requests, r => r.Contains("Bearer"));
    }

    [Fact]
    public async Task SharePoint_link_for_certain_people_asks_for_the_sign_in_and_then_uses_it()
    {
        // A link that is not for anyone sends the visitor to the sign-in page on another host.
        const string linkPath = "/:f:/r/sites/team/Private";
        string link = _server.Base + linkPath;
        string token = OneDriveProvider.EncodeShareLink(link);
        const string select = "$select=id,name,size,file,folder,package,remoteItem,parentReference,fileSystemInfo,lastModifiedDateTime";
        SendsToSignIn(linkPath);
        _server.Json($"/graph/shares/{token}/driveItem?{select}", """{"id":"R","name":"Private","folder":{},"parentReference":{"driveId":"DRV"}}""");
        _server.Json($"/graph/shares/{token}/driveItem/children?$top=200&{select}", """{"value":[]}""");

        await Assert.ThrowsAsync<SignInRequiredException>(() => GuestCapable(new NoTokens()).ResolveAsync(new Uri(link), default));
        // The guest visit stops at the door of the sign-in page: that host is never asked anything.
        Assert.Contains(_server.Seen, r => r.PathAndQuery == linkPath);
        Assert.DoesNotContain(_server.Seen, r => r.Host == FakeServer.AliasHost);

        var signedIn = GuestCapable(new FakeTokens());
        var root = await signedIn.ResolveAsync(new Uri(link), default);
        Assert.Equal("Private", root.Name);
        Assert.Empty(await signedIn.ListAsync(root, default));
        Assert.Contains(_server.Requests, r => r.StartsWith("/graph/shares/") && r.Contains("Bearer token1"));
        Assert.DoesNotContain(_server.Seen, r => r.Host == FakeServer.AliasHost);
    }

    [Fact]
    public async Task SharePoint_link_the_api_refuses_to_a_guest_asks_for_the_sign_in()
    {
        // The link opens, but the API will not serve this visitor (for example a link switched to
        // "people in the organisation" after it was sent): the sign-in route must be tried.
        var sp = new FakeSharePoint(_server, "/:u:/p/someone/Refused");
        _server.Route($"/_api/v2.0/shares/{sp.Token}/driveItem", ctx =>
            FakeServer.WriteAsync(ctx, 403, "{\"error\":{\"code\":\"accessDenied\",\"message\":\"Access denied\"}}", "application/json"));
        await Assert.ThrowsAsync<SignInRequiredException>(() => GuestCapable(new NoTokens()).ResolveAsync(new Uri(sp.Link), default));
        Assert.Equal(1, sp.Visits);
        Assert.Contains(_server.Requests, r => r.StartsWith($"/_api/v2.0/shares/{sp.Token}/driveItem"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharePoint_link_that_has_been_removed_is_reported_and_no_sign_in_is_asked_for(bool signedIn)
    {
        // What SharePoint does with a link that has been removed, has expired or never existed: a page of
        // its own with status 200 and no guest session, and an API that says the link is not there.
        const string linkPath = "/:v:/p/someone/Removed";
        string link = _server.Base + linkPath;
        string token = OneDriveProvider.EncodeShareLink(link);
        _server.Route(linkPath, ctx => FakeServer.WriteAsync(ctx, 200, "<html>This link has been removed.</html>", "text/html"));
        _server.Route($"/_api/v2.0/shares/{token}/driveItem", ctx => FakeServer.WriteAsync(ctx, 404,
            "{\"error\":{\"code\":\"itemNotFound\",\"message\":\"Requested sharing link could not be found\"}}", "application/json"));

        var ex = await Assert.ThrowsAsync<CloudException>(() =>
            GuestCapable(signedIn ? new FakeTokens() : new NoTokens()).ResolveAsync(new Uri(link), default));
        Assert.Equal(404, ex.StatusCode);
        Assert.True(ex.WithoutAccount);
        Assert.StartsWith("SharePoint does not know this link", ex.Message);
        // What SharePoint itself does not know, no account opens: the signed-in route is not tried.
        Assert.Contains(_server.Requests, r => r.StartsWith($"/_api/v2.0/shares/{token}/driveItem"));
        Assert.DoesNotContain(_server.Requests, r => r.StartsWith("/graph/"));
    }

    [Fact]
    public async Task SharePoint_link_the_api_cannot_find_for_a_guest_it_let_in_still_asks_for_the_sign_in()
    {
        // The visit left a guest session, so the link exists. An API that cannot find it there (as may be
        // for a site that is not the host's own) is no proof that it is gone: the sign-in route is tried.
        var sp = new FakeSharePoint(_server, "/:f:/s/team/Elsewhere");
        _server.Route($"/_api/v2.0/shares/{sp.Token}/driveItem", ctx => FakeServer.WriteAsync(ctx, 404,
            "{\"error\":{\"code\":\"itemNotFound\",\"message\":\"Requested sharing link could not be found\"}}", "application/json"));
        await Assert.ThrowsAsync<SignInRequiredException>(() => GuestCapable(new NoTokens()).ResolveAsync(new Uri(sp.Link), default));
        Assert.Equal(1, sp.Visits);
    }

    [Theory]
    [InlineData("https://contoso-my.sharepoint.com/:f:/p/someone/abc", true)]
    [InlineData("https://contoso.sharepoint.com/:u:/s/team/abc", true)]
    [InlineData("https://CONTOSO.SharePoint.com/:u:/s/team/abc", true)]
    [InlineData("https://contoso.sharepoint.cn/:f:/s/team/abc", true)]
    [InlineData("https://contoso.sharepoint.us/:f:/s/team/abc", true)]
    [InlineData("http://contoso-my.sharepoint.com/:f:/p/someone/abc", false)]
    [InlineData("https://contoso.sharepoint.com.evil.example/:f:/p/someone/abc", false)]
    [InlineData("https://evilsharepoint.com/:f:/p/someone/abc", false)]
    [InlineData("https://sharepoint.com/:f:/p/someone/abc", false)]
    [InlineData("https://login.microsoftonline.com/common/oauth2/authorize", false)]
    [InlineData("https://1drv.ms/f/c/1/Example", false)]
    [InlineData("https://onedrive.live.com/?id=ABC&cid=DEF", false)]
    public void Only_work_or_school_hosts_are_visited_as_a_guest(string link, bool expected) =>
        Assert.Equal(expected, OneDriveProvider.IsWorkOrSchoolHost(new Uri(link)));

    [Theory]
    [InlineData("""{"error":{"code":"accessDenied","message":"Plain text"}}""", "Plain text")]
    [InlineData("""{"error":{"code":"-1","message":{"lang":"en-US","value":"One level down"}}}""", "One level down")]
    [InlineData("""{"odata.error":{"code":"-2147024891","message":{"lang":"en-US","value":"Older shape"}}}""", "Older shape")]
    [InlineData("""{"error":{"message":{"lang":"en-US"}}}""", "Forbidden")]
    [InlineData("""{"error":{"message":{"lang":"en-US","value":7}}}""", "Forbidden")]
    [InlineData("""{"error":["not","an","object"]}""", "Forbidden")]
    [InlineData("""["not","an","object"]""", "Forbidden")]
    [InlineData("<html>a page</html>", "Forbidden")]
    public void Error_answers_of_any_shape_are_read_without_failing(string body, string expected)
    {
        using var resp = new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden);
        Assert.Equal($"Access denied: {expected} (HTTP 403)", Errors.FromResponse(resp, body).Message);
    }

    [Theory]
    [InlineData("https://contoso.sharepoint.com/_api/v2.0/shares/u!aHR0cHM6Ly9jb250b3Nv_-Zm9v/driveItem?tempauth=secret#part",
        "contoso.sharepoint.com/_api/v2.0/shares/u!.../driveItem")]
    [InlineData("/_api/v2.0/shares/u!aHR0cHM6Ly9jb250b3Nv/driveItem?tempauth=secret", "/_api/v2.0/shares/u!.../driveItem")]
    [InlineData("https://contoso.sharepoint.com/_api/v2.0/shares/u%21aHR0cHM6Ly9jb250b3Nv/driveItem?tempauth=secret",
        "contoso.sharepoint.com/_api/v2.0/shares/u!.../driveItem")]
    [InlineData("https://contoso.sharepoint.com/personal/someone/_layouts/15/download.aspx?UniqueId=1&tempauth=secret",
        "contoso.sharepoint.com/personal/someone/_layouts/15/download.aspx")]
    public void The_log_line_for_a_failed_request_leaves_out_keys_and_share_links(string location, string logged)
    {
        using var resp = new HttpResponseMessage(System.Net.HttpStatusCode.PermanentRedirect)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get,
                "https://contoso-my.sharepoint.com/_api/v2.0/shares/u!aHR0cHM6Ly9jb250b3Nv/driveItem?tempauth=secret"),
        };
        resp.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        resp.Headers.TryAddWithoutValidation("SPRequestGuid", "1234");
        Assert.Equal($"HTTP 308 from contoso-my.sharepoint.com; location {logged}; SPRequestGuid 1234", Errors.Diagnostics(resp));
    }

    [Fact]
    public async Task The_stand_in_server_answers_under_both_of_its_names()
    {
        // The tests about what may cross from one host to another mean nothing unless the second name works.
        _server.Json("/both", "{}");
        using var http = new HttpClient();
        foreach (string name in new[] { _server.Base, _server.Alias })
            Assert.True((await http.GetAsync(name + "/both")).IsSuccessStatusCode);
        Assert.Equal(["127.0.0.1", FakeServer.AliasHost], _server.Seen.Where(s => s.PathAndQuery == "/both").Select(s => s.Host));
    }

    [Fact]
    public async Task SharePoint_guest_listing_does_not_follow_a_next_page_to_another_host()
    {
        var sp = new FakeSharePoint(_server, "/:f:/p/someone/Elsewhere");
        sp.Root("""{"id":"ROOT","name":"Shared","folder":{},"parentReference":{"driveId":"b!DRV"}}""");
        sp.RootChildren($$"""{"value":[],"@odata.nextLink":"{{_server.Alias}}/_api/v2.0/page2"}""");

        var provider = GuestCapable(new NoTokens());
        var root = await provider.ResolveAsync(new Uri(sp.Link), default);
        var ex = await Assert.ThrowsAsync<CloudException>(() => provider.ListAsync(root, default));
        Assert.Contains("unexpected address", ex.Message);
        Assert.DoesNotContain(_server.Seen, r => r.Host == FakeServer.AliasHost);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SharePoint_single_file_link_downloads_as_a_guest(bool driveRefuses, bool signedIn)
    {
        var sp = new FakeSharePoint(_server, "/:v:/p/someone/Film");
        byte[] film = RandomNumberGenerator.GetBytes(100_000);
        sp.Root($$$"""{"id":"F1","name":"film.mp4","size":{{{film.Length}}},"file":{},"parentReference":{"driveId":"b!DRV"}}""");
        const string ask = "?$select=id,@content.downloadUrl";
        string address = $$"""{"id":"F1","@content.downloadUrl":"{{_server.Base}}/dl/film?tempauth=t"}""";
        if (driveRefuses)
        {
            // Some links let a guest reach the file only through the share itself.
            _server.Route("/_api/v2.0/drives/b%21DRV/items/F1" + ask, ctx =>
                FakeServer.WriteAsync(ctx, 403, "{\"error\":{\"code\":\"accessDenied\",\"message\":\"Access denied\"}}", "application/json"));
            sp.Api($"/shares/{sp.Token}/driveItem{ask}", () => address);
        }
        else
        {
            sp.Api("/drives/b%21DRV/items/F1" + ask, () => address);
        }
        _server.File("/dl/film", film);

        var provider = GuestCapable(signedIn ? new FakeTokens() : new NoTokens());
        var engine = new DownloadEngine(_http) { Backoff = Fast };
        var items = new List<FileItem>();
        await engine.ScanAsync(provider, new Uri(sp.Link), _dir, items.Add, default);
        await engine.DownloadAllAsync(items, default);

        Assert.Equal(FileState.Done, Assert.Single(items).State);
        Assert.Equal(film, File.ReadAllBytes(Path.Combine(_dir, "film.mp4")));
        Assert.DoesNotContain(_server.Requests, r => r.StartsWith("/graph/"));
        Assert.DoesNotContain(_server.Requests, r => r.Contains("Bearer"));
        // The download address stands on its own: the guest cookie is not sent with it.
        Assert.All(_server.Seen.Where(r => r.PathAndQuery.StartsWith("/dl/")), r => Assert.Equal("", r.Cookies));
    }

    [Fact]
    public async Task SharePoint_link_that_gives_no_download_address_fails_once_and_says_why()
    {
        var sp = new FakeSharePoint(_server, "/:f:/p/someone/ViewOnly");
        sp.Root("""{"id":"ROOT","name":"Shared","folder":{},"parentReference":{"driveId":"b!DRV"}}""");
        sp.RootChildren("""{"value":[{"id":"F1","name":"locked.pdf","size":10,"file":{},"parentReference":{"driveId":"b!DRV"}}]}""");
        int asked = 0;
        sp.Api("/drives/b%21DRV/items/F1?$select=id,@content.downloadUrl", () =>
        {
            Interlocked.Increment(ref asked);
            return """{"id":"F1"}""";
        });

        var provider = GuestCapable(new NoTokens());
        var engine = new DownloadEngine(_http) { Backoff = Fast };
        var items = new List<FileItem>();
        await engine.ScanAsync(provider, new Uri(sp.Link), _dir, items.Add, default);
        await engine.DownloadAllAsync(items, default);

        var item = Assert.Single(items);
        Assert.Equal(FileState.Failed, item.State);
        Assert.Contains("did not give a download address", item.Message);
        Assert.False(item.RetryableFailure);
        Assert.Equal(1, asked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharePoint_link_closed_during_a_run_fails_each_file_at_once_after_a_single_new_visit(bool removed)
    {
        const string linkPath = "/:f:/p/someone/Closing";
        var sp = new FakeSharePoint(_server, linkPath);
        sp.Root("""{"id":"ROOT","name":"Shared","folder":{},"parentReference":{"driveId":"b!DRV"}}""");
        sp.RootChildren("""
            {"value":[
              {"id":"F1","name":"a.bin","size":1,"file":{},"parentReference":{"driveId":"b!DRV"}},
              {"id":"F2","name":"b.bin","size":1,"file":{},"parentReference":{"driveId":"b!DRV"}},
              {"id":"F3","name":"c.bin","size":1,"file":{},"parentReference":{"driveId":"b!DRV"}}]}
            """);
        foreach (string id in new[] { "F1", "F2", "F3" })
            sp.DownloadAddress("b!DRV", id, () => _server.Base + "/dl/never");

        var provider = GuestCapable(new NoTokens());
        var engine = new DownloadEngine(_http) { Backoff = Fast, Parallelism = 2 };
        var items = new List<FileItem>();
        await engine.ScanAsync(provider, new Uri(sp.Link), _dir, f => { lock (items) items.Add(f); }, default);
        Assert.Equal(3, items.Count);

        // The owner limits the link to certain people: the visit ends, and a new one is sent to the sign-in page.
        // Or the owner removes the link: a new visit gets a page of SharePoint's own, and no guest session.
        sp.Expire();
        if (removed)
            _server.Route(linkPath, ctx => FakeServer.WriteAsync(ctx, 200, "<html>This link has been removed.</html>", "text/html"));
        else
            SendsToSignIn(linkPath);
        await engine.DownloadAllAsync(items, default);

        Assert.All(items, i =>
        {
            Assert.Equal(FileState.Failed, i.State);
            Assert.Contains("no longer opens without an account", i.Message);
            Assert.False(i.RetryableFailure);
        });
        // The visit that opened the link and one attempt to renew it, however many files there are.
        Assert.Equal(2, _server.Seen.Count(r => r.PathAndQuery == linkPath));
        Assert.DoesNotContain(_server.Seen, r => r.Host == FakeServer.AliasHost);
        Assert.DoesNotContain(_server.Requests, r => r.StartsWith("/dl/"));
    }

    [Fact]
    public async Task SharePoint_that_keeps_turning_a_renewed_guest_away_is_given_up_on_for_the_whole_run()
    {
        var sp = new FakeSharePoint(_server, "/:f:/p/someone/Stubborn");
        sp.Root("""{"id":"ROOT","name":"Shared","folder":{},"parentReference":{"driveId":"b!DRV"}}""");
        sp.RootChildren("""
            {"value":[
              {"id":"F1","name":"a.bin","size":1,"file":{},"parentReference":{"driveId":"b!DRV"}},
              {"id":"F2","name":"b.bin","size":1,"file":{},"parentReference":{"driveId":"b!DRV"}}]}
            """);
        foreach (string id in new[] { "F1", "F2" })
            sp.DownloadAddress("b!DRV", id, () => _server.Base + "/dl/never");

        var provider = GuestCapable(new NoTokens());
        var engine = new DownloadEngine(_http) { Backoff = Fast, Parallelism = 1 };
        var items = new List<FileItem>();
        await engine.ScanAsync(provider, new Uri(sp.Link), _dir, items.Add, default);

        sp.Refuse();
        await engine.DownloadAllAsync(items, default);

        Assert.All(items, i =>
        {
            Assert.Equal(FileState.Failed, i.State);
            Assert.Contains("stopped letting CloudLink in as a guest", i.Message);
        });
        // One file uses up the six attempts, with a new visit before each but the first; the other is told
        // straight away, with no request and no visit of its own.
        Assert.Equal(6, sp.Visits);
        Assert.Equal(6, _server.Requests.Count(r => r.StartsWith("/_api/v2.0/drives/b%21DRV/items/F")));
    }

    [Fact]
    public async Task SharePoint_guest_session_that_runs_out_after_other_failures_is_renewed_and_not_given_up_on()
    {
        var sp = new FakeSharePoint(_server, "/:f:/p/someone/Unlucky");
        byte[] data = RandomNumberGenerator.GetBytes(50_000);
        sp.Root("""{"id":"ROOT","name":"Shared","folder":{},"parentReference":{"driveId":"b!DRV"}}""");
        sp.RootChildren($$$"""
            {"value":[{"id":"F1","name":"a.bin","size":{{{data.Length}}},"file":{},"parentReference":{"driveId":"b!DRV"}}]}
            """);
        // Five passing failures use up the attempts of one request, and the session runs out just before
        // the sixth: that 401 has no renewed visit behind it, so it says nothing about the link.
        int asked = 0;
        _server.Route("/_api/v2.0/drives/b%21DRV/items/F1?$select=id,@content.downloadUrl", ctx =>
        {
            int n = Interlocked.Increment(ref asked);
            return n <= 5 ? FakeServer.WriteAsync(ctx, 503, "{}", "application/json")
                : n == 6 ? FakeServer.WriteAsync(ctx, 401, "{\"error\":{\"code\":\"unauthenticated\",\"message\":\"no session\"}}", "application/json")
                : FakeServer.WriteAsync(ctx, 200, $$"""{"id":"F1","@content.downloadUrl":"{{_server.Base}}/dl/a"}""", "application/json");
        });
        _server.File("/dl/a", data);

        var provider = GuestCapable(new NoTokens());
        var engine = new DownloadEngine(_http) { Backoff = Fast };
        var items = new List<FileItem>();
        await engine.ScanAsync(provider, new Uri(sp.Link), _dir, items.Add, default);
        await engine.DownloadAllAsync(items, default);

        Assert.Equal(FileState.Done, Assert.Single(items).State);
        // The file's next try renewed the visit first, and then got its address.
        Assert.Equal(2, sp.Visits);
        Assert.Equal(7, asked);
    }

    [Fact]
    public async Task SharePoint_answers_that_are_not_a_refusal_are_reported_or_handed_to_the_signed_in_account()
    {
        const string select = "$select=id,name,size,file,folder,package,remoteItem,parentReference,fileSystemInfo,lastModifiedDateTime";

        // An error that says nothing about the link. With nobody signed in it is reported as it came.
        var odd = new FakeSharePoint(_server, "/:f:/p/someone/Odd");
        _server.Route($"/_api/v2.0/shares/{odd.Token}/driveItem", ctx =>
            FakeServer.WriteAsync(ctx, 400, "{\"error\":{\"code\":\"invalidRequest\",\"message\":\"Unexpected\"}}", "application/json"));
        var ex = await Assert.ThrowsAsync<CloudException>(() => GuestCapable(new NoTokens()).ResolveAsync(new Uri(odd.Link), default));
        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("Unexpected", ex.Message);
        Assert.True(ex.WithoutAccount);

        // With an account, the signed-in route gets its turn.
        _server.Json($"/graph/shares/{odd.Token}/driveItem?{select}", """{"id":"R","name":"Through Graph","folder":{},"parentReference":{"driveId":"DRV"}}""");
        var root = await GuestCapable(new FakeTokens()).ResolveAsync(new Uri(odd.Link), default);
        Assert.Equal("Through Graph", root.Name);

        // A page where the shared item should be is put into words, not thrown as a parsing error.
        var page = new FakeSharePoint(_server, "/:f:/p/someone/Page");
        _server.Route($"/_api/v2.0/shares/{page.Token}/driveItem", ctx => FakeServer.WriteAsync(ctx, 200, "<html>a page</html>", "text/html"));
        ex = await Assert.ThrowsAsync<CloudException>(() => GuestCapable(new NoTokens()).ResolveAsync(new Uri(page.Link), default));
        Assert.Contains("answered with a page", ex.Message);
        Assert.False(ex.Transient);
        Assert.True(ex.WithoutAccount);

        // The same for a listing further on, and for an answer that is JSON but no item.
        var later = new FakeSharePoint(_server, "/:f:/p/someone/Later");
        later.Root("""{"id":"ROOT","name":"Shared","folder":{},"parentReference":{"driveId":"b!DRV"}}""");
        _server.Route($"/_api/v2.0/shares/{later.Token}/driveItem/children", ctx => FakeServer.WriteAsync(ctx, 200, "<html>a page</html>", "text/html"));
        var guestProvider = GuestCapable(new NoTokens());
        var shared = await guestProvider.ResolveAsync(new Uri(later.Link), default);
        ex = await Assert.ThrowsAsync<CloudException>(() => guestProvider.ListAsync(shared, default));
        Assert.Contains("answered with a page", ex.Message);
        Assert.True(ex.WithoutAccount);

        var empty = new FakeSharePoint(_server, "/:f:/p/someone/Empty");
        _server.Route($"/_api/v2.0/shares/{empty.Token}/driveItem", ctx => FakeServer.WriteAsync(ctx, 200, "null", "application/json"));
        ex = await Assert.ThrowsAsync<CloudException>(() => GuestCapable(new NoTokens()).ResolveAsync(new Uri(empty.Link), default));
        Assert.Contains("answered with a page", ex.Message);

        // An API that sends the guest to another host is not followed there; the sign-in route is next.
        var moved = new FakeSharePoint(_server, "/:f:/p/someone/Moved");
        _server.Route($"/_api/v2.0/shares/{moved.Token}/driveItem", ctx =>
        {
            ctx.Response.Redirect(_server.Alias + "/login/common/oauth2/authorize");
            ctx.Response.Close();
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<SignInRequiredException>(() => GuestCapable(new NoTokens()).ResolveAsync(new Uri(moved.Link), default));
        Assert.DoesNotContain(_server.Seen, r => r.Host == FakeServer.AliasHost);
    }

    [Fact]
    public async Task Work_or_school_link_problems_are_put_into_words()
    {
        // A link that is gone on SharePoint itself is reported as such, with no sign-in asked for.
        const string gonePath = "/:f:/p/someone/Gone";
        _server.Route(gonePath, ctx => FakeServer.WriteAsync(ctx, 404, "<html>gone</html>", "text/html"));
        var notFound = await Assert.ThrowsAsync<CloudException>(() => GuestCapable(new NoTokens()).ResolveAsync(new Uri(_server.Base + gonePath), default));
        Assert.Equal(404, notFound.StatusCode);

        // Graph's bare redirect, what a personal account gets for a company link, is explained.
        const string orgPath = "/:f:/p/someone/OrgOnly";
        string link = _server.Base + orgPath;
        string token = OneDriveProvider.EncodeShareLink(link);
        const string select = "$select=id,name,size,file,folder,package,remoteItem,parentReference,fileSystemInfo,lastModifiedDateTime";
        SendsToSignIn(orgPath);
        _server.Route($"/graph/shares/{token}/driveItem?{select}", ctx =>
        {
            ctx.Response.StatusCode = 308;
            ctx.Response.Close();
            return Task.CompletedTask;
        });
        var ex = await Assert.ThrowsAsync<CloudException>(() => GuestCapable(new FakeTokens()).ResolveAsync(new Uri(link), default));
        Assert.Equal(308, ex.StatusCode);
        Assert.Contains("would not open this work or school link for the signed-in account", ex.Message);

        // Elsewhere a redirect gets a plain lead rather than "reported an error".
        var plain = new OneDriveProvider(_http, new FakeTokens(), _server.Base + "/graph") { Backoff = Fast, GuestHosts = _ => false };
        var other = await Assert.ThrowsAsync<CloudException>(() => plain.ResolveAsync(new Uri(link), default));
        Assert.StartsWith("The service sent the request elsewhere instead of answering", other.Message);
    }

    [Fact]
    public async Task Google_folder_is_listed_with_an_api_key_and_documents_are_exported()
    {
        const string fields = "id,name,mimeType,size,md5Checksum,modifiedTime,resourceKey,exportLinks,shortcutDetails";
        const string folderId = "1AbCdEfGhIjKlMnOpQrStUvWxYz012345";
        byte[] photo = RandomNumberGenerator.GetBytes(150_000), docx = Encoding.UTF8.GetBytes("exported document");
        string q = Uri.EscapeDataString($"'{folderId}' in parents and trashed = false");
        string list = $"/files?q={q}&pageSize=1000&supportsAllDrives=true&includeItemsFromAllDrives=true&fields=nextPageToken,files({fields})";

        _server.Json($"/files/{folderId}?supportsAllDrives=true&fields={fields}&key=KEY",
            $$"""{"id":"{{folderId}}","name":"Trip","mimeType":"application/vnd.google-apps.folder"}""");
        _server.Json($"{list}&key=KEY", $$"""
            {"nextPageToken":"p2","files":[
              {"id":"photo1","name":"photo.jpg","mimeType":"image/jpeg","size":"{{photo.Length}}","md5Checksum":"{{Convert.ToHexString(MD5.HashData(photo)).ToLowerInvariant()}}","modifiedTime":"2022-05-06T07:08:09.000Z"}]}
            """);
        _server.Json($"{list}&pageToken=p2&key=KEY", """
            {"files":[
              {"id":"doc1","name":"Notes","mimeType":"application/vnd.google-apps.document"},
              {"id":"form1","name":"Survey","mimeType":"application/vnd.google-apps.form"},
              {"id":"short1","name":"Link to photo","mimeType":"application/vnd.google-apps.shortcut","shortcutDetails":{"targetId":"photo1"}}]}
            """);
        _server.Json($"/files/photo1?supportsAllDrives=true&fields={fields}&key=KEY",
            $$"""{"id":"photo1","name":"photo.jpg","mimeType":"image/jpeg","size":"{{photo.Length}}"}""");
        _server.File("/files/photo1", photo);
        _server.File("/files/doc1/export", docx);

        var tokens = new FakeTokens { SignedIn = false };
        var provider = new GoogleDriveProvider(_http, tokens, () => "KEY", _server.Base) { Backoff = Fast };
        var engine = new DownloadEngine(_http) { Backoff = Fast };
        var items = new List<FileItem>();
        await engine.ScanAsync(provider, new Uri($"https://drive.google.com/drive/folders/{folderId}?usp=sharing"), _dir, f => { lock (items) items.Add(f); }, default);
        await engine.DownloadAllAsync(items, default);

        string trip = Path.Combine(_dir, "Trip");
        Assert.Equal(photo, File.ReadAllBytes(Path.Combine(trip, "photo.jpg")));
        Assert.Equal(photo, File.ReadAllBytes(Path.Combine(trip, "Link to photo")));
        Assert.Equal(docx, File.ReadAllBytes(Path.Combine(trip, "Notes.docx")));
        var survey = Assert.Single(items, i => i.State == FileState.Skipped);
        Assert.Contains("form", survey.Message);
        Assert.Equal(3, items.Count(i => i.State == FileState.Done));
    }

    [Fact]
    public async Task Google_without_key_or_sign_in_asks_for_sign_in()
    {
        var provider = new GoogleDriveProvider(_http, new FakeTokens { SignedIn = false }, () => "", _server.Base);
        await Assert.ThrowsAsync<SignInRequiredException>(() =>
            provider.ResolveAsync(new Uri("https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOpQrStUvWxYz012345"), default));
    }
}
