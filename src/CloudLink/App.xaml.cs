using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using CloudLink.Core;
using CloudLink.Providers;

namespace CloudLink;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("Unhandled: " + args.Exception);
            MessageBox.Show(args.Exception.Message, "CloudLink", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);

        // Two copies writing the same .part files would only get in each other's way.
        _single = new Mutex(true, @"Local\CloudLink.SingleInstance", out bool first);
        var wake = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CloudLink.Wake");
        bool demo = Demo;
        AppInfo.ShowCommit = !demo;   // documentation pictures should not carry a commit
        Theme.Still = demo;           // nor be caught half-way through an animation
        if (!first && !demo)
        {
            AllowSetForegroundWindow(-1);   // let the running copy come to the front
            wake.Set();
            Shutdown();
            return;
        }

        if (e.Args.Contains("--software"))
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        if (e.Args.Contains("--light")) (ThemeMode, Theme.Forced) = (ThemeMode.Light, false);
        if (e.Args.Contains("--dark")) (ThemeMode, Theme.Forced) = (ThemeMode.Dark, true);
        Theme.Apply();
        Theme.Watch();
        var window = new MainWindow();
        window.Show();
        if (demo) return;
        new Thread(() =>
        {
            while (wake.WaitOne())
                Dispatcher.BeginInvoke(() =>
                {
                    if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                    window.Activate();
                });
        }) { IsBackground = true, Name = "Wake" }.Start();
    }

    Mutex? _single;

    /// <summary>The sample window for documentation pictures ("--demo"); never in an installed package.</summary>
    public static bool Demo { get; } = !AppPackage.IsPackaged && Environment.GetCommandLineArgs().Contains("--demo");

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);
}

/// <summary>Everything the window needs, wired together once.</summary>
public sealed class Services
{
    public Settings Settings { get; } = Settings.Load();
    public OAuthSession MicrosoftAuth { get; }
    public OAuthSession GoogleAuth { get; }
    public OneDriveProvider OneDrive { get; }
    public GoogleDriveProvider GoogleDrive { get; }
    public HttpClient DownloadClient { get; }
    public IReadOnlyList<ICloudProvider> Providers { get; }

    public Services()
    {
        string agent = "CloudLink/" + (typeof(Services).Assembly.GetName().Version?.ToString(3) ?? "1.0");

        var api = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        }) { Timeout = Timeout.InfiniteTimeSpan };
        api.DefaultRequestHeaders.UserAgent.ParseAdd(agent);

        // No automatic decompression here: file bytes must arrive exactly as stored,
        // with a Content-Length that can be checked and a Range that can be resumed.
        DownloadClient = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 32,
        }) { Timeout = Timeout.InfiniteTimeSpan };
        DownloadClient.DefaultRequestHeaders.UserAgent.ParseAdd(agent);

        MicrosoftAuth = new OAuthSession(() => new OAuthConfig(
            "Microsoft",
            "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
            "https://login.microsoftonline.com/common/oauth2/v2.0/token",
            Settings.EffectiveMicrosoftClientId,
            null,
            // Graph only opens sharing links for apps holding a Files.ReadWrite permission; CloudLink never writes.
            "Files.ReadWrite.All User.Read offline_access",
            "localhost",
            new Dictionary<string, string> { ["prompt"] = "select_account" }), "microsoft.token", api);

        GoogleAuth = new OAuthSession(() => new OAuthConfig(
            "Google",
            "https://accounts.google.com/o/oauth2/v2/auth",
            "https://oauth2.googleapis.com/token",
            Settings.GoogleClientId.Trim(),
            Settings.GoogleClientSecret.Trim(),
            "https://www.googleapis.com/auth/drive.readonly",
            "127.0.0.1",
            new Dictionary<string, string> { ["access_type"] = "offline", ["prompt"] = "consent" }), "google.token", api);

        // A sign-in made through another application ID (an older build, an edited setting) is of no use.
        MicrosoftAuth.ForgetIfForeign();
        GoogleAuth.ForgetIfForeign();

        OneDrive = new OneDriveProvider(api, MicrosoftAuth);
        GoogleDrive = new GoogleDriveProvider(api, GoogleAuth, () => Settings.GoogleApiKey);
        Providers = [OneDrive, GoogleDrive];
    }
}
