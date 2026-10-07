# Architecture

CloudLink is a .NET 10 WPF application with no third-party runtime dependencies.

```
src/CloudLink
├── Core/
│   ├── Models.cs          RemoteEntry, FileItem, ICloudProvider, ITokenSource
│   ├── DownloadEngine.cs  folder walk, parallel download, resume, verification, retry
│   ├── OAuth.cs           browser sign-in (authorization code + PKCE), token refresh, loopback receiver
│   ├── Errors.cs          error classification, backoff, JSON GET with retry
│   ├── Hashing.cs         QuickXorHash, streaming MD5 / SHA-1 / SHA-256
│   ├── PathSafety.cs      Windows-safe, unique file names
│   ├── AppPackage.cs      what differs when installed as a package (the Microsoft Store version)
│   └── Settings.cs        settings, the data folder, version info, log
├── Providers/
│   ├── OneDriveProvider.cs     Microsoft Graph shares API
│   └── GoogleDriveProvider.cs  Google Drive v3 API
├── App.xaml               brand resources and control styles
├── Theme.cs               light / dark brand colours, motion preference
├── MainWindow.xaml(.cs)   the window
├── HelpWindow.xaml(.cs)   in-app help
└── SettingsWindow.xaml(.cs)
tests/CloudLink.Tests      engine, provider and sign-in tests against a scripted local HTTP server
tools/                     icon, Store asset and screenshot generators, app-registration check
store/                     manifest, identity and listing texts of the Microsoft Store package (built by build-msix.ps1)
branding/                  logo files for use outside the program
```

## Providers

A provider turns a share link into `RemoteEntry` objects and hands out download requests. The engine knows
nothing about OneDrive or Google Drive.

```csharp
interface ICloudProvider
{
    bool Matches(Uri link);
    Task<RemoteEntry> ResolveAsync(Uri link, CancellationToken ct);
    Task<IReadOnlyList<RemoteEntry>> ListAsync(RemoteEntry folder, CancellationToken ct);
    Task<HttpRequestMessage> CreateDownloadRequestAsync(RemoteEntry file, CancellationToken ct);
    void OnUnauthorized();
}
```

**OneDrive.** The link is encoded as Graph's `u!<base64url>` share token and resolved with
`GET /shares/{token}/driveItem` and `Prefer: redeemSharingLink`. Children are listed through
`/drives/{driveId}/items/{id}/children`, falling back to `/shares/{token}/items/{id}/children` if the
drive refuses direct addressing. Content comes from `.../content`, which redirects to a short-lived
storage address; the access token is not forwarded to it.

**Google Drive.** The file ID (and resource key, for older links) is parsed from the link. Listing uses
`files.list` with paging; content uses `files.get?alt=media`. Google-native documents are exported.
Shortcuts are followed. Requests carry the signed-in account's token, or an API key if there is no sign-in.

## The download of one file

1. If the destination file exists with the listed size, or with the modified time CloudLink stamps on
   completion, it is done.
2. Otherwise bytes are appended to `name.part`. If a part file exists, the request carries
   `Range: bytes=<length>-`. A `206` at the wrong offset, or a `416`, discards the part and starts over.
   A `200` means the server ignored the range, and the file is rewritten from the start.
3. A watchdog cancels the request if no data arrives for 60 seconds.
4. The finished length is compared with the length the server stated.
5. The file is hashed and compared with the listing. On a mismatch the part is deleted and the file is
   downloaded again. If two complete downloads agree with each other but not with the listing, the listing
   is taken to be stale (SharePoint lists out-of-date checksums for some Office files) and the file is kept
   with a note.
6. The part is tagged with the internet zone, renamed to its real name, and stamped with the remote
   modified time.

Failures are classified as transient (network, timeout, 401, 408, 429, 5xx, throttling 403) or permanent
(404, access denied, disk full). Transient failures are retried with exponential backoff, honouring
`Retry-After`. The failure count resets whenever data arrives, so a slow, flaky connection keeps making
progress; a file is abandoned only after 8 failures in a row with nothing received. While the PC has no
network the loop waits instead of counting failures. When all files have been tried, those that failed for
a transient reason get one more round.

## Sign-in

`OAuthSession` implements the authorization-code flow with PKCE for both services. It opens the system
browser and receives the redirect on a random loopback port, bound to `127.0.0.1` and `::1` only, checking
the `state` value. The refresh token is stored together with the application ID it was issued to, encrypted
with DPAPI for the current Windows user; a sign-in is used only with the application it belongs to.
Access tokens live in memory and are refreshed shortly before they expire or when a request returns `401`.

## User interface

`FileItem` is the only object shared between worker threads and the window. Workers update its fields
without raising change notifications for every chunk; a 250 ms timer on the UI thread refreshes the rows
that are active and recomputes the totals. The list is virtualised, so the cost depends on the number of
files in progress, not the number of files.

Animations (progress shimmer, the drifting light behind the header) run only while work is in progress, and
not at all when Windows animations are off or over a remote session.

## Application ID

Microsoft identifies the app by the application (client) ID of an Entra app registration. CloudLink's is
set by the `CloudLinkMicrosoftClientId` property in `Directory.Build.props`, compiled in as assembly
metadata, and read by `Settings.BuiltInMicrosoftClientId`. An ID entered in Settings takes precedence.
See [APP-REGISTRATION.md](APP-REGISTRATION.md).

## Versioning

The version lives in `Directory.Build.props` and follows [Semantic Versioning](https://semver.org). It is
compiled into the exe (file properties, window header, Help, `User-Agent`). When built from a git checkout,
the commit hash is appended, for example `1.2.0 (a1b2c3d)`.
