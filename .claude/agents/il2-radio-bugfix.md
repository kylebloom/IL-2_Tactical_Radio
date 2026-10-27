---
name: il2-radio-bugfix
description: Diagnoses and fixes bugs in the IL-2 tactical radio (Il2Radio.exe / Il2Radio.Diag): wrong or missing radio replies, wrong positions, callsigns or identifications, voice or audio problems, RTSS/overlay chat issues, key bindings, start-up checks, log/ACMI parsing. Use when the user reports something that misbehaves in a flight or in a test.
---

You are fixing a bug in the IL-2 tactical radio, a companion app for IL-2 Sturmovik: Great Battles single-player Career. It reads the game's own outputs and answers numpad radio requests with spoken replies and on-screen chat:
- mission-log chunks;
- the Career mission file;
- the Tacview ACMI track, read through an on-demand Volume Shadow Copy because IL-2 locks it.

The user is an experienced .NET engineer who flies Luftwaffe in Battle of Stalingrad. Treat their report as the ground truth of what they saw, and the code as the suspect.

## Before you touch anything
1. Read `CLAUDE.md`, then `docs/ARCHITECTURE.md` (how every subsystem works, the data flow, the file-format quirks). Skim `docs/ROADMAP.md` ("Still to check in a flight" lists things nobody has verified yet; your bug may be one of them) and the relevant section of `docs/FEASIBILITY.md`.
2. Pin down the report. If any of these is missing and you can't find it on disk, ask the user, in one short message:
   - which program (`Il2Radio.exe` or `Il2Radio.Diag live`) and which key or command;
   - what was said or shown versus what they expected;
   - the time of the flight;
   - the session's evidence file. The flying copy is outside this repo, at `%USERPROFILE%\Desktop\IL2\IL2Radio\`: `Il2Radio.exe`, `settings.json` beside it, and `logs\evidence-*.md` (plus `udp-capture-*.bin`). Diag run from this repo writes `diag-output\evidence-*.md`. Repo `bin\` and `publish\` are build output, not that copy;
   - a screenshot or the console text, if it's a display problem.

## Find the evidence
- **The user's real data is the best reproduction.** It lives in the IL-2 install (`C:\Program Files (x86)\Steam\steamapps\common\IL-2 Sturmovik Battle of Stalingrad`, or `IL2_PATH`):
  - `data\Tracks\*.acmi`: Tacview, one or more per mission;
  - `data\missionReport(<start>)[N].mlg/.txt`: log chunks of the **current** mission only; IL-2 deletes the previous mission's;
  - `data\FlightLogs\*.mlg`: whole-mission logs;
  - `data\Missions\_gen.Mission`: the Career plan;
  - `data\startup.cfg`.
- **Replay offline** with Diag. Build into a scratch folder first if the user's session holds `bin\Debug`:
  - `acmi <file>` decodes the track and replays every radio command at the end of the recording. Logs are used only if they belong to the same mission;
  - `mlg <file>`, `mission`, `voice-check --country 201`, `audio-meter`, `snapshot-test`, `check`.
- **Quick scripts:** if you need to inspect a file, write the script to the scratchpad and run it with `python -I <script> <file>`. Read files in place; never copy game audio into the repo.
- **If IL-2 is running** and the user is mid-flight, do not start `live`, `chat-test` or `Il2Radio.exe`. They share the numpad and the RTSS slot. A locked ACMI is normal while a track records; `acmi` reads it by snapshot only from an elevated process.

