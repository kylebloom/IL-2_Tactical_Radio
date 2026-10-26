# IL-2 Tactical Radio (feasibility PoC)

A companion app for IL-2 Sturmovik: Great Battles single-player Career Mode. It adds a few realistic radio requests, such as asking friendly aircraft for their positions. It runs outside the game and only reads data IL-2 already exports, so it works with Mods Off and doesn't touch game files or the game process.

**Current milestone:** a proof of concept to find out what IL-2 actually exposes. Read [docs/FEASIBILITY.md](docs/FEASIBILITY.md) first. In short: aircraft identities, flights and statuses are available live. AI positions are only available at discrete events. Continuous positions depend on IL-2's Tacview export, which still needs a live test.

## How to run it
This is the one supported way to fly with the radio. You don't need anything else in this README to use it; the rest is reference.

**Once, before the first flight**
1. Get `Il2Radio.exe`. To build it yourself, install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and run this in this folder:
   ```bash
   dotnet publish src/Il2Radio.App
   ```
   This writes one self-contained `publish\Il2Radio.exe` (about 50 MB), which runs without .NET installed. Put it in a folder of its own, for example `C:\Games\IL-2 Radio\`. Its settings and session logs go next to it.
2. Install RivaTuner Statistics Server and turn its on-screen display on ([steps below](#setting-up-rtss-one-time)). It shows the radio text inside IL-2, including in fullscreen.

**Every flight**
1. Start RivaTuner Statistics Server; it sits in the system tray.
2. Double-click `Il2Radio.exe` and accept the administrator prompt. Administrator rights are needed to read aircraft positions while IL-2 is writing them. A window opens; leave it open while you fly.
   - The Status tab checks that it runs as administrator, that IL-2 exports its mission log and Tacview tracks, and that RTSS is running. Each check is `[ok]` or `[!!]` with what to do; problems are also shown in game at the start of each mission.
   - **On the first start**, if IL-2 is closed and isn't exporting that data yet, it asks whether to switch it on in `startup.cfg` (Yes; the file is backed up first). If IL-2 is running, close it and start the radio again.
   - **Also on the first start**, it offers an optional German voice for aircraft names (Yes / No / Ask me later). See [German phrase voice](#german-phrase-voice-optional); No is fine, the radio works without it.
3. Start IL-2 and fly a Career mission. Once you're in the cockpit, press **LCtrl+R** to start track recording; the positions come from that track. If no track is recording a minute into the mission, the radio reminds you in game.
4. With Num Lock on, use the numpad:

   | Key | Does |
   |---|---|
   | Numpad 1 | Ask all friendlies for their positions |
   | Numpad 2 | Ask your flight where it is |
   | Numpad 3 | Separated: ask your flight to help you rejoin |
   | Numpad 4 | Ask your flight what enemy or unknown aircraft it can see |
   | Numpad 5 | Ask your flight to identify the nearest aircraft it can see |
   | Numpad 0 | Show or hide this list in game |
   | Numpad 7 / 9 | Page back / forward through earlier radio messages |
   | Numpad . | Quit (also writes a session report to `logs\` next to the program) |

   Closing the window, or Quit, also quits cleanly.

### Settings
The first start creates `settings.json` next to `Il2Radio.exe` with every setting at its default. Change them on the Settings tab and choose Save and apply. The volume takes effect on the next transmission; anything else restarts the radio in the same window. A bad value is reported by name and nothing is restarted until it is fixed. Reset to defaults puts the form back; it is saved only when you apply. The file can still be edited by hand (delete it to get the defaults back).

| Setting | Default | Meaning |
|---|---|---|
| `il2Path` | `null` | IL-2 folder (the one with `data\startup.cfg`). `null` looks in `IL2_PATH` and the usual Steam folders |
| `volume` | `0.8` | Radio volume, 0 to 1, independent of IL-2's own |
| `voice` | `"auto"` | `auto`: the game's pilot voices by nation, else Windows TTS. `tts`: always TTS. Or a voice pack's name or folder |
| `realism` | `"pilot"` | `pilot`: a friendly says only what that pilot could know (below). `precise`: bearings and ranges from the live track |
| `unasked.route` | `true` | On a cruise leg, one course call per turn, the new heading to the nearest 5°. The chat says "Number one, heading 180"; the headset says his callsign and number, and the degrees stay on the chat. When you are the leader, the chat shows "Route, heading 180" and the headset stays quiet. Off once you leave the briefed route, except a gentle turn by an AI leader |
| `unasked.contacts` | `true` | One call when the contact picture changes: a group gained, lost, or "on me". The same sentence as Numpad 4. A clock that merely slides is left unsaid |
| `radioWait`, `radioWaitRatio` | `true`, `1.8` | Hold replies while IL-2's own radio is talking ([details](#audio)) |
| `recordingReminderSeconds` | `60` | Remind you to press LCtrl+R this long into a mission without a track; `0` never |
| `chat.output` | `"rtss"` | `rtss` (in game, works in fullscreen), `overlay` (windowed IL-2 only), `window` (e.g. a second screen) or `off` |
| `chat.rtssPosition`, `chat.rtssMargin` | `"left-middle"`, `32` | RTSS text position: `left-middle`, `top-left`, `top-right`, `bottom-left`, `bottom-right`, `default` or `"X,Y"`; margin in screen pixels |
| `chat.rtssColumns`, `chat.rtssZoom`, `chat.rtssCell` | `72`, `null`, `null` | RTSS line width; OSD zoom (`null`: from IL-2's RTSS profile); character size `[w, h]` for the right and bottom presets |
| `chat.screen`, `chat.windowArea`, `chat.fontPixels` | `0`, `[0.015, 0.40, 0.42, 0.30]`, `16` | Overlay/window: screen number, area (left, top, width, height as screen fractions), font size on a 1080-line screen |
| `chat.keepClear` | `true` | Keep the text out of IL-2's own message line and subtitles |
| `piper.enabled` | `null` | The optional German phrase voice ([details](#german-phrase-voice-optional)). `null`: not answered yet, so the radio asks at start. `true`: use it (asks before downloading it again if it is missing). `false`: don't use it, don't ask. The radio writes your answer here itself |
| `piper.voice`, `piper.folder`, `piper.lengthScale` | `"de_DE-thorsten-medium"`, `null`, `0.9` | Which Piper voice (`de_DE-thorsten-medium`, `de_DE-thorsten-high`, `de_DE-thorsten-low`, `de_DE-karlsson-low`); where it is stored (`null`: a `piper` folder beside `Il2Radio.exe`, never the IL-2 folder); speaking pace (below 1 is faster) |
| `keys.*` | `Num1` … `Num.` | One key per action: `friendlyPositions`, `flightPosition`, `rejoin`, `enemyContacts`, `identifyContact`, `menu`, `older`, `newer`, `quit`. Names: `Num0`–`Num9`, `Num.`, `Num+`, `Num-`, `Num*`, `Num/`, `F1`–`F24`, `A`–`Z`, `0`–`9`, `Insert`, `Delete`, `Home`, `End`, `PageUp`, `PageDown`, arrows, `Pause`, `ScrollLock`. `""` leaves an action unbound |

Keys are watched, not taken: IL-2 still receives them, so pick keys IL-2 doesn't use. The options are described in more detail under [Radio text](#radio-text-chat), where they appear as `live` flags of the diagnostic console.

## Layout
- `src/Il2Radio.Core`: decoders (binary/text mission log, UDP telemetry, ACMI, .Mission), `ContactTracker`, radio commands, visibility model, settings
- `src/Il2Radio.Windows`: Windows-only I/O (keys, audio, chat outputs, shadow copies) and the live session
- `src/Il2Radio.App`: the radio app, `Il2Radio.exe`
- `src/Il2Radio.Diag`: diagnostic console for development
- `voices`: voice-pack manifests (file names only; the game packs point into your IL-2 install). Built into both programs.
- `tests/Il2Radio.Core.Tests`: xUnit tests, including a real Career `.mlg` fixture

Docs:
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): how each subsystem works; start here before changing code.
- [docs/ROADMAP.md](docs/ROADMAP.md): what's left, and what still needs checking in a flight.
- [docs/FEASIBILITY.md](docs/FEASIBILITY.md): research and evidence, with sources.

## Build & test
```bash
dotnet test
```
Requires the .NET 10 SDK.

## Diagnostic console
For development: the full monitor (every source, every contact, why each one stayed silent) and offline tools. It takes command-line flags instead of `settings.json`; for `live`, run the terminal as administrator.
```bash
dotnet run --project src/Il2Radio.Diag -- check            # which IL-2 outputs are enabled
dotnet run --project src/Il2Radio.Diag -- check --apply    # enable them (IL-2 must be closed; startup.cfg is backed up)
dotnet run --project src/Il2Radio.Diag -- mission          # planned Career mission (_gen.Mission)
dotnet run --project src/Il2Radio.Diag -- mlg              # decode newest data\FlightLogs\*.mlg
dotnet run --project src/Il2Radio.Diag -- acmi             # summarise newest data\Tracks\*.acmi
dotnet run --project src/Il2Radio.Diag -- snapshot-test    # time reading a locked file via Volume Shadow Copy (admin)
dotnet run --project src/Il2Radio.Diag -- live             # run while flying (see keys below)
```
Pass `--game <IL-2 root>` or set `IL2_PATH` if IL-2 isn't in the default Steam location.

`check --apply` sets these in `data\startup.cfg`:
| Section.key | Value | Purpose |
|---|---|---|
| `telemetrydevice.*` / `motiondevice.*` | enable, 127.0.0.1:29373 | player state and attitude (UDP) |
| `system.mission_text_log` | 1 | text mission log |
| `track_record.tacviewrecord` | 1 | Tacview ACMI while a track records |

The UDP telemetry port can only point to one listener. If you use SRS or a bass-shaker tool, they'll conflict for now (a relay is on the roadmap).

`live` writes `diag-output\evidence-*.md` and a raw UDP capture you can replay. Please share the evidence file after a test flight.

### Radio keys (Num Lock on; they work while IL-2 has focus)
| Key | Command | Example reply |
|---|---|---|
| Numpad 1 | Request friendly positions | "Oriole one, five aircraft together, at your three o'clock, far, level with you." |
| Numpad 2 | Request flight position | "Oriole one, flight of five, at your three o'clock, far, above you." |
| Numpad 3 | Separated, request rejoin | "Oriole one, at your four o'clock, far, above you. Heading east, altitude 2500 metres." |
| Numpad 4 | Request enemy contacts | "Oriole two, four Yaks, at your four o'clock, a few kilometres, high." |
| Numpad 5 | Identify nearest contact | "Oriole two, contact at your two o'clock, close: a Messerschmitt, enemy, heading east." |
| Numpad 0 | Show or hide the command list in the chat output | |
| Numpad 7 / 9 | Page back / forward through the radio log (paging forward past the newest page returns to the live lines) | |
| Numpad . | Quit (writes the evidence file) | |

Aircraft within 2 km and 600 m of each other answer as one group through their lowest-numbered member. Only your own flight answers Numpad 2 and 3.

Those examples are `realism` `pilot`, the default. A pilot gives a position relative to you when he can see you, or when your own flight is in formation. Anyone who can hear the call but cannot see you answers with his own heading and altitude ("Oriole one, I cannot see you. Heading east, altitude 2500 metres.") and does not invent a clock position. He also names the map square he is in ("Grid 1022-5"), or the four digits alone when he is close to a line inside that square. Your own flight does that only while it is still on its briefed route; another friendly flight does it whenever it can hear you. Ranges are close, a few kilometres, or far. Set `realism` to `precise` for the earlier wording ("bearing one-one-zero, approximately 18 kilometres, altitude 2700 metres", and a steer on Numpad 3).

**Unasked calls** are on until you turn them off, each on its own, under `unasked` in `settings.json` or the two checkboxes on the Settings tab. On a cruise leg the chat says the new course once as a corner approaches, to the nearest 5° ("heading 180"), so you can follow an AI leader without the map. His own voice says his callsign and number; the degrees stay on the chat. When you are the leader, that line is labelled "Route" and nothing is spoken. In a fight you get one line when the picture changes, and then silence until it changes again. A numpad press still cuts whatever is waiting. While a track is recording, hearing these means reading the locked file about every 10 seconds; turning both off stops that.

**Contacts and identification (Numpad 4 and 5) aren't radar.** Your flight reports only what its pilots could plausibly see. Flying alone, any friendly in radio range reports instead. In `pilot`, that call is in your clock when the spotter is with you. A pilot in the fight who cannot see you says "four Yaks, on me." One who can see them but not you gives his own clock ("at my three o'clock, a few kilometres"). Up to four aircraft are counted once their shape is visible; otherwise the call is "a few" or "many".
- **Spotting:** likelier for larger aircraft and at closer range. Haze, darkness, a target below you against the ground, and cloud between observer and target all make it harder. It's a game-design model, not physiology.
- **What gets said:** depends on range. In order of getting closer, about four ranges apart for a fighter:
  - a speck: "unidentified aircraft"
  - its shape: "unidentified fighters"
  - its markings, side known (~3 km): "enemy fighters"
  - its type (~1.8 km): "Yaks"

  Bombers can be made out further away than fighters.
- **Uncertainty:** near the edge of identification range, a call can be hedged ("possible Yaks") or mistake a look-alike (Yak ↔ Messerschmitt), and a closer look corrects it.
- **Memory:** a spot is kept for 60 s after the aircraft leaves sight, including a pass that happened between calls. The reply says where it was last seen ("last seen at your three o'clock, close"), not where the track has moved it since. Identifications don't flicker, and they don't get sharper from a look the pilot no longer has.
- **Voices:** recorded game clips cover class words ("fighters", "bombers"), clock positions, ranges and high/low. The game has no recordings of type names or "no contacts": the class word stands in for the type, and "no contacts" uses Windows text-to-speech, unless the optional [German phrase voice](#german-phrase-voice-optional) is installed.

Pilots identify themselves by their Career callsign and number from the mission plan ("Oriole two"). The callsign words come from your install's `data\GUI\DefaultCallsigns.eng`. If a pilot isn't in the plan or the word is unknown, he says "Number two", or his name if he's from another unit.

### Audio
Replies are spoken by **IL-2's own radio voice actors** in the speaker's language: German for Luftwaffe (country 201), Russian for VVS (101), English for RAF and USAAF (102, 103). The app reads the recordings straight from your install (`data\Audio\radio\…\*.ogg`) and joins fragments such as number, clock position, range and high/low. Nothing is copied out of the game or modified. No setup is needed if IL-2 is in the default Steam folder; otherwise pass `--game <IL-2 root>` or set `IL2_PATH`.

The game's fragments are coarser than the English text, which still appears on screen. For example, "flight of five, at your three o'clock, far, above you" is spoken as *Oriole · one · group · three o'clock · long range · high*. "A few kilometres", "on me", "I cannot see you" and a map square stay on screen; with the German phrase voice they are spoken too. Replies the recordings can't express ("you are with us", "no contacts") fall back to Windows text-to-speech, or use the optional [German phrase voice](#german-phrase-voice-optional).

Every transmission then goes through a radio effect:
- squelch burst at the start, tail and click at the end
- 300–3000 Hz voice band with slight overdrive. The game's clips are already radio-processed, so they only get a gentle drive.
- background hiss that gets louder the further away the speaker is

Each flight number gets its own voice actor (four German and five Soviet pilot voices, reused beyond that), or its own TTS pitch and rate. A new request cuts off any replies still waiting to be spoken.
```bash
dotnet run --project src/Il2Radio.Diag -- say                          # sample replies, German pilots
dotnet run --project src/Il2Radio.Diag -- say --country 101            # Soviet pilots
dotnet run --project src/Il2Radio.Diag -- say --voice tts              # the same replies with Windows TTS
dotnet run --project src/Il2Radio.Diag -- say "Number one, you are with us." --range 0.8 --volume 0.6   # free text: TTS
dotnet run --project src/Il2Radio.Diag -- voice-check --country 201 --play   # play every clip with its key
dotnet run --project src/Il2Radio.Diag -- live --volume 0.6            # or --voice tts, or --no-audio
```
Volume is independent of IL-2's own sound settings.

**Not talking over IL-2's radio:** `live` watches the Windows peak meter of Il-2.exe's audio session (read-only, as the volume mixer does). A reply waits while IL-2's audio rises clearly above its engine floor (default 1.8×), for at most 8 s. IL-2 mixes radio, engine and guns into one stream, so this is a heuristic: gunfire also holds replies. It has not yet been checked against real IL-2 chatter. The monitor header shows the peak, floor and state; tune with `--radio-wait-ratio R`, or switch it off with `--radio-wait off`.
```bash
dotnet run --project src/Il2Radio.Diag -- audio-meter --seconds 120     # watch the meter while AI pilots talk, to pick R
```

**Your own recordings:** put WAV or OGG clips and a `voice.json` manifest in `voices/<name>/`. Your packs take precedence over the game's for the countries they list. Keys and the manifest format are in [voices/README.md](voices/README.md). Please don't copy IL-2's audio files into this repository.

**Unverified:** the meaning of each German or Russian fragment is inferred from its file name. For the English set it was spot-checked with speech recognition. If `voice-check --play` shows a clip saying something else, fix its line in `voices/il2-ger.json`.

#### German phrase voice (optional)

**What it is.** IL-2's German pilot recordings have no words for aircraft names ("Yaks"), for "nothing sighted", or for "enemy". Without help, the radio leaves those words on screen only (or uses the English Windows voice for "nothing sighted"). The German phrase voice is a small speech program, [Piper](https://github.com/rhasspy/piper), that runs on your own PC and says just those few words in German, in the middle of the game pilot's reply. You don't need it; it is a matter of taste.

**Step by step**
1. **Start the radio as usual** (double-click `Il2Radio.exe`). The first time, a window titled *German phrase voice* asks:
   > Add a German voice for words the game's pilots can't say (aircraft names, "nothing sighted")?
   > It is a one-time download of 85.7 MB, and works offline afterwards:
   > \- Piper speech engine (MIT licence), 22.5 MB, from github.com/…
   > \- German voice "thorsten" (free CC0 recordings), 63.2 MB, from huggingface.co/…
   > \- voice settings for "thorsten", 4 KB, from huggingface.co/…
   >
   > Saved in …\piper (not in the IL-2 folder).
2. **Choose one of the three buttons:**
   - **Yes, download**: a small window shows the progress (for example *Downloading German voice: 34 % (21.5 of 63.2 MB)*). It takes a minute or two on a normal connection. Then the radio starts as usual.
   - **No, don't ask again**: nothing is downloaded and you won't be asked again. You can change your mind later (step 5).
   - **Ask me later** (or closing the window): nothing is downloaded, and you'll be asked again next time.
   Nothing is ever downloaded without Yes. The radio remembers Yes and No by itself; you don't need to edit any file.
3. **Check it is ready.** On the Status tab you should see `[ok] German phrase voice: installed` (later starts say `ready`).
4. **Check it in a flight.** In a Career mission with track recording on (LCtrl+R):
   - Press **Numpad 4** when nothing is around. Instead of the English computer voice you should hear the game's pilot say his callsign, then a German voice say **"nichts gesichtet"** (nothing sighted), through the same radio crackle.
   - When your flight can make out enemy aircraft up close (under about 2 km for fighters), press **Numpad 4** again. You should hear the game pilot (his callsign, "pair" or "group", the clock position, the range) with the aircraft name, for example **"Jaks"**, in the other voice. **Numpad 5** can also add **"Feind"** (enemy) or **"eigene"** (friendly).
   - The text on screen is unchanged; only what you hear gets the extra words.
5. **Change your mind later.** In the radio window, open **Settings**, section **German phrase voice**:
   - to turn it **on** (or retry a download): tick *Use it for words the game's pilots can't say* and choose **Save and apply**. The same question appears;
   - to turn it **off**: untick it and choose **Save and apply**. The files stay, so turning it on again needs no download;
   - to **remove** it completely: choose **Remove the German voice**. The radio deletes its files (only those it downloaded) and restarts without it.

**What you'll hear** (a real replay with realism set to `precise`; the default `pilot` words its replies differently, with the same idea):

| Reply text (on screen) | Spoken |
|---|---|

| Reply text (on screen) | Spoken |
|---|---|
| Toni Seemann, four Yaks, at your four o'clock, approximately 8 kilometres, high. | *group* · **Jaks** · *four o'clock* · *8 km* · *high* |
| … contact at your four o'clock …: four Yaks, enemy, heading south. | *group* · **Jaks Feind** · *four o'clock* · *8 km* · *high* |
| Oriole one, no contacts. | *Oriole* · *one* · **nichts gesichtet** |

*Italics* are the game's recordings, **bold** is Piper. The fill-in is cut to the same band as those recordings (about 700 Hz to 2.6 kHz) and given a matching hiss, and Piper is asked for a flatter delivery than its normal read-aloud voice. It also says "vermutlich" (possible), "Feind"/"eigene" (enemy/friendly) and "du bist bei uns" (with us). Numbers stay with the recordings (18 km is still "long range"), and Soviet, British and American speakers are unaffected.

**Troubleshooting**
- **"The German phrase voice couldn't be downloaded (no internet connection, or the website didn't answer)."** Nothing was installed, and the radio works as before. It offers the download again at the next start, or choose Save and apply in Settings to try at once.
- **"...arrived damaged or different from what was expected, so it was thrown away."** Every file is checked against its published size and fingerprint before use, and this one didn't match (usually a broken connection, sometimes a proxy or filter). Just try again later, as above.
- **"The German phrase voice is installed but didn't start"**, or you were asked to download again although you already had. An antivirus program has probably blocked or quarantined `piper.exe` (in the `piper\runtime\piper` folder beside `Il2Radio.exe`). Restore it from the antivirus quarantine, or allow that folder, then restart the radio. Piper is open-source software from GitHub (rhasspy/piper, release 2023.11.14-2).
- **No German words in a flight, but the Status tab says ready.** The German words are only used where the game pilot can't speak: you won't hear them in a reply that the game's recordings cover completely. Also check that *Voice* in Settings is `auto` (with `tts`, everything is spoken by the English Windows voice) and that you are flying for the Luftwaffe (Soviet, British and American pilots are not affected).
- **No sound at all.** That isn't the German voice: see [Audio](#audio) (the radio volume in Settings, and the Windows output device).

**Advanced.** The answer is stored as `piper.enabled` in `settings.json` (`null` = not answered, `true`, `false`). `piper.voice` picks another voice (`de_DE-thorsten-high`, 114 MB, a little clearer; `de_DE-thorsten-low`; `de_DE-karlsson-low`), `piper.folder` where it is stored (never inside the IL-2 folder), `piper.lengthScale` the pace. Diag has the same question on the console:
```bash
dotnet run --project src/Il2Radio.Diag -- piper-setup --piper C:\Il2Radio\piper     # same prompt, for the developer console
dotnet run --project src/Il2Radio.Diag -- say --piper C:\Il2Radio\piper             # samples with the phrase voice
```

### Radio text (chat)
IL-2 offers no way for an external program to write into its chat in single-player (see [FEASIBILITY.md](docs/FEASIBILITY.md#radio-text-in-il-2s-chat--message-area-researched-2026-10-25)). Instead, `live` shows the exchange through one of three outputs.
- **On key press:** your call ("Gert Knoll: All friendlies, report positions.") appears straight away, and you hear a mic key click and a short carrier burst at the radio volume. This cuts off any transmission still playing.
- **Replies:** each reply appears under the pilot's name as the voice starts speaking it.
- **No answer:** if nobody can answer, one line says why, e.g. "no reply: position snapshot needs administrator rights".
- Lines fade out after 14 s; Numpad 7 and 9 page through the whole exchange history (the last 200 lines). Each line is stamped with the mission clock, the same time as the watch in the cockpit, once the mission log or a track has been read. Pausing the game pauses the clock. Numpad 0 lists the radio keys. Both views close after 14 s without a key, or when you make a new call.
- **Look:** measured from IL-2's own "Game is Paused" message. Each line has its own black box at 42 % opacity, sized to the text, with light grey (#E0E0DC) Arial text in a single colour, speaker included. The box is 2.6 cap heights tall with 1.45 cap heights of side padding. The tool's own notes ("no reply") are dimmer. RTSS gets the same colours and, behind each row, a black strip at that same 42 %, one character-row tall and only as wide as that row. It thins as the row fades. Text size and any extra shadow still come from RTSS's OSD settings.
- **Keeping clear of IL-2's text:** the overlay and the RTSS text stay out of IL-2's bottom-left message line and the subtitle rows that the planned Career mission uses (read from `_gen.Mission`). By default the overlay is narrowed to end left of the centre subtitles, and the RTSS text is limited in columns and rows (the monitor header says so). `--keep-clear off` disables this. The areas are estimates (`Il2ScreenAreas`).

| `--chat` | What | Works with IL-2 fullscreen (`fullscreen = 1`)? |
|---|---|---|
| `rtss` | Text in the RivaTuner Statistics Server on-screen display, drawn inside the game's frames | **Yes** (needs RTSS running, see below) |
| `overlay` (default) | Transparent, click-through window styled like IL-2's message line | No, hidden while IL-2 has focus. Needs `fullscreen = 0` |
| `window` | Ordinary window, e.g. `--chat-screen 1` for a second monitor | Yes, on the other screen |

```bash
dotnet run --project src/Il2Radio.Diag -- chat-test --chat rtss            # loop a sample exchange; start it, then fly in IL-2
dotnet run --project src/Il2Radio.Diag -- chat-test --png chat.png          # render the overlay's live, menu and log views to chat*.png
dotnet run --project src/Il2Radio.Diag -- live --chat rtss
dotnet run --project src/Il2Radio.Diag -- live --chat rtss --rtss-pos bottom-left --rtss-margin 48
dotnet run --project src/Il2Radio.Diag -- live --chat rtss --rtss-pos 40,300 --rtss-columns 60
dotnet run --project src/Il2Radio.Diag -- live --chat-pos 0.015,0.40,0.42,0.30 --chat-font 16
dotnet run --project src/Il2Radio.Diag -- live --chat window --chat-screen 1
dotnet run --project src/Il2Radio.Diag -- live --chat off
```
- `--chat-pos` is left, top, width and height as fractions of the screen. Text is bottom-aligned, so the default ends just above IL-2's own bottom-left message line. `--chat-font` is pixel size on a 1080-line screen.
- `--rtss-columns` sets where RTSS lines wrap (RTSS doesn't wrap).
- **RTSS position:** `--rtss-pos left-middle|top-left|top-right|bottom-left|bottom-right|default|X,Y` with `--rtss-margin PX` (screen pixels, default 32).
  - **Default `left-middle`:** the text starts at 42 % of the screen height on the left. That keeps it on the canopy side, clear of the gunsight, the instrument panel and RTSS's own top-left counter, and it grows downwards without reaching IL-2's bottom-left message line.
  - **How it's placed:** each preset is converted to RTSS's `<P=x,y>` tag. The game resolution comes from `startup.cfg` (or `--rtss-screen WxH`), and the OSD zoom from IL-2's RTSS profile (or `--rtss-zoom Z`). No RTSS setting needs changing, and the FPS counter stays where RTSS puts it.
  - **Right and bottom presets** use an estimated character size; if they sit off, calibrate with `--rtss-cell W,H` (default `9,16`).
  - `default` leaves the text at RTSS's own OSD position.
  - `X,Y` is in RTSS's zoomed pixels.
  - The monitor header shows the position in use, e.g. `text at <P=16,302> (LeftMiddle, margin 32 px, 2560x1440, zoom 2 …)`.

#### Setting up RTSS (one time)
1. Download RivaTuner Statistics Server **only** from its official distributor, Guru3D: <https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/>. MSI Afterburner from msi.com also bundles it. Install it and start it; it lives in the system tray.
2. Open the RTSS window. On the **Global** profile, set **Show On-Screen Display** to On. If you'd rather limit it to IL-2, press **Add** and pick `…\IL-2 Sturmovik Battle of Stalingrad\bin\game\Il-2.exe`, then turn it on there.
3. Optional: turn off **Show own statistics** if you don't want RTSS's frame-rate counter as well. Use **On-Screen Display zoom** for text size and drag the preview to move the OSD.
4. Nothing else is needed for third-party text. `live` and `chat-test` claim their own OSD slot ("IL-2 Tactical Radio") and release it on exit. The monitor header shows `RTSS: connected` or `RTSS: not running`. Don't run `chat-test` while `live` is running: they share the slot.

RTSS hooks the game's Direct3D output to draw its OSD; this tool only writes text into RTSS's shared memory. If IL-2 doesn't show the OSD, try **Application detection level** Medium in RTSS.

### Live positions need administrator rights
While a track is recording, IL-2 writes the Tacview ACMI file (every aircraft, ~5 updates/s) but keeps it locked. When you press Numpad 1, `live` takes a Windows Volume Shadow Copy snapshot, reads only the new part of the file, and deletes the snapshot straight away. The game process is never touched. Snapshots need administrator rights, so run the terminal as administrator. Snapshots left behind by a crash are removed on the next start.

## Known limitations
- IL-2 telemetry contains no world position, not even the player's.
- Single-player mission logs contain no periodic positions (AType 17 is absent).
- Command 1 only replies from fresh live tracks. With no live position source it deliberately says nothing rather than guessing.
