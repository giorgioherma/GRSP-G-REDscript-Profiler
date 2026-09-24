# Changelog

All notable public changes to G-REDscript Profiler are recorded here.

## [Unreleased]

### Installer and packaging

- Ported the frozen G-CET v1.0.0 dark/cyan/magenta installer shell to the standalone REDscript manager while preserving REDscript-specific capture-title and lifecycle behavior.
- First-page setup layout now mirrors G-CET: white body text, cyan section/link accents, matching CapFrameX 1.9.1.2 Beta wording, header accents, and dark themed dialogs.
- Profiler status keeps prose white and colors only semantic ✅ / ⚠️ / ❌ markers.
- Disabled actions are visually gray; enabled forward actions are cyan; enabled restore remains magenta.
- Replaced the giant self-contained single manager EXE with a small native root launcher plus the self-contained managed application/runtime under `app/`.
- Public package root is limited to the launcher, `MANIFEST.json`, `VERSION.txt`, `app/`, `payload/`, `RESULTS/`, and `docs/`.
- Package-local settings, payload and results resolve from the public package root whether launched through the root EXE or by invoking the internal app directly.
- CI now verifies the clean package layout and smoke-tests launcher/direct-app status, install and restore.

## [1.0.0] - 2026-09-23

First stable public release.

### Standalone product

- Finalized the self-contained .NET 8 WinForms manager and portable ZIP distribution.
- Public native plugin is `G-REDscript-Profiler.dll`.
- Installed data lives in the sibling `red4ext\plugins\G-REDscript-Profiler\` directory.
- Package-local settings remember the game root, capture title, optional frame-time executable/results paths, and pairing preference.
- Unified the second-page readiness UI with G-CET Runtime Profiler:
  - ✅ confirmed good / completed
  - ⚠️ optional, pending, unknown, or still usable
  - ❌ blocking error only

### Capture and reporting

- F11 starts and stops native REDscript measurement.
- Capture title is read at START and included in the capture folder name.
- Game shutdown during recording acts as an implicit STOP and exports the useful partial capture.
- Public report, per-mod/function views, timeline, frames, spikes, markers, framework candidates, and developer outputs are retained.
- 50 ms timeline and Unix timestamps remain available for cross-profiler correlation.

### Installation and restore

- Existing `G-REDscript-Profiler.dll` files are never overwritten.
- Exact packaged DLL already installed is recognized as usable.
- Different GRSP versions/builds and legacy alpha DLLs block installation.
- Restore archives remaining live profiler output, removes managed files, and intentionally leaves the final empty data folder.

### Collection ownership

- GRSP-owned live output is copy-verified into the standalone archive and then removed from the game folder.
- External frame-time files are always copy-only and are never moved, deleted, or modified at source.

### Frame-time companion

- Frame-time pairing remains optional.
- CapFrameX is the tested/recommended companion without becoming a dependency.
- Other frame-time profilers remain supported.

### TOTAL integration

- TOTAL Profiler consumes the exact standalone GRSP release unchanged.
- TOTAL-specific orchestration and correlation stay in TOTAL rather than a special GRSP variant.

## Pre-1.0 development

The alpha/public-preview builds were development iterations leading to the stable 1.0.0 product. They are superseded by v1.0.0.
