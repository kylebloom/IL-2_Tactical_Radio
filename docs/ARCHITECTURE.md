# Architecture

How the IL-2 tactical radio works, for anyone (human or agent) changing it. Read this before editing; [FEASIBILITY.md](FEASIBILITY.md) holds the research and evidence behind each decision, and [ROADMAP.md](ROADMAP.md) what's left.

## The one rule

**Never claim what the integration can't establish.** Every position carries its source, its age and its coordinate frame. Replies are built only from fresh live tracks. Planned waypoints and old event snapshots are never presented as current. Destroyed or despawned aircraft never answer. Enemy aircraft are only reported once the visibility model says someone could have seen them. Tests enforce this; keep it that way.

## Projects

| Project | What it is |
|---|---|
| `src/Il2Radio.Core` (net10.0) | All game-format decoding, the contact model, radio commands, phrasing, audio DSP and the RTSS/screen-layout logic. Platform-neutral and pure, so it is unit-tested without IL-2. |
| `src/Il2Radio.Windows` (net10.0-windows) | Windows-only I/O (UDP, file tailing, Volume Shadow Copy, keys, audio playback, TTS, overlay window, RTSS shared memory) and `LiveSession`, which wires it together. Internals are visible to the two programs below. |
| `src/Il2Radio.App` (net10.0-windows) | The player's program, `Il2Radio.exe`: settings file, start-up checks, then `LiveSession` in its compact view. Published as one self-contained file. |
| `src/Il2Radio.Diag` (net10.0-windows) | The developer console: the full `live` monitor, offline replays and checks, all driven by command-line flags. |
| `tests/Il2Radio.Core.Tests` | xUnit tests for Core, including a real Career `.mlg` fixture. |
| `voices/` | Voice-pack manifests. File names only; they point into the user's IL-2 install. See `voices/README.md`. |

## Data flow during a flight (`Il2Radio.exe` or `Il2Radio.Diag live`)

```
IL-2 writes ──────────────────────────────┐      LiveSession polls every ~1 s
 data\missionReport(<start>)[N].mlg chunks ──► BinaryMissionLogTail ─┐
 data\Missions\_gen.Mission (Career plan) ───► MissionPlan ──────────┤
 data\Tracks\*.acmi (Tacview, LOCKED) ──────► AcmiStream ───────────┼─► ContactTracker
 UDP 29373 motion/telemetry ────────────────► TelemetryDecoder (display only)
                                                                      │
Numpad key (GetAsyncKeyState) ─► Keyed event: request line + mic click (immediately)
                               ─► VSS snapshot of the ACMI tail (~1.2 s, admin) ─► AcmiStream ─► ContactTracker
                               ─► RadioCommands.Execute(command, tracker, settings, SightingMemory)
                               ─► RadioResult (replies: English Text + language-neutral SpeechTokens)
                               ─► Transmitted event ─► RadioVoice (clips or TTS → RadioEffect → PlaySound)
                                                     ─► chat sink (RTSS OSD / overlay window), synced via RadioVoice.Speaking
```

