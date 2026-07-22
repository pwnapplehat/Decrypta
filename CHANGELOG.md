# Changelog

All notable changes to Decrypta are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.2] - 2026-07-22

### Added
- **Pick a specific version to decrypt.** The Decrypt tab has a new **Version** dropdown and a
  **Load versions** button: it lists the app's App Store version history and lets you choose any
  past build (or keep *latest*, the default). Selecting a version pins its identifier and forces
  the App Store download path, since a historical build can't come from the installed copy. The
  raw identifier is still editable under *advanced: external version id*.
- Version discovery uses the bundled `ipatool`, which keeps its own credential store, so it does a
  one-time sign-in with your active Apple ID the first time you load versions. If Apple asks for a
  2FA code, an inline prompt appears right under the picker — enter it and press **Submit & load**.

### Notes
- Only the newest 15 versions are resolved to human-readable version numbers and dates to stay
  well within Apple's rate limits; older builds remain selectable by their identifier.

## [1.0.1] - 2026-07-21

### Fixed
- **"Use installed build" now works with an App Store link or numeric id**, not just a bundle
  id. ipadecrypt only matches an installed app by bundle id, so pasting a link/id previously
  fell through to an App Store download. Decrypta now resolves the id → bundle id first via
  Apple's public lookup (no sign-in), so the installed copy is decrypted in place as intended.
  Output files are also named by bundle id in this case. Falls back to the App Store path with
  a clear note if the id can't be resolved.

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

[1.0.1]: https://github.com/pwnapplehat/Decrypta/releases/tag/v1.0.1
[1.0.0]: https://github.com/pwnapplehat/Decrypta/releases/tag/v1.0.0
