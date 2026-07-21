# Changelog

All notable changes to Decrypta are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-07-21

Initial release.

### Added
- Native Windows desktop app (.NET 10 / WPF) with a Fluent shell, Mica backdrop and a
  violet→fuchsia brand: Decrypt, Sign in, Library, Doctor and Settings.
- Pure-C# usbmux client and lockdown reader — device discovery and details via Apple
  Mobile Device Service, over **USB or Wi-Fi** (no extra drivers, no WSL).
- In-process USB/Wi-Fi → SSH tunnel so the on-device decrypt runs with no manual networking.
- App Store sign-in with 2FA handled in an embedded console; one-time device setup.
- **Multiple Apple IDs** — add, switch between and remove accounts (each fully isolated in
  its own credential store), with a clear signed-in indicator in the header.
- One-click decrypt of a bundle id / App Store id / URL / local `.ipa` into a
  sideload-ready IPA, with live streaming progress.
- **Contained, cleanable cache** — the encrypted-download cache lives inside your chosen
  output folder (nothing in system temp); a one-click **Clean** wipes cached and partial
  (`.tmp`) downloads, and a failed/cancelled decrypt auto-clears its partial. Choose the
  output folder from Settings or the Library tab.
- Library view of produced IPAs; Doctor end-to-end environment check.
- Automatic update check on launch (opt-out in Settings): a banner appears when a newer
  GitHub release exists and can download + SHA-256-verify + silently install it in place.
- Headless `decrypta-cli` (`devices`, `doctor`, `decrypt`) that reuses the app's sign-in.
- Bundled native `ipatool` and `ipadecrypt`; self-contained installer (no .NET required).

[1.0.0]: https://github.com/pwnapplehat/Decrypta/releases/tag/v1.0.0
