using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;

namespace CloudLink.Core;

/// <summary>
/// Walks shared folders and downloads their files. A file is written to "name.part",
/// resumed from where it stopped after any interruption, checked against the size and
/// checksum the service reports, and only then renamed to its real name.
/// </summary>
public sealed class DownloadEngine(HttpClient http)
{
    long _transferred;

    public int Parallelism { get; set; } = 4;
    /// <summary>Failures in a row, without any data arriving in between, before a file is given up on.</summary>
    public int MaxFailures { get; set; } = 8;
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public Func<int, TimeSpan?, TimeSpan> Backoff { get; set; } = Errors.Backoff;
    /// <summary>While this says no, downloads wait instead of using up their attempts.</summary>
    public Func<bool> NetworkAvailable { get; set; } = NetworkInterface.GetIsNetworkAvailable;
    public TimeSpan NetworkPoll { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>Tag finished files as coming from the internet, as browsers do, so Windows keeps its usual guard up.</summary>
    public bool MarkAsDownloaded { get; set; } = true;

    /// <summary>Bytes that actually came over the network, for the speed display.</summary>
    public long BytesTransferred => Interlocked.Read(ref _transferred);

    /// <param name="takenRoots">Names already used at the top of the destination in this run, so that
    /// two links whose folders share a name do not merge into one.</param>
    public async Task ScanAsync(ICloudProvider provider, Uri link, string destination,
        Action<FileItem> found, CancellationToken ct, HashSet<string>? takenRoots = null)
    {
        var root = await provider.ResolveAsync(link, ct);
        takenRoots ??= [];
        string rootName = PathSafety.SanitizeName(root.Name);
        rootName = root.IsFolder ? PathSafety.Unique(takenRoots, rootName) : PathSafety.UniqueFile(takenRoots, rootName);
        if (!root.IsFolder)
        {
            found(MakeItem(provider, root, Path.Combine(destination, rootName), rootName));
            return;
        }

        var visited = new ConcurrentDictionary<string, bool>();
        using var limit = new SemaphoreSlim(4);
        await WalkAsync(root, Path.Combine(destination, rootName), rootName);

        async Task WalkAsync(RemoteEntry folder, string localDir, string display)
        {
            if (!visited.TryAdd(folder.Id, true)) return;

            IReadOnlyList<RemoteEntry> children;
            await limit.WaitAsync(ct);
            try { children = await provider.ListAsync(folder, ct); }
            finally { limit.Release(); }

            Directory.CreateDirectory(localDir);
            var taken = new HashSet<string>();
            var subfolders = new List<Task>();
            // Sorted so that two items with the same name get the same local names on every run.
            foreach (var child in children.OrderBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Id, StringComparer.Ordinal))
            {
                string safe = PathSafety.SanitizeName(child.Name);
                string name = child.IsFolder ? PathSafety.Unique(taken, safe) : PathSafety.UniqueFile(taken, safe);
                string local = Path.Combine(localDir, name);
                string shown = display + "\\" + name;
                if (child.IsFolder) subfolders.Add(WalkAsync(child, local, shown));
                else found(MakeItem(provider, child, local, shown));
            }
            await Task.WhenAll(subfolders);
        }
    }

    static FileItem MakeItem(ICloudProvider provider, RemoteEntry entry, string localPath, string display) =>
        new() { Provider = provider, Entry = entry, LocalPath = localPath, DisplayPath = display, Size = entry.Size };

