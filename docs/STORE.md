# CloudLink in the Microsoft Store

The Store version is the same program, built as an MSIX package and listed as **AMG CloudLink**. The Store signs
the package, so Windows knows the publisher: no *Windows protected your PC*, no blocked start under Smart App
Control, and updates arrive through the Store. `CloudLink.exe` on GitHub stays as it is.

State on 7 October 2026: the product name is reserved, the package builds, and a build registered on the
development PC passed the checks in section 4, including a Google Drive download with an API key. Still to
do there: the Microsoft sign-in with a OneDrive download, the run without a network, and the Windows App
Certification Kit. Nothing has been submitted or certified yet, so sections 5 to 10 are a plan, written from
Microsoft's documentation of that day (Store Policies 7.19 and 7.20, App Developer Agreement 8.11).

**Contents**

1. [Account, name and first audience](#1-account-name-and-first-audience)
2. [The identity](#2-the-identity)
3. [Build the package](#3-build-the-package)
4. [Try the package](#4-try-the-package)
5. [Before you submit](#5-before-you-submit)
6. [The submission, page by page](#6-the-submission-page-by-page)
7. [Listing texts](#7-listing-texts)
8. [Pictures](#8-pictures)
9. [Notes for certification](#9-notes-for-certification)
10. [After certification](#10-after-certification)
11. [Updates](#11-updates)
12. [What is different in the Store version](#12-what-is-different-in-the-store-version)

## 1. Account, name and first audience

- **Account.** The product belongs to the company account of AMG Cloud Engineering. Store policy 10.14 asks
  for a company account when the publisher name reads as a business; an individual account cannot be converted
  into one. The package identity and the publisher name printed in every package come from the account the
  product was created in.
- **Name.** Store names are unique, and the package must carry a reserved one. The names OneDrive, SharePoint
  and Google Drive must stay out of the product name, the keywords and the logo, and no service logo or caption
  may be added to a screenshot. In the description, and in the app's own window as a screenshot shows it, they
  appear as plain text.
- **First audience.** A submission can go to a *private audience* (people you list) and be made public later.
  The other direction does not exist: once public, a product can never be made private again.

## 2. The identity

A product is created under <https://aka.ms/submitwindowsapp> → **New product** → **MSIX or PWA app** → type the
name → **Check availability** → **Reserve product name**. A reservation lapses after three months without a
submission.

**Product management** → **Product identity** then shows the values that go into
[`store/identity.json`](../store/identity.json), exactly as shown (capitals, spaces and punctuation count):

| In Partner Center | In `identity.json` | For AMG CloudLink |
|---|---|---|
| Package/Identity/Name | `IdentityName` | `AMGCloudEngineering.AMGCloudLink` |
| Package/Identity/Publisher | `Publisher` | `CN=3CB22127-D696-4E4D-82B3-C7A209F636F1` |
| Package/Properties/PublisherDisplayName | `PublisherDisplayName` | AMG Cloud Engineering |
| the reserved product name | `DisplayName` | AMG CloudLink |
| Package Family Name | (part of the data folder's name) | `AMGCloudEngineering.AMGCloudLink_zwcny3vzz9sd6` |
| Store ID | (makes the Store link) | `9NWN67GFKTF3` |

These values are printed inside every package; they are not secrets. A fork needs a product and an identity of
its own, and has to replace the family name where the documents give the data folder.

## 3. Build the package

Commit first, then:

```powershell
.\build-msix.ps1
```

It runs the tests, publishes CloudLink as an ordinary self-contained folder, draws the 72 icon and tile
pictures, fills in [`store/AppxManifest.xml`](../store/AppxManifest.xml), builds the picture index
(`resources.pri`) and packs `publish-store\CloudLink_<version>.0_x64.msix`. The package is not signed; the
Store signs it. The build stops if a picture named in the manifest cannot be found through the index, which
`makeappx` itself would not notice.

- It needs `makeappx.exe` and `makepri.exe` from the Windows SDK (found automatically, or `-SdkBin <folder>`).
- The package's version line shows the commit it was built from, so the build stops while there are
  uncommitted changes. `-Uncommitted` builds anyway, for a test on this PC; that package must not be uploaded.
- `-DryRun` builds with a made-up identity into a folder of its own. That package cannot be uploaded either.
- The version is the one in `Directory.Build.props` with `.0` added. The Store keeps the fourth part for
  itself, and every update needs a higher version.
- The logo for the listing is written to `publish-store\listing\store-logo-300.png`.

## 4. Try the package

The Store only signs the package after certification, so a local test needs one of these:

- **Developer Mode** (Settings → System → Advanced → For developers; before Windows 11 25H2: System → For
  developers. It needs an administrator). Then, in an ordinary PowerShell, after a build without `-DryRun`:

  ```powershell
  Add-AppxPackage -Register .\publish-store\stage-x64\layout\AppxManifest.xml
  ```

  This registers the unpacked folder with the real identity, and Windows runs CloudLink from that folder.
  Remove the registration before building again:

  ```powershell
  Get-AppxPackage AMGCloudEngineering.AMGCloudLink | Remove-AppxPackage
  ```

- **A private audience in the Store** (section 10): the certified, Store-signed package, installed the way
  customers will install it. Slower, because each round is a certification of up to three working days.

Close any `CloudLink.exe` first: the two versions bring each other's window forward instead of starting a
second copy. Start AMG CloudLink from the Start menu, not from a terminal that itself belongs to a packaged
app. Then check:

| Check | Expected |
|---|---|
| Start menu entry and taskbar icon, dark and light theme | "AMG CloudLink", the cloud mark at full size, without a coloured plate behind it |
| The version line | "Version 1.3.0, Microsoft Store" |
| Microsoft sign-in, with the default browser closed beforehand | The browser opens, the page says *Signed in*, the account button shows the address |
| A download, and **Stop** then **Continue** | Files arrive, are marked Done and carry on after Stop |
| `Get-ChildItem "$env:LOCALAPPDATA\Packages\AMGCloudEngineering.AMGCloudLink_zwcny3vzz9sd6\LocalCache\Local\CloudLink"` | `settings.json` and, after a sign-in, `microsoft.token` (the log appears with the first error or with **Open log**) |
| **Help**, last card | Names that same folder; **Open that folder** opens it |
| **Help → Open log** | Notepad shows the log, without offering to create a file |
| **Save to** set to a folder under `AppData` | Refused with an explanation |
| Signed in, network unplugged, then **Download** | After about a minute: *The link could not be opened*, with a network message; no crash |
| A second start | Brings the first window forward |
| Remove the registration (or uninstall) | The `Packages\AMGCloudEngineering.AMGCloudLink_zwcny3vzz9sd6` folder is gone; `%TEMP%\.net\CloudLink` got no new folder |

Microsoft also asks for a run of the Windows App Certification Kit before submitting; it is the technical part
of certification. For the registered package, from an elevated PowerShell (not run yet for CloudLink):

```powershell
cd "<Windows Kits>\10\App Certification Kit"
.\appcert.exe reset
.\appcert.exe test -packagefullname (Get-AppxPackage AMGCloudEngineering.AMGCloudLink).PackageFullName -reportoutputpath <full path>\wack-report.xml
```

## 5. Before you submit

- Publish the current [privacy statement](PRIVACY.md), [user guide](USER-GUIDE.md) and
  [security notes](../SECURITY.md) on amgcloud.io, then open the live pages and look for the date of the
  privacy statement and for "Route A" in the guide. The Store links to the privacy page and certification reads
  it; the listing and the notes for certification send people to the guide.
- Try the two paths that have not been run yet, or take them out of the listing (section 7 says where):
  Google sign-in for private links, and a work or school Microsoft account on a SharePoint link. Microsoft's
  certification guidance asks for finished products.
- Make the test material for section 9 and keep it alive after publication; published apps are re-checked.
- In the Microsoft Entra app registration, point the terms and privacy addresses at the amgcloud.io pages, so
  the sign-in page, the Store listing and the app all show the same documents.

## 6. The submission, page by page

**App overview** → **Product release** → **Start submission**.

| Page | Field | Value |
|---|---|---|
| Pricing and availability | Markets | all (the default) |
| | Audience | **Private audience** for the first round, with your own Microsoft account in the group (create it under Engage → Customer groups → Create new group → Known user group) |
| | Base price | Free; no trial |
| Properties | Category | Utilities + tools; subcategory File managers |
| | Does this product access, collect or transmit personal information? | Yes |
| | Privacy policy URL | `https://amgcloud.io/apps/cloudlink/privacy/` (always required for a desktop program) |
| | Website | `https://amgcloud.io/apps/cloudlink/` |
| | Support contact | `https://amgcloud.io/support/` |
| | Contact details | Required for a company account |
| | Product declarations | Leave "This product has been tested to meet accessibility guidelines" unticked until it has been. Untick "Windows can include this product's data in automatic backups to OneDrive": the stored sign-ins are bound to one Windows account. |
| Age ratings | the questionnaire | See below |
| Packages | | Upload the `.msix`. If it says the name is not one of your reserved names, `DisplayName` in `identity.json` is wrong. |
| Store listings | | Sections 7 and 8 |
| Submission options | Publishing hold | For the private round keep "Publish this submission as soon as it passes certification" |
| | Notes for certification | Section 9 |
| | Restricted capabilities | [`store/listing/restricted-capability.txt`](../store/listing/restricted-capability.txt). Without it the submission cannot be approved. |

Then **App overview** → **Submit for certification**. It takes up to three working days, and the review of
`runFullTrust` can add to that.

**Age ratings.** The questionnaire's wording can only be seen in Partner Center. The true answers, by topic:
the app is a utility; no violence, sexual content, profanity, drugs, gambling or hate content; users do not
communicate or exchange content with each other through it; it does not share location or personal
information with third parties; no purchases; it is not a web browser or search engine. That should give the
lowest rating everywhere.

## 7. Listing texts

The texts are plain files in [`store/listing`](../store/listing), one line per paragraph, ready to copy. The
counts below were taken from those files.

| Field in Partner Center | File | Length | Limit |
|---|---|---|---|
| Short description | `short-description.txt` | 239 characters | 1,000; the first 270 are shown in some places |
| Description | `description.txt` | about 4,350 characters | 10,000; plain text, no web addresses |
| Product features | `features.txt` | 14 lines, the longest 107 characters | 20 features of 200 characters |
| Keywords (formerly search terms) | `keywords.txt` | 7 terms, 17 words | 7 terms of 40 characters, 21 words; no other products' names (policy 10.1.3) |
| Copyright and trademark info | `copyright.txt` | 171 characters | 200 |
| Additional system requirements, minimum | `system-requirements.txt` | 4 lines | 11 of 200 characters |

Other fields:

- **What's new in this version**: leave empty for a first submission.
- **Developed by**: AMG Cloud Engineering
- **Additional license terms**: the text of [LICENSE](../LICENSE), or the address
  `https://amgcloud.io/apps/cloudlink/license/`. Left empty, Microsoft's standard terms apply instead of MIT.

The description opens with what the app depends on (policy 10.2.4) and ends with the trademark and
no-affiliation lines.

Some lines describe paths that have not been run against the real services yet. Try them first (section 5).
If Google sign-in for private links has not been tried by then, change the listing to what has been:

- in the description, *Google Drive*: "an API key for links that anyone can open, or an OAuth client for
  private links" becomes "an API key, which opens links that anyone can open", and "walks through both routes"
  becomes "walks through it";
- in the description, *Other limits*: remove the line about documents larger than 10 MB;
- in the system requirements: "Your own Google Cloud API key, for Google Drive links".

If a work or school account has not been tried, remove the paragraph *Work and school accounts* from the
description.

## 8. Pictures

**Screenshots.** One is required, four or more are recommended, ten is the limit; PNG, 1366 × 768 or larger.
Show only CloudLink's own window with the sample data: no pictures of Microsoft's or Google's pages, no added
logos or slogans. The window itself names OneDrive and Google Drive in plain text, as it must to be shown
truthfully. Microsoft's trademark page asks for screenshots free of its product names while the Store policy
asks for screenshots that show the product as it is; if certification objects, that is the point to answer.

Type this in a PowerShell window (`pwsh -File` does not pass the list on as a list):

```powershell
dotnet build src\CloudLink -c Release
tools\screenshots.ps1 -Out publish-store\listing\screenshots -Only downloading-dark, done-dark, empty-light, downloading-light, google-key-4-done
```

The window is 980 × 800 units, so the pictures reach the minimum only on a display set to 150 % scaling or
more (1470 × 1200). The tool needs a connected desktop session.

| Picture | Caption |
|---|---|
| `downloading-dark` | A shared folder on its way down: overall progress, speed, time left and a status for every file. |
| `done-dark` | Finished. Every file was checked against the size and checksum the service reports. |
| `empty-light` | Paste a share link, choose where to save, press Download. |
| `downloading-light` | A dropped connection is retried without your help. Press Stop at any time and continue later. |
| `google-key-4-done` | Google Drive folders too, after a one-time setup with credentials you create yourself. |

**Store logo.** Upload `publish-store\listing\store-logo-300.png` as the 1:1 app tile icon (300 × 300). The box
art and poster art slots are for games.

## 9. Notes for certification

A tester cannot download anything from OneDrive without signing in, and policy 10.3.1 asks for a working test
account when a product needs one. [`store/listing/notes-for-certification.txt`](../store/listing/notes-for-certification.txt)
therefore starts with a test that needs no account at all (Google Drive with a test key) and gives a test
account for the OneDrive one. Replace every `[...]`; the filled text has to stay under 2,000 characters. The
filled copy holds a key and a password: keep it out of the repository.

What the placeholders need:

- **Google sample**: a folder shared as *Anyone with the link*, with a few files of your own, and an API key
  from a separate Google Cloud project restricted to the Google Drive API, as the
  [user guide](USER-GUIDE.md#google-drive) describes. Microsoft's testers see the key; delete it if it is ever
  misused.
- **OneDrive sample**: a folder on a personal OneDrive shared as *Anyone with the link can view*, without
  expiry or password, holding five or six files of your own, 30 to 60 MB in all so that Stop and Continue can
  be tried.
- **Test account**: a personal Microsoft account made only for this, never the Partner Center account. Sign in
  to it once beforehand, so that the tester is not the first to do so.
- Use only material that is yours to share.

## 10. After certification

1. **Private test.** On **Product identity**, copy the address for products that are only visible to certain
   people. Open it on a PC whose Store app is signed in with the personal Microsoft account in the group,
   install, and go through the table in section 4.
2. **Go public.** **Start update** → Pricing and availability → Audience: **Public audience**. On Submission
   options choose "Don't publish this submission until I select Publish now", so the website can change on the
   same day. Submit. Going public cannot be reversed.
3. When it has passed, press **Publish now**, and on the same day set `status` and `storeUrl` in the website's
   `content/apps/cloudlink.json` (`https://apps.microsoft.com/detail/9NWN67GFKTF3`), mention the Store in the
   README's *Getting started*, take the "is being prepared" sentences out of the privacy statement, the
   security notes and the user guide, and check that the Store page shows the publisher you intended.
4. The badge comes from <https://apps.microsoft.com/badge>; use its markup as generated.

## 11. Updates

1. Raise `Version` in `Directory.Build.props`, add the changelog entry, and commit.
2. `.\build-msix.ps1`
3. **Product release** → **Start update** → Packages → upload the new `.msix` → **Submit for certification**.
   Windows installs Store updates by itself. CloudLink's manifest asks Windows to wait with an update until the
   app is closed, so a running download is not cut off (Windows 11 24H2 and later).

Uploads can be scripted later with the Microsoft Store Developer CLI
(`msstore publish <path to the .msix> -id 9NWN67GFKTF3`). It needs an Entra tenant linked to the Partner Center
account and an Entra application with the Manager role, and it cannot make the first submission.

## 12. What is different in the Store version

| | `CloudLink.exe` from GitHub | Microsoft Store |
|---|---|---|
| Name in the Start menu | none (a single file) | AMG CloudLink |
| Signed by | a self-signed certificate (Windows says *Unknown publisher*) | the Store |
| Updates | download the new exe | the Store, by itself |
| Settings, sign-ins, log | `%LOCALAPPDATA%\CloudLink`, kept until deleted | `%LOCALAPPDATA%\Packages\AMGCloudEngineering.AMGCloudLink_zwcny3vzz9sd6\LocalCache\Local\CloudLink`, removed on uninstall |
| Version line in the window | `Version 1.3.0 (commit)` | `Version 1.3.0, Microsoft Store`; Help and Settings add the commit |
| Guide button | the guide on GitHub | the guide on amgcloud.io |
| Saving under `AppData` | allowed | refused: Windows would keep the files where File Explorer does not show them |
| `--demo` | sample window for documentation pictures | ignored |

The two do not share settings or sign-ins: someone who moves from the exe to the Store version signs in again
and enters the Google credentials again. Only one of them runs at a time; starting the second brings the
first one's window forward.

The code that knows about the package is [`Core/AppPackage.cs`](../src/CloudLink/Core/AppPackage.cs).
