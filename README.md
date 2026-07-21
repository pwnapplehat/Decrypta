<div align="center">

# Decrypta

**Download App Store apps and decrypt them into sideload-ready IPAs — on Windows.**

No macOS. No WSL. Just your PC and a jailbroken iPhone/iPad — connected over USB or Wi-Fi.

</div>

---

Decrypta is a native Windows desktop app. You give it a bundle id, App Store id, App
Store URL, or a local `.ipa`; it signs in to the App Store, downloads the app, and
decrypts it on your connected device into a clean, installable IPA — then hands it to your
Library ready for TrollStore / Sideloadly / etc.

![Decrypt](docs/screenshots/01-decrypt.png)

## Why a device is required (and why WSL can't help)

App Store binaries are wrapped in Apple **FairPlay DRM**. The content key is bound to
Apple's **Secure Enclave**, so only the Apple kernel can decrypt the binary, at app-load,
on genuine Apple silicon. There is no software-only FairPlay decrypter on any OS, and WSL
(a Linux VM on x86) has no Apple kernel and no Secure Enclave, so it cannot decrypt either.

Decrypta does **100% of the orchestration on Windows** and uses your jailbroken device
purely as the decryption engine — you never touch the phone manually. It talks to the
device through usbmuxd, so it works over the **USB cable or Wi-Fi** (a network-paired
device) transparently.

```
 Windows (Decrypta.exe, native .NET)                 iPhone / iPad (jailbroken)
 ───────────────────────────────────                 ──────────────────────────
 App Store sign-in + download  ─── ipatool ───▶ Apple CDN
 usbmux TCP tunnel  127.0.0.1 ────USB / Wi-Fi──▶ device :22 (OpenSSH)
 ipadecrypt over SSH/SFTP  ─────────────────────▶ on-device helper
                                                   (task_for_pid + mach_vm_read →
                                                    FairPlay plaintext)
 ◀──── decrypted, cryptid-patched, repackaged .ipa ────  saved to your Library
```

## Requirements

**Windows**
- Windows 10/11 x64
- [Apple Devices](https://apps.microsoft.com/detail/9np83lwlpz9k) or iTunes (provides Apple
  Mobile Device Service / usbmux, used to talk to the device). No .NET install needed — the
  installer ships a self-contained build.

**On the jailbroken iPhone/iPad** (palera1n or Dopamine; iOS 14–17, A10–A14 work best) —
install from Sileo:
- **OpenSSH**
- **AppSync Unified** — add repo `https://lukezgd.github.io/repo`
- **appinst** — same repo

The same Apple ID must be used on the device and for sign-in.

## Using Decrypta

1. **Doctor** — open the app and hit *Run checks*. It verifies the tools, Apple Mobile
   Device Service, your device, the USB→SSH tunnel and sign-in state.
2. **Sign in** — enter your Apple ID and the device SSH login (palera1n default is
   `root` / `alpine`). When Apple sends a 6-digit code, type it into the **response** box
   and press *Send*. This is a one-time setup.
3. **Decrypt** — type an app, choose *From App Store* or *Use installed build*, and click
   *Decrypt*. Progress streams live; the finished IPA lands in your **Library**.

![Sign in](docs/screenshots/02-signin.png)
![Doctor](docs/screenshots/04-doctor.png)

## Command line

A headless companion (`decrypta-cli.exe`) ships alongside the app for scripting:

```powershell
decrypta-cli devices
decrypta-cli doctor
decrypta-cli decrypt com.burbn.instagram
decrypta-cli decrypt 389801252 --use-installed --udid <udid> -o out.ipa
```

Sign-in (Apple ID + 2FA) is done once in the desktop app; the CLI reuses it.

## Updates

Decrypta checks GitHub for a newer release on launch (you can turn this off in Settings).
When one is available it shows a banner; clicking **Install update** downloads the new
installer, verifies its SHA-256 against the release's `SHA256SUMS.txt`, and runs it in
place. Nothing is downloaded or executed unless you click.

## Building from source

```powershell
dotnet build Decrypta.slnx -c Release
dotnet test  tests/Decrypta.Core.Tests/Decrypta.Core.Tests.csproj -c Release
dotnet run   --project src/Decrypta.App/Decrypta.App.csproj -c Release
```

Requires the .NET 10 SDK. The native `ipatool.exe` and `ipadecrypt.exe` live in `tools\`
and are copied next to the built app automatically.

## Security & legal

- Credentials are stored locally under `%LOCALAPPDATA%\Decrypta` (ipadecrypt's own config
  format keeps the Apple ID and device SSH password in plain text). Treat the machine as
  trusted; consider a secondary Apple ID.
- Use Decrypta only with apps you own or have a license for. It is intended for research,
  analysis, and sideloading your own apps.

## Credits

- [majd/ipatool](https://github.com/majd/ipatool) — App Store auth + download
- [londek/ipadecrypt](https://github.com/londek/ipadecrypt) — on-device decrypt + repackaging,
  built on [34306/TrollDecryptJB](https://github.com/34306/TrollDecryptJB)
- Inspired by [34306/macOSAppstoreDecrypter](https://github.com/34306/macOSAppstoreDecrypter)
  (which needs an M1 Mac); Decrypta targets Windows users with a jailbroken device instead.

Released under the [MIT License](LICENSE).