    public async Task DownloadAllAsync(IReadOnlyList<FileItem> items, CancellationToken ct)
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Parallelism, 1, 16), CancellationToken = ct };
        await Parallel.ForEachAsync(items.Where(i => i.State != FileState.Done), options,
            async (item, c) => await DownloadAsync(item, c));

        // Whatever broke those files may have passed while the rest were downloading: one more round for them.
        var again = items.Where(i => i.State == FileState.Failed && i.RetryableFailure).ToList();
        if (again.Count == 0) return;
        Log.Write($"Second round for {again.Count} failed file(s)");
        await Task.Delay(Backoff(3, null), ct);
        await Parallel.ForEachAsync(again, options, async (item, c) => await DownloadAsync(item, c));
    }

    public async Task DownloadAsync(FileItem item, CancellationToken ct)
    {
        var entry = item.Entry;
        if (entry.SkipReason is not null)
        {
            item.Set(FileState.Skipped, entry.SkipReason);
            return;
        }

        string part = item.LocalPath + PathSafety.PartSuffix;
        int failures = 0, unreachable = 0, attempts = 0, checksumFailures = 0;
        byte[]? previousBadHash = null;

        while (true)
        {
            bool progressed = false;
            try
            {
                ct.ThrowIfCancellationRequested();
                while (!NetworkAvailable())
                {
                    item.Set(FileState.Retrying, "Waiting for the network to come back");
                    await Task.Delay(NetworkPoll, ct);
                }
                attempts++;

                if (await AlreadyCompleteAsync(item, ct))
                {
                    long have = new FileInfo(item.LocalPath).Length;
                    item.Size = have;
                    item.Received = have;
                    item.Set(FileState.Done, "Already downloaded");
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(item.LocalPath)!);
                long offset = 0;
                if (File.Exists(part))
                {
                    if (entry.Resumable) offset = new FileInfo(part).Length;
                    else File.Delete(part);
                }
                if (entry.Size is long listed && offset > listed)
                {
                    File.Delete(part);
                    offset = 0;
                }

                item.Received = offset;
                item.Set(FileState.Downloading);
                long? total = await FetchAsync(item, part, offset, () => progressed = true, ct);

                long length = new FileInfo(part).Length;
                if (total is long expected && length != expected)
                    throw new CloudException($"Only {Format.Bytes(length)} of {Format.Bytes(expected)} arrived", true);

                string note = "";
                if (entry.Hash is not null)
                {
                    item.Set(FileState.Verifying);
                    byte[] actual = await Hashing.ComputeAsync(part, entry.HashKind, ct);
                    if (!actual.AsSpan().SequenceEqual(entry.Hash))
                    {
                        if (previousBadHash is not null && actual.AsSpan().SequenceEqual(previousBadHash))
                        {
                            // Two complete, independent downloads agree with each other but not with the
                            // listing. The service lists stale checksums for some files (SharePoint rewrites
                            // Office documents on the way out), so the content is what the server holds.
                            note = "Done (checksum in the listing is out of date)";
                            Log.Write($"Checksum differs from listing twice with identical content: {item.DisplayPath}");
                        }
                        else
                        {
                            previousBadHash = actual;
                            checksumFailures++;
                            File.Delete(part);
                            item.Received = 0;
                            throw new ChecksumException();
                        }
                    }
                }

                if (MarkAsDownloaded) MarkOfTheWeb(part);
                File.Move(part, item.LocalPath, overwrite: true);
                if (entry.Modified is { } modified)
                {
                    try { File.SetLastWriteTimeUtc(item.LocalPath, modified.UtcDateTime); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException) { }
                }
                item.Size = length;
                item.Received = length;
                item.Set(FileState.Done, note);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                item.Set(FileState.Canceled);
                throw;
            }
            catch (Exception ex)
            {
                // Not reaching the service at all says nothing about the file: keep trying for as long as
                // it takes, at the longest pause, without using up the file's attempts.
                bool offline = Errors.IsUnreachable(ex) || !NetworkAvailable();
                if (offline)
                {
                    unreachable++;
                }
                else
                {
                    unreachable = 0;
                    if (ex is not ChecksumException && progressed) failures = 0;
                    failures++;
                }
                bool giveUp = !Errors.IsTransient(ex) || failures > MaxFailures || checksumFailures > 3;
                string why = offline ? "No connection to the service" : Errors.Describe(ex);
                Log.Write($"{item.DisplayPath}: attempt {attempts}: {ex.GetType().Name}: {why}");
                if (giveUp)
                {
                    item.RetryableFailure = Errors.IsTransient(ex);
                    item.Set(FileState.Failed, why);
                    return;
                }

                var wait = Backoff(offline ? unreachable : failures, (ex as CloudException)?.RetryAfter);
                item.Set(FileState.Retrying, $"{why}. Trying again in {Math.Max(1, (int)wait.TotalSeconds)} s");
                try
                {
                    await Task.Delay(wait, ct);
                }
                catch (OperationCanceledException)
                {
                    item.Set(FileState.Canceled);
                    throw;
                }
            }
        }
    }

    /// <summary>Writes the Zone.Identifier stream (zone 3, internet). Silently skipped on drives without streams.</summary>
    static void MarkOfTheWeb(string path)
    {
        try { File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { }
    }

    sealed class ChecksumException() : CloudException("The downloaded data did not match the checksum", true);

    /// <summary>
    /// Whether the destination already holds this file. A file CloudLink finished earlier carries the
    /// remote modified time; any other file of the right size has to pass the checksum first.
    /// </summary>
    static async Task<bool> AlreadyCompleteAsync(FileItem item, CancellationToken ct)
    {
        var entry = item.Entry;
        var info = new FileInfo(item.LocalPath);
        if (!info.Exists) return false;

        bool stamped = entry.Modified is { } modified
            && Math.Abs((info.LastWriteTimeUtc - modified.UtcDateTime).TotalSeconds) < 2;
        if (stamped && (info.Length > 0 || entry.Size == 0)) return true;
        if (entry.Size != info.Length) return false;
        if (entry.Hash is null) return true;

        item.Set(FileState.Verifying);
        byte[] actual = await Hashing.ComputeAsync(item.LocalPath, entry.HashKind, ct);
        if (!actual.AsSpan().SequenceEqual(entry.Hash)) return false;
        if (entry.Modified is { } stamp)
        {
            try { File.SetLastWriteTimeUtc(item.LocalPath, stamp.UtcDateTime); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException) { }
        }
        return true;
    }

    /// <summary>Appends the rest of the file to the .part file. Returns the full size if the server stated it.</summary>
    async Task<long?> FetchAsync(FileItem item, string part, long offset, Action progressed, CancellationToken ct)
    {
        var entry = item.Entry;
        if (offset > 0 && entry.Size == offset) return offset;   // everything is already on disk; just verify it

        using var request = await item.Provider.CreateDownloadRequestAsync(entry, ct);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);

        // One watchdog for the whole transfer: it is pushed back whenever data arrives.
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
        watchdog.CancelAfter(StallTimeout);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, watchdog.Token);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The server has nothing beyond what is on disk: either that is the whole file, or the part is stale.
            if (response.Content.Headers.ContentRange?.Length == offset) return offset;
            File.Delete(part);
            item.Received = 0;
            throw new CloudException("The partial download no longer matches the file; starting over", true);
        }
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(watchdog.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized) item.Provider.OnUnauthorized();
            throw Errors.FromResponse(response, body);
        }

        long? total;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range?.From != offset)
            {
                File.Delete(part);
                item.Received = 0;
                throw new CloudException("The server resumed at the wrong position; starting over", true);
            }
            total = range.Length ?? offset + response.Content.Headers.ContentLength;
        }
        else
        {
            // A plain 200 means the server sent the file from the beginning.
            offset = 0;
            item.Received = 0;
            total = response.Content.Headers.ContentLength;
        }
        if (total is not null) item.Size = total;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            await using var file = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read,
                1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            file.SetLength(offset);
            file.Position = offset;
            await using var network = await response.Content.ReadAsStreamAsync(ct);
            while (true)
            {
                watchdog.CancelAfter(StallTimeout);
                int n = await network.ReadAsync(buffer, watchdog.Token);
                if (n == 0) break;
                await file.WriteAsync(buffer.AsMemory(0, n), ct);
                item.AddReceived(n);
                Interlocked.Add(ref _transferred, n);
                progressed();
            }
            await file.FlushAsync(ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return total;
    }
}
