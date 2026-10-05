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
