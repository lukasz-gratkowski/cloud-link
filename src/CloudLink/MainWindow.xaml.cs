using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using CloudLink.Core;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace CloudLink;

public partial class MainWindow : Window
{
    /// <summary>What the status card is showing.</summary>
    enum Stage { Idle, Looking, Running, Success, Problems, Stopped, Error }

    readonly Services _services = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    List<FileItem> _items = [];
    DownloadEngine? _engine;
    CancellationTokenSource? _cts;
    bool _busy, _downloading, _looking, _moving;
    int _filterSignature = -1;
    int _scanned, _ticks;
    long _lastTransferred;
    long _lastTick;
    double _speed;
    int _lastClipboardOffer;   // a hash of the clipboard text last looked at; the text itself is not kept

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = "Version " + AppInfo.Brief;
        string last = _services.Settings.LastFolder;
        DestinationBox.Text = KeepOutOfProgramFolder(last.Length > 0 ? last
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 24);
        _timer.Tick += (_, _) => Tick();
        Activated += (_, _) => OfferClipboardLink();
        Closing += OnClosing;
        StateChanged += (_, _) => Animate();
        Loaded += OnLoaded;
        UpdateAccountButtons();
    }

    // ---- links ---------------------------------------------------------------

    List<(Uri Link, ICloudProvider Provider)> ParseLinks(string text, out string? problem)
    {
        problem = null;
        var result = new List<(Uri, ICloudProvider)>();
        foreach (string raw in text.Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = raw.Trim('<', '>', '"', '\'', ',', ';');
            if (!candidate.Contains("://")) candidate = "https://" + candidate;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                problem = $"\"{Shorten(raw)}\" is not an https link.";
                continue;
            }
            var provider = _services.Providers.FirstOrDefault(p => p.Matches(uri));
            if (provider is null)
            {
                problem = $"\"{Shorten(uri.Host)}\" is not a OneDrive or Google Drive address.";
                continue;
            }
            if (!result.Any(r => r.Item1 == uri)) result.Add((uri, provider));
        }
        return result;
    }

    static string Shorten(string s) => s.Length <= 60 ? s : s[..57] + "…";

    void OfferClipboardLink()
    {
        if (_busy || LinksBox.Text.Trim().Length > 0) return;
        try
        {
            if (!Clipboard.ContainsText()) return;
            string text = Clipboard.GetText().Trim();
            if (text.Length is 0 or > 4000 || text.GetHashCode() == _lastClipboardOffer) return;
            _lastClipboardOffer = text.GetHashCode();
            if (ParseLinks(text, out string? problem).Count > 0 && problem is null)
            {
                LinksBox.Text = text;
                if (StatusText.Text == "Ready when you are")
                    DetailText.Text = "Found a link on the clipboard. Press Download to start.";
            }
        }
        catch (COMException) { }   // another program holds the clipboard
    }

    void AddLinks(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        string existing = LinksBox.Text.TrimEnd();
        LinksBox.Text = existing.Length == 0 ? text : existing + Environment.NewLine + text;
        LinksBox.CaretIndex = LinksBox.Text.Length;
    }

    void Paste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText()) AddLinks(Clipboard.GetText());
        }
        catch (COMException) { }
    }

    void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_busy && e.Data.GetDataPresent(DataFormats.UnicodeText) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Window_Drop(object sender, DragEventArgs e)
    {
        if (!_busy && e.Data.GetData(DataFormats.UnicodeText) is string text && text.Length <= 4000) AddLinks(text);
    }

    void LinksBox_TextChanged(object sender, TextChangedEventArgs e) =>
        LinksHint.Visibility = LinksBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Where should the files go?" };
        if (Directory.Exists(DestinationBox.Text)) dialog.InitialDirectory = DestinationBox.Text;
        if (dialog.ShowDialog(this) == true) DestinationBox.Text = dialog.FolderName;
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string path = DestinationBox.Text.Trim();
        // By full path: a bare "explorer.exe" would first be looked for next to CloudLink, where downloads may land.
        string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(explorer) { ArgumentList = { path } });
        else if (!_busy) Say(Stage.Idle, "That folder does not exist yet", "It is created when a download starts.");
    }

    // ---- accounts ------------------------------------------------------------

    void UpdateAccountButtons()
    {
        var s = _services.Settings;
        bool microsoft = _services.MicrosoftAuth.IsSignedIn, google = _services.GoogleAuth.IsSignedIn;
        bool googleKey = s.GoogleApiKey.Trim().Length > 0;
        MicrosoftText.Text = microsoft ? (s.MicrosoftAccount.Length > 0 ? s.MicrosoftAccount : "Microsoft: signed in")
            : s.HasMicrosoftClientId ? "Microsoft: sign in" : "Microsoft: set up";
        MicrosoftDot.SetResourceReference(Shape.FillProperty, microsoft ? "OkBrush" : "IdleBrush");
        MicrosoftButton.ToolTip = microsoft ? "Signed in for OneDrive links. Click to sign out."
            : s.HasMicrosoftClientId ? "Sign in with the Microsoft account that should open OneDrive links"
            : "OneDrive needs an application ID first. Click to set it up.";
        System.Windows.Automation.AutomationProperties.SetName(MicrosoftButton, "Microsoft account: " + (microsoft ? MicrosoftText.Text + ", signed in" : "not signed in"));
        GoogleText.Text = google ? (s.GoogleAccount.Length > 0 ? s.GoogleAccount : "Google: signed in")
            : googleKey ? "Google: public links only" : "Google: set up";
        GoogleDot.SetResourceReference(Shape.FillProperty, google ? "OkBrush" : googleKey ? "WarnBrush" : "IdleBrush");
        GoogleButton.ToolTip = google ? "Signed in for Google Drive links. Click to sign out."
            : googleKey ? "An API key is set, which opens public links. Click to sign in for private ones."
            : "Google Drive needs a one-time setup. Click to start.";
        System.Windows.Automation.AutomationProperties.SetName(GoogleButton, "Google account: " + (google ? GoogleText.Text + ", signed in" : GoogleText.Text));
    }

    async void Microsoft_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_services.MicrosoftAuth.IsSignedIn)
        {
            if (Ask("Sign out of the Microsoft account?"))
            {
                _services.MicrosoftAuth.SignOut();
                _services.Settings.MicrosoftAccount = "";
                _services.Settings.Save();
                UpdateAccountButtons();
            }
            return;
        }
        if (!MicrosoftConfigured()) return;
        await RunSignInAsync(SignInMicrosoftAsync);
    }

    const string MicrosoftSetupReason = "This build of CloudLink has no Microsoft application ID built in, so OneDrive needs one entered below.";

    /// <summary>Whether there is an application ID to sign in to Microsoft with, offering Settings if not.</summary>
    bool MicrosoftConfigured()
    {
        if (_services.Settings.HasMicrosoftClientId) return true;
        ShowSettings(MicrosoftSetupReason);
        return _services.Settings.HasMicrosoftClientId;
    }

    async void Google_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_services.GoogleAuth.IsSignedIn)
        {
            if (Ask("Sign out of the Google account?"))
            {
                _services.GoogleAuth.SignOut();
                _services.Settings.GoogleAccount = "";
                _services.Settings.Save();
                UpdateAccountButtons();
            }
            return;
        }
        if (!GoogleSignInConfigured())
        {
            ShowSettings("Google sign-in needs a client ID and secret first. The steps are below.");
            if (!GoogleSignInConfigured()) return;
        }
        await RunSignInAsync(SignInGoogleAsync);
    }

    bool GoogleSignInConfigured() =>
        _services.Settings.GoogleClientId.Trim().Length > 0 && _services.Settings.GoogleClientSecret.Trim().Length > 0;

    async Task RunSignInAsync(Func<CancellationToken, Task> signIn)
    {
        SetBusy(true);
        _cts = new CancellationTokenSource();
        try
        {
            await signIn(_cts.Token);
            Say(Stage.Idle, "Signed in", "Paste a shared link above and press Download.");
        }
        catch (OperationCanceledException) when (_cts?.IsCancellationRequested == true)
        {
            Say(Stage.Idle, "Sign-in stopped", "Nothing was changed.");
        }
        catch (Exception ex)
        {
            Log.Write("Sign-in: " + ex);
            Say(Stage.Error, "Sign-in did not work", Errors.Describe(ex));
        }
        finally
        {
            SetBusy(false);
        }
    }

    async Task SignInMicrosoftAsync(CancellationToken ct)
    {
        Say(Stage.Looking, "Waiting for the Microsoft sign-in", "Finish signing in in your browser. Microsoft's page mentions editing files; " +
            "that permission is what lets an app open share links, and CloudLink only downloads. Stop cancels.");
        await _services.MicrosoftAuth.SignInAsync(ct);
        Activate();
        try { _services.Settings.MicrosoftAccount = await _services.OneDrive.GetAccountNameAsync(ct); }
        catch (CloudException ex) { Log.Write("Account name: " + ex.Message); }
        _services.Settings.Save();
        UpdateAccountButtons();
    }

    async Task SignInGoogleAsync(CancellationToken ct)
    {
        Say(Stage.Looking, "Waiting for the Google sign-in", "Finish signing in in your browser, then come back here. Stop cancels.");
        await _services.GoogleAuth.SignInAsync(ct);
        Activate();
        try { _services.Settings.GoogleAccount = await _services.GoogleDrive.GetAccountNameAsync(ct); }
        catch (CloudException ex) { Log.Write("Account name: " + ex.Message); }
        _services.Settings.Save();
        UpdateAccountButtons();
    }

    /// <summary>Makes sure the service behind a link can be reached, signing in if that is what is missing.</summary>
    async Task<bool> EnsureAccessAsync(ICloudProvider provider, CancellationToken ct)
    {
        if (provider == _services.OneDrive)
        {
            if (!MicrosoftConfigured()) return false;
            if (!await StoredSignInWorksAsync(_services.MicrosoftAuth, ct)) await SignInMicrosoftAsync(ct);
            return true;
        }
        bool hadGoogleSignIn = _services.GoogleAuth.IsSignedIn;
        if (await StoredSignInWorksAsync(_services.GoogleAuth, ct)) return true;
        if (hadGoogleSignIn && GoogleSignInConfigured())
        {
            // It was there and Google no longer accepts it: ask again rather than quietly fall back to
            // the API key, which would turn private links into "not found".
            await SignInGoogleAsync(ct);
            return true;
        }
        if (_services.GoogleDrive.CanWork) return true;
        if (!GoogleSignInConfigured())
        {
            ShowSettings("Google Drive needs a one-time setup: either an API key (public links) or a client ID and secret (sign-in).");
            if (_services.GoogleDrive.CanWork) return true;
            if (!GoogleSignInConfigured()) return false;
        }
        await SignInGoogleAsync(ct);
        return true;
    }

    /// <summary>
    /// Tries the stored sign-in before the work starts. One that the service no longer accepts (revoked,
    /// expired, or made through a different application ID) is dropped here, so the user is asked to sign
    /// in now rather than seeing every file fail later.
    /// </summary>
    static async Task<bool> StoredSignInWorksAsync(OAuthSession auth, CancellationToken ct)
    {
        if (!auth.IsSignedIn) return false;
        try
        {
            await auth.GetAccessTokenAsync(ct);
            return true;
        }
        catch (SignInRequiredException)
        {
            return false;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && Errors.IsTransient(ex))
        {
            // Not an answer about the sign-in. The listing that follows retries and reports it properly.
            return true;
        }
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) ShowSettings(null);
    }

    void ShowSettings(string? reason)
    {
        var settings = _services.Settings;
        string microsoftBefore = settings.EffectiveMicrosoftClientId, googleBefore = settings.GoogleClientId.Trim();
        new SettingsWindow(settings, reason) { Owner = this }.ShowDialog();

        // A sign-in belongs to the application it was made through, so a changed ID ends it, on disk too.
        if (!string.Equals(microsoftBefore, settings.EffectiveMicrosoftClientId, StringComparison.OrdinalIgnoreCase))
        {
            _services.MicrosoftAuth.SignOut();
            settings.MicrosoftAccount = "";
        }
        if (googleBefore != settings.GoogleClientId.Trim())
        {
            _services.GoogleAuth.SignOut();
            settings.GoogleAccount = "";
        }
        settings.Save();
        UpdateAccountButtons();
    }

    void Help_Executed(object sender, ExecutedRoutedEventArgs e) => new HelpWindow { Owner = this }.ShowDialog();

    void Stop_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_busy) Cancel_Click(sender, e);
    }

    // ---- download ------------------------------------------------------------

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var links = ParseLinks(LinksBox.Text, out string? problem);
        if (problem is not null || links.Count == 0)
        {
            Say(Stage.Error, problem is null ? "There is no link yet" : "That link cannot be used",
                problem ?? "Paste a shared OneDrive or Google Drive link into the box above.");
            LinksBox.Focus();
            return;
        }

        string destination = DestinationBox.Text.Trim();
        try
        {
            // A packaged app has no working directory worth resolving a relative name against.
            if (!Path.IsPathFullyQualified(destination))
                throw new ArgumentException(@"Enter the full path of a folder, for example C:\Users\you\Downloads, or press Browse.");
            destination = KeepOutOfProgramFolder(Path.GetFullPath(destination));
            if (AppPackage.IsPackaged && AppPaths.IsUnderAppData(destination))
                throw new ArgumentException("The Microsoft Store version of CloudLink cannot save under AppData: Windows may keep such files where File Explorer does not show them. Choose another folder, for example Downloads.");
            Directory.CreateDirectory(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Say(Stage.Error, "That folder cannot be used", ex.Message);
            DestinationBox.Focus();
            return;
        }
        DestinationBox.Text = destination;
        _services.Settings.LastFolder = destination;
        _services.Settings.Save();

        SetBusy(true);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _engine = new DownloadEngine(_services.DownloadClient) { Parallelism = _services.Settings.ParallelDownloads };
        _items = [];
        FilesList.ItemsSource = null;
        EmptyState.Visibility = Visibility.Visible;
        StatsPanel.Visibility = Visibility.Collapsed;
        CountText.Text = "FILES";
        FilterAll.IsChecked = true;
        SetProgress(0, animate: false);
        PreventSleep(true);

        string step = "Sign-in did not work";
        object? opening = null;   // the service whose link is being looked through
        try
        {
            foreach (var provider in links.Select(l => l.Provider).Distinct())
            {
                Say(Stage.Looking, "Checking the sign-in", "");
                if (!await EnsureAccessAsync(provider, ct))
                {
                    Say(Stage.Error, $"{provider.Name} is not set up yet", "Open Settings to finish the one-time setup, then press Download again.");
                    return;
                }
            }

            _scanned = 0;
            step = "The link could not be opened";
            Say(Stage.Looking, "Looking through the shared items", "Finding every file and folder behind the link.");
            var found = new List<FileItem>();
            var roots = new HashSet<string>();
            foreach (var (link, provider) in links)
            {
                opening = provider;
                await Task.Run(() => _engine.ScanAsync(provider, link, destination, item =>
                {
                    lock (found) found.Add(item);
                    Interlocked.Increment(ref _scanned);
                }, ct, roots), ct);
            }

            // The same file reached through two links is downloaded once.
            _items = found.GroupBy(f => f.LocalPath, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .OrderBy(f => f.DisplayPath, StringComparer.CurrentCultureIgnoreCase).ToList();
            FilesList.ItemsSource = _items;
            EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CountText.Text = _items.Count == 1 ? "1 FILE" : $"{_items.Count:N0} FILES";

            if (_items.Count == 0)
            {
                Say(Stage.Problems, "The link opened, but it is empty", "There are no files in the shared folder.");
                return;
            }
            string room = await Task.Run(() => HasRoom(destination, out string why) ? "" : why, ct);
            if (room.Length > 0)
            {
                Say(Stage.Error, "Not enough free space", room);
                return;
            }

            _lastTransferred = 0;
            _lastTick = Stopwatch.GetTimestamp();
            _speed = 0;
            step = "The download ran into a problem";
            Say(Stage.Running, "Starting the download", "");
            PercentText.Text = "0%";
            StatsPanel.Visibility = Visibility.Visible;
            _downloading = true;
            await Task.Run(() => _engine.DownloadAllAsync(_items, ct), ct);
            ShowOutcome();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            foreach (var item in _items.Where(i => i.IsActive)) item.Set(FileState.Canceled);
            Tick();
            _downloading = false;
            Say(Stage.Stopped, "Stopped", "Nothing is lost. Press Continue to carry on from where it stopped.");
            StartText.Text = "Continue";
        }
        catch (Exception ex)
        {
            Log.Write("Run failed: " + ex);
            _downloading = false;
            // "Not found" from Google Drive without a sign-in is what a private link looks like to an API key.
            bool privateForKey = ex is CloudException { StatusCode: 404 }
                && ReferenceEquals(opening, _services.GoogleDrive) && !_services.GoogleAuth.IsSignedIn;
            string hint = ex is not CloudException { StatusCode: 403 or 404 } ? ""
                : privateForKey
                    ? " An API key opens only links that anyone with the link can open. For a private link, click the Google button and sign in first."
                    : " Check that the link is complete and that the signed-in account is allowed to open it.";
            Say(Stage.Error, step, Errors.Describe(ex).TrimEnd('.') + "." + hint);
        }
        finally
        {
            _downloading = false;
            PreventSleep(false);
            UpdateAccountButtons();
            SetBusy(false);
        }
    }

    /// <summary>
    /// Files from a share must never land beside CloudLink.exe, where Windows would load a planted
    /// DLL or program from. If that folder is chosen, a "CloudLink" subfolder is used instead.
    /// </summary>
    static string KeepOutOfProgramFolder(string folder)
    {
        string own = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        string exe = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? own;
        string chosen = Path.TrimEndingDirectorySeparator(folder);
        bool same = chosen.Equals(own, StringComparison.OrdinalIgnoreCase) || chosen.Equals(exe, StringComparison.OrdinalIgnoreCase);
        return same ? Path.Combine(chosen, "CloudLink") : folder;
    }

    bool HasRoom(string destination, out string message)
    {
        message = "";
        try
        {
            long needed = _items.Where(i => i.Entry.SkipReason is null && !File.Exists(i.LocalPath)).Sum(i => i.Size ?? 0);
            foreach (var item in _items)
            {
                var part = new FileInfo(item.LocalPath + ".part");
                if (part.Exists) needed -= Math.Min(part.Length, item.Size ?? 0);
            }
            long free = new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace;
            if (needed <= free) return true;
            message = $"{Format.Bytes(needed)} to download, but only {Format.Bytes(free)} free on {Path.GetPathRoot(destination)} Choose another folder or free some space.";
            return false;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return true;   // network shares and the like: free space unknown, let the download find out
        }
    }

    void ShowOutcome()
    {
        Tick();
        _downloading = false;
        int done = _items.Count(i => i.State == FileState.Done);
        int failed = _items.Count(i => i.State == FileState.Failed);
        int skipped = _items.Count(i => i.State == FileState.Skipped);
        long bytes = _items.Where(i => i.State == FileState.Done).Sum(i => i.Size ?? 0);
        StartText.Text = "Download";

        if (failed == 0 && skipped == 0)
        {
            SetProgress(100);
            Say(Stage.Success, done == 1 ? "Done. Your file is ready" : $"Done. All {done:N0} files are ready",
                $"{Format.Bytes(bytes)} in place. Every file was checked against the size and checksum the service reports.");
            return;
        }

        var parts = new List<string> { $"{done:N0} of {_items.Count:N0} files downloaded" };
        if (failed > 0) parts.Add($"{failed:N0} failed");
        if (skipped > 0) parts.Add($"{skipped:N0} cannot be downloaded");
        Say(failed > 0 ? Stage.Error : Stage.Problems, failed > 0 ? "Finished with problems" : "Finished, with some items skipped",
            string.Join(", ", parts) + (failed > 0
                ? ". Press Try again for the failed ones; finished files are not downloaded twice."
                : ". The skipped items have no file content; the list below says why."));
        if (failed > 0) StartText.Text = "Try again";
        FilterProblems.IsChecked = true;
        ApplyFilter();
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusText.Text = "Stopping";
        DetailText.Text = "Finishing what is in hand.";
    }

    void Tick()
    {
        _ticks++;
        bool stopping = _cts?.IsCancellationRequested == true;
        if (!_downloading || _engine is null)
        {
            if (_busy && _looking && _scanned > 0 && !stopping)
                DetailText.Text = $"{_scanned:N0} files found so far.";
            return;
        }

        long received = 0, total = 0;
        int finished = 0, done = 0, problems = 0, active = 0;
        foreach (var item in _items)
        {
            if (item.IsActive) { item.Refresh(); active++; }
            if (item.IsFinished) finished++;
            if (item.State == FileState.Done) done++;
            if (item.IsProblem) problems++;
            if (item.State == FileState.Skipped) continue;
            long r = item.Received;
            received += r;
            total += Math.Max(item.Size ?? 0, r);
        }

        long now = Stopwatch.GetTimestamp();
        double seconds = Stopwatch.GetElapsedTime(_lastTick, now).TotalSeconds;
        if (seconds >= 1)
        {
            long transferred = _engine.BytesTransferred;
            double current = (transferred - _lastTransferred) / seconds;
            _speed = _speed <= 0 ? current : _speed * 0.7 + current * 0.3;
            _lastTransferred = transferred;
            _lastTick = now;
        }

        double fraction = total > 0 ? Math.Min(1, (double)received / total) : (double)finished / Math.Max(1, _items.Count);
        string percent = $"{Math.Floor(fraction * 100):0}%";
        SetProgress(fraction * 100);
        PercentText.Text = percent;
        Taskbar.ProgressValue = fraction;
        Title = $"{percent} - CloudLink";
        StatDone.Text = $"{done:N0} done";
        StatLeft.Text = $"{_items.Count - finished:N0} to go";
        StatProblems.Text = problems == 1 ? "1 problem" : $"{problems:N0} problems";

        // The In progress and Problems views change as files move on; keep them current without churning.
        int signature = FilterAll.IsChecked == true ? -1 : HashCode.Combine(finished, problems, active);
        if (signature != _filterSignature && _ticks % 4 == 0)
        {
            _filterSignature = signature;
            if (signature != -1) CollectionViewSource.GetDefaultView(_items).Refresh();
        }

        if (!_busy || stopping) return;
        StatusText.Text = $"Downloading {Format.Bytes(received)} of {Format.Bytes(total)}";
        string speed = _speed > 0 ? Format.Bytes((long)_speed) + "/s" : "Connecting";
        string left = _speed > 1024 && total > received
            ? ", about " + Format.Duration(TimeSpan.FromSeconds((total - received) / _speed)) + " left" : "";
        DetailText.Text = speed + left;
    }

    void Filter_Click(object sender, RoutedEventArgs e) => ApplyFilter();

    void ApplyFilter()
    {
        if (FilesList.ItemsSource is null) return;
        var view = CollectionViewSource.GetDefaultView(FilesList.ItemsSource);
        view.Filter = FilterProblems.IsChecked == true ? o => ((FileItem)o).IsProblem
            : FilterActive.IsChecked == true ? o => ((FileItem)o).IsActive
            : null;
    }

    void FilesList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The file name gets whatever the fixed columns leave over.
        FileColumn.Width = Math.Max(180, FilesList.ActualWidth - 90 - 110 - 280 - 56);
    }

    // ---- status card ---------------------------------------------------------

    /// <summary>Sets the headline, the explanation under it, and everything that signals the stage.</summary>
    void Say(Stage stage, string headline, string detail)
    {
        StatusText.Text = headline;
        DetailText.Text = detail;
        _looking = stage == Stage.Looking;
        _moving = stage is Stage.Looking or Stage.Running;
        // Tell screen readers that the headline changed.
        System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(StatusText)
            ?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);

        BadgeGlyph.Visibility = PercentText.Visibility = CheckMark.Visibility = Visibility.Collapsed;
        bool solid = stage is Stage.Success or Stage.Error or Stage.Problems;
        BadgeFill.SetResourceReference(Shape.FillProperty, stage switch
        {
            Stage.Success => "OkBrush",
            Stage.Error => "ErrorBrush",
            Stage.Problems => "WarnBrush",
            _ => "TrackBrush",
        });
        if (solid) BadgeGlyph.Foreground = Brushes.White;
        else BadgeGlyph.ClearValue(TextBlock.ForegroundProperty);

        switch (stage)
        {
            case Stage.Running:
                PercentText.Visibility = Visibility.Visible;
                break;
            case Stage.Success:
                CheckMark.Visibility = Visibility.Visible;
                break;
            default:
                BadgeGlyph.Text = stage switch
                {
                    Stage.Looking => "",
                    Stage.Stopped => "",
                    Stage.Error => "",
                    Stage.Problems => "",
                    _ => "",
                };
                BadgeGlyph.Visibility = Visibility.Visible;
                break;
        }

        OverallBar.IsIndeterminate = _looking;
        Animate();

        Taskbar.ProgressState = stage switch
        {
            Stage.Looking => TaskbarItemProgressState.Indeterminate,
            Stage.Running => TaskbarItemProgressState.Normal,
            Stage.Stopped => TaskbarItemProgressState.Paused,
            Stage.Error => TaskbarItemProgressState.Error,
            _ => TaskbarItemProgressState.None,
        };

        if (solid) Celebrate(stage == Stage.Success);
    }

    void SetProgress(double value, bool animate = true)
    {
        if (!animate || !Theme.Motion)
        {
            OverallBar.BeginAnimation(RangeBase.ValueProperty, null);
            OverallBar.Value = value;
            return;
        }
        OverallBar.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation(value, TimeSpan.FromMilliseconds(350))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
    }

    /// <summary>The badge pops, and on success the tick draws itself.</summary>
    void Celebrate(bool success)
    {
        if (!Theme.Motion) return;
        var pop = new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(450))
        {
            EasingFunction = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut },
        };
        BadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        BadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        if (success)
            CheckMark.BeginAnimation(Shape.StrokeDashOffsetProperty,
                new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(500)) { BeginTime = TimeSpan.FromMilliseconds(150) });
    }

    /// <summary>Motion means work: it runs while something is in progress and the window can be seen.</summary>
    void Animate()
    {
        bool moving = _moving && WindowState != WindowState.Minimized;
        OverallBar.Tag = moving && Theme.Motion ? "active" : null;
        Aurora(moving);
    }

    /// <summary>Lets the light behind the header drift while work is going on, and rest otherwise.</summary>
    void Aurora(bool moving)
    {
        if (!moving || !Theme.Motion)
        {
            AuroraA.BeginAnimation(TranslateTransform.XProperty, null);
            AuroraB.BeginAnimation(TranslateTransform.XProperty, null);
            AuroraB.BeginAnimation(TranslateTransform.YProperty, null);
            return;
        }
        if (AuroraA.HasAnimatedProperties) return;
        static DoubleAnimation Drift(double to, double seconds)
        {
            var drift = new DoubleAnimation(0, to, TimeSpan.FromSeconds(seconds))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Timeline.SetDesiredFrameRate(drift, 20);   // slow movement; no need to redraw 60 times a second
            return drift;
        }
        AuroraA.BeginAnimation(TranslateTransform.XProperty, Drift(140, 7));
        AuroraB.BeginAnimation(TranslateTransform.XProperty, Drift(-120, 9));
        AuroraB.BeginAnimation(TranslateTransform.YProperty, Drift(50, 6));
    }

    // ---- window state --------------------------------------------------------

    void SetBusy(bool busy)
    {
        _busy = busy;
        StartButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        LinksBox.IsReadOnly = busy;
        DestinationBox.IsReadOnly = busy;
        PasteButton.IsEnabled = BrowseButton.IsEnabled = SettingsButton.IsEnabled = !busy;
        MicrosoftButton.IsEnabled = GoogleButton.IsEnabled = !busy;
        if (busy)
        {
            _scanned = 0;
            _timer.Start();
            CancelButton.Focus();
        }
        else
        {
            _timer.Stop();
            Title = "CloudLink";
            _cts?.Dispose();
            _cts = null;
            StartButton.Focus();
        }
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        if (!Ask(_downloading ? "A download is running. Stop it and close? It can be continued later."
                : "CloudLink is still working. Stop and close?"))
        {
            e.Cancel = true;
            return;
        }
        _cts?.Cancel();
    }

    bool Ask(string question) =>
        MessageBox.Show(this, question, "CloudLink", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint flags);

    static void PreventSleep(bool on)
    {
        const uint Continuous = 0x80000000, SystemRequired = 0x00000001;
        SetThreadExecutionState(on ? Continuous | SystemRequired : Continuous);
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Theme.Motion)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Body.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450)) { EasingFunction = ease });
            BodyShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(550)) { EasingFunction = ease });
        }
        LinksBox.Focus();
        if (App.Demo) ShowDemo();
    }

    // Development aid: "CloudLink.exe --demo" shows the window with sample rows; tools\screenshots.ps1 uses it.
    // Further flags: --done, --empty (with --link to keep the link), --google, --google-private (with
    // --signed-in or --waiting), --help, --settings, --light, --dark, --software.
    void ShowDemo()
    {
        var sample = new RemoteEntry { Id = "x", Name = "x" };
        FileItem Row(string path, long size, long received, FileState state, string message = "")
        {
            var item = new FileItem { Provider = _services.OneDrive, Entry = sample, LocalPath = path, DisplayPath = path, Size = size, Received = received };
            item.Set(state, message);
            return item;
        }
        LinksBox.Text = "https://1drv.ms/f/c/0123456789abcdef/Example";
        DestinationBox.Text = @"C:\Users\you\Downloads";
        VersionText.Text = "Version " + AppInfo.Version;
        // Never the real accounts: these pictures end up in the documentation.
        MicrosoftText.Text = "you@example.com";
        MicrosoftDot.SetResourceReference(Shape.FillProperty, "OkBrush");
        GoogleText.Text = "Google: set up";
        GoogleDot.SetResourceReference(Shape.FillProperty, "IdleBrush");
        // Fixed names for the screenshot tool, whatever this PC's real sign-in state is.
        System.Windows.Automation.AutomationProperties.SetName(MicrosoftButton, "Microsoft account");
        System.Windows.Automation.AutomationProperties.SetName(GoogleButton, "Google account");
        _items =
        [
            Row(@"Holiday\2024\IMG_0001.jpg", 4_200_000, 4_200_000, FileState.Done),
            Row(@"Holiday\2024\IMG_0002.jpg", 3_900_000, 3_900_000, FileState.Done, "Already downloaded"),
            Row(@"Holiday\2024\Video.mp4", 1_800_000_000, 640_000_000, FileState.Downloading),
            Row(@"Holiday\Notes.docx", 48_000, 0, FileState.Retrying, "Network problem. Trying again in 4 s"),
            Row(@"Holiday\Plan.xlsx", 22_000, 0, FileState.Waiting),
            Row(@"Holiday\Form", 0, 0, FileState.Skipped, "Google form items cannot be downloaded as files"),
        ];
        FilesList.ItemsSource = _items;
        EmptyState.Visibility = Visibility.Collapsed;
        CountText.Text = "6 FILES";
        StatsPanel.Visibility = Visibility.Visible;
        StatDone.Text = "2 done";
        StatLeft.Text = "3 to go";
        StatProblems.Text = "1 problem";
        Say(Stage.Running, "Downloading 648 MB of 1.69 GB", "11.4 MB/s, about 1 min 32 s left");
        PercentText.Text = "36%";
        SetProgress(36, animate: false);

        string[] args = Environment.GetCommandLineArgs();
        bool google = args.Contains("--google");
        if (google)
        {
            // The Google Drive walkthrough: a public folder fetched with an API key.
            FileItem File(string path, long size) => Row(path, size, size, FileState.Done);
            LinksBox.Text = "https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOpQrStUvWxYz012345";
            MicrosoftText.Text = "Microsoft: sign in";
            MicrosoftDot.SetResourceReference(Shape.FillProperty, "IdleBrush");
            GoogleText.Text = "Google: public links only";
            GoogleDot.SetResourceReference(Shape.FillProperty, "WarnBrush");
            _items =
            [
                File(@"Trip\Day 1\IMG_2041.jpg", 5_100_000),
                File(@"Trip\Day 1\IMG_2042.jpg", 4_700_000),
                File(@"Trip\Day 2\Clip.mp4", 212_000_000),
                File(@"Trip\Itinerary.docx", 31_000),
            ];
            FilesList.ItemsSource = _items;
            CountText.Text = "4 FILES";
            StatDone.Text = "4 done"; StatLeft.Text = "0 to go"; StatProblems.Text = "0 problems";
            SetProgress(100, animate: false);
            Say(Stage.Success, "Done. All 4 files are ready",
                "212 MB in place. Every file was checked against the size and checksum the service reports.");
        }
        bool privateLink = args.Contains("--google-private");
        if (privateLink)
        {
            // The Google sign-in walkthrough: a private folder, opened after signing in.
            FileItem File(string path, long size) => Row(path, size, size, FileState.Done);
            LinksBox.Text = "https://drive.google.com/drive/folders/1ZyXwVuTsRqPoNmLkJiHgFeDcBa987654";
            MicrosoftText.Text = "Microsoft: sign in";
            MicrosoftDot.SetResourceReference(Shape.FillProperty, "IdleBrush");
            bool signedIn = args.Contains("--signed-in");
            GoogleText.Text = signedIn ? "you@example.com" : "Google: set up";
            GoogleDot.SetResourceReference(Shape.FillProperty, signedIn ? "OkBrush" : "IdleBrush");
            _items =
            [
                File(@"Project files\Contract.pdf", 1_400_000),
                File(@"Project files\Drawings\Floor plan.dwg", 8_300_000),
                File(@"Project files\Drawings\Site photo.jpg", 3_600_000),
                File(@"Project files\Meeting notes.docx", 42_000),
            ];
            FilesList.ItemsSource = _items;
            CountText.Text = "4 FILES";
            StatDone.Text = "4 done"; StatLeft.Text = "0 to go"; StatProblems.Text = "0 problems";
            SetProgress(100, animate: false);
            Say(Stage.Success, "Done. All 4 files are ready",
                "12.7 MB in place. Every file was checked against the size and checksum the service reports.");
        }
        if (args.Contains("--done"))
        {
            foreach (var item in _items) { item.Received = item.Size ?? 0; item.Set(FileState.Done); }
            StatDone.Text = "6 done"; StatLeft.Text = "0 to go"; StatProblems.Text = "0 problems";
            SetProgress(100, animate: false);
            Say(Stage.Success, "Done. All 6 files are ready", "1.69 GB in place, each file checked against the size and checksum the service reports.");
        }
        if (args.Contains("--empty"))
        {
            // The link stays only for the picture of the step that pastes it.
            if (!args.Contains("--link")) LinksBox.Text = "";
            FilesList.ItemsSource = null;
            EmptyState.Visibility = Visibility.Visible;
            StatsPanel.Visibility = Visibility.Collapsed;
            CountText.Text = "FILES";
            SetProgress(0, animate: false);
            Say(Stage.Idle, "Ready when you are", "Paste a shared link above and press Download.");
        }
        if (args.Contains("--help")) new HelpWindow { Owner = this }.Show();
        bool waiting = args.Contains("--waiting");
        if (waiting) Say(Stage.Looking, "Waiting for the Google sign-in", "Finish signing in in your browser, then come back here. Stop cancels.");
        // A run in progress looks the way the real window does: Stop in place of Download, the rest locked.
        bool running = !google && !privateLink && !args.Contains("--done") && !args.Contains("--empty");
        if (waiting || running)
        {
            StartButton.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Visible;
            LinksBox.IsReadOnly = DestinationBox.IsReadOnly = true;
            PasteButton.IsEnabled = BrowseButton.IsEnabled = SettingsButton.IsEnabled = false;
            MicrosoftButton.IsEnabled = GoogleButton.IsEnabled = false;
        }
        if (args.Contains("--settings"))
        {
            // Made-up values of the right shape; the boxes for the key and the secret show dots anyway.
            var shown = new Settings
            {
                GoogleApiKey = google ? "placeholder-not-a-real-key-000000000" : "",
                GoogleClientId = privateLink ? "123456789012-abcdefghijklmnopqrstuvwxyz012345.apps.googleusercontent.com" : "",
                GoogleClientSecret = privateLink ? "placeholder-not-a-real-secret-0000" : "",
            };
            // Opened from the Google button with no client saved, Settings explains why it opened.
            string? notice = privateLink ? "Google sign-in needs a client ID and secret first. The steps are below." : null;
            new SettingsWindow(shown, notice) { Owner = this }.Show();
        }
    }
}
