# IL-2 Tactical Radio: player guide

IL-2 Tactical Radio adds radio calls to single-player Career in IL-2 Sturmovik: Great Battles. Press a numpad key and the pilots on your side answer: where the friendlies are, where your own flight is, and what they can see. They speak with the game's own pilot voices for your air force, and the words appear inside the game.

Pilots only say what they could know. Positions come from the live mission, not the briefing; shot-down pilots don't answer; enemy aircraft are reported only once someone could have seen them. If nobody can answer, the radio says why instead of guessing.

The radio runs beside the game. It reads the logs IL-2 already writes and where everyone is from the game's memory (read only), or from a Tacview track when memory can't be read. To show its text in the game, even in fullscreen, it loads a small overlay of its own into IL-2 that only draws the radio's text, menu and kneeboard; it reads and writes nothing in the game. It works with Mods Off. The only IL-2 file it ever changes is `data\startup.cfg`, once, with your permission and a backup.

## What you need

- Windows 10 or 11, 64-bit.
- IL-2 Sturmovik: Great Battles (Steam or the standalone version), flying single-player Career.
- Administrator rights. The radio asks for them each time it starts; it needs them only to read a track recording while IL-2 is writing it.

## Install

**Installer:** run `IL2-Tactical-Radio-Setup-<version>.exe`. It installs for your Windows user only (in `%LOCALAPPDATA%\Programs\IL-2 Tactical Radio`), finds your IL-2 folder or asks for it, and adds a Start menu entry.

**Portable zip:** unzip into a folder of its own, for example `C:\Games\IL-2 Radio\`. Settings and session reports are kept next to `Il2Radio.exe`.

The program isn't code-signed yet, so Windows may say "Windows protected your PC". Choose **More info**, then **Run anyway**. Some antivirus programs may also be wary of a new program that asks for administrator rights.

Earlier versions showed the text through RivaTuner Statistics Server (RTSS). It is no longer used or needed. If you'd rather not have the radio's overlay in IL-2, choose **overlay** (IL-2 in windowed mode only) or **window** (for a second screen) on the Settings tab.

## First start

1. Close IL-2.
2. Start **IL-2 Tactical Radio** and accept the administrator prompt. Leave its window open while you fly.
3. It asks whether to switch on the outputs it reads (the mission log and Tacview tracks) in IL-2's `startup.cfg`. Choose **Yes**. The file is backed up first, next to it, as `startup.cfg.radio-backup-<date>`.

Your wingmen speak with IL-2's own pilot recordings, read from your install. Anything those recordings can't say, such as aircraft names or your altitude, is on the radio text only.

The **Status** tab lists checks marked `[ok]` or `[!!]`. Every `[!!]` says what to do.

## Every flight

1. Start the radio and accept the administrator prompt.
2. Start IL-2 and fly a Career mission. Positions are read from the game's memory, so nothing needs recording. If the radio can't read them, it reminds you a minute into the mission to press **LCtrl+R** to record a track instead.
3. With **Num Lock on**, use the numpad:

| Key | Does |
|---|---|
| Numpad 1 | Ask all friendlies for their positions |
| Numpad 2 | Ask your flight where it is |
| Numpad 3 | Separated: ask your flight to help you rejoin |
| Numpad 4 | Ask your flight what enemy or unknown aircraft it can see |
| Numpad 5 | Ask your flight to identify the nearest enemy or unknown aircraft it can see |
| Numpad 0 | Open the radio menu in the game; choose with Numpad 1–5. Numpad 0 again goes back, Numpad . closes it |
| Numpad / | Your kneeboard: small, then large, then put away |
| Numpad 7 / 9 | Page back / forward through earlier messages |
| Numpad . | Quit the radio |

The **Kneeboard** is a sheet of your briefed route and home field, north up, with the map squares and a scale, so you need not open IL-2's map. On an escort it shows the bombers' route out and back, dashed and labelled as theirs, and then your own leg home. Nothing on it is live: it has pencil marks only for what the radio has said, each with its age, fading as it gets older. You are marked when your leader tells you where you are on the route, when a mate has you with him, or when you fix your own position by an airfield you can see (radio menu, Kneeboard › Fix position); between fixes a dashed ring carries a pencil on by dead reckoning — the course and speed noted at the fix, walked off by a wrong wind — and the ring grows quickly. The plus is that pencil, not the aircraft. Mates are marked where they last reported, contacts where they were called.

The **Help** tab in the radio window explains when to use each call. Your flight also speaks up on its own: a new heading on a turn, a change in the contacts it can see, and a loss the game's own pilots don't call. Each of these can be turned off on the Settings tab.

## Settings

Everything is on the **Settings** tab: the IL-2 folder, volume, voices, where the text appears, the kneeboard's size and place on screen, how much pilots may claim (**pilot** or **precise**), the unasked calls and the keys. Choose **Save and apply**. They are stored in `settings.json` next to the program; delete that file to get the defaults back.

The radio watches keys without taking them, so IL-2 still sees them. Choose keys IL-2 doesn't use.

## If something doesn't work

- **Look at the Status tab first.** Problems are also shown in the game at the start of each mission.
- **No text in the game:** check that **Show text** on the Settings tab is **ingame**, and the Status tab's "Chat" line. The text appears once IL-2 is drawing the cockpit.
- **Pilots say they can't give a position:** the game's memory couldn't be read. Press LCtrl+R in the cockpit to record a track; the radio then needs administrator rights.
- **IL-2 not found:** set the IL-2 folder (the one containing `data\startup.cfg`) on the Settings tab.
## Reporting a bug

Each time the radio closes, it writes a session report to the `logs` folder next to the program (the Start menu has a link to it). Please attach the newest `evidence-*.md` to your report, with the `positions-*.csv` of the same time if there is one, and say what you pressed and what you expected. The report includes the radio's version, which is also shown on the Help tab.

Report bugs at https://github.com/kylebloom/AI_Friendly_Radio/issues

## Uninstall

Use **Uninstall IL-2 Tactical Radio** in the Start menu or Windows Settings. It asks whether to delete your settings and session reports too. For the portable zip, delete its folder.

IL-2's `startup.cfg` is left as it is. The two outputs the radio switched on (the text mission log and Tacview tracks while recording) are harmless, but to undo them, close IL-2 and copy the `startup.cfg.radio-backup-<date>` file in IL-2's `data` folder over `startup.cfg`. Any IL-2 settings changed since then are lost too.

## Licence

MIT licence, see `LICENSE.txt`. This project is not affiliated with the makers of IL-2 Sturmovik. It ships no game files: the pilot voices come from your own IL-2 install.
