# Voice packs

A voice pack is a JSON manifest that maps clip **keys** to audio files. Replies are spoken by joining clips in key order, then putting the result through the radio effect. What a pack can't say is left out of the audio and stays on the on-screen text; nothing else speaks in its place.

The app looks for manifests in `./voices` and in the `voices` folder next to the program (`Il2Radio.exe` or `Il2Radio.Diag.exe`):
- `*.json` directly in either folder;
- `*/voice.json` one folder down.

The four IL-2 manifests in this folder are also built into the program, so `Il2Radio.exe` needs no `voices` folder. A manifest of the same pack name in one of the folders above takes precedence over the built-in copy, so an edited copy wins.

## The IL-2 packs in this folder
`il2-ger.json`, `il2-rus.json`, `il2-usa.json` and `il2-gbr.json` point at the game's own pilot recordings in **your** install (`{il2}` = the IL-2 root, found automatically or from `--game` / `IL2_PATH`). They contain file names only. The recordings are read in place and never copied, so please don't copy IL-2's `.ogg` files into this repository or share them. See [docs/FEASIBILITY.md](../docs/FEASIBILITY.md#recorded-radio-voices-researched-2026-10-25).

Check what a pack resolves to, and listen to every clip with its key:
```bash
dotnet run --project src/Il2Radio.Diag -- voice-check --country 201 --play
```

## Making your own pack
Put your recordings (WAV 8/16/24/32-bit or float, or Ogg Vorbis; any rate; mono or stereo) in a folder such as `voices/my-voice/`, and add `voices/my-voice/voice.json`:
```jsonc
{
  "name": "My Luftwaffe voice",
  "language": "de",
  "countries": [201],          // IL-2 countries: 101 USSR, 102 GB, 103 USA, 201 Germany; [] = any
  "root": ".",                 // relative to this file, or "{il2}/..." for the game install
  "speakers": ["anton", "bruno"], // optional subfolders, substituted for {speaker}; number 1 → first, 2 → second, ...
  "preprocessed": false,       // true if the clips already sound like radio (lighter effect is applied)
  "clips": {
    "callsign.8": "{speaker}/pirol.wav",
    "number.1": "{speaker}/eins.wav",
    "clock.3": "{speaker}/drei_uhr.wav",
    "range.far": "{speaker}/weit.wav"
  },
  "words": {                   // optional: what a callsign clip says, shown in the chat instead of the English word
    "callsign.8": "Pirol"
  }
}
```
`words` keeps the chat and the voice naming the same callsign: without it, the chat shows the English word from `data\GUI\DefaultCallsigns.eng` ("Oriole"), whatever the clip says. `il2-ger.json` has one for every callsign clip (only `callsign.9` = "Lumme" has been heard; the rest are inferred, so correct them by ear).

Your own packs take precedence over the game's for the same country. To use one pack for every speaker, pass `--voice voices/my-voice` (or part of its name).

## Keys
Every key is optional. A part of a reply the pack has no clip for is left out of the audio, and the rest still plays; the on-screen text always carries the whole report. A reply the pack can say nothing of is text only. No other voice is mixed in.

| Key | Says | Notes |
|---|---|---|
| `callsign.<N>` | the speaker's career callsign word, N = IL-2 callsign number (8 = "Oriole"; see `data\GUI\DefaultCallsigns.eng`) | |
| `number.1` … `number.20` | the speaker's number within his callsign, or in your flight | |
| `group`, `pair` | "flight of N" / "two aircraft" (`pair` falls back to `group`) | `group` also stands in for pilot "a few" / "many" |
| `separated` | "separated" | |
| `clock.1` … `clock.12` | "three o'clock" | |
| `compass.n`, `.ne`, `.e`, `.se`, `.s`, `.sw`, `.w`, `.nw` | direction (bearing, steer, heading, a cruise course) | |
| `steer` | "steer" (before the direction) | |
| `range.close` (under 1.5 km, and pilot "close"), `range.1`, `range.2`, … (whole km), `range.far` (beyond 10 km, and pilot "far"), `range.few` | range | pilot "a few kilometres" uses `range.few` only |
| `vertical.above`, `vertical.below`, `vertical.level` | height relative to you (level = within 300 m) | |
| `altitude` + `altitude.<metres to nearest 100>` (e.g. `altitude.2700`) | "altitude 2700 metres" | |
| `heading` | "heading" (in front of the compass clip) | without it, a heading is the compass clip alone |
| `turn.left`, `turn.right` | "turning left" / "turning right", before a cruise course | |
| `information`, `withus` | "you are in formation with us" / "you are with us" (each falls back to the other, then to `ack`) | |
| `nocontacts` | "no contacts" (falls back to `ack`) | |
| `ack` | an acknowledgement ("Roger"): the placeholder for a whole message the pack has no recording of | |
| `beside.left`, `beside.right`, `beside.behind` | "to the left" / "to the right" / "behind": where a mate in formation sits off your nose | said instead of `ack` for "in formation" / "with us"; ahead has none |
| `spotted` | "spotted", after the callsign of an unasked call for a newly gained contact | |
| `destroyed` | "target destroyed", for an enemy a mate saw go down | |
| `class.fighter`, `class.fighters`, … `bomber(s)`, `attack(s)`, `recon(s)`, `transport` | contact class | also stands in for the type name |
| `side.enemy`, `side.friendly`, `hedge` | "enemy" / "friendly", "possible" | |
| `contact.type` | one generic clip for the aircraft type | the class word is dropped when this plays |
| `few`, `many`, `onme`, `cannotsee`, `lastseen`, `grid` | pilot mode: "a few", "many", "on me", "I cannot see you", "last seen", "grid" | |

The game packs map `ack` to `const\roger`, `turn.left`/`turn.right` to `const\trnleft`/`trnright`, `destroyed` to `const\rep_trgdestr`, `spotted` to `const\spt` and `beside.left`/`right`/`behind` to `varDir\toLeft`/`toRight`/`toBack`. Those meanings come from Windows speech recognition on the English actors, whose script matches the German and Russian file for file; the other languages have not been heard yet.

Each clip is levelled to the same loudness and joined with a 25 ms crossfade. Clips with little silence at either end sound most natural.
