using System.Security.Cryptography;
using CloudLink.Core;

namespace CloudLink.Tests;

sealed class FakeProvider(string baseUrl) : ICloudProvider
{
    public Dictionary<string, List<RemoteEntry>> Folders { get; } = [];
    public RemoteEntry? Root { get; set; }
    public int UnauthorizedCalls;
    public Func<RemoteEntry, string?>? UrlFor;

    public string Name => "Fake";
    public bool Matches(Uri link) => true;
    public Task<RemoteEntry> ResolveAsync(Uri link, CancellationToken ct) => Task.FromResult(Root!);
    public Task<IReadOnlyList<RemoteEntry>> ListAsync(RemoteEntry folder, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RemoteEntry>>(Folders[folder.Id]);
    public Task<HttpRequestMessage> CreateDownloadRequestAsync(RemoteEntry file, CancellationToken ct) =>
        Task.FromResult(new HttpRequestMessage(HttpMethod.Get, UrlFor?.Invoke(file) ?? $"{baseUrl}/f/{file.Id}"));
    public void OnUnauthorized() => Interlocked.Increment(ref UnauthorizedCalls);
}

public sealed class EngineTests : IDisposable
{
    readonly FakeServer _server = new();
    readonly string _dir = Path.Combine(Path.GetTempPath(), "cloudlink-tests-" + Guid.NewGuid().ToString("N"));
    readonly HttpClient _http = new(new SocketsHttpHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    readonly FakeProvider _provider;
    readonly DownloadEngine _engine;
    readonly byte[] _content = RandomNumberGenerator.GetBytes(3_000_000);

    public EngineTests()
    {
        Directory.CreateDirectory(_dir);
        AppPaths.Root = Path.Combine(_dir, "appdata");
        _provider = new FakeProvider(_server.Base);
        _engine = new DownloadEngine(_http)
        {
            Backoff = (_, _) => TimeSpan.FromMilliseconds(20),
            StallTimeout = TimeSpan.FromSeconds(2),
            MaxFailures = 4,
        };
    }

    public void Dispose()
    {
        _server.Dispose();
        _http.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    FileItem Item(string id = "a", bool hash = true, long? size = -1, bool resumable = true, DateTimeOffset? modified = null)
    {
        var entry = new RemoteEntry
        {
            Id = id,
            Name = id + ".bin",
            Size = size == -1 ? _content.Length : size,
            HashKind = hash ? HashKind.Md5 : HashKind.None,
            Hash = hash ? MD5.HashData(_content) : null,
            Resumable = resumable,
            Modified = modified,
        };
        return new FileItem { Provider = _provider, Entry = entry, LocalPath = Path.Combine(_dir, id + ".bin"), DisplayPath = id + ".bin", Size = entry.Size };
    }

    void AssertDownloaded(FileItem item)
    {
        Assert.Equal(FileState.Done, item.State);
        Assert.Equal(_content, File.ReadAllBytes(item.LocalPath));
        Assert.False(File.Exists(item.LocalPath + ".part"));
    }

    string[] RangedRequests => _server.Requests.Where(r => !r.Contains("range= ")).ToArray();

    [Fact]
    public async Task Downloads_verifies_and_stamps_the_modified_time()
    {
        _server.File("/f/a", _content);
        var when = new DateTimeOffset(2024, 3, 5, 10, 20, 30, TimeSpan.Zero);
        var item = Item(modified: when);
        await _engine.DownloadAsync(item, default);
        AssertDownloaded(item);
        Assert.Equal(when.UtcDateTime, File.GetLastWriteTimeUtc(item.LocalPath));
        Assert.Equal(_content.Length, _engine.BytesTransferred);
    }

    [Fact]
    public async Task Resumes_after_the_connection_is_cut_repeatedly()
    {
        _server.File("/f/a", _content, n => n <= 3 ? new Fault(CutAfter: 400_000) : null);
        var item = Item();
        await _engine.DownloadAsync(item, default);
        AssertDownloaded(item);
        Assert.Equal(4, _server.Requests.Count);
        Assert.Equal(3, RangedRequests.Length);
        // Nothing was fetched twice.
        Assert.Equal(_content.Length, _engine.BytesTransferred);
    }

    [Fact]
    public async Task Keeps_going_while_progress_is_made_even_past_the_failure_limit()
    {
        _server.File("/f/a", _content, n => n <= 10 ? new Fault(CutAfter: 200_000) : null);
        var item = Item();
        await _engine.DownloadAsync(item, default);
        AssertDownloaded(item);
    }

    [Fact]
    public async Task Recovers_from_a_stalled_connection()
    {
        _server.File("/f/a", _content, n => n == 1 ? new Fault(CutAfter: 500_000, Hang: true) : null);
        var item = Item();
        await _engine.DownloadAsync(item, default);
        AssertDownloaded(item);
        Assert.Single(RangedRequests);
    }

    [Fact]
    public async Task Retries_server_errors_and_throttling()
    {
        _server.File("/f/a", _content, n => n switch
        {
            1 => new Fault(Status: 503),
            2 => new Fault(Status: 429, RetryAfterSeconds: 0),
            3 => new Fault(Status: 401),
            _ => null,
        });
        var item = Item();
        await _engine.DownloadAsync(item, default);
        AssertDownloaded(item);
        Assert.Equal(1, _provider.UnauthorizedCalls);
    }

    [Fact]
    public async Task Gives_up_when_the_server_never_recovers()
    {
        _server.File("/f/a", _content, _ => new Fault(Status: 500));
        var item = Item();
        await _engine.DownloadAsync(item, default);
        Assert.Equal(FileState.Failed, item.State);
        Assert.Equal(5, _server.Requests.Count);
        Assert.False(File.Exists(item.LocalPath));
    }

    [Fact]
    public async Task Does_not_retry_a_missing_file()
    {
        var item = Item();
        await _engine.DownloadAsync(item, default);
        Assert.Equal(FileState.Failed, item.State);
        Assert.Single(_server.Requests);
        Assert.Contains("404", item.Message);
    }

    [Fact]
    public async Task Downloads_again_when_the_data_is_corrupt()
    {
        _server.File("/f/a", _content, n => n == 1 ? new Fault(Corrupt: true) : null);
        var item = Item();
        await _engine.DownloadAsync(item, default);
        AssertDownloaded(item);
        Assert.Equal(2, _server.Requests.Count);
        Assert.Equal("", item.Message);
    }

    [Fact]
    public async Task Never_leaves_a_truncated_file_under_the_real_name()
    {
        _server.File("/f/a", _content, _ => new Fault(CutAfter: 0));
        var item = Item();
        await _engine.DownloadAsync(item, default);
        Assert.Equal(FileState.Failed, item.State);
        Assert.False(File.Exists(item.LocalPath));
    }

    [Fact]
    public async Task Continues_a_part_file_left_by_an_earlier_run()
    {
        _server.File("/f/a", _content);
        var item = Item();
        File.WriteAllBytes(item.LocalPath + ".part", _content[..1_000_000]);
        await _engine.DownloadAsync(item, default);
        AssertDownloaded(item);
        Assert.Equal(_content.Length - 1_000_000, _engine.BytesTransferred);
    }

    [Fact]
    public async Task Finishes_a_part_file_that_is_already_complete()
    {
        _server.File("/f/a", _content);
        var item = Item();
        File.WriteAllBytes(item.LocalPath + ".part", _content);
        await _engine.DownloadAsync(item, default);
        AssertDownloaded(item);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task Replaces_a_part_file_whose_content_is_wrong()
    {
        _server.File("/f/a", _content);
        var item = Item();
        File.WriteAllBytes(item.LocalPath + ".part", new byte[1_000_000]);
        await _engine.DownloadAsync(item, default);
        AssertDownloaded(item);
    }

    [Fact]
    public async Task Starts_over_when_the_server_ignores_the_range()
    {
        _server.File("/f/a", _content, ranges: false);
        var item = Item();
        File.WriteAllBytes(item.LocalPath + ".part", _content[..1_000_000]);
        await _engine.DownloadAsync(item, default);
        AssertDownloaded(item);
    }

    [Fact]
    public async Task Skips_a_file_that_is_already_there()
    {
        var item = Item();
        File.WriteAllBytes(item.LocalPath, _content);
        await _engine.DownloadAsync(item, default);
        Assert.Equal(FileState.Done, item.State);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task Downloads_files_of_unknown_size_without_resume()
    {
        _server.File("/f/a", _content);
        var item = Item(hash: false, size: null, resumable: false);
        File.WriteAllBytes(item.LocalPath + ".part", new byte[10]);
        await _engine.DownloadAsync(item, default);
        AssertDownloaded(item);
        Assert.Empty(RangedRequests);
    }

    [Fact]
    public async Task Trusts_the_server_over_a_wrong_size_in_the_listing()
    {
        _server.File("/f/a", _content);
        var item = Item(hash: false, size: _content.Length + 777);
        await _engine.DownloadAsync(item, default);
        AssertDownloaded(item);
        Assert.Equal(_content.Length, item.Size);
    }

    [Fact]
    public async Task Cancel_keeps_the_part_file_for_later()
    {
        _server.File("/f/a", _content, _ => new Fault(CutAfter: 500_000, Hang: true));
        var item = Item();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _engine.DownloadAsync(item, cts.Token));
        Assert.Equal(FileState.Canceled, item.State);
        Assert.Equal(500_000, new FileInfo(item.LocalPath + ".part").Length);
    }

    [Fact]
    public async Task Waits_for_the_network_instead_of_failing()
    {
        _server.File("/f/a", _content);
        int asked = 0;
        _engine.NetworkAvailable = () => Interlocked.Increment(ref asked) > 3;
        _engine.NetworkPoll = TimeSpan.FromMilliseconds(20);
        var item = Item();
        await _engine.DownloadAsync(item, default);
        AssertDownloaded(item);
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task Gives_failed_files_a_second_round()
    {
        // Fails long enough to exhaust the first round (MaxFailures 4 means 5 requests), then recovers.
        _server.File("/f/a", _content, n => n <= 5 ? new Fault(Status: 503) : null);
        _server.File("/f/b", _content);
        var items = new List<FileItem> { Item("a"), Item("b") };
        await _engine.DownloadAllAsync(items, default);
        Assert.All(items, AssertDownloaded);
    }

    [Fact]
    public async Task A_missing_file_is_not_tried_again_in_the_second_round()
    {
        var items = new List<FileItem> { Item("gone") };
        await _engine.DownloadAllAsync(items, default);
        Assert.Equal(FileState.Failed, items[0].State);
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task Finished_files_are_marked_as_downloaded_from_the_internet()
    {
        _server.File("/f/a", _content);
        var when = new DateTimeOffset(2024, 3, 5, 10, 20, 30, TimeSpan.Zero);
        var item = Item(modified: when);
        await _engine.DownloadAsync(item, default);
        Assert.Contains("ZoneId=3", File.ReadAllText(item.LocalPath + ":Zone.Identifier"));
        Assert.Equal(when.UtcDateTime, File.GetLastWriteTimeUtc(item.LocalPath));
    }

    [Fact]
    public async Task A_file_of_the_right_size_but_wrong_content_is_replaced()
    {
        _server.File("/f/a", _content);
        var item = Item();
        File.WriteAllBytes(item.LocalPath, new byte[_content.Length]);
        await _engine.DownloadAsync(item, default);
        AssertDownloaded(item);
        Assert.Equal("", item.Message);
    }

    [Fact]
    public async Task Keeps_trying_while_the_service_cannot_be_reached()
    {
        _server.File("/f/a", _content);
        _engine.MaxFailures = 1;
        _engine.StallTimeout = TimeSpan.FromSeconds(20);   // a refused connection takes Windows about two seconds to report
        int calls = 0;
        // A port nothing listens on, for more attempts than a file is normally given.
        _provider.UrlFor = _ => Interlocked.Increment(ref calls) <= 3 ? "http://127.0.0.1:9/f/a" : null;
        var item = Item();
        await _engine.DownloadAsync(item, default);
        AssertDownloaded(item);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task Two_links_with_the_same_folder_name_do_not_merge()
    {
        _provider.Root = new RemoteEntry { Id = "root", Name = "Photos", IsFolder = true };
        _provider.Folders["root"] = [new RemoteEntry { Id = "1", Name = "a.jpg", Size = 1 }];
        var found = new List<FileItem>();
        var roots = new HashSet<string>();
        await _engine.ScanAsync(_provider, new Uri("https://example.test/1"), _dir, found.Add, default, roots);
        await _engine.ScanAsync(_provider, new Uri("https://example.test/2"), _dir, found.Add, default, roots);
        Assert.Equal([@"Photos\a.jpg", @"Photos (2)\a.jpg"], found.Select(f => f.DisplayPath).ToArray());
    }

    [Fact]
    public async Task A_remote_item_cannot_take_another_files_temporary_name()
    {
        _provider.Root = new RemoteEntry { Id = "root", Name = "Share", IsFolder = true };
        _provider.Folders["root"] =
        [
            new RemoteEntry { Id = "2", Name = "a.iso.part", Size = 1 },
            new RemoteEntry { Id = "1", Name = "a.iso", Size = 1 },
        ];
        var found = new List<FileItem>();
        await _engine.ScanAsync(_provider, new Uri("https://example.test/x"), _dir, found.Add, default);
        var names = found.Select(f => f.DisplayPath).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal([@"Share\a.iso", @"Share\a.iso (2).part"], names);
    }

    [Fact]
    public async Task Scan_mirrors_the_tree_with_safe_unique_names()
    {
        _provider.Root = new RemoteEntry { Id = "root", Name = "My: Share", IsFolder = true };
        _provider.Folders["root"] =
        [
            new RemoteEntry { Id = "1", Name = "report.txt", Size = 1 },
            new RemoteEntry { Id = "2", Name = "Report.txt", Size = 1 },
            new RemoteEntry { Id = "3", Name = "a/b?.txt", Size = 1 },
            new RemoteEntry { Id = "4", Name = "CON", Size = 1 },
            new RemoteEntry { Id = "sub", Name = "Sub.", IsFolder = true },
            new RemoteEntry { Id = "root", Name = "Loop", IsFolder = true },
        ];
        _provider.Folders["sub"] = [new RemoteEntry { Id = "5", Name = "deep.bin", Size = 1 }];

        var found = new List<FileItem>();
        await _engine.ScanAsync(_provider, new Uri("https://example.test/x"), _dir, f => { lock (found) found.Add(f); }, default);

        var names = found.Select(f => f.DisplayPath).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
        [
            @"My_ Share\Report.txt",
            @"My_ Share\Sub\deep.bin",
            @"My_ Share\_CON",
            @"My_ Share\a_b_.txt",
            @"My_ Share\report (2).txt",
        ], names);
        Assert.True(Directory.Exists(Path.Combine(_dir, "My_ Share", "Sub")));
    }

    [Fact]
    public async Task Downloads_many_files_in_parallel()
    {
        var items = new List<FileItem>();
        for (int i = 0; i < 40; i++)
        {
            int k = i;
            _server.File("/f/p" + i, _content, n => n == 1 && k % 3 == 0 ? new Fault(CutAfter: 100_000) : null);
            items.Add(Item("p" + i));
        }
        _engine.Parallelism = 6;
        await _engine.DownloadAllAsync(items, default);
        Assert.All(items, AssertDownloaded);
    }
}
