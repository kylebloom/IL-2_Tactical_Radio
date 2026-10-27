# Feasibility assessment — IL-2 Great Battles tactical radio

Status as of 2026-10-25. Evidence levels used throughout:

| Tag | Meaning |
|---|---|
| **VERIFIED** | Checked on this machine against real IL-2 output (BoS install, Career Mode files) |
| **DOCUMENTED** | Official or well-established documentation; not yet re-tested here |
| **COMMUNITY** | Reverse engineered or reported by community projects |
| **SPECULATIVE** | Plausible, untested |

## The critical question

> Can an external application access the actual, current positions and identities of AI aircraft during a running single-player Career mission?

**Answer (2026-10-25): the data exists live, but IL-2 locks it.** With `tacviewrecord = 1` and track recording on (LCtrl+R), IL-2 writes a plain-text ACMI file to `data\Tracks` in real time (~18 KB/s). It contains **every** aircraft and ground vehicle in the mission at ~5 Hz, with real lat/lon/altitude, attitude, AGL, type, pilot name and coalition. On the 2026-10-27 Career track, AGL was present on every aircraft sample; an aircraft with its wheels on the ground read about 1.4 m (VERIFIED). IL-2 opens the file **without read sharing** while recording, so no other process can read it until recording stops. Mission-log events arrive live in ~33 s chunks (see §5–6).

| Data | Available? | Source | Evidence |
|---|---|---|---|
| Identity of every aircraft (type, pilot name, country/coalition, object id) | Yes | mission log AType 12 | VERIFIED |
| Which aircraft are in the player's flight, and their formation numbers | Yes | mission log AType 10 (`FORM`, `ISPL`) | VERIFIED |
| Status: airborne, landed, damaged, destroyed, bailed out, despawned | Yes | AType 5/6/2/3/18/16/4 | VERIFIED |
| AI position at spawn, take-off, landing, damage, kill, bail-out | Yes, as snapshots | same events | VERIFIED |
| Continuous AI positions | **Not in mission logs**: AType 17 appears in none of 66 local logs (63,431 records) | — | VERIFIED |
| Continuous AI positions | **Yes, but only readable after recording stops** (file locked while open) | `data\Tracks\*.acmi` | VERIFIED |
| Player attitude, IAS, AGL, engine, hits | Yes, ~real time | UDP motion + telemetry | COMMUNITY (formats); UDP path tested with synthetic packets |
| Player **world position** | **No**: neither UDP stream contains it | — | COMMUNITY (IL2Shaker decoders) |
| Planned flight, waypoints, enemy flights in the mission | Yes, as plan only | `data\Missions\_gen.Mission` | VERIFIED |

The player's own position is just as much a blocker as AI positions. Bearing and range need it, and the UDP telemetry does not carry it.

## Findings by research item

### 1–2. Modding capabilities, Mods On/Off
IL-2 GB has no plugin API, no in-mission scripting runtime and no public SDK. The "mods" system (JSGME-style file overrides, which need Mods On) is for skins, sounds and the GUI, not for game logic. Everything in this project uses outputs the game already provides and edits only `startup.cfg`. It is therefore **Mods Off compatible**. Nothing here touches `.gtp` archives, the game process or its memory. (DOCUMENTED / general community knowledge)

