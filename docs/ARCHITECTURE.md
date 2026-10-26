# Architecture

How the IL-2 tactical radio works, for anyone (human or agent) changing it. Read this before editing; [FEASIBILITY.md](FEASIBILITY.md) holds the research and evidence behind each decision, and [ROADMAP.md](ROADMAP.md) what's left.

## The one rule

**Never claim what the integration can't establish.** Every position carries its source, its age and its coordinate frame. Replies are built only from fresh live tracks. Planned waypoints and old event snapshots are never presented as current. Destroyed or despawned aircraft never answer. Enemy aircraft are only reported once the visibility model says someone could have seen them. Tests enforce this; keep it that way.

## Projects

| Project | What it is |
|---|---|
| `src/Il2Radio.Core` (net10.0) | All game-format decoding, the contact model, radio commands, phrasing, audio DSP and the RTSS/screen-layout logic. Platform-neutral and pure, so it is unit-tested without IL-2. |
| `src/Il2Radio.Windows` (net10.0-windows) | Windows-only I/O (UDP, file tailing, Volume Shadow Copy, keys, audio playback, TTS, overlay window, RTSS shared memory) and `LiveSession`, which wires it together. Internals are visible to the two programs below. |
| `src/Il2Radio.App` (net10.0-windows) | The player's program, `Il2Radio.exe`: a window for flight status and settings, start-up checks, then `LiveSession` in its compact view. Published as one self-contained file. |
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
  - Callsign words come from the install's `data\GUI\DefaultCallsigns.*` (`CallsignTable`), in English. When the speaker's voice pack names what its callsign clip says (manifest `words`, e.g. German `callsign.9` = "Lumme"), that word replaces the English one in the text, so chat and voice name the same callsign: `ContactTracker.CallsignWords`, wired from `VoiceSelection.CallsignWord` by `LiveSession` (and the Diag `acmi` replay). The German recordings don't say the English table's words.
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
  - **Memory:** `SightingMemory` (one per session) keeps a spot for 60 s after the aircraft leaves sight. Each closed ACMI frame is looked at as the track is read, so a pass between radio calls is recorded. A later call says where it was last seen, and does not follow the live position once the look is lost. Identification only improves on a look that still sees it.
  - **Vocabulary:** `AircraftTypes` maps IL-2 model names to radio words and wingspans.
  - **Realism** (`RadioSettings.Realism`, `realism` in `settings.json`, default `pilot`): a speaker gives a position relative to the player only when he can see the player (the same visibility model and 15 s look) or, for the player's own flight, is in formation. Otherwise he reports his own eight-point heading and altitude to the nearest 500 m, and the map square of his live position when that fix is still within 10 km of his flight's waypoint chain (`PlanSquare`, `MapGrid`). The square is omitted within 1 km of a 10 km line, and the ninth (the keypad cell, 1 southwest through 9 northeast) within 500 m of a line inside the square. The chain is the briefed route, never the position he reports. Only the Stalingrad terrain is labeled; the latitude-to-metre fit is in FEASIBILITY.md. Any friendly inside radio range answers that way; he can hear the call. An enemy call uses the player's clock when the spotter is with him; a spotter in the fight who cannot see the player says "on me"; otherwise he uses his own clock. Counts are exact for a single aircraft, or for up to four once their shape is visible, and otherwise "a few" or "many". Ranges are close (under 2 km), a few kilometres (up to 8 km) or far. `precise` is the previous wording. The player's program and Diag default to `pilot`; `new RadioSettings()` stays `precise`, which is what the older command tests lock.
  - All tunables live in `RadioSettings` / `VisibilitySettings`; the model is a game-design approximation, documented in FEASIBILITY.md.
- **Phrasing:** `RadioPhrases` builds the English text ("Oriole one", spoken digits, clock code, metres). Each reply also carries `SpeechToken`s (`SpeechTokens.cs`), the same meaning in language-neutral form, for recorded voices. Tokens mirror the text exactly: `ContactType` carries the called type's family in `Name` only when the text names it, `ContactSide` only when the text says "enemy"/"friendly", `Hedge` when it says "possible"/"looks like".
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
  - `PhraseSegments` adds an optional phrase synthesizer's word list (`SynthWords`, German only). It fills, in place, only the tokens the recordings can't say at all: `NoContacts`, `WithUs`/`InFormation` (required), `ContactType` with a `Name`, `ContactSide`, `Hedge`, and in pilot mode `RangeBand` "a few kilometres", `ContactAmount` "a few"/"many", `OnMe`, `CannotSee`, `Grid` (optional). "Close" and "far" stay on the game's range clips. When the type will be spoken, the class word is dropped. Neighbouring synthesized words merge into one segment ("vermutlich Jaks", "Jak bei mir"). Numbers the clips say coarsely (18 km → "long range", altitude, three-digit headings) are never synthesized.
