# WWII radio realism — findings

26 October 2026. What the tactical radio does today, set against how a fighter net was actually used, and which of the gaps can be closed from data the game already writes.

This is a findings note, not a build plan. Nothing here is scheduled. The behaviour it describes is [ARCHITECTURE.md](ARCHITECTURE.md); what the game files do and do not contain is [FEASIBILITY.md](FEASIBILITY.md); work already queued is [ROADMAP.md](ROADMAP.md).

The standard used below is a Luftwaffe single-seat fighter on the Stalingrad map in the late summer of 1942, which is the Career this install is flying. The mission file open while this was written is dated 24 August 1942 in its `Options` block. Later Reich-defence brevity, RAF procedure and Soviet voice procedure are mentioned only where copying them onto that net would sound wrong.

Evidence tags are the same as in FEASIBILITY.md: **VERIFIED** on this machine, **DOCUMENTED** from a published source not re-checked here, **COMMUNITY** for reverse-engineered or sim-community material, **SPECULATIVE** for a design choice.

## What it does today

Five calls from the numpad, with `realism: pilot` as the default in the player's program:

| Key | Call | Who answers |
|---|---|---|
| Numpad 1 | "All friendlies, report positions." | Every friendly group inside 40 km |
| Numpad 2 | "Flight, report your position." | The player's own flight |
| Numpad 3 | "Flight, I am separated. Request rejoin." | The main part of that flight |
| Numpad 4 | "Flight, report contacts." | The flight, or any friendly in range when flying alone |
| Numpad 5 | "Flight, identify nearest contact." | The same observers, one contact |

The player's line is English text plus a microphone click. It is not spoken. Replies are IL-2's own pilot recordings for the speaker's nation, with the optional German phrase voice filling words those recordings have no clip for. The on-screen text stays English.

In pilot mode a speaker puts something on the player's clock only when he can see the player, or when a flight mate is inside 1.5 km. Otherwise he gives his own eight-point heading and his altitude to the nearest 500 m, and says he cannot see the player. A pilot in a fight he cannot place on the player says the enemy is on him. Ranges are close, a few kilometres, or far. Counts are exact for one aircraft, or for up to four once their shape is visible, and otherwise "a few" or "many". A look is remembered for 60 seconds as "last seen", at the place of that look.

Aircraft within 2 km and 600 m answer as one group, through the lowest formation number. Anything outside 40 km is silence. The channel is half-duplex: a new key cuts off whatever is still playing. Hiss grows as the speaker approaches that 40 km. Destroyed aircraft stay silent. Enemy aircraft never transmit.

## What already fits a wartime cockpit

These are worth keeping. They are the parts a 1942 net would recognise.

- **The leader speaks for the formation.** The lowest formation number answers for aircraft flying together. That is the Rottenführer or Schwarmführer keying, rather than every wingman.
- **A pilot reports from his own cockpit.** Clock position is withheld when he cannot see the player. "On me", "I cannot see you", a coarse range and a hedged or mistaken type are eyeball reports. The track's exact bearing and kilometre count stay behind `realism: precise`.
- **A lost look stays lost.** "Last seen" does not follow the aircraft after it enters cloud or passes out of sight. Identification does not get sharper from a look he no longer has.
- **The net is one frequency, own side only.** Friend or foe is decided by eye, which is right for 1942. There is no IFF reply and no intercepted enemy transmission. FuG 25 *Erstling* is a later radar-IFF set and does not belong on this map (DOCUMENTED, equipment histories).
- **The voice is the nation, and the units are metres.** German and Soviet pilots used metres. The callsign is the one the mission assigned, spoken by the game's own clip.
- **Keying steps on the channel.** Cutting the previous transmission is how a crowded fighter frequency behaved. Waiting while IL-2's own pilots are talking, if the meter check holds up in a flight, is the same courtesy.

## The net this theatre used

