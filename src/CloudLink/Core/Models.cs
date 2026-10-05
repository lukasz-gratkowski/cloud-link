using System.ComponentModel;
using System.Net.Http;

namespace CloudLink.Core;

public enum HashKind { None, Md5, Sha1, Sha256, QuickXor }

/// <summary>A file or folder as the cloud service describes it.</summary>
public sealed record RemoteEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsFolder { get; init; }
    public long? Size { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public HashKind HashKind { get; init; }
    public byte[]? Hash { get; init; }
    /// <summary>False when the server cannot continue a partial download (Google exports).</summary>
    public bool Resumable { get; init; } = true;
    /// <summary>Set when the item exists but has no downloadable content.</summary>
    public string? SkipReason { get; init; }

    // Provider-specific addressing.
    public string? DriveId { get; init; }
    public string? ShareToken { get; init; }
    public bool IsShareRoot { get; init; }
    public string? ResourceKey { get; init; }
    public string? ExportMime { get; init; }
    public string? ExportUrl { get; init; }
}

public interface ICloudProvider
{
    string Name { get; }
    bool Matches(Uri link);
    Task<RemoteEntry> ResolveAsync(Uri link, CancellationToken ct);
    Task<IReadOnlyList<RemoteEntry>> ListAsync(RemoteEntry folder, CancellationToken ct);
    /// <summary>A fresh, authorised request for the file's content. The engine adds the Range header.</summary>
    Task<HttpRequestMessage> CreateDownloadRequestAsync(RemoteEntry file, CancellationToken ct);
    /// <summary>The server refused the current access token; get a new one next time.</summary>
    void OnUnauthorized();
}

public interface ITokenSource
{
    bool IsSignedIn { get; }
    Task<string> GetAccessTokenAsync(CancellationToken ct);
    void Invalidate();
}

public enum FileState { Waiting, Downloading, Verifying, Retrying, Done, Skipped, Failed, Canceled }

/// <summary>One file of a download run. Workers update it; the window refreshes it on a timer.</summary>
public sealed class FileItem : INotifyPropertyChanged
{
    long _received;
    long _size = -1;
    volatile FileState _state;
    volatile string _message = "";

    public required ICloudProvider Provider { get; init; }
    public required RemoteEntry Entry { get; init; }
    public required string LocalPath { get; init; }
    public required string DisplayPath { get; init; }

    public string FileName => System.IO.Path.GetFileName(DisplayPath);
    /// <summary>The folder part of the path, shown dimmed after the name.</summary>
    public string FolderText => System.IO.Path.GetDirectoryName(DisplayPath) is { Length: > 0 } dir ? "   " + dir : "";

    /// <summary>What a screen reader says for the row.</summary>
    public override string ToString() => $"{DisplayPath}, {StatusText}";

    public event PropertyChangedEventHandler? PropertyChanged;

    public long? Size
    {
        get { long s = Interlocked.Read(ref _size); return s < 0 ? null : s; }
        set => Interlocked.Exchange(ref _size, value ?? -1);
    }

    public long Received
    {
        get => Interlocked.Read(ref _received);
        set => Interlocked.Exchange(ref _received, value);
    }

    public void AddReceived(long n) => Interlocked.Add(ref _received, n);

    public FileState State => _state;
    public string Message => _message;

    public void Set(FileState state, string message = "")
    {
        _state = state;
        _message = message;
        Refresh();
    }

    /// <summary>Set when the last failure was of a kind that another try might get past.</summary>
    public bool RetryableFailure { get; set; }

    /// <summary>Drives the colour of the status dot: ok, active, warn, error or idle.</summary>
    public string Kind => _state switch
    {
        FileState.Done => "ok",
        FileState.Downloading or FileState.Verifying => "active",
        FileState.Retrying or FileState.Skipped => "warn",
        FileState.Failed => "error",
        _ => "idle",
    };

    public bool IsProblem => _state is FileState.Failed or FileState.Skipped;
    public bool IsActive => _state is FileState.Downloading or FileState.Verifying or FileState.Retrying;
    public bool IsFinished => _state is FileState.Done or FileState.Skipped or FileState.Failed;

    public double Percent
    {
        get
        {
            if (_state == FileState.Done) return 100;
            long? size = Size;
            if (size is null or 0) return 0;
            return Math.Min(100, Received * 100.0 / size.Value);
        }
    }

    public string SizeText => Size is long s ? Format.Bytes(s) : (Received > 0 ? Format.Bytes(Received) : "");

    public string StatusText => _state switch
    {
        FileState.Waiting => "Waiting",
        FileState.Downloading => Size is > 0 ? $"{Percent:0}%" : "Downloading",
        FileState.Verifying => "Checking",
        FileState.Retrying => _message,
        FileState.Done => _message.Length > 0 ? _message : "Done",
        FileState.Skipped => "Skipped: " + _message,
        FileState.Failed => "Failed: " + _message,
        FileState.Canceled => "Stopped",
        _ => "",
    };

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

public static class Format
{
    public static string Bytes(long n)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = n;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{n} B" : $"{v:0.##} {units[u]}";
    }

    public static string Duration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes} min";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes} min {t.Seconds} s";
        return $"{Math.Max(1, (int)t.TotalSeconds)} s";
    }
}
