# Roadmap

What's left, in priority order. How each part works today is in [ARCHITECTURE.md](ARCHITECTURE.md); the research behind it is in [FEASIBILITY.md](FEASIBILITY.md).

**Working now** (flown in Career, Battle of Stalingrad, 2026-10-25):
- Numpad 1–5: friendly positions, flight position, rejoin, enemy contacts, identify.
- Career callsigns, the game's own German and Russian pilot voices with a radio effect, and RTSS on-screen chat in exclusive fullscreen.

**Built, not yet flown:** `Il2Radio.exe` (`src/Il2Radio.App`), the player's program. It is one self-contained file that asks for administrator rights itself and opens a window: flight status, and settings saved to `settings.json` beside it. At start-up it checks rights, the `startup.cfg` outputs (asking before it changes them) and RTSS. During a mission it reminds the player to press LCtrl+R. See ARCHITECTURE.md, "The app". `Il2Radio.Diag` stays the developer console.

## 1. Joystick buttons
Today only the numpad works (`NumpadKeys`, polled with `GetAsyncKeyState`). The original brief asked for joystick buttons too.
- Read buttons without taking the device from IL-2: `Windows.Gaming.Input.RawGameController`, or DirectInput in non-exclusive background mode.
- Bindings go in the settings file (device + button → command, next to `keys`; `KeySettings`/`KeyBindings` today), with a "press a button to bind" helper.
- Never consume or remap input; IL-2 must still see every button.

