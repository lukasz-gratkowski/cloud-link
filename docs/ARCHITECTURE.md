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
│   ├── OneDriveProvider.cs     Microsoft Graph shares API, and the guest route below
│   ├── SharePointGuest.cs      the guest visit for work or school links shared with anyone
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

**SharePoint links shared with anyone.** A work or school link (`*.sharepoint.com`, or the same under `.cn`
or `.us`) is first opened the way a private browser window opens it, whether or not an account is signed
in. CloudLink follows the link with a cookie store of its own and takes each redirect itself, only over
HTTPS and only to another SharePoint host; an address anywhere else, such as the Microsoft sign-in page
that a link for certain people leads to, is not requested. SharePoint redirects to its folder page and
leaves a guest session cookie (`FedAuth`). With that cookie the OneDrive API v2.0 on the same host
(`https://{host}/_api/v2.0`, the same JSON as Graph) serves the guest:
`/shares/{token}/driveItem` for the shared item, `.../driveItem/children` for its contents, and
`/drives/{driveId}/items/{id}/children` below it (`/shares/{token}/items/{id}` is closed to a guest). A
next page of a listing is followed only on that host. A file's content comes from its
`@content.downloadUrl`, asked for afresh on every attempt: a pre-authorised address that needs no cookie
and honours `Range`, so the download engine fetches it like any other, without the guest cookie. A file
that is itself the shared item is asked for through its drive first and through the share if the drive
refuses. The guest's requests carry a User-Agent that begins like a Windows browser's
(`Mozilla/5.0 (Windows NT 10.0; Win64; x64) CloudLink/x.y.z`); SharePoint refuses the API to a guest
otherwise. The cookie lives in memory only, in a store that belongs to that one link, and is never written
to disk.

When the guest session runs out the API answers 401 `unauthenticated`, and the visit is made again with an
empty cookie store, once for all the requests that wait on it. If that new visit does not end where the
first one did or leaves no guest session, or SharePoint goes on refusing the renewed guest, the visit is
closed for the rest of the run: the files still to come fail at once, each with the reason, instead of each
making a visit of its own.
A file for which SharePoint gives no download address (a link that allows viewing only) fails once, without
further attempts.

What happens when a link does not open as a guest depends on the answer. If the link leads away from
SharePoint, if SharePoint refuses the visit, or if the API refuses the guest with 401, 403 or 404 or sends
it elsewhere, the link goes the Graph way above, and only then does the window ask for the sign-in, saying
why. A link SharePoint does not know is reported, and nothing asks for a sign-in. SharePoint shows that in
one of two ways: it answers the link with 404, or, for a link that has been removed or has expired, with a
page of its own (status 200, no guest cookie) while the API says the sharing link cannot be found (404; a
link that exists but needs an account gets 401 there). Any other answer, such as a 400 or a page where the item should be, is reported as it stands when
nobody is signed in; with an account signed in, the link is handed to Graph instead. A passing failure (a
timeout, 429, 5xx) is tried again a few times and then reported either way. Microsoft does not document
guest access to this API; it was worked out from what a browser does, and the fallback is what keeps a
refusal on Microsoft's side from leaving such links unopened. For an answer that is not a refusal, the
window tells a user who is not signed in that signing in first sends the link the Graph way. Only links from a person's OneDrive
(`{tenant}-my.sharepoint.com`) have been tried; for a link to a team site the API address on the host's
root may not be the right one, and the link would then go the Graph way.

When Graph answers a work or school link with a redirect (308, often with no `Location`), the signed-in
account cannot open it; typically it is a personal account, which an organisation's link rarely admits. By
then the link has been tried as a guest too, and the message says both. A failed request to the Graph,
Drive or guest API, other than a 401 or a 429, is logged with whichever of the `request-id`,
`client-request-id`, `x-ms-ags-diagnostic`, `SPRequestGuid` and `x-msedge-ref` headers it carries and the
host and path of any `Location`, with a share's encoded link (`u!...`) left out. Of the guest visit itself
the log has hosts and status codes only, and of a failed download the file's name and the error text.

**A change Microsoft has announced.** According to Message Center post MC1492958 (October 2026), from
1 April 2027 Microsoft's file APIs stop returning download addresses that carry their own key, and stop
redirecting to them. Both routes download from such addresses today: Graph's `.../content` redirects to
one, and the guest route asks for `@content.downloadUrl`. Both have to be tried against the new behaviour
before that date.

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
