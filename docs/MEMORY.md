# Reading IL-2's memory

Why: IL-2 stops a track recording when the `.trk` reaches about 500 MiB, and the Tacview ACMI stops with it. On the 2026-10-27 18:25 Career sortie that took about 22 minutes. Reading positions straight from the game removes the dependency on recording. The rule (CLAUDE.md): read-only, from our own process, never writing and never injecting code; single-player only; positions read this way are still subject to the honesty rule and the visibility model.

## Status

| Phase | What | State |
|---|---|---|
| 0 | Diag tools (`mem-*`) | Done, and used against IL-2 on 2026-10-27 (`mem-near` and `--fields` added during that session) |
| 1 | Find the player's aircraft and its fields | Done: position, velocity, speed and orientation for every object, by id (see Findings) |
| 2 | Find the list of all aircraft and a static path to it | List found (an array of every object's scene nodes); no static path yet. Alternative below |
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

## Findings (2026-10-27, Career mission, Il-2.exe file version 1.0.0.1)

Session: player Gert Knoll, Bf 109 F-4, mission-log object id 1217535 (`0x1293FF`; the ACMI ids use the same numbers in hex). All addresses below are heap addresses from that run and change every launch; the layouts are what matter.

- **Coordinates are doubles, in metres, in the mission-log frame:** x = north, then height above sea level, then z = east. VERIFIED: a 20 s watch while flying a shallow turn heading 255 at about 400 m gave a track of about 261° and heights 342→390 m; level flight heading 270 gave 272° (height 537 m ASL with the QFE altimeter reading 470 m over a field at 57 m).
- The id occurs about 1000 times (references from other objects). Filtering for two coordinate-sized doubles within 0x200 bytes (`mem-near f64 20000..400000 --count 2`) left 48; two of them were live.
- **Structure B, a packed array of 0xE0-byte records, one object each** (offsets from the record's id field):
  - `-0x38` 8-byte value differing per record; `-0x30` i32 counter twice, the same in every record and rising (5101, then 5801 between dumps); `-0x28` f32 50.0; `-0x20` i32 another id-like number (`0x152FFF`, next record `0x152BFF`); `-0x08` the same pointer in every record.
  - `+0x00` object id (i32). `+0x08` position x, y, z (f64). `+0x20` and `+0x38` two earlier positions.
  - `+0x50` velocity x, y, z (f32, m/s), stored twice: (6.02, 0.54, -109.45) flying heading 270 is 395 km/h toward 273°.
  - `+0x68` three f32 (angular velocity?). `+0x74`, `+0x84`, `+0x94` three unit quaternions (f32), one per stored position.
  - The next record held wingman id 1196031 with his position; after that, no more records (unrelated data). So B is not the full roster. The bytes before the player's record were URL-style text (`aimAngleYaw=0.0000&...&orient=0.4975|0.3836`), so B may be a serialization or network packet of some kind.
  - **Not the track recorder:** with recording off (segment `_01` closed 20:56:37, `_02` opened 20:56:55) B and D both kept updating. VERIFIED.
  - IL-2 pauses while its window is out of focus: every field freezes while the player is in the terminal. Expect that in watches.
- **Structure D, a scene node** (offsets from its id field): `+0x04` i32 2; `+0x10..0x20` three pointers to sibling blocks 0x1F8 apart; `+0x2C` f32 speed in m/s (109.8 = 395 km/h); `+0x90` a 4×4 double matrix (identity); `+0x110` the world matrix, 4×4 doubles, row-major: row 0 is the nose direction (0.037, 0.015, -0.999 → heading 272°, pitch 0.9°), row 1 is up, row 2 is right, row 3 the position, then 1.0; `+0x198` a pointer into `Il-2.exe` (+0xBD9408, perhaps a vtable).
  - D sits in a run of 0x1F8-byte blocks, each starting with i32 0, i32 16, then the owner id, i32 2, and the constant `0x84222325`. Four such blocks carried the player's id; only D had the matrix and speed. Nothing points at D's id or the 0x20 bytes before it (`mem-refs`), so whatever owns it points further back.
- **Every object has D-type nodes (VERIFIED).** The constant `0x84222325` (i32 -2078137563) occurs 20,208 times, so it marks a common node type, not aircraft. Keeping the hits with coordinates within 0x200 bytes left 2,196; offsets from the constant: **id at -8, speed f32 at +0x24, position f64 at +0x168/+0x170/+0x178**. Those included the player, Soviet aircraft from the mission log (Pe-2 1016831 at 2,477 m and 76 m/s; LaGG-3 36863 at 3,248 m and 105 m/s, 3 nodes; LaGG-3 53247 at 3,185 m and 123 m/s, 7 nodes a few metres apart) and objects parked on the player's airfield at 57 m whose ids the mission log does not list (978943, 1445887, 1616895). So the game's memory holds every aircraft's id, position and speed whether or not a track is recording.
- **The node owner: one object per scene node, 0x2E88 bytes before the node's id field** (VERIFIED for the player and a Pe-2: identical internal self-pointers at identical offsets, and dozens of outside pointers to that base). From that base: id `+0x2E88` (i32), speed `+0x2EB4` (f32, m/s), position `+0x2FF8/+0x3000/+0x3008` (f64 north, height, east).
- **A pointer array of every object's nodes** (one of several that list the same objects in the same order, which is how they were found: pointers to the player and the Pe-2 came in pairs 0x168 apart in many places). Walked with `mem-ptrs --fields "+2E88:i32,+2EB4:f32,+2FF8:f64,+3000:f64,+3008:f64"` it gave, for every aircraft in the mission, about ten nodes each (airframe, bot pilot, turrets, gunners, each with its own id from the mission log's AType:12 lines): all six of the player's flight, six Pe-2s at 2,380–2,500 m and 75 m/s, and a Bf 109 G-2 of another German flight at 3,317 m and 125 m/s. Every name matched the mission log. Later entries are other node classes, where these offsets read garbage, so a reader must check each entry's class (a vtable pointer is the likely test; not yet found).
  - The array's first entry follows a 16-byte header (`0x100`, `0x800004002C8EB4D6`) and sits right after another pointer array; nothing points to the first entry or within 0x40 before it. So it is not a plain vector with an owner; perhaps part of an arena or a larger object.
- **Open:** a static path from `Il-2.exe` to these nodes (a full scan takes 9 s over 11.5 GB, too slow and too loose for the radio); which node is an aircraft's main one when it has several; coalition, type and alive state (the mission log already gives type and coalition by id, so the id alone may be enough).

## Without a static path: find the nodes once per mission

A fixed path from `Il-2.exe` needs a multi-level pointer search (Cheat Engine-style pointer scan) and breaks more easily with patches. The radio may not need one:

1. At mission start the mission log already gives every aircraft's id, type, name and coalition (AType:12, AType:10).
2. Scan once for the player's id followed by `2` and `0x84222325` (a 12-byte exact pattern, so it is fast and precise), take the D-type node with a position, and step back 0x2E88 to its object.
3. Find the pointer arrays that hold that object (`mem-refs` style, one 8-byte exact search), and read the array every second: for each entry, check its class, read id, position and speed, and match the id to the mission log.
4. Verify continuously: the player's memory position must agree with the ACMI when one is recording, and ids must be ones the mission log announced. On any mismatch, rediscover; failing that, fall back to ACMI.

This depends only on field offsets, never on code addresses, so it may survive patches that move code. To test next: how long step 2–3 take on a full mission (aim: a few seconds, once), whether the array moves during a mission (new spawns), and which pointer identifies the node class.
