# FluentFlyout Sideloader (unofficial)

**One-file installer, installer-replacement, and updater for the FluentFlyout GitHub release - built with nothing but the tools that ship inside Windows.**

> **Unofficial community project.** This is a third-party *installation wrapper* for [FluentFlyout](https://github.com/unchihugo/FluentFlyout). It is not affiliated with, endorsed by, or produced by the FluentFlyout/unchihugo authors. The FluentFlyout app, its `.msixbundle`, its signing certificate, and its logo belong to their respective authors and are redistributed here under their own license (GPL-3.0). If anything feels off, prefer the official install path described in [upstream's README](https://github.com/unchihugo/FluentFlyout#how-to-install-).

---

## Why this exists (the story)

FluentFlyout's GitHub exe is wrapped in an MSIX package signed with the author's own self-signed certificate. The official manual install therefore means:

1. download a `.cer` file
2. open the cert wizard, install to **Local Machine -> Trusted Root Certification Authorities**
3. download the `.msixbundle`, install, and re-do the wizard dance on every reinstall

That's slow, easy to get wrong, and very easy to forget to undo (the root cert, the Apps-list entry, and the package itself all linger if you do it by hand). So this project replaces the whole dance with a single exe that:

- **installs** the app + certificate (with a modern Win11-style UI),
- **uninstalls** everything it ever created (app, cert, Apps-list entry, scheduled task, cache files) - no leftovers,
- **updates** the app from the official feed, on click, never silently,
- and rebuilds itself from source using **only the C# compiler that ships with Windows** (`csc.exe`, .NET Framework 4.x - no Visual Studio, no SDK, no external tools).

## What the exe does

### Install (first run, no args)
- shows a themed (dark/light, follows system) borderless Win11-style window
- extracts its **embedded** copies of the official `.cer` and `.msixbundle` (byte-for-byte the files from the official release kit - the exe is just a smarter container)
- trusts the release certificate into `LocalMachine\Root` (same store the manual wizard uses)
- installs the package via PowerShell's `Add-AppxPackage -ForceUpdateFromAnyVersion -ForceApplicationShutdown` (per-user, but silently reruns the app instance if it is running)
- launches the app **non-elevated** (via the standard `explorer.exe shell:AppsFolder` shim so FluentFlyout never runs with admin rights)
- writes a normal `HKLM\...\Uninstall\FluentFlyoutGitHub` entry so the app shows in "Installed Apps" with size and version
- creates a daily scheduled task **`FluentFlyoutUpdateCheck`** (logged-on user, silent when nothing is new)
- unpacked progress UI: real percent, marquee states, cancel handling; failures show the *actual* PowerShell error instead of cryptic CLIXML noise

### Uninstall (`/uninstall` or the chooser)
Removes **all** of it: the GitHub package, the embedded certificate, every certificate thumbprint recorded by past updates, the uninstall entry, the scheduled task, and `%LocalAppData%\FluentFlyoutSetup` (update cache). Tested to leave the system clean.

### Modify (chooser)
Double-clicking the exe while the app is installed shows a non-elevated chooser: **Reinstall** / **Uninstall** (plus a live update-check status line). Nothing touches the system before you choose, and UAC only appears once you actually pick something elevated.

### Automatic updates - the headline feature
FluentFlyout's GitHub version famously ships without an updater ("updates must be done manually" per upstream). Not anymore:

- **check surfaces:** on every launcher run *and* a daily scheduled task - both fat-finger-proof and rate-limit-friendly (30 min minimum between checks, silent failure offline)
- **what it fetches:** the latest release **version** from the GitHub API (`tag_name`, with an HTML-redirect fallback), and the actual artifacts from the **official website feed** `https://fluentflyout.com/files/latest/x64` - the ZIP that upstream's own download page hands out
- **why the website feed:** during development, GitHub's release API began reporting `assets: []` for every release - upstream moved the files behind their download page. This tool reads the JSON tag, downloads their official kit zip, and pulls the `.msixbundle` + `.cer` out of it. No third-party mirrors; no unusable endpoints. A guard compares the version encoded in the served zip's filename against the installed one, so a lagging feed can never silently downgrade you.
- **apply flow:** download with a real percent bar -> unzip -> compare the downloaded cert's thumbprint against the machine root store -> **if the cert is unchanged (the common case): installs with zero UAC prompts** (per-user `Add-AppxPackage`). If the release rotated its signing cert: exactly one UAC prompt to trust it, continuing as administrator
- **consent model:** never applies silently. It always shows a window with "Update now / Not now" (scheduled runs) or runs when you clicked "Update now" in the chooser. Your install, your click
- **bookkeeping for full uninstall:** after each update the exe stores the newest release cert + every thumbprint it has seen to `%LocalAppData%\FluentFlyoutSetup\` so a later uninstall can remove *those* certs too - not just the embedded one

### Microsoft Store safety
The Store build and this GitHub build share the same package identity (Name, Publisher, and PackageFamilyName). The difference is the **signature kind**: the Store build is Store-signed, while this sideload is signed by upstream's self-signed certificate (`SignatureKind = Developer`). Every package query in this tool requires the GitHub publisher (`CN=49793F74-1457-4B66-A672-4ED3A640FC76`, read from the embedded bundle manifest) **and** a non-Store signature. If only the Store build is installed, setup refuses to touch it and tells you to uninstall it first. The Store package is never detected, updated, launched, or uninstalled by this tool.

### Command line reference

| invocation | effect |
|---|---|
| *(no args)* | install, or chooser when already installed |
| `/uninstall` (`/u`) | full uninstall (elevates) |
| `/preview` | show the install UI with no changes - safe to click through |
| `/check` | silent update check; window only if a newer version exists (respects the 30-minute throttle) |
| `/check /pretend-old` | *test hook:* treats the installed version as `0.0.1` so the full update pipeline runs against the real current release |

### Diagnostics
Any failed update check writes the exact reason (incl. HTTP status codes) to `%LocalAppData%\FluentFlyoutSetup\last-check-error.txt`. Success clears it.

## Why does it install a root certificate?

Self-signed MSIX packages only install if the signing certificate is trusted. Upstream's own manual instructions tell users to import their `.cer` into **Local Machine -> Trusted Root Certification Authorities**. This tool automates exactly that, and removes the certificate again on uninstall.

Current embedded release certificate:

- Subject/issuer: `CN=49793F74-1457-4B66-A672-4ED3A640FC76`
- SHA-1 thumbprint: `057B902D4C45DD4780555CAF02D2A69C8860C57C`
- Valid until: 2027-10-16

If upstream rotates the certificate, the updater asks for one UAC confirmation before trusting the new one; it never trusts a new certificate silently.

## Building it yourself

Everything compiles with the compiler that is already on your machine:

```
FluentFlyout-Sideloader\
├── fetch-official-kit.ps1      ← gets the current official .msixbundle + .cer into SystemFiles\
├── SystemFiles\
│   └── FluentFlyoutMSIX_..._GitHub Release.cer     (committed; 812 bytes)
├── SetupBuild\
│   ├── build.ps1               ← creates the payload + exe icon, invokes csc.exe
│   ├── preview.ps1             ← no-damage UI preview of the installer window
│   ├── Installer.cs            ← the entire program (UI + operations + updater)
│   └── payload\
│       ├── logo.png            ← app logo used for the badge + exe icon
│       └── app.ico
└── FluentFlyout-Sideloader_<version>_x64_Setup.exe  ← (built binary; ships as a Release asset, not in git)
```

```powershell
# one-time (or whenever you want the newest bundled version):
powershell -ExecutionPolicy Bypass -File .\fetch-official-kit.ps1

# build:
cd .\SetupBuild
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

`build.ps1` reads the version and publisher from the bundle manifest, verifies that the certificate subject matches the bundle publisher, embeds `SystemFiles\*.cer` + `SystemFiles\*.msixbundle` as resources, and emits `FluentFlyout-Sideloader_<version>_x64_Setup.exe` (~91 MB; the size is the embedded package, not the code - the code is ~3,000 lines of C# 5).

The `.msixbundle` is intentionally **not committed to git** (95 MB). Use `fetch-official-kit.ps1`, or download the kit manually from [fluentflyout.com](https://fluentflyout.com/donate/) -> *"No thanks, continue to download"* -> zip -> `SystemFiles\`.

## How the updater was validated (live tests)

- `/check` with app installed and up to date -> silent exit 0, no window, no error file
- `/check /pretend-old` -> updater prompt appears
- live update client -> API tag resolved, ~91 MB feed download completed, zip extracted, cert verified against `LocalMachine\Root`
- `/check /apply <bundle> <cert> <version>` -> package updated, app restarted, uninstall entry refreshed, exit 0
- update state records the check time and every cert thumbprint (`last-release.cer`, `last-release.txt`)
- manual `schtasks /Run` of the update task -> `Last Result: 0`
- full uninstall -> scheduled task gone, state folder gone, root cert gone, package gone, uninstall entry gone, process gone
- reinstall after uninstall -> app, cert, entry and task recreated
- package filters require the GitHub publisher and exclude `SignatureKind = Store`; the Store build shares the family name but is never touched (code-level validation; a Store-only live test was not possible in the sandbox)

## Licensing & legal

- **FluentFlyout** is (c) its authors, licensed **GPL-3.0** ([source](https://github.com/unchihugo/FluentFlyout), [license](https://github.com/unchihugo/FluentFlyout/blob/master/LICENSE)). The embedded `.msixbundle` is their GPL-3.0 build, redistributed unmodified; its source is therefore what the upstream repo publishes. This project makes no changes to the app.
- **This wrapper** (Installer.cs, build scripts, updater, this README) is likewise released under **GPL-3.0** (see `LICENSE`) - the license upstream uses, so the combined artifact keeps one consistent license.
- The FluentFlyout logo/name used by this installer is upstream branding used to *identify the app being installed*, redistributed under GPL-3.0. Trademark use is nominative; this repo does not present itself as the official project.
- The signing certificate embedded/installed is the one **upstream distributes publicly themselves** for manual sideloading (their own docs instruct installing it manually into `Trusted Root`). This tool automates precisely those instructions and removes the cert again on uninstall.
- **No official relationship.** No affiliation, endorsement, or sponsorship from unchihugo/FluentFlyout or Microsoft is claimed or implied. Consider [sponsoring upstream](https://github.com/sponsors/unchihugo) - they build a great free app.
- **Microsoft Store version exists.** If you want an official auto-updating install with support, use the Store listing (9N45NSM4TNBP). This sideloader exists only because a community-maintained option for the *free GitHub build* seemed useful; it deliberately does not touch the Store.

## Safety notes

- The exe is unsigned -> Windows SmartScreen may warn on first run ("Run anyway"). That is expected for any self-built tool; security-wise this is why **building from source** is the cleanest option.
- The installer only ever installs **the exact upstream-released package** and never re-signs or modifies it.
- The updater only ever downloads from **upstream's own endpoints** (GitHub API for versioning, `files.fluentflyout.com` for the kit) over TLS 1.2+.
- The embedded cert gets installed into the machine root store (that's what makes self-signed MSIX install work); it's removed on uninstall and equals the one upstream's manual docs tell you to install.
- This repository contains **source only**. The large built exe and the upstream `.msixbundle` are not committed to git; the exe is attached to Releases with a SHA-256 checksum. If you are from upstream and want a change or removal, please open an issue - we'll comply.

## Credits

- [unchihugo](https://github.com/unchihugo) & contributors - FluentFlyout
- The FluentFlyout community (docs, translations, testing)
- This repository's wrapper: hand-built WinForms/C# 5, csc.exe-only toolchain, Microsoft docs + a lot of trail and error