### 3. Career Mode mission generation
- Before each sortie, the Career generator writes the mission as **text** to `data\Missions\_gen.Mission` and binary to `_gen.msnbin`. Localisation goes to `_gen.eng` etc. (VERIFIED: 4.4 MB text file, 34 `Plane` blocks)
- The player plane is the one with `AILevel = 0`. `Name = "Pilot Name,rosterId,n"`, `Country`, `Callsign`, `Callnum`, `Script` (aircraft type) and the start position are all present. (VERIFIED)
- Career state lives in SQLite at `data\Career\cp.db`. IL-2 Integrator reads planned routes from its `mission.route` column ([source](https://github.com/rafaelvcaetano/il2integrator/blob/master/service-career/src/main/java/me/magnum/il2mapintegrator/career/CareerDatabase.java)). (COMMUNITY)
- IL-2 Allied Radio uses the same `_gen.Mission` approach for Career/AQMB missions ([forum](https://forum.il2-series.com/topic/5325-il-2-allied-radio/)). (COMMUNITY)

No per-mission editing is needed, so existing careers keep working.

### 4. Telemetry protocols
Two UDP outputs, configured in `startup.cfg`:
```
[KEY = telemetrydevice]  addr = "127.0.0.1"  decimation = 2  enable = true  port = 29373  [END]
[KEY = motiondevice]     addr = "127.0.0.1"  decimation = 2  enable = true  port = 29373  [END]
```
- Motion packet `0x494C0100`: tick, yaw/pitch/roll, angular rates, accelerations. Telemetry packet `0x54000101`: indicators (RPM, manifold pressure, gear, IAS, AGL, flaps...) plus events (vehicle name, guns, bombs, hits, damage, seat). Layouts are taken from [IL2Shaker's decoders](https://github.com/sicsix/IL2Shaker/tree/master/IL2TelemetryRelay) and the official `IL2_MotionOutput.pdf` ([forum thread](https://forum.il2sturmovik.com/topic/72958-key-telemetrydevice-what-are-the-packet_id-outputs)). (COMMUNITY)
- **Only the player's vehicle. No world position.** (COMMUNITY, consistent across all decoders)
- Only one `addr:port` per device, so it conflicts with SRS (UDP 4322), Allied Radio and bass-shaker tools. A relay or forwarder will be needed for coexistence. (COMMUNITY / [SRS troubleshooting](https://srsforil2.com/))

### 5–6. Live AI positions and properties
**Mission logs.** IL-2 writes a binary `data\FlightLogs\missionReport(...).mlg` for every mission, used by the debriefing (`data\LParser\BoS.LogParser.Offline.exe`). With `mission_text_log = 1` it also writes text `missionReport(...)[N].txt` files that rotate during the mission. Server stats tools parse those in near real time ([il2_stats](https://github.com/vaal-/il2_stats/blob/master/src/mission_report/parse_mission_log_line.py)).

This project decodes the binary format directly (layout in [`BinaryMissionLog.cs`](../src/Il2Radio.Core/MissionLog/BinaryMissionLog.cs)). All 66 local logs (63,431 records) decode with consistent framing (VERIFIED). Results:
- Every aircraft is identified, with pilot name, type, country and status changes. (VERIFIED)
- In single-player, AType 10 marks the whole player flight with formation numbers 1..N and `ISPL` on the human. (VERIFIED)
- **No AType 17 periodic positions** in any local log. In the sample Career mission, the player's 7 wingmen have exactly **one** position each over 50 minutes (take-off). (VERIFIED)
- **Live delivery (VERIFIED in a Career flight on 2026-10-25):** with `mission_text_log = 1` and no `text_log_folder`, IL-2 writes rotating chunks `missionReport(<start>)[N].mlg` and a matching `.txt` **directly in `data\`**, about every 33 s of wall time. So log events (spawns, take-offs, kills...) reach the app with roughly ≤35 s latency. Chunk `[0]` holds the spawn roster, including the player's flight (`AType 10`, formation numbers). No AType 17 appeared during the flight either.

**Tacview ACMI.** `[KEY = track_record] tacviewrecord = 1` makes IL-2 write ACMI files while a track is being recorded (LCtrl+R, or automatic track recording) ([Tacview IL-2 guide](https://tacview.fandom.com/wiki/User_Guide_–_IL‑2_Sturmovik?oldid=710)). ACMI is an open text format with a position for every object every frame. A Tacview forum note says IL-2 exports "only the objects around the local aircraft". That would actually suit this project's realism goals. (COMMUNITY) Unknown so far:
1. Is the file written **incrementally** while recording, or only on close?
2. Is it plain text or zip-compressed (`.zip.acmi`, which can't be read until closed)?
3. Which objects and properties does it include (`Pilot`, `Coalition`, radius)?

**Live test results (VERIFIED, Career, 107 s recording):** plain text (not zipped), `FileVersion=2.1`, absolute lat/lon (`ReferenceLongitude/Latitude` = 0). All 11 aircraft were present, including enemies 60 km away (so it is not proximity-limited), each with 537 updates (~5 Hz), plus 233 ground objects. Names look like `Name=Bf 109 F-4 - Heinz Koelzig` with **unescaped commas** in AI roster suffixes and a 0x01 prefix on flight-mates. The player appears under the account nickname, which also appears as `0,Author=`. Coalition values are `Allies`/`Axis`. Replaying command 1 at the end of the recording gave correct replies for all five flight-mates ("Number one, bearing zero-three-zero, approximately 22 kilometres, altitude 1100 metres").

**Blocker:** while recording, IL-2 holds the file with no read sharing (`ERROR_SHARING_VIOLATION`). Options: (a) stop and restart recording periodically, since each stop closes a readable file; (b) read through a Volume Shadow Copy snapshot (documented Windows backup API, needs admin, heavy at high frequency); (c) use logs live and the ACMI only after a sortie.

**Resolved (VERIFIED 2026-10-25, Career flight, elevated terminal):** on-demand Volume Shadow Copy via `Win32_ShadowCopy`. One Numpad 1 press read 563 KB of new ACMI data: snapshot 1010 ms, read 25 ms, delete 134 ms, about 1.2 s total. It produced correct replies from all five flight-mates, about 17 km away at bearing 110, with fix age 0.2 s. It needs administrator rights and never touches the game process.

**ACMI east-west scale (VERIFIED, 2026-10-26 Career track).** Because IL-2's ACMI has no reference point, longitudes are absolute (about 43° E). Scaling each aircraft's longitude by the cosine of its own latitude puts a north-south gap into the east-west distance: the leader at 48.4388° N 43.1965° E and the player at 48.7562° N 43.5813° E came out 36.3 km apart, against 45.3 km by the great circle. The 40 km radio limit let a "heading 015" call through from 45 km. One cosine per stream (the first latitude seen) gives 45.3 km. Before the fix, ranges and bearings between aircraft far apart north to south were wrong by several kilometres; within a few kilometres the error was tens of metres.

**Who announces a loss in IL-2 (INFERRED from the recordings and the log, UNVERIFIED by ear).** The German pilot folders (`actor51`–`54\const`) hold `rep_trgdestr` (target destroyed), `rep_imhit`, `rep_infire` and `rep_falling`, and nothing for reporting someone else's loss. In the 2026-10-26 log a collision is AID -1 on both aircraft and both pilots, at the same tick (Bf 109 number six and a Hurricane, T:32503); the flight was 1–3 km away in the track. So the game says nothing about a collision, while a shooter can say his own kill and a hit pilot his own trouble. `LossCalls` follows that split.

**Unasked calls (built 2026-10-26, the cost is UNCHECKED in a flight).** Hearing a course or a contact without a key press means taking that same snapshot about every 10 s for as long as one of `unasked.route` or `unasked.contacts` is on. Ten seconds is long enough that a cruise leg still crosses the 400 m–3.5 km corner window (at 500 km/h the aircraft covers about 1.4 km in that time). Whether a one-second copy that often hitches the sim is not known; turning both settings off stops the copies. The course itself is the leg bearing to the nearest 5°. The German pilot recordings have `cn_01`–`cn_20` and no zero (VERIFIED, `actor51\varCallsign`, 2026-10-26), so a course is not assembled from those clips and the phrase voice is not asked to say it. The number stays on the chat. His callsign and formation number still play.

### 7. Mission scripting exposing state
The mission editor's MCU logic (check zones, timers, triggers) runs inside the game. It has no file, network or console output except the mission log itself. A custom mission could fire log events (e.g. `MCU_CheckZone` → objective events, AType 8) as a crude proximity sensor. Career missions are generated, though, so this would mean rewriting `_gen.Mission` before every sortie. That is fragile and not recommended. (SPECULATIVE)

### 8. Extending in-game radio commands
The in-game radio menu (Tab) is hard-coded per aircraft and nation. It cannot be extended without modifying game data. Commands will come from external hotkeys and joystick buttons instead. (DOCUMENTED / community consensus)

### 9. Independent audio
Yes. SRS and Allied Radio both play their own audio alongside IL-2 (SRS uses NAudio with radio filters: [source](https://github.com/ciribob/IL2-SimpleRadioStandalone/tree/master/IL2-SR-Client/Audio)). (COMMUNITY)

### 10. How existing tools integrate
| Project | Data used | Live AI positions? |
|---|---|---|
| [IL-2 SRS](https://github.com/ciribob/IL2-SimpleRadioStandalone) | telemetry events 10–14 (server address, client id, coalition); multiplayer | No |
| [IL-2 Integrator](https://github.com/rafaelvcaetano/il2integrator) | Career `cp.db` (planned route), PWCG files, HTTP/UDP for map apps | No (planned only) |
| [IL-2 Allied Radio](https://forum.il2-series.com/topic/5325-il-2-allied-radio/) | `_gen.Mission` date and nationality + player telemetry | No |
| [IL2Shaker](https://github.com/sicsix/IL2Shaker) | motion + telemetry UDP | No (player only) |
| [il2_stats](https://github.com/vaal-/il2_stats) | text mission logs (server) | Event positions only |

No known tool gets live AI positions in single-player. This project would be the first, and only if ACMI streams.

## Technology recommendation: C# / .NET 10

The integration surface is UDP datagrams, binary and text files that grow while being written, SQLite, Windows audio and DirectInput/XInput input. None of it needs in-process code, Lua or C++.
- **Binary parsing and file tailing**: `Span<T>` and `BinaryPrimitives` are ideal and allocation-light. The `.mlg` decoder is ~150 lines.
- **Audio**: NAudio (as SRS uses) for clip playback and radio filters. `System.Speech` / WinRT SAPI for optional TTS.
- **Input**: global hotkeys via Win32 `RegisterHotKey`. Joystick via DirectInput (SharpDX/Vortice) or `Windows.Gaming.Input`, read-only, so it never steals bindings from IL-2.
- **Deployment**: single-file self-contained `.exe`, no runtime install.
- **Testability**: xUnit with recorded fixtures.

Python would also work for parsing but is weaker for global input, audio and packaging. C++ brings no benefit since there is nothing to inject into. .NET is chosen on these merits, not just familiarity.

## Architecture (as implemented in the PoC)

```
startup.cfg outputs ─┬─ UDP motion/telemetry ──► TelemetryDecoder ─┐
                     ├─ FlightLogs\*.mlg ──────► BinaryMissionLogTail ├─► ContactTracker ─► FriendlyPositionsCommand ─► (text now; audio later)
                     ├─ logs\text\*.txt ───────► TextMissionLog      │     every fix keeps Source + age + frame
                     └─ Tracks\*.acmi ─────────► AcmiStream ──────────┘
_gen.Mission ─────────► MissionPlan (PLANNED only: names, flight, coalition table)
```
Safeguards built into the core:
- `PositionFix.Source`: `LogEvent` snapshots are never treated as live. Only `AcmiTrack` fixes younger than `MaxFixAgeSeconds` produce position replies.
- `CoordinateFrame`: IL-2 world and ACMI positions are never mixed until a calibration is proven.
- Destroyed, bailed-out and despawned aircraft never answer. Pilot kills are told apart from gunner kills.
- A source that stops updating ages all of its fixes, using the wall clock.

## Next test (needs you, ~10 minutes)
1. Close IL-2, then run `Il2Radio.Diag check --apply` (backs up `startup.cfg` and enables telemetry, text log and Tacview).
2. Run `Il2Radio.Diag live`, then start a Career mission. Make sure a track is recording (LCtrl+R if not automatic).
3. Fly 3–5 minutes near your flight, press **Numpad 1** a few times, then **Numpad .** to quit (Num Lock on).
4. Send back `diag-output\evidence-*.md`. Also run `Il2Radio.Diag acmi` on the resulting `.acmi`.

The evidence file answers: does the `.mlg` grow during flight, do text logs rotate live, does ACMI grow live, and how many AI aircraft received continuous updates.

## Roadmap (evidence-driven)

> Historical: this was the plan at the feasibility stage, and items 1–6 have since been done. The current roadmap is [ROADMAP.md](ROADMAP.md).

1. **Run the live test above.** Decide the position source.
2. **If ACMI streams:** confirm frame and units, identify the player object, and measure the object radius and update rate. Calibrate ACMI against IL-2 coordinates with take-off events, so planned waypoints and log snapshots can be shown alongside. Then build command 1 for real, plus commands 3 and 5 (formation position, rejoin vector).
3. **If ACMI is close-only or zip-only:** reduce scope. Offer status-based reports from logs ("Number Two airborne", "Number Four going down", "Number Five landed") and planned-route guidance clearly phrased as plan ("briefed route: next waypoint bearing...", never "I see"). Dead reckoning from player telemetry is possible but has unbounded drift. It would only be used with explicit uncertainty wording.
4. **Detection model** for commands 2 and 4 (enemy contacts, identification). Visual range, aspect and cloud give probabilistic detection and identification. Identification confidence grows with time and proximity ("single-engine fighter" → "looks like a Messerschmitt"). Only applies to objects that actually exist in the live source.
5. **Input**: global hotkeys plus joystick button mapping (read-only).
6. **Audio**: NAudio playback of clip sequences with a band-pass/noise radio effect and an independent volume. Optional SAPI TTS fallback.
7. **Coexistence**: UDP relay so SRS, shakers and this tool can share telemetry.
8. Other GB modules: no module-specific code is expected. Re-verify on Bodenplatte and Normandy maps.

## Radio text in IL-2's chat / message area (researched 2026-10-25)

> Can an external app put the radio request and the replies into IL-2's own chat or message log during single-player Career?

**Answer: no supported path exists. The tool draws its own chat-styled overlay instead, and that overlay only shows if IL-2 runs windowed.**

| Path | Result | Evidence |
|---|---|---|
| UDP telemetry / motion output | Output only. `startup.cfg` sets a *destination* `addr:port`, and every decoder and the official `IL2_MotionOutput.pdf` describe outgoing packets only. No inbound channel. | COMMUNITY ([IL2Shaker](https://github.com/sicsix/IL2Shaker/tree/master/IL2TelemetryRelay), [forum](https://forum.il2sturmovik.com/topic/72958-key-telemetrydevice-what-are-the-packet_id-outputs)) |
| Remote console `chatmsg roomtype id message` | The command exists, but the shipped `bin\console\readme.txt` says the server side is "coded into the dserver.exe binary" and is enabled through DServer's `startup.cfg` (`rcon_start`). The strings `rcon_start` and `chatmsg` occur in `bin\game\DServer.exe` and **not** in `Il-2.exe`. Career runs offline in `Il-2.exe`, so there is nothing to connect to. | VERIFIED (shipped readme + string search of both binaries); [forum thread on the packet format](https://forum.il2sturmovik.com/topic/61076-calls-to-the-remote-console-rconclient-from-code/) |
| `serverinput` (an external trigger for mission logic) | DServer remote console only, same as above. | VERIFIED (same readme) |
| `MCU_TR_Subtitle` in the Career mission | Career already uses them: this machine's `_gen.Mission` has 5 (e.g. `PLAYER_DESTROYED`, `LCText = 247` → `_gen.eng` "Your aircraft is critically damaged…"), with `SubtitleInfo { Duration, FontSize, HAlign, VAlign, R/G/BColor }`. **But** the text is a fixed localisation entry, it can only fire from mission logic, single-player has no external trigger, and the replies depend on live geometry, so they cannot be written in advance. It would also mean rewriting the generated mission (probably `_gen.msnbin`, which the game likely loads; SPECULATIVE) between generation and take-off. `&` and `<` also break subtitles. Rejected. | VERIFIED (local `_gen.Mission` / `_gen.eng`); COMMUNITY ([subtitle quirks](https://forum.il2sturmovik.com/topic/53595-the-mystery-of-the-disappearing-subtitle-translator)) |
| `MCU_Icon` | Map icons and lines, not messages. | VERIFIED (531 in `_gen.Mission`) |
| Memory writes, DLL injection, D3D11 `Present` hooks (how Steam/Discord/RTSS draw in-game) | Excluded by project rule: process injection, and a possible anti-cheat concern in multiplayer builds. | (rule) |
| Community tools | None found that put text into single-player chat. SRS's overlay is a separate window, and its guide says it "does not appear in Full-Screen mode or in VR". | COMMUNITY ([SRS guide](https://forum.il2-series.com/topic/73-installation-and-usage-of-srs-for-il-2-voice-comms-app-by-jason-williams/), [SRS overlay in VR](https://forum.il2sturmovik.com/topic/76980-srs-overlay-in-vr/)) |

### Alternative implemented: chat-styled overlay (`live --chat …`, `chat-test`)
`src/Il2Radio.Windows/ChatOverlay.cs` is a separate per-pixel-alpha layered window. It is click-through (`WS_EX_TRANSPARENT`), never takes focus (`WS_EX_NOACTIVATE`, shown without activation), stays topmost (and re-asserts that every 2 s), and is hidden from the taskbar and Alt-Tab. It shows `Speaker: text` lines newest at the bottom, each on a translucent dark box like IL-2's own "Game is Paused" line, fading after 14 s. `Il2Radio.Core/Radio/RadioChat.cs` builds the lines: first the player's call (e.g. "Gert Knoll: All friendlies, report positions."), then each `RadioReply.Text` exactly as spoken, under the replying pilot's name. Nothing is written to the game or its files.

**Fullscreen test on this machine (2026-10-25: IL-2 running a paused Career mission, `graphics.fullscreen = 1`, Il-2.exe compatibility flags only `HIGHDPIAWARE`, so fullscreen optimizations are not disabled):**
- IL-2's window is a `WS_POPUP` covering 1920×1080. While IL-2 was **not** the foreground window, the overlay showed over the game in a screen capture. (VERIFIED)
- Once IL-2 had focus, it set `WS_EX_TOPMOST` on itself. The overlay stayed above it in z-order (`EnumWindows`) but was **absent** from three captures over 9 s, including with `CAPTUREBLT`. The overlay's rendered bitmap was checked separately and was correct. So when focused, `fullscreen = 1` bypasses desktop composition here and hides other windows. (VERIFIED by capture; please confirm by eye)
- The overlay did not take focus from IL-2 or minimise it. (VERIFIED: IL-2 stayed foreground and not minimised)

**Consequence:** to see the overlay in flight, IL-2 has to run **windowed** (`fullscreen = 0`, `win_width = 1920`, `win_height = 1080`). Untested trade-offs:
- Whether IL-2's windowed mode at desktop size has a title bar or overhangs the screen is unknown. (SPECULATIVE; [a forum thread](https://forum.il2sturmovik.com/topic/76162-game-goes-over-the-borders-of-my-screen-when-open) reports overhang with Windows scaling above 100%)
- Composed (windowed) presentation is usually close to fullscreen performance on Windows 10/11 ([DirectX blog](https://devblogs.microsoft.com/directx/demystifying-full-screen-optimizations)). One VR user reports a 10–20 fps loss in windowed mode. (DOCUMENTED / COMMUNITY)
- In VR the overlay is never visible in the headset.
- **Second-monitor option:** `--chat window --chat-screen 1` shows an ordinary dark window on another display and works with any IL-2 mode. Untested here because this machine has one display.

**Known approximations:** the request line appears when the reply is computed, after the ~1.2 s snapshot rather than at the key press. With audio on, each reply now appears when `RadioVoice.Speaking` reports that its transmission started. If that event never arrives, the reply still appears after its estimate plus 10 s. With `--no-audio`, replies are spaced by an airtime estimate (0.9 s + 14 characters/s). The colours were replaced on 2026-10-25 by IL-2's own message style, measured from an in-game "Game is Paused" screenshot: one black box per line at 42 % opacity and single-colour #E0E0DC Arial text (`Il2ChatStyle`).

**User check by eye (2026-10-25):** "the text shows until I put IL-2 in focus, then it disappears." This confirms the capture result. The user wants to stay in fullscreen, hence the RTSS output below.

### RTSS on-screen display output (`--chat rtss`, implemented 2026-10-25)
RivaTuner Statistics Server (RTSS) already draws on-screen text inside games' own frames, including exclusive fullscreen. It also accepts text from other programs through a documented named shared-memory block, `RTSSSharedMemoryV2`. MSI Afterburner, CapFrameX and many small tools use it, and RTSS's own description invites "display any custom text in On-Screen Display directly from your own application" ([Guru3D RTSS page](https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/)). RTSS hooks the game to render; this project only writes text into RTSS's memory and never opens or touches the IL-2 process.

**Layout and protocol** (DOCUMENTED: RTSS SDK `RTSSSharedMemory.h` and the SDK sample's `UpdateOSD`/`ReleaseOSD`, as carried in [CapFrameX's copy of the header](https://github.com/CXWorld/CapFrameX/blob/master/source/CapFrameX.RTSSInterface/Include/RTSSSharedMemory.h) and [its client](https://github.com/CXWorld/CapFrameX/blob/master/source/CapFrameX.RTSSInterface/RTSSCoreControl.cpp), which `static_assert`s the entry offsets; the same pattern appears in [SysLat](https://github.com/Skewjo/SysLat_Software/blob/master/RTSSClient.cpp) and [YetAnotherKeyDisplayer](https://github.com/Jagailo/YetAnotherKeyDisplayer/blob/master/RTSSCustomOSD/OSD.cpp)):

| Field | Offset | Use |
|---|---|---|
| `dwSignature` | 0 | `'RTSS'` (0x52545353) when valid; `0xDEAD` while shutting down |
| `dwVersion` | 4 | `0x0002xxxx` required |
| `dwOSDEntrySize` / `dwOSDArrOffset` / `dwOSDArrSize` | 20 / 24 / 28 | OSD slot table (stride, offset, count) |
| `dwOSDFrame` | 32 | incremented after each change so RTSS redraws |
| `dwBusy` | 36 | v2.14+: set bit 0 atomically while writing; if it was already set, skip this update |
| slot `szOSD[256]` / `szOSDOwner[256]` / `szOSDEx[4096]` | 0 / 256 / 512 in each entry | text (≤ v2.6), owner name, extended text (v2.7+) |

The client scans slots from **1**, leaving slot 0 to the primary client (Afterburner). It first looks for a slot it already owns, otherwise claims the first slot with an empty owner. It writes zero-terminated ANSI text, increments `dwOSDFrame`, and on exit zeroes its whole entry and increments the frame again. Format tags need v2.11+. The SDK sample documents `<C=RRGGBB>` … `<C>` for colour and `<P=x,y>` for absolute position, and warns that `<P>` can overlap other clients' text. Size tags appear only as variables (`<S1=75>`, `<S1>`) in the sample, so they are not used.

**Background bar** (DOCUMENTED: Unwinder, [Guru3D, 2020-10-16](https://forums.guru3d.com/threads/drawing-a-black-square-and-a-white-square-taking-priority.434699/)): `<C=AARRGGBB>` sets colour with alpha, and six digits (no alpha) means opaque. `<B=width,height>` draws a solid bar in the current colour. A zero width and height means the whole layer, which in the overlay editor is that layer's text but in this shared-memory slot is a large screen region. `\b` returns the cursor so that text is drawn on the bar. No spaces are allowed inside a tag, and an alpha of 00 is not usable. Positive sizes are zoomed pixels; negative sizes are character cells. The same tags are what the shared-memory sample's hypertext goes through.

**VERIFIED in IL-2 (2026-10-26):** `<B=0,0>` drew one tall translucent rectangle down the left of the canopy. The line "Gert Knoll: All friendlies," sat at the top right, outside that rectangle. Zero size is not the text extent on this path.

**Implementation:**
- `Il2Radio.Core/Osd/RtssOsd.cs` holds the slot logic and the text formatting, working over a span of the whole mapping. It is unit-tested against a fake memory block (`RtssOsdTests`): claiming, reuse, other owners, no free slot, invalid or v1 headers, an out-of-range table, the busy lock, the legacy 256-byte field, release, colour tags, the background bar, wrapping, tag sanitising and the size limit.
- Each visible row is prefixed with a bar at IL-2's message-box black (`Il2ChatStyle.BoxAlpha`, 107, about 42 %), scaled by that row's fade, sized `<B=-columns,-1>`: one character-row tall and as many character cells wide as the row. A fully faded row omits the bar. `<B=0,0>` is not used. Glyph colours stay six-digit, so a parser that ignores the new tags still draws the words as before.
- `Il2Radio.Windows/RtssChat.cs` opens the named mapping on each update, like the SDK sample, so RTSS can start or restart at any time. It rewrites on every change and every 2 s, releases its slot when nothing is visible and on exit, and reports `RTSS: not running`, `no free OSD slot (owners…)` or `connected (vX.Y)`.
- Fading uses the shared `ChatTimeline`. Glyphs are darkened over the last 2 s (six-digit colours have no alpha). Each row's bar alpha follows that row.

**Verified here (2026-10-25)** without RTSS installed:
- `chat-test --chat rtss` reports "not running".
- A stand-in process then created a v2.21-shaped `RTSSSharedMemoryV2` mapping. The real code path claimed slot 1, wrote the coloured, wrapped text, left `dwBusy = 0`, bumped `dwOSDFrame` on each change, and cleared the slot on exit.

**Not verified (needs RTSS and IL-2):**
- RTSS rendering in IL-2's exclusive fullscreen. That is RTSS's normal use and widely reported for IL-2 frame counters (COMMUNITY), but it is untested here.
- Whether RTSS honours the space indentation of wrapped lines (it depends on the OSD font).
- Whether the colours read well over the cockpit.
- Whether `<B=-columns,-1>` sits on the glyphs. The alpha byte did take effect on `<B=0,0>` (the misplaced rectangle was translucent), so the open question is the character-cell size and the cursor return, not whether a bar can be drawn.

**User test (2026-10-25):** "It looks and sounds great." The text sat at the very top-left with no padding, which is RTSS's default profile OSD position, because no position tag was sent.

### RTSS text position and press feedback (2026-10-25)
**How RTSS positions third-party text:**
- **Global profile position:** each application profile has an OSD position (`dwOSDX/dwOSDY` in the app entry). The header documents "coordinate wrapping is allowed, i.e. -5 defines 5 pixel offset from the right side". Unwinder confirms on the [Guru3D forum](https://forums.guru3d.com/threads/osd-not-displaying-right-positioning.326359/) that negative values offset from the bottom/right and that coordinates are in zoom units. It moves every slot, including the frame counter, and is set by the user in RTSS. (DOCUMENTED)
- **Per-slot hypertext tag `<P=x,y>`:** text after it is drawn at absolute coordinates from the top-left, "in zoomed pixel units". This is the SDK sample comment in [CapFrameX's copy](https://github.com/CXWorld/CapFrameX/blob/master/source/CapFrameX.RTSSInterface/RTSSCoreControl.cpp), which warns about overlapping other clients; usage guides such as [EliteSpeedrunTool's](https://github.com/GTA5Oracle/EliteSpeedrunTool/blob/master/doc/RTSS/RTSS-en.md) and [acr_telemetry's](https://github.com/decnet100/acr_telemetry/blob/main/docs/RTSS_OVERLAY.md) say the same. (DOCUMENTED / COMMUNITY) I found no source that the tag accepts negative wrap-around values, so they are not used.

**Chosen:** `<P=x,y>` in our slot only, so RTSS settings and the frame counter are untouched.
- Presets are converted to zoomed pixels from the game resolution (`startup.cfg`) and the OSD zoom, read from IL-2's RTSS app entry (`dwOSDPixel`, "OSD pixel zooming ratio", at offset 324 of `RTSS_SHARED_MEMORY_APP_ENTRY`).
- Default `left-middle` (margin 32 px, top at 42 % height): the left canopy side, clear of the gunsight, the instrument panel, the top-left counter and IL-2's bottom-left message line (~73 %). It is anchored at its top, so it needs no text-size estimate. Right and bottom presets need the OSD character size, which is estimated (9×16 zoomed pixels) and can be calibrated.
- **VERIFIED** against the real RTSS on this machine (shared memory v2.21, IL-2 hooked): the app entry for `Il-2.exe` was found with zoom 2, and the `<P>` position was computed for 2560×1440. Not yet verified by eye in IL-2. The inference that the `<P>` zoom equals `dwOSDPixel` is based on both being described as the OSD zoom; if positions look scaled, pass `--rtss-zoom`.

**Press feedback:** `LiveSession.Keyed` fires as soon as a numpad key is accepted, before the ~1.2 s snapshot. It shows the player's call line at once (the RTSS output updates immediately; the overlay ticks every 50 ms) and plays `RadioVoice.PlayKeyClick()`: a ~3 ms click plus ~200 ms of carrier through `RadioEffect`, at the radio volume. Because `PlaySound` has a single channel, the click cuts off any playing transmission and drops queued ones, like keying over the channel. When the request then can't refresh positions (no track, no admin rights, snapshot error), the chat says why ("no reply: position snapshot failed"). A player-side recorded request clip was not added; a click is enough for now.

## Recorded radio voices (researched 2026-10-25)

Goal: replace or augment Windows TTS (only Microsoft David/Mark/Zira, all English, are installed here, VERIFIED via the `Speech` and `Speech_OneCore` registry tokens) with voices that sound like IL-2's own radio chatter, in the speaker's language.

### a. IL-2's own radio recordings
**Where they are (VERIFIED, BoS install, 2026-10-25).** The radio speech is **not** inside the `.gtp` archives. It is 8,878 loose Ogg Vorbis files (44.1 kHz mono) under `data\Audio\radio\<nation>\<actor>\`, next to the FMOD banks `MasterBank.bank` / `MasterBank.strings.bank`:

| Nation folder | Actors | Notes |
|---|---|---|
| `ger` | 51–54 (226 clips each), 61, 71, 81–82, 91–93 | German |
| `rus` | 01–05 (226 each), 11–12, 21–22, 31–32, 41–45, `fem\actor01`, `fem\actor31` | Russian, with female voices |
| `usa` | 101–105 (229 each), 111–113, 121–122, 131–132 | English |
| `gbr` | 151–155 (229 each), 161–162, 171–172, 181–182 | English |

Every actor folder has `const\` (whole phrases) and `var*\` (fragments the game fills in). Actors 51–54, 01–05, 101–105 and 151–155 share one layout, file for file (the English sets add `out`/`over`). They hold the air-to-air vocabulary:
- `varCallsign\cn_01…cn_20` (numbers), `acn_*` (a longer variant), `cs_bird_*`, `cs_tree_*`, `cs_storm_*` (callsign words)
- `varDir\01clock…12clock`, `to_n|ne|e|se|s|sw|w|nw`, `toHigh`, `toLow`, `toLeft`, `toRight`, `toBack`, `toCourse`
- `varRange\1_km…10_km`, `close`, `long`
- `const\grp`, `pair`, `fighters`, `bombers`, `roger`, `copy`, `ord_rejoin`, `rep_*` (reports), `req_*` (requests) and so on.

The roles of the other actors are inferred from their file names (SPECULATIVE): 61/11/12 are ground control (`clrtakeoff`, `clrtolnd`, `varWind`), and 91–93/41–45 are tank crews (`varShell`, `varTarget`, `varRange\50_m…5000_m`).

**What the fragments say.** I couldn't listen to them, so meanings come from two sources:
1. File names (SPECULATIVE on their own).
2. Windows speech recognition (en-US, constrained grammar) on `usa\actor101`. Results: `cn_01` "…one…" (confidence 0.82), `cn_02` "…two…" (0.74), `to_e` "…east" (0.80), `toHigh` "high" (0.92), `toLow` "below/low" (0.81), `long` "long range" (0.73), `close` "close" (0.90). Clock clips weren't matched; dictation heard "…o'clock". (PARTIALLY VERIFIED for English)

The German and Russian sets are the same script, file for file, so the same meanings are assumed (SPECULATIVE until heard). `Il2Radio.Diag voice-check --country 201 --play` plays every mapped clip with its key so this can be confirmed by ear.

**The recordings are already radio-processed (VERIFIED by measurement).** There's no silence anywhere: a hiss floor of 5–10 % RMS runs through the whole file. RMS is ~0.25 at a 0.89 peak, so they're heavily compressed. The mean frequency (first-difference energy ratio) is ~1.5 kHz, so they're band-limited. The game therefore can't be adding all of the radio sound at runtime. This project gives them only the squelch, a gentle drive and the range-dependent hiss.

**What they can't say.** There are no altitude numbers in metres for pilots (only tank crews have 50–5000 m, with different speakers), no three-digit bearings or headings, no "steer", no "you are with us" / "in formation", and no ranges beyond 10 km except "long range". Clip replies are therefore coarser than the English text: 18 km → "long range", bearing 110 → "east", altitude → only "high/low" relative to you. Those parts stay on the chat (before 2026-10-26 a reply the clips could not express went to Windows TTS).

**Can they be played from the user's install?** Terms checked: the IL-2 series [user agreement](https://il2-series.com/user-agreement/) (il2sturmovik.com/user-agreement redirects there). No separate Great Battles EULA text could be found online. Relevant clauses:
- 5.1: the user "has the right to use Objects".
- 7.1: the operator holds the rights to "multimedia content" among the Objects.
- 5.3: forbids "debugging, modifying, disassembling, decompiling", bypassing DRM, and cheats or bots "to gain unauthorized advantages".
- Schedule 3: forbids mods "that unfairly alter game functionality".

Reading a loose `.ogg` that the game ships unpacked, and playing it on the same PC, involves none of the forbidden actions. Nothing is unpacked, decompiled or modified, and no gameplay advantage is gained. That is my reading, not legal advice. **Redistributing the clips is clearly not allowed** (7.1). So:
- the repo ships only manifests (`voices/il2-*.json`) that list file names relative to `{il2}`;
- clips are read in place at runtime and never copied;
- the game still works with Mods Off, and nothing in `data\` is written.

Sources: [user agreement](https://il2-series.com/user-agreement/); [Cliffs of Dover Steam EULA](https://store.steampowered.com/eula/eula_63950), a different product but the same 1C licence style ("private use"); the install's `data\Licenses.txt`, which lists libogg/libvorbis, consistent with the Vorbis files found.

### b. Phrase concatenation from clips (implemented)
- Replies now carry language-neutral **`SpeechToken`s** next to the unchanged English text: `Speaker(1) Group(5) Clock(3) Range(18000) Vertical(2400)`. See [SpeechTokens.cs](../src/Il2Radio.Core/Radio/SpeechTokens.cs).
- **`ClipPhraser`** maps tokens to clip keys (`callsign.1 group clock.3 range.far vertical.above`). It tries the most precise clip first, then coarser ones. Tokens that carry the message (direction, range, "with us") are required; decorations (number, group size, above/below, altitude, heading) are skipped when a pack lacks them.
- A **`VoicePack`** is a JSON manifest mapping keys to WAV/OGG files ([voices/README.md](../voices/README.md)). It can have several speakers (`{speaker}`), and its root can be `{il2}` (the user's own install) or a folder of the user's own recordings. Its `countries` field selects it by the speaker's nationality.
- **`ClipVoice`** decodes clips (WAV built in; OGG via [NVorbis](https://github.com/NVorbis/NVorbis), MIT, ~70 KB managed, the only new dependency). It resamples them with a windowed-sinc filter to 16 kHz, levels each clip to the same RMS and joins them with 25 ms crossfades. `RadioEffect` is applied on top.

### c. Better local TTS (researched, not implemented)
| Option | Languages | Licence | Cost / effort | Verdict |
|---|---|---|---|---|
| Windows OneCore voices (`Windows.Media.SpeechSynthesis`) | de-DE, ru-RU voices can be added through Windows language/speech packs (not re-checked here) | Part of Windows | Needs a WinRT target framework; only English voices installed here | Same robotic character as SAPI; no gain in realism |
| Windows "natural" voices (Narrator) | many | Windows | Not exposed to the public SAPI/WinRT synthesis API; a community adapter exists (COMMUNITY, unverified) | Unsupported path |
| [Piper](https://github.com/rhasspy/piper) | de_DE (e.g. *thorsten*, dataset [CC0](https://huggingface.co/rhasspy/piper-voices/blob/main/de/de_DE/thorsten/high/MODEL_CARD)), ru_RU (e.g. *dmitri*, [CC0](https://huggingface.co/rhasspy/piper-voices/blob/main/ru/ru_RU/dmitri/medium/MODEL_CARD)) | Engine MIT; the repo was archived 2025-10-06 and development moved to [OHF-Voice/piper1-gpl](https://github.com/OHF-Voice/piper1-gpl) (GPL, by name); voices licensed per model card | ~20–110 MB download per voice plus the piper runtime with espeak-ng; runs as a separate process | Best TTS option, but a German voice reading English text gives German-accented English. Native German would need German phrasing, which the tokens now make possible. **Built as an optional phrase voice for the clips' gaps**: see "German phrase voice (Piper)" |

### Recommendation and status
**Use the game's own recordings, through the clip-concatenation engine.** Until 2026-10-26 Windows TTS was the fallback for a reply the clips could not say, and Piper filled single words. Both were removed that day ([REQUIREMENTS-recorded-voice.md](REQUIREMENTS-recorded-voice.md)): a second voice in a wingman's sentence breaks the cockpit however well its spectrum is matched, because it is a different man. The chat carries what the clips cannot say, and a few game phrases stand in for whole messages (below).

### Placeholder clips (checked 2026-10-26)
Windows speech recognition (en-US, a constrained grammar of about 60 candidate phrases, distractors included) on `usa\actor101`–`105`, whose `const` files match the German and Russian sets file for file:

| File | Heard as (best confidence) | Mapped as | Stands in for |
|---|---|---|---|
| `roger` | "roger" (0.90 on actors 102 and 103; 0.70 on 104) | `ack` | "no contacts", "with us", "in formation" |
| `copy` | "copy" (0.70, actor 104) | not mapped | |
| `trnleft`, `trnright` | "turning left" / "turning right" (0.61–0.74, actors 102–104) | `turn.left`, `turn.right` | an AI leader turning onto a new course, before the compass point |
| `rep_trgdestr` | "target destroyed" (0.61, actor 105) | `destroyed` | an enemy a mate saw go down |
| `spt`, `obj_spotted` | "spotted" (0.85), "object spotted" (0.66–0.71) | `spotted` | a newly gained contact, after the callsign (unasked calls) |
| `atk` | "engaging" (0.82, actor 104); "engage" (0.89, actor 104, 2026-10-27) | `engaging` | an AI leader breaking off his course for an enemy the flight has seen |
| `ord_followme` | "follow me" (0.78–0.93, actors 104–105, 2026-10-27) | `followme` | the end of the break-off call |
| `ord_rejoin` | "rejoin formation" (0.61–0.90, actors 102–105, 2026-10-27) | `rejoin` | a mate telling the player, who is ahead of the flight, to slow down and rejoin |
| `trnclimb` | "climbing" (0.76, actor 104) | not mapped | |
| `toLeft`, `toRight`, `toBack` | "to the left", "to the right", "behind" (0.67–0.74) | `beside.left`, `beside.right`, `beside.behind` | where a mate in formation sits off the player's nose, in place of "Roger" (30–120° either side; beyond 120° behind; ahead keeps "Roger") |
| `outsector` | "leaving sector" (0.68–0.75) | not mapped | |
| `rep_underattack`, `rep_imhit`, `toCourse`, `im`, `flw` | no match | not mapped | |

PARTIALLY VERIFIED for English. The German and Russian files are ASSUMED to say the same (same file names and script); not heard. `voice-check --country 201 --play` plays `ack`, `turn.left`, `turn.right`, `destroyed`, `spotted`, the three `beside` keys, `engaging`, `followme` and `rejoin` with the rest.

"A few kilometres" was given no placeholder: the nearest kilometre clip would claim a measured range the pilot model deliberately does not have.
- It is the only option that sounds like IL-2's chatter, because it *is* IL-2's chatter (the same actors and processing).
- It matches the speaker's nation (201 German, 101 Soviet, 102 British, 103 American).
- It needs no download and adds one small dependency.
- The same engine plays user-made or freely licensed recordings.

Status (VERIFIED on this machine):
- `say` renders the sample replies in German (`--country 201`, the default) and Russian (`--country 101`), at the same loudness as TTS.
- `voice-check` resolves all 56 keys for every pilot actor of all four nations.
- `live` passes each reply's tokens and the speaker's country (from the mission log, else the planned player's country).

Not yet verified: how it sounds in a real flight, and whether the German and Russian fragments mean what their file names suggest.

**Follow-ups:**
- Listen with `voice-check --play` and correct `voices/il2-ger.json` if a key is wrong.
- ~~Use Career callsigns instead of a bare number~~: done, see below.
- ~~Add German/Russian phrasing for TTS fallback (Piper) for what the clips can't say.~~ German built (optional download), see "German phrase voice (Piper)" below; Russian not yet.
- `RadioVoice.Speaking` now reports each transmission's real start and duration, which the chat overlay could use instead of its airtime estimate.

### Career callsigns (implemented 2026-10-25)
**Data (VERIFIED on this machine's Career mission and log, 2026-10-25):**
- Every `Plane` block in `_gen.Mission` has `Callsign` and `Callnum`. The player's flight is `Callsign = 8`, `Callnum` 1–6, and the player is 4.
- The mission log's AType 10 formation numbers (`FORM`) for the same six pilots are also 1–6. Callnum and formation number agree here; when both exist, the reply uses Callnum.
- Career writes the player's flight-mates' names in `_gen.Mission` with a **0x01 prefix** (`"\u0001Heinz Koelzig"`, the same marker as in the ACMI). Names are now cleaned of control characters, otherwise they never match the log.
- Other flights in the plan have their own callsigns (e.g. 12, 13, 15, 17, 19).

**Callsign words (VERIFIED file contents):** `data\GUI\DefaultCallsigns.<lang>` is a plain-text table in the install:
- `[ObjectType=1]` Planes: 1–20 are birds, for countries 101 and 201: Stork, Gannet, Raven, Rook, Thrush, Crane, Finch, Oriole, Canary, Swan, Kittiwake, Eagle, Pelican, Swift, Redshank, Duck, Pheasant, Seagull, Brambling, Hawk. 46–66 are British (Acorn, Charlie, …) and 67–85 American (Blue Bird, Plastic, …).
- `[ObjectType=2]` Airfields: 21–40 are trees (Acacia, Beech, …).
- `[ObjectType=3]` Spotters: 41–45 are storms (Storm, Typhoon, Hurricane, Cyclone, Volcano).
- The Russian table (CP1251) lists the same IDs in Russian alphabetical order: Аист, Баклан, Ворон, Грач, Дрозд, Журавль, Зяблик, **Иволга** (8), Кайра, Лебедь, Марабу, Орёл, Пеликан, Стриж, Травник, Утка, Фазан, Чайка, Юрок, Ястреб. So the numbering is the original Russian list, and the English names are translations (Кайра = guillemot appears as "Canary", Марабу as "Kittiwake").
- The `.ger` file contains the English words, untranslated.

The display text uses the English table ("Oriole one, flight of five…"), unless the speaker's voice pack names its callsign word (see "Clips" below: the German pack does, so a German flight shows "Pirol one, flight of five…"). The repo carries no copy of the table; it is read from the install by `MissionPlan.Load`.

**Clips (INFERRED, structurally strong):**
- The pilot actors' `varCallsign` folders hold `cs_bird_01…20`, `cs_tree_21…40` and `cs_storm_41…45`. These are exactly the table's families and ID ranges, so `callsign.N` → `cs_<family>_NN` in `voices/il2-ger.json` and `il2-rus.json`.
- All 45 clips exist for every German (51–54) and Soviet (01–05) pilot actor.
- German clip `cs_bird_08` presumably says "Pirol" and the Russian one "Иволга"; not heard.
- **The German recordings say German words for the Russian list, not the English table's words (VERIFIED for callsign 9, by ear, 2026-10-25).** In a flight as callsign 9 the user heard the German voice say something like "Luma" while the chat showed the English table's "Canary". The Russian word for 9 is Кайра (guillemot), German *Lumme*; "Kanarienvogel" would be twice as long (the clip is 0.74 s with its hiss). Previously the text always used the English table, so chat and voice named different birds whenever the German pack spoke.
- Fix: `voices/il2-ger.json` now has a `words` map giving the German word for each of the 45 callsign clips, and the text uses the speaker's voice's word (`ContactTracker.CallsignWords` ← `VoiceSelection.CallsignWord`). Only 9 is heard; the other 44 are INFERRED translations of the Russian words (e.g. 2 Kormoran for Баклан, where English says "Gannet"; 11 Marabu, English "Kittiwake"). `voice-check --country 201` prints each word next to its clip. The Russian pack has no `words` yet, so a Soviet speaker's text still uses the English word.
- Speech recognition on the *English* actors' `cs_bird_*` didn't match the bird list (mostly no match; `cs_bird_20` came back as "Pelican", not "Hawk"). Since US/British callsigns use IDs 46+, those clips likely hold different words, so the US/GB packs map numbers only.
- The `acs_*` / `acn_*` variants (about twice as long) are unused; their meaning is unknown.
- Spoken order: `callsign.8 number.2 …` ("Oriole two"). `voice-check --country 201 --play` plays the 45 words for checking by ear.

**Fallbacks:** no plan match, or no word in the table → "Number N" in the player's flight, otherwise the pilot's name (both as before). No callsign clip → the number clip alone.

**Scope note:** pilots of other flights seen only in the ACMI also get their callsign when the plan has their name ("Eagle one"), because that's how they'd answer on the radio.

## German phrase voice (Piper, researched and built 2026-10-25; removed 2026-10-26)

**Not used.** The code was removed on 2026-10-26 in favour of game recordings only (see "Recommendation and status" above). The research below is kept as evidence. A `piper` folder an earlier version downloaded is left alone; the uninstaller still offers to delete it.

Goal: say the few things the German game recordings can't (aircraft type names, "no contacts", the side, "possible", "with us") in German, without replacing the game's voices and without downloading anything unless the player agrees.

**Nothing below was downloaded or run on this machine.** Re-read on 2026-10-25 from the GitHub release API (asset size, `"digest": null`), the Hugging Face tree API at tag `v1.0.0` (file size, LFS SHA-256, git blob id), the model-card text files, `src/cpp/main.cpp` at tag `2023.11.14-2`, and the Windows job in `.github/workflows/main.yml`. No zip, model or `.onnx.json` body was downloaded.

| Fact | Evidence | Source |
|---|---|---|
| Last native Piper release with a Windows build: `2023.11.14-2` (also what `/releases/latest` returned on 2026-10-25), asset `piper_windows_amd64.zip`, **22,477,236 bytes** | DOCUMENTED (GitHub API asset `size`; the file body was not fetched) | [release](https://github.com/rhasspy/piper/releases/tag/2023.11.14-2), `api.github.com/repos/rhasspy/piper/releases/tags/2023.11.14-2` |
| GitHub publishes **no checksum** for it (`"digest": null`) | DOCUMENTED | same API |
| Engine licence MIT (Michael Hansen); repository archived 2025-10-06, development moved to [OHF-Voice/piper1-gpl](https://github.com/OHF-Voice/piper1-gpl) (GPL, Python package, no Windows executable) | DOCUMENTED | [LICENSE.md at the tag](https://github.com/rhasspy/piper/blob/2023.11.14-2/LICENSE.md), repository banner |
| CLI: text lines on stdin; `--model`, `--config`, `--output_file`, `--output_dir`, `--output_raw`, `--length_scale`, `--noise_scale`, `--noise_w`, `--sentence_silence`, `--speaker`, `--json-input`, `--espeak_data`, `-q/--quiet` | DOCUMENTED (read from source) | [src/cpp/main.cpp at the tag](https://github.com/rhasspy/piper/blob/2023.11.14-2/src/cpp/main.cpp), README |
| Default mode (`--output_dir`, default "."): **each stdin line → a new `<timestamp>.wav` in the folder, and its path printed on stdout** (`cout << path << endl`, which flushes). `--output_raw` streams 16-bit mono PCM at the model's rate to stdout and, on Windows, sets **stdin** to binary too (`setmode` on both), so a line of text per phrase would no longer be a line. The radio uses `--output_dir` for that reason. With `--output_file`, all of stdin is read first (one file per run). Logging goes to stderr (`spdlog`); `-q` turns it off before the "output directory" line | DOCUMENTED (source) | main.cpp |
| `espeak-ng-data` is looked for **beside the executable** (`GetModuleFileNameW` on Windows) unless `--espeak_data` is given | DOCUMENTED (source) | main.cpp |
| Zip layout: top-level `piper\` folder. The Windows job configures `-DCMAKE_INSTALL_PREFIX=_install/piper`, installs, then `Compress-Archive -LiteralPath piper`. CMake installs the `piper` executable into that prefix and copies `share/espeak-ng-data` in as `espeak-ng-data` beside it, plus the phonemize `*.dll` files. Not extracted here; the installer still searches for `piper.exe` with `espeak-ng-data` beside it | DOCUMENTED (build script, not the zip bytes) | [main.yml](https://github.com/rhasspy/piper/blob/2023.11.14-2/.github/workflows/main.yml) `build_windows`, [CMakeLists.txt](https://github.com/rhasspy/piper/blob/2023.11.14-2/CMakeLists.txt) `install()` |
| Voices: `rhasspy/piper-voices`, tag `v1.0.0`, `resolve/v1.0.0/de/de_DE/<speaker>/<quality>/de_DE-<speaker>-<quality>.onnx` and `.onnx.json`. The tree API's `lfs.oid` / `lfs.size` are the model's SHA-256 and byte size; a non-LFS file's `oid` / `size` are the git blob id and byte size of the `.onnx.json`. Re-read 2026-10-25; the pinned values match. No HEAD of the file bodies in this pass | DOCUMENTED | `huggingface.co/api/models/rhasspy/piper-voices/tree/v1.0.0/de/de_DE/<speaker>/<quality>` |

German voices on the hub (`de_DE`), from the tree at `v1.0.0`: eva_k (x_low), karlsson (low), kerstin (low), mls (medium, 236 speakers), pavoque (low), ramona (low), thorsten (low, medium, high), thorsten_emotional (medium, 8 emotion speakers). The model cards do not state gender; eva_k, kerstin and ramona are the names that read as women, so they are not the default. Pinned in `PiperCatalog` (thorsten, a man's name, because the game's German pilots are men, plus karlsson as a second male low voice):

| Voice | Model | SHA-256 | Rate | Dataset licence (model card) |
|---|---|---|---|---|
| **de_DE-thorsten-medium** (default) | 63,201,294 B | `7e64762d…bb7819` | 22.05 kHz | CC0 ([Thorsten-Voice](https://github.com/thorstenMueller/Thorsten-Voice)) |
| de_DE-thorsten-high | 113,895,201 B | `9df1c43c…ea4f1` | 22.05 kHz | CC0 |
| de_DE-thorsten-low | 63,104,526 B | `9ac27fad…2cf375` | 16 kHz | CC0 |
| de_DE-karlsson-low | 63,104,526 B | `f9793bf7…64e8fe` | 16 kHz | M-AILABS ("see URL") |

Not pinned: pavoque (card links [CC BY-NC-SA 4.0](http://creativecommons.org/licenses/by-nc-sa/4.0/), which is not a licence to ship or to treat as free), thorsten_emotional (CC0, 8 emotion speakers; nothing here needs them), mls (CC-BY 4.0, 236 speakers), eva_k and ramona (M-AILABS, card says "see URL"). kerstin is CC0 but is one of the names above. Sample rates in the table are the cards' "Samplerate" lines (thorsten medium/high 22,050 Hz, thorsten low and karlsson 16,000 Hz). The thorsten **high** card's "Quality" line says "medium"; the folder name and the 113,895,201-byte file are what is pinned. **Licence caveat (the cards, plus COMMUNITY discussion of the base voice):** thorsten medium and high say "finetuned from U.S. English lessac"; thorsten low and karlsson say "finetuned from U.S. English Ryan". The lessac dataset's own terms are more restrictive than CC0, which the Piper community has discussed. For private use on the player's PC this doesn't matter; it would if the model were redistributed, which this project never does (the player downloads it from Hugging Face).

**Design (implemented):**
- Piper speaks **only the gaps**, in place, between the game actor's recorded clips ("group · *Jaks* · four o'clock · 8 km · high"), not whole replies. Alternative considered: Piper speaking the whole reply in German whenever the clips can't. One consistent voice, but it would replace the game's own actors most of the time, which is the most authentic part. Trade-off: a change of voice mid-sentence.
- **The fill-in is coloured to the measured clip band (VERIFIED by measurement, 2026-10-25; the ear check in a flight is still open).** Twelve short clips from German actors 51–54, resampled to 16 kHz and level-matched: mean frequency about 1.4–1.7 kHz, essentially no energy below 300 Hz, about 80% of the energy in 0.8–2.2 kHz, crest about 9–12 dB, and a 20 ms noise floor around a third to a half of the RMS. The same phrases from `de_DE-thorsten-medium` ("Jaks", "vermutlich", "nichts gesichtet", "Feind", "Iljuschins") centre near 110 Hz with about 85% of the energy below 300 Hz. A 300–3000 Hz pass leaves that bass in place, which is the jarring part. `RadioEffectSettings.LikeGameRecordings` is therefore a 700 Hz–2.6 kHz pass, drive 6 and a hiss level of 0.04, applied after resampling to 16 kHz; run through the same transmission effect as the clips, that lands on the clips' band shape and hiss ratio. Piper is also started with `--noise_scale 0.4` and `--noise_w 0.5` (model defaults 0.667 and 0.8) so the word is steadier. The speaker is still Thorsten.
- Only words that carry meaning the clips can't express at all. Numbers stay with the recordings, coarsely ("long range"), to keep Piper's share small.
- German words: "nichts gesichtet", "du bist bei uns", "du bist in Formation", "vermutlich", "Feind"/"eigene", and German spellings of the type names ("Jak", "Lawotschkin", "Iljuschin", "Me hundertzehn"). Pilot mode adds "ein paar Kilometer", "einige", "viele", "bei mir" and "ich sehe dich nicht". Plain German, not reconstructed Luftwaffe brevity code (UNVERIFIED which terms were used).
- Consent: off by default (`piper.enabled` false: no question, no download). When turned on and missing, the start-up lists each file, its source URL, its size and the total, and downloads only on an explicit yes (the player window's Yes button, or a typed `y` in Diag). Anything else, or nobody who can be asked, does not download. Files are streamed to `downloads\*.part`, refused beyond the pinned size or if the URL is not HTTPS (a redirect off HTTPS is refused too), checked for size and SHA-256 (model) or git blob SHA-1 (config), then moved in. The engine zip is checked by size and contents, and its SHA-256 is printed so it can be recorded. Never stored inside the IL-2 folder.
- Runtime: one `piper.exe` child process for the session, one stdin line per phrase, WAV path back on stdout; phrases cached by text; model loaded in the background at start. Any failure means the radio speaks as before (class word, or TTS for "no contacts").
- Honesty: Piper says only what the reply's tokens say, and the tokens mirror the English text (type only when identified, side only when the markings were seen).

## Cockpit clock (verified 2026-10-25)

The radio log used to stamp each line with the PC's clock. The cockpit watch runs on the mission clock instead, and IL-2 already writes that clock down.

Checked against this machine's Career files:

| Source | What it is | Evidence |
|---|---|---|
| `_gen.Mission` `Time` / `Date` | Clock at mission start | `14:11:57`, `26.8.1942`, the sortie that was running while this was checked |
| Log AType 0 `GTime` / `GDate` | The same instant, at tick 0 | Text chunk `missionReport(2026-10-25_22-42-19)[0].txt`: `GDate:1942.8.26 GTime:14:11:57`. Ticks are 50 per simulation second |
| ACMI `ReferenceTime` | That clock at the first recorded frame | `1942-08-26T14:12:55Z` on the 20:38 track. The `Z` is not a zone: `RecordingTime` is local wall time with the same suffix (`2026-10-25T20:40:21Z` equals the file's local creation time) |
| ACMI `#t` | Simulation seconds since `ReferenceTime`, **not** wall seconds | The 20:06 track's file spans 874 s and its last `#t` is 658 s. `ReferenceTime` 17:20:08 minus log `GTime` 17:18:59 is 69 s; the wall gap from the log name `20:07:37` to the file's creation `20:08:43` is 66 s |
| End of the 15:58 sortie | The two clocks meet | Log `GTime` 13:38:50 plus max tick 30120 (602.4 s) is 13:48:52. Segment `_01`'s `ReferenceTime` 13:46:06 plus last `#t` 168.84 is 13:48:55. Within 3 s |

So `ReferenceTime + #t` is the watch, and it stops while the sim is paused. The log's newest tick is the same clock but only as fresh as the last event in the latest ~33 s chunk. The radio log uses the track once a frame has been read, and the log until then. Between reads, file growth (visible even while IL-2 locks the ACMI) says whether the sim is still running; three seconds without growth is treated as a pause. Visibility's time of day is still mission start plus recording time, not this clock.

## Enemy contacts and identification (built 2026-10-25)

The ACMI stream gives every aircraft on the map. Reporting all of it would be radar, so commands 2 and 4 pass it through a visibility model ([Visibility.cs](../src/Il2Radio.Core/Radio/Visibility.cs), [EnemyCommands.cs](../src/Il2Radio.Core/Radio/EnemyCommands.cs)). Everything below is a **game-design model (SPECULATIVE as physiology)**, tuned to feel plausible, not measured.

- **Observers:** the player's own flight, with live fixes and within radio range. Flying alone, any friendly in radio range observes instead. The player is not an observer; he has his own eyes.
- **Spotting:** each observer gets one look per 15 s of track time. As the recording is read, every closed frame is looked at, so a pass between radio calls is not thrown away. The chance used for that slot is the best look in it: a ten-second close pass counts the same as a long one.
  - Probability is `1 / (1 + (r / r50)^4)`, with `r50` = 400 m per metre of wingspan (fighter ≈ 4 km, Ju 88 ≈ 8 km). Nothing is spotted beyond 2.2 × `r50`.
  - Ranges shrink with haze (`Options.Haze`), darkness (crude clock-based curve; no sun position yet) and a target below the observer against the ground (× 0.75).
  - Looks that cross the mission's cloud layer (`CloudLevel` to `CloudLevel + CloudHeight`) get through only a `1 − coverage` share. Coverage comes from the `CloudConfig` preset number: 00 clear to 04 overcast. That naming is community knowledge and UNVERIFIED for every preset.
  - The dice are deterministic per observer, target and 15 s slot, so repeating a call quickly gives the same answer.
- **Identification ranges** (per metre of wingspan): class 400 m, side 300 m, type 180 m. That gives a fighter about 4 km for its shape, 3 km for its markings and 1.8 km for its type. Near the edge of type range a call may be hedged ("possible Yaks"), and with a 12 % chance names a classic look-alike (Yak ↔ Bf 109, Fw 190 ↔ La-5, Il-2 ↔ Hs 129). A closer look corrects it.
- **Memory:** a spotted aircraft is still mentioned for 60 s after it leaves sight (changed 2026-10-25, after a Career sortie in which a pass between calls came back as "no contacts"). The reply gives the place of that look ("last seen"), not the track position since. Its identification level only rises while someone can still see it, so calls don't flicker and a lost look does not grow sharper.
- **Honesty:** only aircraft present in the live track can be reported, and destroyed or despawned ones never are. Friendlies count as "unidentified" until their markings are made out, and are left out of enemy reports once identified. With no live player position, there is no reply.
- **Replay check (VERIFIED, Career recording 2026-10-25 15:58, ACMI only):** "four Yaks, at your four o'clock, approximately 8 kilometres, high" was reported by a friendly fighter dogfighting them at 350–700 m, with the player 7–9 km away. That sentence is `realism: precise`. The default `pilot` does not put those Yaks in the player's clock: the spotter was the one in the fight, kilometres from the player, so he says they are on him.
- **Pilot realism (built 2026-10-25, SPECULATIVE as radio procedure).** Same visibility model for "can this pilot see the player", including the 15 s dice. What he is then allowed to say is a design choice, not a transcript of Luftwaffe R/T: close under 2 km, a few kilometres up to 8 km, otherwise far; own altitude to 500 m; heading as an eight-point compass point. No landmark calls, because the mission data has no verified place names. A flight mate inside 1 km (1.5 km before 2026-10-26) reports as in formation even when the look fails. `precise` keeps the track's bearings and kilometre ranges.
- **Map square (built 2026-10-26, SPECULATIVE as radio procedure).** A pilot who cannot see the player names the IL-2 map square he is actually in. The player's own flight does so only while it is still within 10 km of its waypoint chain; the chain is how that flight has been navigating. Another friendly flight names the square whenever it can hear the call. **Why the chain is not required of them (VERIFIED, Career `_gen.Mission` and track `career-11-Gert Knoll-1942.2026-10-26_07-58-50_00`, 2026-10-26):** Adler (callsign 12, Werntgen and Schenk) was briefed with `MCU_CMD_AttackArea` `FRIENDLY_F-AT_AREA` (15 km radius) and a single `FRIENDLY_F-RTB_WAYPOINT` at the airfield. They orbited 1–2 km from the area centre, 71–75 km from that waypoint, inside square 0823 for the whole flight, and so the route gate left the square out of every "I cannot see you". Rabe, on a real waypoint chain, did say 1020. The game's 10 km grid stands in for a plan-square. The ninth is the keypad cell (1 southwest, 9 northeast) and is said only when the fix is at least 500 m from a line inside the square; within 1 km of a 10 km line the square is left out. **Labels (VERIFIED, Stalingrad map, this install, 2026-10-26):** southwest 2301-1, northwest 0101-7, northeast 0136-9, southeast 2336-3, so the first pair counts south from 01 and the second counts east from 01. Karpovka 1022-5, Kalach 1019-2, Basargino 1123-9 and Verbovka 1219-2 match their mission-file positions. **ACMI lat/lon to those metres (VERIFIED on four airfields the same day):** a linear fit (origin 47.459346589° N, 41.113008203° E, 107254.37 m/° north, 73567.07 m/° east) stays inside 360 m and in the same cell. Tusov is under 1 km from a line, so he does not say the square. Other maps are not labeled. The game clips have no grid word; the German phrase voice says "Planquadrat" and the digits, with "zwo" for 2.
- **Found during the replay (VERIFIED):** a closed recording ends with one frame removing every object (152 removals at the last timestamp). The ACMI reader now applies removals only when the next frame starts, and the tracker revives aircraft that reappear in the next recording segment.
- **Not modelled yet:** sun glare and bounces out of the sun, the observer's head and cockpit blind spots, AGL or terrain masking, and combat awareness from log events (an aircraft shooting at you is obviously spotted).
