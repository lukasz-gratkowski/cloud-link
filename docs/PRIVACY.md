# CloudLink privacy statement

*Last updated: 5 October 2026*

CloudLink is a program that runs on your own computer. AMG Cloud Engineering, its publisher, operates no
service for it and **receives no data from it**: no account details, no file names, no usage statistics.

## What CloudLink communicates with

Only the services you ask it to download from, and only from your computer:

| Service | What is exchanged |
|---|---|
| Microsoft (sign-in, Microsoft Graph, OneDrive and SharePoint storage) | Your sign-in, made in your browser on Microsoft's own pages; the share links you paste; requests to list and download the files behind them. |
| Google (sign-in, Google Drive API) | The same, for Google Drive links; if you use an API key instead of signing in, that key. |

Those exchanges are covered by Microsoft's and Google's own privacy statements. CloudLink contains no
telemetry, analytics, advertising or update check, and contacts no other party. The guide buttons in Settings
open pages on github.com, Microsoft and Google in your browser.

## What CloudLink is allowed to do with your account

| Service | Permission | What CloudLink does with it |
|---|---|---|
| Microsoft | Read and write access to files you can reach (`Files.ReadWrite.All`), your basic profile (`User.Read`), and staying signed in (`offline_access`) | Opens share links and downloads the files in them; shows which account is signed in. Microsoft requires a read-write permission to open share links, but CloudLink never uploads, changes or deletes files. Opening a link records your account as having opened it, exactly as opening it in a browser does. |
| Google | Read-only access to Drive (`drive.readonly`) | Lists and downloads the files behind the links you paste. |

CloudLink never sees your password.

## What CloudLink keeps on your computer

In `%LOCALAPPDATA%\CloudLink`:

- your settings, including the address of each signed-in account and the last folder you saved to (not
  encrypted);
- your sign-ins (refresh tokens) and, if you entered them, your Google API key and client secret, all
  encrypted with Windows data protection for your Windows account;
- a log containing file names and error messages, which helps when something goes wrong. It contains no
  passwords, tokens or keys, and is never sent anywhere.

The files you download are saved where you choose.

## Removing it

- **Sign out** with the account buttons in CloudLink; this deletes the stored sign-in. Changing the application
  ID in Settings deletes the sign-in made through the old one as well.
- Delete the folder `%LOCALAPPDATA%\CloudLink` to remove everything CloudLink stored.
- Withdraw the permission at the service: Microsoft personal accounts at
  <https://account.live.com/consent/Manage>, work or school accounts at <https://myapps.microsoft.com>,
  Google at <https://myaccount.google.com/permissions>.

## Questions

Open an issue at <https://github.com/lukasz-gratkowski/cloud-link/issues>. For a security problem, see
[SECURITY.md](../SECURITY.md).
