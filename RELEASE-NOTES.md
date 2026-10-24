# FluentFlyout Sideloader v1.0.0 - first public build

**Unofficial one-file installer + updater for the FluentFlyout GitHub release.**
Not affiliated with FluentFlyout/unchihugo. The app inside is the unmodified official GPL-3.0 release (v2.16.0).

## What's new

- **Single-exe install & uninstall** of the GitHub MSIX build (cert + package + Apps-list entry handled automatically, with a real uninstaller sweep - no leftovers)
- **Automatic updates:** launch check + daily scheduled check; applies updates only on click, with percent-bar downloads from the official fluentflyout.com feed; **no UAC** when the signing certificate is unchanged (single UAC only if the release rotates its cert)
- **Modern Win11-style UI** (dark/light, real app logo, fades) - no dialogs from 2005
- **Full uninstall cleanliness:** app, certificate(s), uninstall entry, scheduled task and update cache all removed
- **Zero dependency build:** compiles itself with the `csc.exe` shipped in Windows; source included

## Safety / correctness fixes in this build

- Package queries require the GitHub publisher and exclude Store-signed packages (`SignatureKind = Store`), so the Microsoft Store build is never detected, updated, launched, or uninstalled.
- The update success message and uninstall-entry version now use the version actually served by the feed, not the GitHub API tag (which can be ahead of the feed).
- Update downloads pin TLS 1.2 explicitly.
- The uninstaller removes every certificate thumbprint recorded by past updates, not just the newest one.
- The build reads the app version/publisher from the bundle manifest, so the exe name and UI stay correct after running `fetch-official-kit.ps1`.

## Notes

- The bundled app in this file is **2.16.0**; the updater installs newer versions from the official feed when released.
- The exe is unsigned - expect a SmartScreen notice ("More info -> Run anyway") or build from source instead.
- SHA-256 of `FluentFlyout-Sideloader_2.16.0_x64_Setup.exe`: **`29BD60C9AB263DC9CE2DC7B97DA752C2250C22D5A4FF3AB0E60BAC6DE48A92E6`**

## Source

- Wrapper source: this repository.
- Bundled FluentFlyout 2.16.0 source: <https://github.com/unchihugo/FluentFlyout/tree/v2.16.0> (GPL-3.0).