### Game data sources (all read-only; the game process is never touched)
- **Mission log** (`Core/MissionLog`). IL-2's "AType" event log.
  - With `mission_text_log = 1`, IL-2 writes rotating chunks `missionReport(<start>)[N].mlg` plus `.txt` directly in `data\`, about every 33 s, and deletes the previous mission's chunks.
  - The complete `.mlg` lands in `data\FlightLogs` at mission end.
  - `BinaryMissionLog` decodes the binary format: `tick:u32 atype:u8 len:u16 payload 0x0A`, strings `len:u32 + bytes`, positions as three f32. Ticks run at 50/s.
  - It gives identities (AType 12, with a `BotPilot_*` child confirming an aircraft), the player's flight with formation numbers (AType 10), and status events: take-off, landing, damage, kill, bail-out, despawn.
  - **Positions only at events; no periodic positions in single-player.**
- **Mission plan** (`Core/MissionFile`). Career writes the sortie as text to `data\Missions\_gen.Mission`.
  - `MissionFileParser` is a generic block parser.
  - `MissionPlan` gives the player (`AILevel = 0`), each plane's `Callsign`/`Callnum`, country to coalition, and `MissionWeather` (cloud base and depth, coverage from the preset name, haze, start time).
  - Callsign words come from the install's `data\GUI\DefaultCallsigns.*` (`CallsignTable`).
  - Career prefixes flight-mates' names with a 0x01 byte, here and in the log and the ACMI. Strip control characters when matching names.
- **Tacview ACMI** (`Core/Acmi/AcmiStream`). The only source of continuous positions: every aircraft and vehicle at about 5 Hz, with absolute lat/lon.
  - Written only while a track is recording (`tacviewrecord = 1` + LCtrl+R). One mission can produce several files (`_00`, `_01`).
  - IL-2 holds the file **without read sharing**. The session reads only the new bytes through an on-demand Volume Shadow Copy (`Windows/ShadowCopy.cs`, WMI `Win32_ShadowCopy`, needs admin, about 1 s). Every snapshot is recorded in a ledger file, and leftover snapshots are cleaned up on start.
  - IL-2 quirks the parser handles:
    - unescaped commas in `Name=Type - Pilot,roster,n`;
    - the 0x01 name prefix;
    - the player named by account nickname (= `0,Author=`);
    - yaw = true heading;
    - **a closed recording removes every object in its final frame**, so removals are applied only when the next frame starts.
- **UDP telemetry** (`Core/Telemetry`). Player-only attitude and state, with **no world position**. Shown in the monitor; nothing depends on it.

### Contact model (`Core/Contacts/ContactTracker`)
- One `Contact` per aircraft, merged across sources:
  - log id, keyed `log:<id>`;
  - ACMI id, matched by pilot name, or `acmi:<id>`;
  - the player's ACMI object folded into the logged player contact.
- `PositionFix` = position + `PositionSource` (`LogEvent` snapshot or `AcmiTrack`) + source time + heading.
- `AgeSeconds` = how far the source clock moved since the fix, plus wall time since the source last updated. A source that stops updating ages all of its fixes.
- `CoordinateFrame`: IL-2 world (log) and ACMI positions are never compared (`Navigation.Relative` returns null across frames).
- Status (`Airborne`, `Landed`, `Destroyed`, `BailedOut`, `Despawned`) comes from log events and ACMI removal. A track-caused despawn is undone if the aircraft reappears in the next segment.
- `MissionStarted` (AType 0) resets everything.

### Radio commands (`Core/Radio`)
- `RadioCommands.Execute` dispatches `RadioCommand` → `RadioResult(Outcome, Replies, Suppressed)`. `Suppressed` lists every contact deliberately left out and why; the monitor and tests use it.
- `RadioPicture.Build` (shared gate): the player must have a live ACMI fix (else `PlayerPositionUnknown`). Candidates need a live fix younger than `MaxFixAgeSeconds` (10), must be operational, and must be within `RadioRangeMetres` (40 km).
- `FriendlyPositionsCommand` (Num1) and `FlightCommands.FlightPosition`/`Rejoin` (Num2/3) group aircraft with single-linkage (2 km, 600 m). The lowest formation number speaks.
- `EnemyCommands` (Num4/5) and `Visibility`:
  - **Observers** are the player's flight, or, flying alone, any friendly in radio range.
  - **Spotting:** each observer gets one deterministic look per 15 s slot. Detection probability scales with wingspan and drops with range, haze, darkness, an against-ground background and cloud between observer and target.
  - **Identification levels:** `Sighted`, `Class`, `Side`, `Type`, by range per metre of wingspan.
  - **Uncertainty:** hedged look-alike calls near the type limit.
  - **Memory:** `SightingMemory` (one per session) keeps contacts tracked for 60 s, and their identification only improves.
  - **Vocabulary:** `AircraftTypes` maps IL-2 model names to radio words and wingspans.
  - All tunables live in `RadioSettings` / `VisibilitySettings`; the model is a game-design approximation, documented in FEASIBILITY.md.
- **Phrasing:** `RadioPhrases` builds the English text ("Oriole one", spoken digits, clock code, metres). Each reply also carries `SpeechToken`s (`SpeechTokens.cs`), the same meaning in language-neutral form, for recorded voices.
- **Chat:** `RadioChat` turns an exchange into `ChatLine`s: the player's call (`RequestPhrase`), the replies, or a system line explaining a silence.

### Audio
- `Core/Audio/RadioEffect`: pure DSP on 16-bit PCM:
  - squelch burst and tail with a click;
  - 300–3000 Hz Butterworth band-pass;
  - soft-clip drive;
  - in-band hiss that grows with `RangeFraction`;
  - a tanh limiter, so it never clips.
- `Core/Audio/ClipPhraser`: tokens → clip keys, most precise first, with fallbacks (`range.18` → `range.far`).
  - Tokens that carry the message (direction, range, "no contacts") are **required**: if a pack can't say one, the whole reply goes to TTS.
  - Decorations are optional and skipped.
  - The type name (`contact.type`) is optional; the class word stands in, because the game has no type-name clips.
- `VoicePack` (JSON manifest; clips read in place via `{il2}`), `ClipAudio` (WAV/Ogg decode via NVorbis, resample, loudness match, crossfade), `ClipVoice` (render).
- `Windows/VoiceSelection` picks a pack per speaker nation (`voice` setting, `--voice auto|tts|<pack>`). User packs win over the game's; the repository's `voices/*.json` manifests are embedded in `Il2Radio.Windows`, and a same-named manifest in a `voices` folder overrides the built-in one.
- `Windows/RadioVoice` runs a single background queue:
  - each item is rendered (clip voice, else System.Speech TTS), put through the effect, and played with winmm `PlaySound`;
  - a new request drops queued items;
  - `PlayKeyClick` interrupts for immediate feedback;
  - the `Speaking` event reports when each transmission really starts.
- `Windows/Il2AudioMeter` watches Il-2.exe's audio-session peak meter, so replies wait while IL-2's own radio is talking. It's a heuristic and unverified.

### On-screen chat
- `Core/Radio/ChatTimeline`, shared by every output:
  - when lines appear (synced to `Speaking`, with a timeout);
  - a 14 s hold, then a fade;
  - 8 lines on screen;
  - 200-line history paged with Num7/Num9;
  - the Num0 menu.
- `Windows/RtssChat` + `Core/Osd/RtssOsd`: **the fullscreen path.**
  - Writes into RivaTuner Statistics Server's documented shared memory (`RTSSSharedMemoryV2`): claims a slot named "IL-2 Tactical Radio", writes text with `<C=RRGGBB>` colours, releases the slot on exit.
  - RTSS draws it into the game's frames.
  - Position comes from a per-slot `<P=x,y>` tag (`RtssPlacement`, presets, margin, zoom read from RTSS).
- `Windows/ChatOverlay`: a click-through, layered, topmost window styled like IL-2's message line (`Il2ChatStyle`). **IL-2 in exclusive fullscreen hides it whenever the game has focus (verified)**, so it's for windowed mode or a second screen.
- `Core/Osd/Il2ScreenAreas`: estimated areas of IL-2's own message line and subtitles, which both outputs keep clear of.

## The app (`Il2Radio.App`)
- **Start-up:** a single-instance mutex; `settings.json` beside the exe (`Core/Settings/AppSettings`: every value defaulted, unknown names rejected, each bad value reported by its setting name; created with all defaults on first start). Everything is resolved from `AppContext.BaseDirectory`, because an elevated start from Explorer runs in System32.
- **Checks** (`StartupChecks`), each an `[ok]`/`[!!]` line kept on screen in the compact view:
  - administrator rights (the `app.manifest` asks for `requireAdministrator`);
  - the required `startup.cfg` outputs (`Core/Il2Outputs`: `mission_text_log`, `tacviewrecord`; telemetry is Diag-only). If IL-2 is closed it offers to set them, with a backup, only on an explicit `y`;
  - RTSS running when the chat output is `rtss`; the overlay with IL-2 in exclusive fullscreen.
  - Failed checks also go to the chat output through `LiveSession.AlertEachMission`: at start-up and again at each mission start.
- **Track-recording reminder:** `LiveSession` takes the running mission's start time from the log-chunk names (`missionReport(<local start>)[N]`). A mission counts as running while its chunks grew in the last 90 s. Recording means the newest ACMI created since the mission started fails a shared open, because IL-2 locks it exactly while writing it. `Core/TrackRecordingWatch` decides when to remind: once per stretch without recording, `recordingReminderSeconds` in. The reminder goes to the console and the chat output.
- **Compact view** (`LiveOptions.Compact`): mission, track and player state, start-up results, and the radio's own replies. It shows no contact table and no "silent: ... because" lines, which would name aircraft nobody reported. No UDP telemetry is opened (`LiveOptions.UdpPort = null`), so SRS or shaker tools keep the port.
- **Keys** (`Windows/KeyBindings`, names in `Core/Input/VirtualKeys`): bindings from the settings; the menu, legend and log hint are built from them.
- **Exit:** the quit key, or closing the window (SIGHUP/SIGTERM handlers cancel the session and wait up to 4 s, so the RTSS slot is released and `logs\evidence-*.md` is written).
- **Publish:** `dotnet publish src/Il2Radio.App` → `publish\Il2Radio.exe` (win-x64, self-contained, single file, compressed; publish-only properties under `_IsPublishing`).

## Diag commands
`check [--apply]` · `mission` · `mlg` · `acmi [file]` (offline replay of all commands; reads a locked file by snapshot when elevated) · `snapshot-test` · `live` · `chat-test` · `audio-meter` · `say` · `voice-check`. Run without arguments for the full flag list.

## Working on this repo
- **Build and test:** `dotnet test` at the root. If the user's `live` session or `Il2Radio.exe` is running it locks its `bin\Debug`; then run `dotnet test tests/Il2Radio.Core.Tests` and build the programs elsewhere with `dotnet build src/Il2Radio.Diag -o <scratch dir>` (or `src/Il2Radio.App`).
- **Admin:** live positions need an elevated terminal (VSS). Non-elevated runs work, but report the ACMI as locked.
- **Testing:** tests build scenarios from synthetic ACMI text (`TestSupport.Acmi.Object`) and log events (`MlgWriter`, or event records directly), with `ManualTimeProvider` for ages. Visibility tests use near-certain spotting settings, so identification ranges are deterministic. Avoid rounding boundaries in expected text: 45° bearings, x.5 km ranges.
- **Verified vs assumed:** say which in docs and comments ("VERIFIED", "UNVERIFIED", "inferred from file names"). A lot here is reverse-engineered from the user's own files.
- **Don't:**
  - touch the IL-2 process or its memory, or inject anything;
  - modify game files (only `startup.cfg`, with a backup, and only with the user's consent);
  - copy or commit IL-2's audio;
  - run `chat-test`, or a second `live` or `Il2Radio.exe`, while one of them is running (they share the RTSS slot "IL-2 Tactical Radio");
  - download models without asking.
- **Real data for replays** lives in the user's install: `data\Tracks\*.acmi`, `data\FlightLogs\*.mlg`, and the current mission's `data\missionReport(...)[N].*` chunks. IL-2 deletes the previous mission's chunks, so an older ACMI replays without flight membership.