## 2. Other theatres and mission types
Nothing is Stalingrad-specific, but nothing has been tested elsewhere either.
- **Maps:** Moscow, Kuban, Bodenplatte and Normandy. Check that lat/lon from ACMI and the cloud presets behave the same.
- **Voices:** US and British packs (the English voices' callsign clips are not mapped yet), and the Soviet side as the player (Russian voice pack, Allies coalition).
- **Mission types:** Quick Missions, scripted campaigns and PWCG. Check `_gen.Mission` vs other mission file names, AType 10 flight membership, and whether callsigns exist.
- **Aircraft vocabulary:** extend `AircraftTypes` for Bodenplatte and Normandy types not yet listed.

## Optional extras
- **Calls the flight makes without being asked**, such as "Oriole three, I'm hit, going down", using mission-log events (damage, kill, bail-out, landing). Only if wanted; the brief asked for no unnecessary chatter.
- ~~**A downloadable German voice** (Piper, 20–110 MB) for phrases the game's recordings can't say: aircraft type names, "no contacts". Ask before downloading anything.~~ Built 2026-10-25; the radio asks once at first start (Yes / No / Later, saved in `piper.enabled`), and it can be turned on, off or removed on the Settings tab; see ARCHITECTURE.md, "German phrase voice". Possible next steps: a Russian voice for the VVS side (`ru_RU-dmitri-medium`, CC0) with Russian words in `SynthWords`; German words for altitude and three-digit headings, if mixing voices turns out to sound acceptable.
- **Sharing UDP telemetry** with SRS or bass-shaker tools. Only matters if they're used. Positions come from Tacview now, so telemetry is only shown in the monitor and could be switched off entirely.

## Still to check in a flight
These were built and unit-tested but not yet seen or heard in IL-2:
- a friendly who cannot see you and is still on his route (Numpad 1): the square he names, including the ninth, is the one he occupies on the map you have open. Near a grid line he should give the four digits only, or heading and altitude alone when he is within a kilometre of a 10 km line. A friendly who has left the route should not name a square;
- realism `pilot` (the default; `precise` is on the Settings tab): a wingman you can see answers with your clock and close / a few kilometres / far, and high, level or low; a friendly who can hear you but cannot see you, your own flight included, gives an eight-point heading and altitude to the nearest 500 m and says "I cannot see you", and adds the map square when he is still on his briefed route (Numpad 1, and Numpad 3 with no steer); a pilot fighting away from you says the enemy is on him, and one who can see a distant aircraft but not you gives his own clock. A spot kept from a pass between calls is "last seen" at that place for 60 s, and the German phrase voice says "zuletzt gesehen". The chat carries "on me", "I cannot see you" and "last seen"; the game clips say the clock, close or far, and the heading.
- `Il2Radio.exe` as a whole: the elevation prompt on double-click, the published single file (WMI shadow copies, System.Speech and the overlay inside a single-file bundle), and a clean exit when the window is closed (the RTSS slot released, `logs\evidence-*.md` written);
- the window itself: Status shows mission, track, you, the start-up lines and the radio's own replies, and does not name aircraft the radio did not report; Save and apply changes the volume on the next transmission and comes back with new keys and chat output; Yes is required before `startup.cfg` or the German voice is touched;
- the track-recording reminder: whether "running mission" (log chunks grown in the last 90 s) and "recording" (the newest ACMI locked) hold up in a real Career mission, including pauses and a second recording segment;
- start-up warnings re-shown in game at each mission start;
- the mic-click's loudness and length;
- the RTSS presets other than the default (`bottom-left`, `top-right`, ...) and the estimated character size behind them (`--rtss-cell`);
- the RTSS background, after the 2026-10-26 correction: `<B=0,0>` drew a tall rectangle down the left of the canopy with the words at the top right, outside it. Each row should now have its own dark strip, about 40% black, only as wide and tall as that row's letters. A wrapped reply should be one strip per row, not one block down the screen. The strips thin out as their lines fade;
- the window overlay's absolute font size (`--chat-font`) and whether IL-2 colours chat speaker names;
- whether replies really hold back while IL-2's own pilots are talking (`Il2AudioMeter` threshold, `--radio-wait-ratio`);
- what the German callsign recordings actually say (`voice-check --country 201 --play`, which prints the word the chat shows next to each clip). Their mapping is inferred from file names, and only callsign 9 ("Lumme") has been heard; the other German words in `voices/il2-ger.json` `words` are translations of the Russian list, to be corrected by ear;
- enemy callouts in German, using the class word in place of the type;
- the German phrase voice (Piper), none of which has run on this machine yet, because nothing was downloaded during development:
  - the first-start question (Yes / No / Ask me later) on a fresh `settings.json`: No should write `"enabled": false` and not ask again; Later should ask again next start; closing the dialog counts as Later; Yes should write `true`, show the progress window and finish;
  - the download itself: sizes and hashes should match, and the engine's SHA-256 (not published by GitHub) is shown on the Status tab for the record;
  - the Settings tab: ticking and Save and apply asks again; unticking stops it without deleting; "Remove the German voice" deletes `piper\runtime` and `piper\voices` (piper.exe must not be held open by then) and restarts without it; saving other settings while still unanswered keeps the question for next start;
  - a failed download (unplug the network after Yes): the plain-words message on the Status tab, the radio running without it, and the question back at the next start;
  - Windows Defender's verdict on `piper.exe` after download, and the "installed but didn't start" warning if it is quarantined;
  - that `piper.exe` 2023.11.14-2 starts from `piper\runtime\piper\piper.exe` (the zip's `piper\` folder is how the release workflow packs the cmake install; not extracted or run here), finds `espeak-ng-data` beside it, and answers one WAV path per stdin line with `--output_dir` (read from its source, not run);
  - UTF-8 on stdin (the type names are spelt for a German voice: "Jak", "Lawotschkin", "Iljuschin", "Ju zweiundfünfzig"): no garbled words;
  - how a Piper word sounds between the game actor's clips after the 2026-10-25 retune (700 Hz–2.6 kHz, hiss matched to actors 51–54, flatter Piper delivery). The spectrum now lines up with those clips on the bench; the remaining question is whether the change of voice mid-sentence is acceptable in a flight;
  - latency: the first phrase after start (model load, warmed in the background) and later ones (cached per text);
  - the German words themselves ("nichts gesichtet", "Feind", "eigene", "vermutlich") as radio phrasing;
  - in the double-clicked, elevated `Il2Radio.exe` window, the dialog and the download progress show and finish before the Status tab takes over;
- whether the visibility model feels too sharp-eyed or too blind (`VisibilitySettings`);
- radio-log paging after the 2026-10-25 fix (Num9 from the short oldest page used to blank the chat): with 8–13 lines logged, Num7, Num7, Num9 should show the newest page again, and only a further Num9 returns to the live lines. Also whether Num7 = older / Num9 = newer suits the player, or Num9 (the PgUp key) should be older;
- radio-log times (Numpad 7): a line should show the same hour and minute as the cockpit watch. Pause, wait 20 seconds, and page the log again: those times should not have moved on. The first look, before any radio call has read the track, follows the mission log and can be half a minute off; the next look, after a call, should settle onto the watch. A second recording segment (`_01`) should keep the same clock.

## Known gaps in the visibility model
Sun glare and bounces out of the sun; cockpit blind spots; terrain masking; combat awareness from log events (an aircraft shooting at you is obviously spotted); time of day uses mission start time plus recording time, not the mission clock the radio log now uses.
