# Requirement: game recordings only, chat for the rest

26 October 2026. Decision for the spoken radio. How speech works today is in [ARCHITECTURE.md](ARCHITECTURE.md) (Audio, German phrase voice). What the German actors actually have on disk is in [FEASIBILITY.md](FEASIBILITY.md) (Recorded radio voices) and [WWII-RADIO-REALISM.md](WWII-RADIO-REALISM.md) (Recordings the radio does not use).

## Decision

The headset plays only phrases that already exist as recordings in the active voice pack. For the German pilots that pack is IL-2's own actors. A second voice is never inserted into that transmission.

The on-screen chat is the complete message. Anything the recordings cannot say stays in the subtitle and is left out of the audio.

The reason is immersion. The German phrase voice (Piper, thorsten) is a different speaker from actors 51–54. Colouring it to the clip band does not make it the same man. Hearing him in the middle of a wingman's sentence breaks the cockpit. The subtitle already says the whole report, in the same words the tests lock, so the missing words have a place to live.

## What the player gets

On a radio key, in this order:

1. The microphone click, as today.
2. The player's request line on the chat, as today. It is text. It is not spoken.
3. Each reply's **full sentence** on the chat, in the wording `RadioReply.Text` already produces. Callsign words still follow the speaker's voice pack (`Lumme`, not the English table's "Canary").
4. The same speaker's **game clips**, joined in the same order as the sentence, for every part the pack can say. The radio effect on those clips stays as it is.
5. Silence for every part the pack cannot say. That silence is a gap inside the transmission, or a transmission that is only the click plus the subtitle when no clip applies. It is not filled.

A reply with no playable clip still appears on the chat, at the moment a spoken reply would have started. It does not wait for a `Speaking` event that will never come.

## Spoken vocabulary

These are the keys `voices/il2-ger.json` already maps onto `data\Audio\radio\ger\actor51`–`actor54`. They are the only German phrases this change is allowed to play. Meanings are inferred from file names except where a flight has confirmed them (callsign 9, "Lumme").

| Clip keys already mapped | Heard as (inferred) | Still spoken |
|---|---|---|
| `callsign.1`–`callsign.45` | the Career callsign word | yes |
| `number.1`–`number.20` | the speaker's number | yes |
| `group`, `pair` | a formation, or two aircraft | yes |
| `clock.1`–`clock.12` | clock position | yes |
| `compass.n` … `compass.nw` | eight-point direction | yes, including a heading (see below) |
| `range.close`, `range.1`–`range.10`, `range.far` | close, whole kilometres, long range | yes. Pilot "close" and "far" use `range.close` and `range.far` |
| `vertical.above`, `vertical.below` | high, low | yes |
| `class.fighter(s)`, `class.bomber(s)`, `class.attack(s)`, `class.recon(s)`, `class.transport` | the class word | yes. This is what the headset says when the subtitle names a type |

A heading speaks the compass clip the pack has (`compass.e` for east). The German pack has no separate "heading" clip, and the direction recording is still a game phrase. A pack that also has a `heading` clip plays that clip in front of the compass point, as it does today.

The same rule applies to every nation, not only Germany. A Soviet, British or American speaker plays the clips in his own pack. He does not receive German synthesized words, and he does not receive an English Windows voice to cover a missing clip.

## Left to the subtitle

The German recordings have no clip for these. They are in the chat sentence and absent from the headset. Piper's German words for them are removed: "nichts gesichtet", "du bist bei uns", "du bist in Formation", "vermutlich", "Feind", "eigene", "Jak" / "Jaks" and the other type spellings, "ein paar Kilometer", "einige", "viele", "bei mir", "ich sehe dich nicht", "zuletzt gesehen", "Planquadrat …".

| Meaning in the chat | Example of the words that stay on screen |
|---|---|
| No contacts | "no contacts", "no contacts to identify" |
| With us / in formation | "you are with us", "you are in formation with us" |
| Aircraft type | "Yaks", "Messerschmitt". The headset says the class ("fighters") when that clip exists |
| Side and hedge | "enemy", "friendly", "possible", "looks like" |
| Coarse range between close and far | "a few kilometres" |
| Uncounted strength | "a few", "many" |
| The fight is on the speaker | "on me" |
| He cannot see the player | "I cannot see you" |
| A remembered look | "last seen" |
| Map square | "Grid 1022-5" |
| Altitude in metres | "altitude 2500 metres" |
| "Level with you" | the German pack has high and low, and no level clip |
| The player's own request | "Flight, report contacts." |

Altitude stays on the subtitle. Tank-crew clips that contain metre figures are a different speaker and are not a substitute.

"Close" and "far", the clock, and the eight compass points stay on the recordings. A precise-mode range the pack can say (`range.4`, `range.far` past 10 km) stays on the recordings. The kilometre the clips round away (18 km spoken as long range, while the subtitle says "approximately 18 kilometres") stays as it is.

## Rules

1. **One speaker per transmission.** A reply that uses a voice pack is built only from that pack's clips. No phrase synthesizer, and no Windows text-to-speech, is mixed in or substituted for the whole sentence.
2. **A missing clip drops that part, and the rest still plays.** A token the pack cannot say is skipped. It does not fail the utterance. Today a missing required token (`NoContacts`, `WithUs`, `InFormation`, and a clock or range the pack lacks) makes `ClipPhraser` return nothing, and `RadioVoice` then speaks the entire English sentence with Windows TTS. That fallback is the behaviour this requirement removes. The callsign and number still play on a "no contacts" call.
3. **The class word stands in on the headset.** When the subtitle names a type, the audio says the class clip and does not say the type. A user pack that supplies `contact.type` may play that clip, because it is a recording in the pack, not a synthesizer.
4. **Windows TTS is only the explicit voice mode.** `voice: "tts"` still speaks the English sentence with the installed Windows voice, for the whole reply. The default `voice: "auto"` does not enter that mode because a word was missing.
5. **The chat text does not shrink to match the audio.** `RadioReply.Text` stays the full report. The honesty rule is unchanged: the sentence still says only what the live track and the visibility model established.
6. **Chat off means those words are not shown.** `chat.output: "off"` is unchanged. The headset still plays only the recordings. The default remains `rtss`.
7. **No new clip mappings in this change.** Files that exist under `data\Audio\radio\ger` and are not in `il2-ger.json` (`roger`, `copy`, `ord_rejoin`, `toLeft`, `rep_imhit`, and the rest listed in WWII-RADIO-REALISM.md) stay unmapped until someone has heard them. A file name is not evidence of the words.

## Removed from the product

The player-facing program no longer offers, downloads, or speaks a German phrase voice.

- No first-start Yes / No / Later question.
- No Settings control for enabling, choosing, or removing that voice.
- No Diag path whose only job is to set up or exercise it (`piper-setup`, and the with/without phrase-voice lines in `acmi`).
- A `settings.json` that still contains a `piper` object still loads, and the radio still flies. Those values have no effect. The program does not delete a `piper` folder the player already downloaded; that folder is unused and can be removed by hand.

## Out of scope

- Rewriting reply sentences into period German, or into Luftwaffe brevity.
- Mapping additional game clips, including acknowledgements and trouble reports. Those wait on a listen, as in WWII-RADIO-REALISM.md.
- Changing who answers, what they are allowed to know, or the visibility model.
- Highlighting, inside the subtitle, which words were spoken and which were not. The whole sentence is the subtitle.
- A Russian phrase synthesizer. The roadmap item for one is dropped with this decision.

## Acceptance

Heard on a German Career flight, or rendered by `Il2Radio.Diag say` / `acmi` against the German pack:

| Chat (unchanged) | Headset, German pack |
|---|---|
| "Lumme one, no contacts." | `callsign.9` `number.1`. No second voice after the number. |
| "Lumme one, you are with us." | `callsign.9` `number.1` only. |
| "Lumme two, four Yaks, at your four o'clock, a few kilometres, high." | callsign, number, `class.fighters`, `clock.4`, `vertical.above`. The type, the count "four", and "a few kilometres" are on screen. |
| "Lumme two, contact at your two o'clock, close: possible Messerschmitt, enemy." | callsign, number, clock, `range.close`, and `class.fighter` when the class is in the sentence. "Possible", the type, and "enemy" are on screen. |
| "Lumme one, I cannot see you. Heading east, altitude 2500 metres. Grid 1022-5." | callsign, number, `compass.e`. The admission, the altitude, and the square are on screen. |
| A reply whose every token lacks a clip | the click, then the subtitle, and no spoken sentence. |

In every row the speaker is one game actor for the whole transmission. `voice: "auto"` with the German pack installed never describes a transmission as Piper or as Windows TTS.

Tests that lock today's synthesized segments ("Jaks", "nichts gesichtet", "vermutlich Messerschmitt", "Iljuschins Feind") are updated to the clip-only rows above. A German-pack utterance that used to become Windows TTS because "no contacts" or "with us" had no clip becomes the callsign and number clips instead.

`dotnet test` stays green. An existing `settings.json` with `piper.enabled` set still parses.

## Docs to update when this is built

- [ARCHITECTURE.md](ARCHITECTURE.md): drop the German phrase voice from the audio path. Describe clip-only playback and the chat as the carrier of unspoken words.
- [FEASIBILITY.md](FEASIBILITY.md): record the decision and why the spectrum match was not enough. Leave the Piper research in place as evidence, marked as not used.
- [ROADMAP.md](ROADMAP.md): remove the Piper flight checks and the Russian phrase-voice follow-up.
- [README.md](README.md) and [voices/README.md](voices/README.md): remove the first-start German voice and the `piper.*` settings. State that the subtitle carries what the recordings skip.
- [WWII-RADIO-REALISM.md](WWII-RADIO-REALISM.md): the phrase voice is no longer the register for unspoken words. The subtitle is.
