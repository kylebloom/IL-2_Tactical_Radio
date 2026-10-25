# Voice packs

A voice pack is a JSON manifest that maps clip **keys** to audio files. Replies are spoken by joining clips in key order, then putting the result through the radio effect. When a pack can't say a required part of a reply, that reply is spoken with Windows text-to-speech instead.

The app looks for manifests in `./voices` and in the `voices` folder next to `Il2Radio.Diag.exe`:
- `*.json` directly in either folder;
- `*/voice.json` one folder down.

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
  }
}
```
Your own packs take precedence over the game's for the same country. To use one pack for every speaker, pass `--voice voices/my-voice` (or part of its name).

## Keys
Required keys must exist for a reply to be spoken from clips. Optional keys are skipped when missing.

| Key | Says | Used by |
|---|---|---|
| `callsign.<N>` | the speaker's career callsign word, N = IL-2 callsign number (8 = "Oriole"; see `data\GUI\DefaultCallsigns.eng`) | optional |
| `number.1` … `number.20` | the speaker's number within his callsign, or in your flight | optional |
| `group`, `pair` | "flight of N" / "two aircraft" (`pair` falls back to `group`) | optional |
| `separated` | "separated" | optional |
| `clock.1` … `clock.12` | "three o'clock" | **required** for flight position |
| `compass.n`, `.ne`, `.e`, `.se`, `.s`, `.sw`, `.w`, `.nw` | direction (bearing, steer) | **required** for friendly positions and rejoin |
| `steer` | "steer" (before the direction) | optional |
| `range.close` (under 1.5 km), `range.1`, `range.2`, … (whole km), `range.far` (beyond 10 km when no exact clip) | range | **required** |
| `vertical.above`, `vertical.below`, `vertical.level` | height relative to you (level = within 300 m) | optional |
| `altitude` + `altitude.<metres to nearest 100>` (e.g. `altitude.2700`) | "altitude 2700 metres" | optional |
| `heading` | "heading" (followed by the compass clip) | optional |
| `information`, `withus` | "you are in formation with us" / "you are with us" (each falls back to the other) | **required** for those replies |

Each clip is levelled to the same loudness and joined with a 25 ms crossfade. Clips with little silence at either end sound most natural.
