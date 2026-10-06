# Changelog

All notable changes are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com),
and versions follow [Semantic Versioning](https://semver.org).

## [Unreleased]

### Fixed
- `build.ps1` writes `SHA256SUMS.txt` with a Unix line ending, so `sha256sum -c` accepts it. (The file
  attached to the 1.2.0 release was corrected by hand.)

### Changed
- Documentation: the README points to the latest release and says what has and has not been tested; the
  signing guide explains the `Status` values and mentions Smart App Control; the user guide covers an error
  on Microsoft's sign-in page and the wording of the Google button.

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
