# Microsoft app registration

## The short version

**People who use CloudLink need nothing from Azure.** No tenant, no subscription, no registration. They sign
in with the Microsoft account they already have.

Microsoft lets a program sign people in only if the program itself is registered with Microsoft. That
registration is made once, by the publisher, and is identified by an *application (client) ID*. CloudLink's
ID is compiled into the program, and its registration accepts both personal Microsoft accounts and work or
school accounts from any organisation.

| Who | What they need |
|---|---|
| Someone using CloudLink with a personal Microsoft account | Nothing. Sign in and approve Microsoft's permission page once. |
| Someone using CloudLink with a work or school account | Nothing of their own. By default an administrator of their organisation has to approve CloudLink once; see [Work and school accounts](#work-and-school-accounts). |
| The publisher of CloudLink | One app registration, which lives in a Microsoft Entra tenant; see [what it needs](#what-cloudlinks-own-registration-needs). |
| Someone publishing a fork, or who prefers a registration they control | One app registration of their own; see [Creating a registration](#creating-a-registration). |
| Someone using a build made *without* an application ID (it shows **Microsoft: set up**) | The ID of a registration, their own or one they were given, entered in **Settings**. |

The application ID is an identifier, not a secret. A desktop program cannot keep a secret, so this kind of
sign-in does not use one. The ID is in this repository, in `Directory.Build.props`.

## What Microsoft's permission page asks for

After you sign in, Microsoft shows a permission page listing what the app may do:

| Scope | Shown roughly as | Why CloudLink asks |
|---|---|---|
| `Files.ReadWrite.All` | Have full access to all files you have access to | Microsoft Graph opens a share link only for an app holding a read-write files permission, and the shared files are in someone else's OneDrive. CloudLink only downloads: it never uploads, changes or deletes files. Opening a link records your account as having opened it, as a browser does. |
| `User.Read` | Sign you in and read your profile | To show which account is signed in. |
| `offline_access` | Maintain access to data you have given it access to | So that you are not asked to sign in for every download. |

None of these requires an administrator according to Microsoft's permissions reference. Where an
administrator is needed, it is because of who may approve apps in an organisation, which by default excludes
apps from an unverified publisher; see below.

The page labels the app **unverified**. That refers to *publisher verification*, a Microsoft programme for
businesses (see [below](#publisher-verification)); it says nothing about the program itself.

To withdraw the permission later: personal accounts at <https://account.live.com/consent/Manage>; work or
school accounts at <https://myapps.microsoft.com>, or through their administrator.

## Personal Microsoft accounts

Sign in, read the permission page, approve. There is no administrator involved and nothing to set up.

## Work and school accounts

Organisations decide which apps their people may approve. By default Microsoft does not let users approve an
app from an unverified publisher when it asks for more than sign-in and profile, and many organisations go
further. In that case Microsoft's page says **Need admin approval** (or **Approval required**, where the
organisation has a request process) instead of asking for permission.

If Microsoft passes the refusal back, CloudLink shows *Your organisation requires an administrator to approve
CloudLink*. If the page simply stays open, close it and press **Stop** in CloudLink.

Ways forward:

1. **Ask an administrator to approve CloudLink for the organisation.** An administrator (Privileged Role
   Administrator, Cloud Application Administrator or Application Administrator) opens this address and
   accepts; they will also be warned that the publisher is unverified:

   `https://login.microsoftonline.com/organizations/adminconsent?client_id=21041c7e-49f3-4ccd-9961-9d0e05cd888d`

   `organizations` can be replaced by the organisation's tenant ID. After accepting, the browser tries to open
   `http://localhost` and shows a connection error. That is expected: the approval has been recorded, and the
   address in the browser contains `admin_consent=True`. The link grants the permissions listed on the
   registration (see [what it needs](#what-cloudlinks-own-registration-needs)).
2. **Use a registration of the organisation's own.** Someone in the organisation creates one (below) and its
   ID is entered in CloudLink under **Settings → OneDrive**. Apps are not treated as unverified inside the
   tenant they are registered in.
3. **For a link from a personal OneDrive** (`1drv.ms`, `onedrive.live.com`) that is shared with anyone: sign in
   with a personal Microsoft account instead.

Choosing a narrower permission would not avoid this: the default applies to any permission beyond sign-in
and profile.

## Publisher verification

Verification replaces *unverified* on the permission page with the publisher's name and lets users in other
organisations approve the app themselves where their organisation allows it. Microsoft does not charge for
it. It requires:

- a verified Microsoft AI Cloud Partner Program account for a registered business;
- a custom domain as the registration's publisher domain (not an `onmicrosoft.com` address);
- that the registration was created by a work or school account in the tenant. A registration created by a
  personal Microsoft account cannot be verified, and a registration cannot be moved to another tenant.

## Creating a registration

Only needed for a fork, or to sign in through a registration you control. It costs nothing.

The registration has to live in a Microsoft Entra tenant. People with a work or school account already
belong to one and can usually register apps in it. Someone with only a personal Microsoft account needs a
tenant first; signing up for a free Azure account creates one (a payment card is asked for to confirm
identity).

1. Open the [Microsoft Entra admin center](https://entra.microsoft.com) → **Entra ID** →
   **App registrations** → **New registration**.
2. **Name**: what people will see on the permission page.
3. **Supported account types**: **Any Entra ID Tenant + Personal Microsoft accounts**. Older pages call the
   same option *Accounts in any organizational directory and personal Microsoft accounts*.
4. Press **Register** and copy the **Application (client) ID** from the Overview page.
5. **Authentication** → **Add Redirect URI** → **Mobile and desktop applications** → enter `http://localhost`.
   CloudLink listens on a different port each time; Microsoft ignores the port for `localhost`.
6. **API permissions** → **Add a permission** → **Microsoft Graph** → **Delegated permissions** → add
   `Files.ReadWrite.All` and `offline_access` (`User.Read` is already there). People can sign in without
   this step, but the administrator approval link above grants only what is listed here.
7. **Branding & properties**: add a logo, a **Terms of service URL** and a **Privacy statement URL**. If
   either link is missing, the permission page shows a warning.

Do not create a client secret or certificate, and leave **Allow public client flows** as it is.

### Checking it

```powershell
.\tools\check-registration.ps1 -ClientId <application id>
```

This needs PowerShell 7 (`pwsh`). Without signing in, it asks Microsoft's sign-in service whether the ID is
known, accepts personal accounts, and accepts `http://localhost`. It cannot check work and school accounts;
for those, the first real sign-in is the test, and it should be made with an account from a *different*
organisation than the one the registration lives in.

### Using it

- **For yourself**: paste the ID into **Settings → OneDrive → Application (client) ID**.
- **For a fork**: set `CloudLinkMicrosoftClientId` in `Directory.Build.props`.
- **For one build**: `.\build.ps1 -MicrosoftClientId <application id>`. With `-MicrosoftClientId none` the
  build has no ID, and every user enters one in Settings.

Changing the ID ends the sign-in made through the old one, so you sign in again.

## What CloudLink's own registration needs

For the maintainer. CloudLink's registration is `21041c7e-49f3-4ccd-9961-9d0e05cd888d`.
`tools\check-registration.ps1` confirms that the ID is known, accepts personal accounts and accepts
`http://localhost`; everything else can only be seen in the Entra admin center, and the last point only by
signing in.

- Supported account types: **Any Entra ID Tenant + Personal Microsoft accounts**.
- Redirect URI `http://localhost`, listed under **Mobile and desktop applications** (not Web or Single-page
  application).
- **API permissions** list `Files.ReadWrite.All`, `offline_access` and `User.Read` (delegated), so that the
  administrator approval link works.
- **Branding & properties**:
  - Logo: `branding/CloudLink-logo-215-opaque.png` (see [BRAND.md](BRAND.md)).
  - Home page URL: `https://github.com/lukasz-gratkowski/cloud-link`
  - Terms of service URL: `https://github.com/lukasz-gratkowski/cloud-link/blob/main/docs/TERMS.md`
  - Privacy statement URL: `https://github.com/lukasz-gratkowski/cloud-link/blob/main/docs/PRIVACY.md`
- After any change: one sign-in with a personal Microsoft account, and one with a work or school account from
  another organisation.

## Further reading (Microsoft Learn)

- [Register an application](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app)
- [Redirect URI restrictions](https://learn.microsoft.com/en-us/entra/identity-platform/reply-url)
- [Publisher verification](https://learn.microsoft.com/en-us/entra/identity-platform/publisher-verification-overview)
- [Risk-based step-up consent](https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/configure-risk-based-step-up-consent)
- [The consent experience](https://learn.microsoft.com/en-us/entra/identity-platform/application-consent-experience)
- [Admin consent endpoint](https://learn.microsoft.com/en-us/entra/identity-platform/v2-admin-consent)
- [Microsoft Graph permissions reference](https://learn.microsoft.com/en-us/graph/permissions-reference)
