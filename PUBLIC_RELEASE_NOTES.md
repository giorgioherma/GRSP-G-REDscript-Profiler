# G-REDscript Profiler v1.0.0

First stable release of the standalone G-REDscript Profiler.

## Highlights

- Portable, self-contained Windows ZIP — extract and run.
- Native REDscript profiling through the released `G-REDscript-Profiler.dll`.
- F11 START / F11 STOP + export.
- Capture titles and structured result archives.
- Verified collection and cleanup of GRSP-owned live files.
- Safe restore with strict existing-DLL handling.
- Optional frame-time companion; CapFrameX is recommended/tested but not required.
- External frame-time results are copy-only.
- Unified profiler readiness/status UI using the frozen G-CET dark/cyan/magenta shell.
- White status prose with colored semantic markers; disabled actions gray, enabled forward actions cyan, restore magenta.
- Dark themed dialogs retaining the normal Windows warning/information/error icons.
- Small native root launcher with the self-contained manager/runtime isolated under `app\`.
- Package-local settings and results.
- Headless interface for the exact same standalone package consumed by TOTAL Profiler.

## Portable package

```text
G-REDscript-Profiler-v1.0.0.zip
└─ G-REDscript-Profiler\
   ├─ G-REDscript-Profiler.exe
   ├─ MANIFEST.json
   ├─ VERSION.txt
   ├─ app\
   │  ├─ G-REDscript-Profiler.App.exe
   │  └─ .NET runtime files...
   ├─ payload\
   │  ├─ G-REDscript-Profiler.dll
   │  └─ CaptureTitle.txt
   ├─ RESULTS\
   └─ docs\
      ├─ README.md
      ├─ CHANGELOG.md
      ├─ RELEASE_NOTES.md
      ├─ FRAMEWORK_AUTHOR_GUIDE.md
      ├─ OUTPUT_SCHEMA.md
      ├─ TOTAL_INTEGRATION_CONTRACT.md
      └─ THIRD_PARTY_NOTICE.txt
```

Extract to a writable folder and run `G-REDscript-Profiler.exe`.

## Frozen canonical build

- Source commit: `8c043c971a37835aee6bbb87261c1426dd7f4d38`
- GitHub Actions run: `36168178334`
- Frozen artifact ID: `10879746258`
- Canonical release asset: `G-REDscript-Profiler-v1.0.0.zip`
- SHA-256: `9fb2a356553ad5dedf4859c02b6637b7a06513f953086f939925ae5fe800a5ac`

This exact artifact is the frozen v1.0.0 public build. GitHub Releases is the canonical download surface; later repository-only documentation or metadata changes do not redefine the v1.0.0 binary.
