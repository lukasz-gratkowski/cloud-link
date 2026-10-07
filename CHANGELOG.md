# Changelog

All notable changes are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com),
and versions follow [Semantic Versioning](https://semver.org).

## [Unreleased]

Version 1.3.0 in the making: the first version that is also built as a Microsoft Store package.

### Added
- A Microsoft Store package. `build-msix.ps1` builds an MSIX from the same source: an ordinary
  self-contained folder instead of the single-file exe, the icons and tiles Windows asks for, and a manifest
  whose identity comes from `store\identity.json`. The Store signs the package. `docs/STORE.md` has the steps;
  the listing texts and the notes for certification are in `store\listing`.
- When CloudLink runs from an installed package it names its real data folder
  (`%LOCALAPPDATA%\Packages\<package family name>\LocalCache\Local\CloudLink`) instead of relying on the private
  view Windows gives a packaged app of `%LOCALAPPDATA%\CloudLink`. **Help → Open log** therefore works there,
  Help shows the folder that is really used and can open it, and the Store version leaves the folder of a
  `CloudLink.exe` on the same PC alone. It also says ", Microsoft Store" after its version, opens the guide on amgcloud.io,
  refuses to save downloads under AppData (File Explorer would not show them), ignores `--demo`, and lets a
  Store update wait until it is closed.
- A **Privacy** button in Help opens the privacy statement.

### Fixed
- `build.ps1` writes `SHA256SUMS.txt` with a Unix line ending, so `sha256sum -c` accepts it. (The file
  attached to the 1.2.0 release was corrected by hand.)

- A relative **Save to** folder is no longer resolved against the folder CloudLink happened to be started
  from; it asks for a full path.

### Changed
- The privacy statement covers the Microsoft Store version (where it keeps its data, what Microsoft reports
  to the publisher) and mentions the look at the clipboard for a share link. The security notes and the user
  guide say where each version keeps its files.
- The demo mode, which draws the documentation pictures, uses `you@example.com` as the sample address. The
  text CloudLink finds on the clipboard is no longer kept in memory while it runs, only a hash of it.
- Documentation: the README points to the latest release and says what has and has not been tested; the
  signing guide explains the `Status` values and mentions Smart App Control; the user guide covers an error
  on Microsoft's sign-in page and the wording of the Google button.
- The user guide has the Google Drive setup step by step, with pictures, for both routes: an API key
  restricted to the Google Drive API (public links) and sign-in through your own "Desktop app" client
  (private links). It follows Google's current console (Google Auth platform) and is also published at
  <https://amgcloud.io/apps/cloudlink/guide/>.
- When a Google Drive link is not found while only an API key is in use, CloudLink says that the link may be
  private and that signing in with the Google button is needed.
- `tools/screenshots.ps1` can mark and number controls in a picture and waits until the window has been
  drawn; the demo mode has Google Drive scenarios for the guide's pictures. The pictures of a running
  download now show **Stop** in place of **Download**, as the window does.
- The user guide has a section on checking the downloaded exe, and its troubleshooting table covers the
  Google sign-in, Google's rate limit and Smart App Control.

## [1.2.0] - 2026-10-05

### Changed
- OneDrive sign-in goes through CloudLink's Microsoft Entra app registration. The application ID is set in one
  place, `Directory.Build.props`, and compiled in; a fork or a single build can use another
  (`build.ps1 -MicrosoftClientId`).
- A stored sign-in is tied to the application ID it was made with. Changing the ID in Settings ends the
  sign-in made through the old one and deletes it.
- A build without an application ID says so: the account button reads **Microsoft: set up** and Settings
  explains what to enter, with a link to the registration guide.
- The application ID field in Settings rejects anything that is not an ID.
- `build.ps1` also uses a certificate issued to AMG Cloud Engineering, prefers one from a certificate
  authority over a self-signed one, skips a certificate whose private key cannot be opened, checks for the
  certificate before building, and stops if no application ID is configured. If signing fails, the exe is
  renamed `CloudLink.UNSIGNED.exe`.
- Published by AMG Cloud Engineering under the MIT licence.

### Added
- `docs/APP-REGISTRATION.md`: what the app registration is, who needs one, and how to create one.
- `tools/check-registration.ps1`: checks a registration without signing in.
- A sign-in refused because an organisation requires administrator approval is explained in plain words.
- At the start of a download the stored sign-in is tested; if it no longer works, the sign-in page opens
  straight away instead of the files failing one by one.
- Privacy statement and terms of use (`docs/PRIVACY.md`, `docs/TERMS.md`).
- Logo files for the app registration in `branding/`.

## [1.1.0] - 2026-10-03

### Added
- Logo, application icon and a brand theme for light and dark mode.
- Redesigned window: link and destination with one primary action, a status card with overall progress,
  speed, time left and counts, and a file list with **All / In progress / Problems**.
- In-app Help (<kbd>F1</kbd>) explaining links, sign-in, resuming, verification and problems.
- Version shown in the window, Help, Settings and the file properties.
- Drop a link anywhere on the window.
- Downloads wait for the network to come back instead of using up their attempts.
- Files that failed for a temporary reason get a second round at the end of a run.
- Completed files are tagged as downloaded from the internet (`Zone.Identifier`).
- Only one copy of CloudLink runs at a time; starting it again brings the running window forward.
- `build.ps1` signs the exe and writes `SHA256SUMS.txt`.

### Changed
- **Download** becomes **Stop** while running, then **Continue** or **Try again** as appropriate.
- The Google API key and client secret are stored encrypted for the Windows user.
- Only `https` links are accepted.

### Security
- Access tokens are never sent to a listing or export address on a host other than the service's own.

## [1.0.0] - 2026-10-03

### Added
- Download shared OneDrive and Google Drive folders and files through the official APIs.
- Resume with `.part` files, size and checksum verification, retries with backoff.
- Browser sign-in with OAuth and PKCE; sign-ins stored encrypted for the Windows user.
- Windows-safe file names; export of Google Docs, Sheets and Slides.
