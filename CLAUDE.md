# IL-2 tactical radio

A companion app for IL-2 Sturmovik: Great Battles single-player Career. It answers numpad radio requests with live, honestly-limited information from the game's own outputs (mission logs, Tacview ACMI read through on-demand Volume Shadow Copy), spoken by the game's pilot voices and shown in RTSS's on-screen display.

For bug reports, use the `il2-radio-bugfix` agent (`.claude/agents/il2-radio-bugfix.md`); its triage steps and symptom map apply to any bug fix. For new features, use `il2-radio-feature` (`.claude/agents/il2-radio-feature.md`); its recipes cover adding commands, settings, data sources and voice or chat capabilities.

Read before changing anything:
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): how every subsystem works, data flow, conventions, pitfalls.
- [docs/ROADMAP.md](docs/ROADMAP.md): what's left, and what still needs checking in a real flight.
- [docs/FEASIBILITY.md](docs/FEASIBILITY.md): research and evidence (verified vs assumed), with sources.

Essentials:
- **Honesty rule:** replies come only from fresh live ACMI tracks; never present planned or stale data as current; destroyed aircraft never answer; enemies only once the visibility model says they were seen, or (the one exception, at the user's request) once an AI leader's own turn onto a live enemy shows the game's pilot has seen it (`UnaskedCalls.TurnedToward`).
- **No game memory access or game-file changes.** `startup.cfg` only, with a backup and the user's consent. Never copy or commit IL-2's audio files.
- **One exception, decided 2026-10-27:** OverlayKit's host DLL (`overlay/`, `overlaykit64.dll`) may be loaded into `Il-2.exe`, and only when the player chooses chat output `ingame`. It hooks `Present` to draw our own layers and must never read or write game memory. Don't load it into IL-2 yourself while the user's session runs; test against `overlay/bin/OverlayKitTestHost.exe` (`--ingame-exe OverlayKitTestHost`). Native parts are built by `overlay/build.ps1` (VS C++ workload + Windows SDK); see `overlay/README.md`.
- `dotnet test` must stay green. If the user's `live` session or `Il2Radio.exe` holds `bin\Debug`, run `dotnet test tests/Il2Radio.Core.Tests` and build the programs into a scratch folder (`dotnet build src/Il2Radio.Diag -o <dir>`, same for `src/Il2Radio.App`).
- Projects: `Il2Radio.App` is the player's program (`Il2Radio.exe`, `settings.json`); `Il2Radio.Diag` is the developer console; both use `Il2Radio.Windows` (I/O and `LiveSession`) and `Il2Radio.Core`.
- The copy the user flies is outside this repo: `%USERPROFILE%\Desktop\IL2Radio\Il2Radio.exe`. `settings.json` is beside it. Session logs for troubleshooting are `%USERPROFILE%\Desktop\IL2Radio\logs\` (`evidence-*.md`, `udp-capture-*.bin`), written when that window closes. Diag, run from this repo, writes `diag-output\` instead. Repo `bin\` and `publish\` are build output, not the flying copy.
- The user flies Luftwaffe in Battle of Stalingrad. Controls are on the numpad (F and Q are bound in IL-2). Live positions need administrator rights (`Il2Radio.exe` asks for them; Diag needs an administrator terminal).
- Don't start the app or Diag `live`/`chat-test` while the user's session runs: they share the RTSS slot and the numpad.
- When editing files from the shell, prefer the editor tools: heredoc/Python edits in this environment have mangled backslash escapes (`\n`, `\\`) in C# literals more than once.