- **German phrase voice (Piper, optional):**
  - `Core/Audio/PhraseSynthesis.cs`: `IPhraseSynthesizer` (language, `Synthesize(text)` → clean `MonoAudio` or null) and the German words. `ClipVoice.Render(..., synth)` uses a synthesizer only when its language matches the pack's (`de`), trims its padding (`ClipAudio.TrimSilence`), resamples to the 16 kHz join rate and colours it to the measured game-clip band (`RadioEffectSettings.LikeGameRecordings`: 700–2600 Hz, drive, a hiss floor about a third of the RMS, no squelch or click), then joins it with the clips. The colouring is done at 16 kHz because the hiss level depends on the sample rate. If the synthesizer fails, the reply is re-phrased from the clips alone (class word back), or goes to TTS when a required part is missing.
  - `Core/Audio/PiperCatalog`: pinned downloads (URL, size, SHA-256 of the model from Hugging Face's LFS id, git blob SHA-1 of the config; the engine zip has no published hash, so size and contents). `PiperInstaller`: what's missing, the consent text (each file's URL and size), and the install (`<folder>\downloads\*.part` → size/hash check → move; the zip is extracted to `runtime.new`, checked for `piper.exe` with `espeak-ng-data` beside it, then swapped in). The 2023.11.14-2 Windows zip is a `piper\` folder (the release workflow's cmake prefix), so the executable lands at `runtime\piper\piper.exe`. `PiperSetup.PrepareAsync` is the start-up decision. `piper.enabled` is three-state: null (not answered, the default), true, false. False → nothing, no question. Installed → ready (an unanswered setting is then remembered as true). Missing, with null or true → ask Yes / No / Later (`PiperAnswer`; a null asker downloads nothing). Only Yes reaches the injected `open` function. `PiperSetupResult.Remember` carries what to save: Yes → true (also when the download then fails, so the next start offers it again), No → false, Later → nothing. Never inside the IL-2 folder. `FriendlyError` turns a failure into two plain sentences; the technical cause goes to the log. `PiperInstaller.Remove` deletes only what the installer made (`runtime`, `voices`, `downloads`), then the folder if empty. `AppSettings.WithPiperEnabled` writes the answer into the settings text, keeping every other value. Non-HTTPS URLs are refused, including a redirect from HTTPS to HTTP.
  - `Windows/PiperSynthesizer`: one long-lived `piper.exe --model --config --output_dir <temp> --length_scale --noise_scale 0.4 --noise_w 0.5 --quiet`. The noise scales (`PhraseDelivery`) are flatter than thorsten-medium's defaults (0.667 and 0.8), so a one-word insert wanders less. Each stdin line becomes a WAV whose path piper prints on stdout. Phrases are cached by text, the model is loaded in the background at start (`WarmUp`), and after three failures in a row it gives up for the session. `Windows/PiperConsole`: the HTTPS-only streamed `HttpClient` download, plus the question. Diag asks on the console (`piper-setup`, `--piper DIR` for `say`/`live`; `[Y]es / [N]o / [L]ater`, where Enter or a closed console is Later). `Il2Radio.exe` passes the window's three-button dialog (`PlayerDialogs.YesNoLater`; closing it is Later) and a progress window (`StartupChecks.PhraseVoice`). A null asker downloads nothing.
  - Settings: `piper.enabled` (default null = ask), `piper.voice`, `piper.folder` (default `piper` beside the exe), `piper.lengthScale`. It is neither asked about nor used with `voice: "tts"` or without German recordings in use.
- `VoicePack` (JSON manifest; clips read in place via `{il2}`), `ClipAudio` (WAV/Ogg decode via NVorbis, resample, loudness match, crossfade, silence trim), `ClipVoice` (render).
- `Windows/VoiceSelection` (plus its optional `Synth`, the phrase voice) picks a pack per speaker nation (`voice` setting, `--voice auto|tts|<pack>`). User packs win over the game's; the repository's `voices/*.json` manifests are embedded in `Il2Radio.Windows`, and a same-named manifest in a `voices` folder overrides the built-in one.
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
  - 200-line history paged with Num7 (older) / Num9 (newer). Paging counts pages back from the newest, not lines: the oldest page is clamped to the log's first lines, so Num9 from it lands on the next page, and only Num9 on the newest page returns to the live lines. Each past line is stamped with the mission clock (`MissionClock`), the time a cockpit watch shows: ACMI `ReferenceTime` plus `#t` once a frame has been read, and until then the log's `GTime` plus the newest tick. While a track is recording, its file size is polled (visible even though IL-2 locks the file); 3 s without growth pauses the clock with the sim. When recording stops, that pause ends and a newer log tick may move the clock forward, never backward. A new mission freezes the stamps already shown. Before any sample, the stamp is the wall clock;
  - the Num0 menu.
- `Windows/RtssChat` + `Core/Osd/RtssOsd`: **the fullscreen path.**
  - Writes into RivaTuner Statistics Server's documented shared memory (`RTSSSharedMemoryV2`): claims a slot named "IL-2 Tactical Radio", writes text with `<C=RRGGBB>` colours and, on each row, a `<B=-columns,-1>` bar (IL-2's 42 % black, one character-row tall and as wide as that row, fading with the row), releases the slot on exit. `<B=0,0>` is the whole OSD layer, not the text.
  - RTSS draws it into the game's frames.
  - Position comes from a per-slot `<P=x,y>` tag (`RtssPlacement`, presets, margin, zoom read from RTSS).
