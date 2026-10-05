# Security

## What CloudLink does and does not do

- It only downloads from OneDrive and Google Drive. It never uploads, changes or deletes files there. Opening a
  OneDrive link records the signed-in account as having opened it, as opening it in a browser does.
- It never runs, opens or unpacks what it downloads.
- It talks only to Microsoft's and Google's sign-in and API hosts, and to the storage addresses those APIs
  redirect to. There is no telemetry and no update check.

## Sign-in and stored data

- Sign-in happens in your browser using OAuth 2.0 authorization code with PKCE. CloudLink never sees a
  password. The redirect is received on a random loopback port (`127.0.0.1` / `::1` only) and is accepted
  only with the matching `state` value.
- Refresh tokens, the Google API key and the Google client secret are stored in `%LOCALAPPDATA%\CloudLink`,
  encrypted with Windows DPAPI for the current user. Access tokens are kept in memory only.
- `settings.json` in the same folder also holds, unencrypted, the address of each signed-in account, the last
  folder saved to, and the application and client IDs.
- Access tokens are sent only to the API host. They are not forwarded on redirects to storage hosts, and
  listing pages or export links that point to another host are refused.
- The log (`cloudlink.log`) records file names and error messages. It does not record tokens or keys.

## Downloaded files

- Names from the service are sanitised before use: path separators and other characters Windows forbids
  are replaced, reserved device names are prefixed, and `.` / `..` cannot be produced, so a hostile name
  cannot place a file outside the destination folder.
- Completed files are tagged with the internet zone (`Zone.Identifier`), as browsers do, so SmartScreen and
  Office Protected View still apply when a downloaded file is opened.
- Content is verified against the checksum the service lists.

## Permissions requested

| Service | Scope | Why |
|---|---|---|
| Microsoft | `Files.ReadWrite.All`, `User.Read`, `offline_access` | Graph opens share links only for apps with a read-write files permission. `User.Read` shows which account is signed in. |
| Google | `drive.readonly` | Read files and folders shared with, or visible to, the signed-in account. |

The Microsoft application (client) ID built into CloudLink identifies CloudLink's app registration. It is
not a secret: desktop apps sign in without one. You can sign in through your own registration instead by
entering its ID in **Settings**; see [docs/APP-REGISTRATION.md](docs/APP-REGISTRATION.md).

## Release integrity

Releases carry an Authenticode signature and a `SHA256SUMS.txt`. See [docs/SIGNING.md](docs/SIGNING.md).

## Reporting a vulnerability

Please open a private security advisory on the repository (**Security → Report a vulnerability**) rather
than a public issue. Include the version shown in the window header and steps to reproduce.
