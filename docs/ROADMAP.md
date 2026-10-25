# Roadmap

What's left, in priority order. How each part works today is in [ARCHITECTURE.md](ARCHITECTURE.md); the research behind it is in [FEASIBILITY.md](FEASIBILITY.md).

**Working now** (flown in Career, Battle of Stalingrad, 2026-10-25):
- Numpad 1–5: friendly positions, flight position, rejoin, enemy contacts, identify.
- Career callsigns, the game's own German and Russian pilot voices with a radio effect, and RTSS on-screen chat in exclusive fullscreen.

**Built, not yet flown:** `Il2Radio.exe` (`src/Il2Radio.App`), the player's program. It is one self-contained file that asks for administrator rights itself and reads `settings.json` beside it. At start-up it checks rights, the `startup.cfg` outputs (offering to set them) and RTSS. During a mission it reminds the player to press LCtrl+R. See ARCHITECTURE.md, "The app". `Il2Radio.Diag` stays as the developer tool.

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
- **A downloadable German voice** (Piper, 20–110 MB) for phrases the game's recordings can't say: aircraft type names, "no contacts". Ask before downloading anything.
- **Sharing UDP telemetry** with SRS or bass-shaker tools. Only matters if they're used. Positions come from Tacview now, so telemetry is only shown in the monitor and could be switched off entirely.

## Still to check in a flight
These were built and unit-tested but not yet seen or heard in IL-2:
- `Il2Radio.exe` as a whole: the elevation prompt on double-click, the published single file (WMI shadow copies, System.Speech and the overlay inside a single-file bundle), and a clean exit when the console window is closed (the RTSS slot released, `logs\evidence-*.md` written);
- the track-recording reminder: whether "running mission" (log chunks grown in the last 90 s) and "recording" (the newest ACMI locked) hold up in a real Career mission, including pauses and a second recording segment;
- start-up warnings re-shown in game at each mission start;
- the mic-click's loudness and length;
- the RTSS presets other than the default (`bottom-left`, `top-right`, ...) and the estimated character size behind them (`--rtss-cell`);
- the window overlay's absolute font size (`--chat-font`) and whether IL-2 colours chat speaker names;
- whether replies really hold back while IL-2's own pilots are talking (`Il2AudioMeter` threshold, `--radio-wait-ratio`);
- what the German callsign recordings actually say (`voice-check --country 201 --play`). Their mapping is inferred from file names;
- enemy callouts in German, using the class word in place of the type;
- whether the visibility model feels too sharp-eyed or too blind (`VisibilitySettings`).

## Known gaps in the visibility model
Sun glare and bounces out of the sun; cockpit blind spots; terrain masking; combat awareness from log events (an aircraft shooting at you is obviously spotted); time of day uses mission start time plus recording time, not the real game clock.