A Bf 109 of this period carried the **FuG 7**, an HF voice set, about 2.5–7.5 MHz and about 7 watts. Published summaries give it a range of about 50 km in good weather. It was the fighter set on the Bf 109 through the G-2 and on the early Fw 190; the Ju 87 and Hs 129 kept it after the later fighters had moved on. The **FuG 16** VHF set, 38.5–42.3 MHz, arrived with the Bf 109 G-3/G-4 and the Fw 190 A-4, and the ground-steered *Y-Verfahren* fighter control that used it is a 1943 system (DOCUMENTED: equipment summaries, including the [Wikipedia FuG survey](https://en.wikipedia.org/wiki/Luftwaffe_radio_equipment_(Funkger%C3%A4t)_of_World_War_II); the "50 km" figure is that kind of summary, not a measurement made here). For the Stalingrad Career the set to imitate is the noisy HF one. A clean VHF horizon belongs to Bodenplatte and to the later Fw 190s, and only after the variant is checked.

Who was allowed to talk was doctrine, repeated in unit practice and in memoirs: the leader transmits, the wingman listens, and the wingman keys only for the enemy, for trouble, or because he has lost the formation (DOCUMENTED as practice; no 1942 signal order was opened for this note). The call itself was plain speech. Ordinary procedure words were "von" between the station called and the station calling, "kommen" for over, "Ende" for out, and "Viktor" for understood. German military speech used "zwo" so that "zwei" would not be heard as "drei".

The code list that circulates in sim communities — *Indianer* for enemy fighters, *Pauke Pauke* for the attack, *Kirchturm* for altitude, *dicke Autos* for heavy bombers — is the fighter-control code of the Reich defence, heard by Allied Y-Service and compiled second-hand, and it clusters in 1943–45. Two public compilations were looked at and not treated as manuals: the [Gyges brevity list](https://www.gyges.dk/Operatonal%20brevity%20code%20Ver%2010_1.pdf) and [OpenFlightSchool's WWII-Deutsch page](https://www.openflightschool.de/mod/book/view.php?chapterid=284&id=259&lang=de). The second mixes those code words with modern training phrasing ("IN" / "OUT"). A 20 May 1942 Luftwaffe signals brochure exists in the German-Russian archival project ([file 293](https://wwii.germandocsinrussia.org/ru/nodes/2634-delo-293-rukovodstvo-po-osnovam-tehniki-radiosvyazi-v-vvs)); it was not read. Until it is, the safe register is the one the phrase voice already uses: ordinary German, no reconstructed code book (FEASIBILITY.md, "German phrase voice").

The game's own German pilots already say clock positions. Those clips are the vocabulary to build on. Replacing them with a 1944 code word would fight both the recordings and the year.

Soviet flights of the same year often had a transmitter in the leader's aircraft and a receiver only in the wingmen's (RSI-3 / RSI-4). That is the standard account (DOCUMENTED, secondary). The mission log does not record which set an aircraft carries, so any such rule would be a setting by formation number, not a fact read from the sortie. It matters when the player flies the VVS. It does not change a Luftwaffe sortie, because Soviet aircraft already stay off this frequency.

RAF and USAAF nets of the same war used the clock, and they used feet and "angels", with "over" and "out" as separate words. The English actor folders contain `out.ogg` and `over.ogg`, which the German folder does not (VERIFIED, file names). Metric replies are right for the German and Russian packs and wrong for those English packs. That is a later-theatre item, not a Stalingrad one.

## Findings

In the order they would change how a flight feels. Each one says what the radio does, what a 1942 leader would have done, and whether the game already gives us the facts.

### 1. The position call asks the whole map to answer

Numpad 1 invites every friendly group inside 40 km to report, nearest first. The current Career file has several other flights, on callsigns 3, 10, 13 and 19, besides the player's (VERIFIED, `_gen.Mission`). Each of those leaders can key a position. A real call of this kind went to the speaker's own Schwarm. Another Staffel on the same frequency would come up for a contact, not to read out where it was.

Numpad 2 is already the flight-only call. The period shape is that call, plus silence from everyone else. A wider call earns a transmission only from someone who can see an enemy, which is Numpad 4, or from the one nearest leader when the player's own flight cannot answer. Four contact groups can also be spoken on one press (`MaxReportedGroups` is 4). A leader passed the dangerous picture, usually one formation, and left the rest.

This needs no new data. It removes traffic, which matches the original wish for no unnecessary chatter. It is the highest-value change in this note.

### 2. A contact report stops short of the direction they are flying

Numpad 4 gives the number, the type or the class, a clock or "on me", a range band, and high or low. The heading is held back for Numpad 5, and then only once the type has been made out. A pilot who can see a formation well enough to call it fighters can see which way the noses point before he can read a number on the wing. The same ACMI fix that places the contact already carries the heading (VERIFIED, the track format).

The report that matches the look is: how many, what they appear to be, where they are, high or low, and which way they are going. "Heading west" can use the eight-point compass clips already mapped. "Crossing to the left" would use `toLeft` / `toRight`, which exist on the pilot actors and are not in the voice manifest; the file names are a guess until someone listens (see the clip table). A "last seen" call keeps the heading from that look. It does not take a newer heading off the track.

### 3. The player's transmission is a click, and the request is modern English

The line on screen is "All friendlies, report positions" or "Flight, identify nearest contact." The headset hears the click and then the answer. The answer starts with the speaker's callsign, which is the right opening, and then the facts. Nobody says understood, nobody hands the channel back, and the player never hears his own sentence.

The click should stay. It is the sound of the button. An acknowledgement in front of the answer is the next small piece, and only after `roger.ogg` or `copy.ogg` has been heard: the names suggest it, and the German word might be "Viktor", "verstanden", or something else. Speaking the player's own sentence is a larger step. There is no game clip for "report what you see", and the phrase voice is a different speaker from the wingmen. English on screen can stay. The tests and the chat are English; the period flavour belongs in who talks and what they are allowed to say.

Code words are the wrong fix for this gap. "Indianer, drei Uhr" would sound researched and would be the wrong year.

### 4. The calls a net actually carried unasked are the trouble calls

The roadmap already records unasked calls as optional, because the brief asked for no chatter. The transmissions standing orders expected are short and tied to something that just happened: I am under attack, I am hit, I am bailing out. Patrol chat, "follow me", and formation changes are the game's own radio menu. A second program saying them would talk over the game.

The log can support the trouble calls, with a delay. Hits, damage, kills and bail-outs are AType 1, 2, 3 and 18, and they arrive in chunks about every 33 seconds, so a call can be up to about 35 seconds late (VERIFIED, FEASIBILITY.md). The German pilot folder has `rep_underattack`, `rep_imhit`, `rep_wounded`, `rep_falling` and `rep_rtb`. Those names are inferred.

What can be said without inventing:

- A bail-out or a kill is the pilot himself, once. He does not answer a later request. The delay may make it a late echo of something the player already saw. It is worth a flight test before it is kept.
- Damage should be a call when it is heavy. A light hit that produces a sentence every time will become the chatter the brief ruled out.
- Fuel and ammunition stay silent. The log does not give AI fuel or rounds, and the UDP telemetry is the player only (VERIFIED). A "bingo" line would be a guess from endurance, and the honesty rule already forbids presenting a guess as a report.

The same hit is also an eyesight fact: an aircraft that has just shot at the flight has been seen. The roadmap already lists that gap in the visibility model. One event can feed both the spot and, when it is the player or his wingman being hit, the radio call.

### 5. Inside the formation, "nine o'clock" is a long way to say "on your left"

Within 1.5 km the flight says "you are in formation" or gives a clock. The same pilot actors have `toLeft`, `toRight`, `toBack` and `toCourse`, and the manifest does not use them (VERIFIED, file list against `voices/il2-ger.json`). At a few hundred metres those are the words a wingman uses. Clock position remains the right language for a contact, and for a flight seen across a gap.

`ord_rejoin` is the game's own "rejoin" order. Numpad 3 currently answers with a place and a heading. If that clip is actually the rejoin order, it belongs in the answer a separated pilot hears. Listen before mapping it. A clip named `ord_rejoin` might be the leader giving the order, which is the wrong direction for a reply to the player.

"Du bist in Formation", which the phrase voice says for the in-formation token, is an English shape. "Du bist bei uns" is already the other token, and it is the better German. Small, and only worth doing when those lines are next touched.

### 6. Every aircraft, at every height, has the same 40 km and the same hiss

Beyond 40 km a friendly cannot hear the call and does not answer. Hiss rises with the fraction of that distance, over a 300–3000 Hz band, with a squelch burst at each end (`RadioEffect`). For a FuG 7 the 40 km cap is in the neighbourhood of the published "about 50 km" figure, and that figure is one number for good weather. It does not fall when both aircraft are on the deck, and the hiss is quiet when the speaker is close. HF in 1942 was noisy at any useful range. Static, fading and a narrower intelligible band were the character of the set. VHF, later, stays clearer and then stops near the radio horizon, about 3.57 × (√h₁ + √h₂) kilometres with heights in metres (DOCUMENTED, standard horizon approximation; useful voice range sits inside it and depends on power).

A model that stays inside the evidence: keep a hard cap near today's 40 km for these Stalingrad fighters, raise the noise floor so a close FuG 7 is already dirty, and shorten the cap when both ends are low. Skip skywave. A transmission that fades in from a few hundred kilometres would be a trick, not a radio. Give FuG 16 behaviour only to variants that carried it, using the type string the track already has, and only in theatres where those variants flew. Many wartime receivers sat open and hissed until a voice arrived; the game clips already contain that hiss (VERIFIED by measurement, FEASIBILITY.md). The squelch burst is a fair cue that someone keyed. A second, modern noise gate would be the wrong direction.

### 7. A bearing home is a real call, and the two maps are not yet the same map

Separated pilots asked the field for a course back. The current mission has seven airfields, each with a name, a country, a callsign and an IL-2 world position. Tusov is the German field, country 201, callsign 30. `Options.WindLayers` gives a direction-like value and a speed-like value at several heights. `MissionWeather` reads cloud, haze and start time, and does not read the wind (VERIFIED, this mission file and `MissionFile.cs`).

The live player position is the ACMI frame. The airfield is IL-2 world metres. Those frames are never compared, because their origins are not known to coincide (`CoordinateFrame`). A steer toward Tusov computed by treating the numbers as one grid would be a bearing the integration cannot establish. The check that would unlock it is the one FEASIBILITY.md already names: a take-off, where the log's world position and the ACMI position are the same aircraft at the same moment. Until that check passes, the radio should not offer a course to the field.

Wind does not need that check. It is the weather the mission is simulating, and the tower recordings have `wind_n` through `wind_w`, `wind_calm` and `wind_strong` (actor 61, file names). A short wind call is honest. It is also low value once the fight has started, and the game's own tower already talks on the airfield net. The German tower actor's callsign clips are birds 1–20 only. Callsign 30 is a tree in the game's table, so that actor cannot speak this field's callsign (VERIFIED, file list). Taxi and landing clearances are the game's radio; repeating them would be the chatter to avoid.

### 8. A ground controller who reads the mission plan is radar with another voice

Actor 71's file names are a controller's: intercept, in contact, hold in sector, fighters, range, direction, wind, and the storm callsigns the game uses for spotters (41–45). The current mission has no unit on those callsigns (VERIFIED, the callsigns present are aircraft flights and the airfields).

A controller may say only what an observer at a known point could see, under the same visibility model the pilots use. He may not read enemy flights out of `_gen.Mission`. That file is the plan. The honesty rule already stops pilots presenting it as a sighting, and a ground voice is the same rule. On this front in 1942 the picture was eyes, flares and the occasional report, not a Freya commentary. A running plot of the map is the 1944 Reich net, and it needs both the radar and the code book this theatre did not have.

`insector` and `outsector` on the pilot actors are the same kind of temptation. They are honest only if the sector is a briefed area and the aircraft's live position can be tested against it. That test has the same frame problem as the airfield.

### 9. Every friendly is assumed to have a working transmitter

That is reasonable for a 1942 Bf 109, which carried the FuG 7, with finding 1 doing the discipline. It is the wrong default for a Soviet flight of the same summer: the leader transmits, the wingmen receive. The log will not confirm the fit, so this stays a setting, used when the player is on the VVS side. Wingmen who are hit, bailing, or separated are the exception, which is the same exception as on the German side.

### 10. The headset drops the altitude the text got right

When a pilot cannot see the player, the text gives his altitude to the nearest 500 m. The pilot recordings have no altitude figures. The phrase voice is deliberately not asked to say them, so that Piper's share stays the words the clips cannot say at all. Tank-crew clips do have 50–5000 m, in a different voice, and using them would put a gunner in a fighter's mouth.

Leave this until the present Piper inserts have been heard in a flight. The roadmap already has that listen open. If the voice change is acceptable, one rounded altitude phrase is the piece most worth adding, because it is the number a separated wingman would actually pass. Until then the number stays on screen, which is an honest place for it.

### 11. Wording, once the calls themselves are right

"Flight of five" is RAF language. The German group clip is the game's word, and the text can say the number without the RAF noun. "Zwei" against "zwo" waits on hearing `cn_02`. Neither is worth a change ahead of who is allowed to answer.

Sun glare, cockpit blind spots, terrain masking and the mission clock for time of day are already listed on the roadmap. They change which contacts are spoken. They are the eyes, not the procedure, and they are not redesigned here.

## Recordings the radio does not use

VERIFIED as file names under `data\Audio\radio\ger\` on this install, 26 October 2026. What they say is **inferred from the file name**, the same standard as FEASIBILITY.md. German actors 51–54 share the pilot set. Nothing here was played.

| Folder | Files the radio leaves unused | Inferred role |
|---|---|---|
| Pilot `const` | `roger`, `copy` | Acknowledgement |
| Pilot `const` | `rep_underattack`, `rep_imhit`, `rep_wounded`, `rep_falling`, `rep_rtb` | Trouble and going home. `rep_bingofuel` and `rep_outammo` have no data behind them |
| Pilot `const` | `ord_rejoin`, `ord_coverme`, `ord_followme`, `ord_atkair`, the formation orders | Orders. The game menu already speaks these. Easy to duplicate |
| Pilot `const` | `insector`, `outsector` | Sector. Needs a briefed area in the same frame as the track |
| Pilot `varDir` | `toLeft`, `toRight`, `toBack`, `toCourse` | Close-in direction. Clock, high, low and the eight compass points are already mapped |
| Tower actor 61 | `clrtakeoff`, `clrtolnd`, `varWind\*` | Airfield. Wind is in the mission; a bearing to the field is not, until the frames match. Callsign clips are birds 1–20 only |
| Actor 71 | `intercept`, `incontact`, `holdinsec`, storm callsigns 41–45 | A controller voice. No such unit in the current mission, and no licence to read the plan |
| Actors 81–82 | `gun_*` | Gunners, not the fighter net |
| Actors 91–93 | tank `varRange` in metres | The wrong voice for a pilot's altitude |

The longer callsign takes `acs_*` and `acn_*` are still unexplained, as in FEASIBILITY.md. US and British pilot folders add `out` and `over`.

## Order, if any of this is built

1. **Own flight only on the position call.** Other flights stay quiet unless they are the eyes for a contact, or the player's flight cannot answer. Speak one contact picture, not four.
2. **Heading on a contact that is still in sight**, from the fix the call already uses, including on a "last seen" look.
3. **Listen** to `roger`, `copy`, `ord_rejoin`, `toLeft`, `toBack` and `rep_imhit` with `voice-check --country 201 --play` before any of them is mapped. Correct the manifest by ear.
4. **Trouble calls**, sparse: bail-out, destruction, and heavy damage. One flight to judge the 30-second delay. No fuel and no ammunition.
5. **Noisier HF, and a shorter reach at low level**, still with a hard cap. Variant-specific VHF only when that variant is actually in the sortie.
6. **A course to the field** only after a take-off has shown that IL-2 world positions and ACMI positions are the same grid.

Leave out, for this theatre: Reich-defence code words, a controller who reads `_gen.Mission`, enemy transmissions on the German frequency, IFF, and any fuel or ammunition call. The phrase voice's plain German is the right register to keep.

## Sources

- This repository: [ARCHITECTURE.md](ARCHITECTURE.md), [FEASIBILITY.md](FEASIBILITY.md), [ROADMAP.md](ROADMAP.md), and the command and phrase code they describe.
- This machine, 26 October 2026: `data\Audio\radio\ger\` file names; `data\Missions\_gen.Mission` (`Date = 24.8.1942`, airfields, callsigns, `WindLayers`). Clip meanings were not checked by ear.
- [Luftwaffe radio equipment of World War II](https://en.wikipedia.org/wiki/Luftwaffe_radio_equipment_(Funkger%C3%A4t)_of_World_War_II), for FuG 7 and FuG 16 dates, bands and the secondary "about 50 km" figure.
- [Gyges, Luftwaffe operational brevity code](https://www.gyges.dk/Operatonal%20brevity%20code%20Ver%2010_1.pdf), a secondary compilation of fighter-control codes.
- [OpenFlightSchool, WWII Deutsch](https://www.openflightschool.de/mod/book/view.php?chapterid=284&id=259&lang=de), a sim-school page, not a wartime manual.
- German-Russian archival catalogue, [20 May 1942 Luftwaffe signals brochure](https://wwii.germandocsinrussia.org/ru/nodes/2634-delo-293-rukovodstvo-po-osnovam-tehniki-radiosvyazi-v-vvs), title only.
