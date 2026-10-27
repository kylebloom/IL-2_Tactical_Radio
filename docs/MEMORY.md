# Reading IL-2's memory

Why: IL-2 stops a track recording when the `.trk` reaches about 500 MiB, and the Tacview ACMI stops with it. On the 2026-10-27 18:25 Career sortie that took about 22 minutes. Reading positions straight from the game removes the dependency on recording. The rule (CLAUDE.md): read-only, from our own process, never writing and never injecting code; single-player only; positions read this way are still subject to the honesty rule and the visibility model.

## Status

| Phase | What | State |
|---|---|---|
| 0 | Diag tools (`mem-*`) | Done. Unit-tested against a fake process; smoke-tested against a live PowerShell process. Not yet run against IL-2 |
| 1 | Find the player's aircraft and its fields | Not started; needs the user in-game |
| 2 | Find the list of all aircraft and a static path to it | Not started |
| 3 | Survive game updates (byte patterns, version check, fall back to ACMI) | Not started |
| 4 | `PositionSource.Memory` in the radio, checked against ACMI for a whole sortie | Not started |

## The tools

Code: `Core/Memory` (`ScanPattern`, `MemoryScanner`, `ScanState`, `MemoryDump`, `AddressExpression`; tests in `MemoryScanTests`), `Windows/ProcessMemory` (`OpenProcess` with `PROCESS_QUERY_INFORMATION | PROCESS_VM_READ` only, `VirtualQueryEx`, `ReadProcessMemory`), `Diag/MemTools`.

Every command attaches, does one thing and exits, so they run from any administrator terminal while IL-2 is open. A scan's results are saved to `diag-output\mem-scan.bin` together with the game's process id and start time; after a game restart the old results are refused.

| Command | Use |
|---|---|
| `mem-info` | Process, file version, modules, how much memory is readable |
| `mem-scan <type> <value\|a..b>` | New search. Types `i32 i64 f32 f64 ascii utf16`. A single float matches to the precision written (`1234` is ±0.5, `1234.5` is ±0.05). `--align N`, `--module Il-2.exe` (static data only), `--writable`, `--max N` |
| `mem-next <filter>` | Narrow the saved results: `changed`, `unchanged`, `increased`, `decreased`, or a value/range |
| `mem-list` | Saved results with their current values |
| `mem-dump <address>` | 8 bytes per row as hex, two i32, two f32, f64, text, and "→ module+offset" when the value points into readable memory. `--bytes N` |
| `mem-refs <address>` | Pointers to the address or up to `--before` (hex, default 400) bytes ahead of it, i.e. to the structure holding it. Static ones (inside a module) are listed first. Saves the results, so `rN` then names a pointer |
| `mem-watch <addr:type[:label]>...` | Print values every `--interval` ms for `--seconds` |

Addresses: hex (`1A2B`, `0x1A2B`), `module+offset` (`Il-2.exe+3C0`), `[x]` reads the 8-byte pointer at x, `rN` is saved result N (`@N` also works outside PowerShell, which reads a bare `@0` as splatting). Example: `[[Il-2.exe+1F2A30]+18]+40`. `--process NAME --pid N` reads another process, for testing the tools.

## Phase 1 plan: the player's aircraft

Fly a Quick Mission with a few AI wingmen, **with track recording on**: the ACMI is the ground truth for everything found. Pausing freezes values, which makes `unchanged` scans clean.

Ways in, cheapest first:
1. **Object id.** ACMI ids such as `5b3ff` may equal the mission log's object ids. `mem-scan i32 0x5b3ff` for the player's id, then `mem-dump` around each hit.
2. **Names.** `mem-scan utf16 "Gert Knoll"` and `ascii`/`utf16` for the type (`bf109f4`), then `mem-refs` on the hits to reach the objects that own them.
3. **Position.** On the ground, scan for the position the mission log gives in game metres (as f64, then f32), taxi, `mem-next changed`, pause, `mem-next unchanged`, and repeat.

Then `mem-dump` the structure around the hit and name fields against UDP telemetry (heading, pitch, roll, speed) and the ACMI.
