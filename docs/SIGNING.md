# Verifying and signing CloudLink

## Verifying a download

Two independent checks tell you the `CloudLink.exe` you have is the one that was built.

**Checksum.** Compare the file's SHA-256 with `SHA256SUMS.txt` from the same release:

```powershell
Get-FileHash .\CloudLink.exe -Algorithm SHA256
```

**Signature.** Right-click the file → **Properties** → **Digital Signatures**, or:

```powershell
Get-AuthenticodeSignature .\CloudLink.exe | Format-List SignerCertificate, Status, TimeStamperCertificate
```

A signature guarantees the file has not been altered since it was signed. What Windows says about the
*publisher* depends on the kind of certificate:

| Certificate | Signature | Windows on first run |
|---|---|---|
| Issued by a public certificate authority | `Valid` | Shows the publisher's name. SmartScreen may still warn until the file or certificate has built reputation. |
| Self-signed | `UnknownError` (untrusted root) | *Unknown publisher*, and the SmartScreen "Windows protected your PC" prompt: **More info → Run anyway**. |

A self-signed build is tamper-evident but its publisher is not vouched for by anyone. If you trust the person
you got it from, you can make Windows trust their certificate on your own PC by importing the
`CloudLink-signing.cer` that ships next to the exe into **Trusted Publishers** and **Trusted Root Certification
Authorities** for your user. Only do this for a certificate you received from a source you trust.

## Signing a build

`build.ps1` signs `publish\CloudLink.exe` with SHA-256 and a DigiCert timestamp, so the signature stays
valid after the certificate expires. It looks for a certificate in this order:

1. `-CertThumbprint <thumbprint>` or the `CLOUDLINK_SIGN_THUMBPRINT` environment variable.
2. A valid code-signing certificate in your certificate store whose subject starts with
   `CN=AMG Cloud Engineering` or `CN=CloudLink`; one issued by a certificate authority is preferred over a
   self-signed one. A certificate whose private key Windows can no longer open is skipped with a warning.
3. With `-CreateSelfSignedCert`, a new self-signed certificate (3072-bit RSA, private key not exportable).

Without a certificate the build stops before anything is built. If signing fails afterwards, for example
because the timestamp server cannot be reached, the exe is renamed `CloudLink.UNSIGNED.exe` so that nothing
that looks finished is left behind. `-NoSign` builds an unsigned exe on purpose.

```powershell
.\build.ps1 -CertThumbprint 0123456789ABCDEF0123456789ABCDEF01234567   # a CA-issued certificate
.\build.ps1 -CreateSelfSignedCert                                      # first self-signed build
.\build.ps1                                                            # later builds reuse it
```

## Getting a certificate Windows trusts

To have Windows show a publisher name instead of *Unknown publisher*, the certificate must come from a public
certificate authority. Options:

- **Azure Trusted Signing**: a subscription service from Microsoft with identity validation.
- **SignPath Foundation**: free code signing for qualifying open-source projects.
- **An OV or EV code-signing certificate** from a certificate authority such as DigiCert, Sectigo or
  GlobalSign. These are issued on a hardware token or cloud HSM; once it is plugged in, the certificate
  appears in the certificate store and `build.ps1 -CertThumbprint ...` uses it unchanged.