- `Windows/ChatOverlay`: a click-through, layered, topmost window styled like IL-2's message line (`Il2ChatStyle`). **IL-2 in exclusive fullscreen hides it whenever the game has focus (verified)**, so it's for windowed mode or a second screen.
- `Core/Osd/Il2ScreenAreas`: estimated areas of IL-2's own message line and subtitles, which both outputs keep clear of.

## The app (`Il2Radio.App`)
- **Window:** WinForms (`OutputType` WinExe). Status shows the compact flight view. Settings edits every value in `settings.json`. Diag stays a console. The window is not topmost, so it does not cover IL-2.
- **Start-up:** a single-instance mutex (a second copy shows a message and exits); `settings.json` beside the exe (`Core/Settings/AppSettings`: every value defaulted, unknown names rejected, each bad value reported by its setting name; created with all defaults on first start). A bad file or a missing IL-2 folder leaves Settings open instead of exiting. Everything is resolved from `AppContext.BaseDirectory`, because an elevated start from Explorer runs in System32.
- **Checks** (`StartupChecks`), each an `[ok]`/`[!!]` line kept on the Status tab:
  - administrator rights (the `app.manifest` asks for `requireAdministrator`);
  - the required `startup.cfg` outputs (`Core/Il2Outputs`: `mission_text_log`, `tacviewrecord`; telemetry is Diag-only). If IL-2 is closed it asks in a dialog, with a backup, only on Yes (Enter does not mean yes);
  - RTSS running when the chat output is `rtss`; the overlay with IL-2 in exclusive fullscreen.
  - The German phrase voice, while unanswered or enabled but not installed, asks once in a Yes / No / Later dialog with the short consent text, and shows download progress on Yes. `MainForm.RememberPiper` saves Yes or No into `settings.json` (`AppSettings.WithPiperEnabled`, so unsaved form edits and other values are untouched) and ticks or unticks the Settings checkbox; Later saves nothing. The Settings tab keeps an unanswered value unanswered when the box was never touched, so saving other settings is not a "no". "Remove the German voice" stops the session (Piper holds its files), calls `PiperInstaller.Remove`, saves false and restarts. After start-up, `StartupChecks` waits up to 10 s for Piper's warm-up phrase and warns in plain words if it failed (typically an antivirus blocking `piper.exe`).
  - Failed checks also go to the chat output through `LiveSession.AlertEachMission`: at start-up and again at each mission start.
- **Track-recording reminder:** `LiveSession` takes the running mission's start time from the log-chunk names (`missionReport(<local start>)[N]`). A mission counts as running while its chunks grew in the last 90 s. Recording means the newest ACMI created since the mission started fails a shared open, because IL-2 locks it exactly while writing it. `Core/TrackRecordingWatch` decides when to remind: once per stretch without recording, `recordingReminderSeconds` in. The reminder goes to the Status tab and the chat output.
- **Compact view** (`LiveOptions.Compact`, delivered to the window through `LiveOptions.Status` so the console is not redrawn): mission, track and player state, start-up results, and the radio's own replies. It shows no contact table and no "silent: ... because" lines, which would name aircraft nobody reported. No UDP telemetry is opened (`LiveOptions.UdpPort = null`), so SRS or shaker tools keep the port.
- **Applying settings:** the volume replaces `RadioVoice`'s effect settings and is used on the next transmission. Everything else is written to `settings.json` and applied by stopping the session and starting it again in the same process. Save and apply is that restart.
- **Keys** (`Windows/KeyBindings`, names in `Core/Input/VirtualKeys`): bindings from the settings; the menu, legend and log hint are built from them. The Settings tab captures a key into the box.
- **Exit:** the quit key, Quit, or closing the window cancels the session and waits up to 4 s, so the RTSS slot is released and `logs\evidence-*.md` is written. The session runs off the UI thread; status is marshalled back onto it.
- **Publish:** `dotnet publish src/Il2Radio.App` → `publish\Il2Radio.exe` (win-x64, self-contained, single file, compressed; publish-only properties under `_IsPublishing`).

## Diag commands
`check [--apply]` · `mission` · `mlg` · `acmi [file]` (offline replay of all commands, with how the German game pack would say each reply with and without the phrase voice; reads a locked file by snapshot when elevated) · `snapshot-test` · `live` · `chat-test` · `audio-meter` · `say` · `voice-check` · `piper-setup`. Run without arguments for the full flag list.

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