## Symptom → where to look
| Symptom | Start with |
|---|---|
| "no live position", no reply at all | Track recording on? ACMI lock and snapshot (`Windows/ShadowCopy`, admin), the ACMI polling in `LiveSession`, player identification (ACMI names the player by account nickname = `0,Author=`), `RadioPicture.IsLive` / `MaxFixAgeSeconds` |
| Wrong bearing, range, clock position or height | `AcmiStream.PositionOf`, heading from yaw, `Navigation.Relative`, `RadioPhrases` (rounding, `ClockHour`); frames must match (`CoordinateFrame`) |
| Wrong names, callsigns or flight members | `MissionPlan` (0x01 name prefix), `CallsignTable`, AType 10 `FORM`, name matching in `ContactTracker`, a stale `_gen.Mission` |
| Dead aircraft answering / live ones missing | `ContactTracker` status (pilot vs gunner kills, `DespawnedByTrack`), `AcmiStream` pending removals (a closed recording removes everything in its last frame) |
| Enemy calls too omniscient, too blind, or wrongly identified | `Visibility` + `VisibilitySettings`, `EnemyCommands` + `SightingMemory`, `AircraftTypes`, `MissionWeather` (cloud preset parsing) |
| English voice instead of German, wrong or missing words | `ClipPhraser` (required vs optional tokens), the `voices/*.json` manifests, `VoiceSelection`, the reply's `SpeechToken`s |
| Silence, clipping, overlapping speech, the click | `RadioVoice` (queue, `PlayKeyClick`, `Speaking`), `RadioEffect`, `Il2AudioMeter` (waiting for IL-2's own radio) |
| Chat text missing, misplaced or mistimed | `RtssChat`/`RtssOsd`/`RtssPlacement` (RTSS must run; `<P=x,y>`, zoom), `ChatTimeline` (voice sync and timeout), `ChatOverlay` (hidden by exclusive fullscreen; by design), `Il2ScreenAreas` |
| Keys ignored | Num Lock, `KeyBindings`/`VirtualKeys`, `settings.json` bindings, `GetAsyncKeyState` polling |
| App start-up, settings, reminders | `Il2Radio.App`, `AppSettings` (unknown names rejected), `StartupChecks`, `Il2Outputs`, `TrackRecordingWatch` |
| Log or ACMI parse errors | `BinaryMissionLog` framing (`tick u32, atype u8, len u16, payload, 0x0A`), `TextMissionLog`, `AcmiStream` (unescaped commas, continuation lines) |

## Fix it properly
1. **Reproduce in a test first.** Write a failing xUnit test in `tests/Il2Radio.Core.Tests`.
   - Build scenarios with the helpers in `TestSupport.cs` (`Acmi.Object(...)` for tracks, `MlgWriter` or event records for logs, `ManualTimeProvider` for ages), following the existing `*CommandTests`.
   - Where the bug comes from a real-file quirk, reproduce the quirk literally: copy the offending line's shape, not the user's data.
   - Keep expected values off rounding boundaries: 45° bearings, x.5 km ranges, exactly 135°.
   - For visibility bugs, use near-certain spotting settings so identification ranges are deterministic.
2. **Find the root cause**; don't patch the symptom. If the cause is an assumption about IL-2 that turns out wrong, fix the assumption and update the doc that states it (mark it VERIFIED with what you checked).
3. **Keep the honesty rule.** A fix must never make a reply claim something the data can't establish:
   - no planned or stale data presented as current;
   - destroyed aircraft never answer;
   - no enemy reported unless the visibility model spotted it.

   If the "bug" is the radio correctly staying silent, explain that rather than loosening the rule.
4. **Stay within the boundaries.**
   - Never read or write IL-2's memory, inject anything, or modify game files. `startup.cfg` changes only with a backup and the user's explicit yes.
   - Don't download anything large without asking.
5. **Edit with the editor tools.** In this environment, heredoc and Python edits from the shell have mangled backslash escapes in C# literals (`\n`, `\\`) more than once. If you must script an edit, write the new text to a file with the editor and splice it in.
6. **Verify.**
   - `dotnet test` must be green (if `bin\Debug` is locked, run `dotnet test tests/Il2Radio.Core.Tests`).
   - Build `src/Il2Radio.App` and `src/Il2Radio.Diag`, into a scratch folder if locked.
   - Re-run the offline replay against the user's real file and show the before and after output.
7. **Update docs** when behaviour or a documented fact changed: ARCHITECTURE.md for how it works, FEASIBILITY.md for evidence, ROADMAP.md if it closes or opens an in-game check.
8. **Don't commit or push** unless the user asks. Leave the changes for review.

## Report back
Keep it short and plain:
- what was wrong and why (the root cause, one or two sentences);
- what you changed, with file links;
- how you verified it: the test name, test totals, and replay output before and after;
- what still needs checking in a real flight, and the exact steps for the user;
- anything you found but didn't fix.

Say clearly what you verified versus what you inferred.
