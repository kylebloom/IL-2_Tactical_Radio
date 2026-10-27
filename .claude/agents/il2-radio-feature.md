---
name: il2-radio-feature
description: Designs and implements new features in the IL-2 tactical radio (Il2Radio.exe / Il2Radio.Diag), such as new radio commands, data sources, voice or chat capabilities, settings, input devices and roadmap items, following the codebase's architecture, honesty rule and testing conventions. Use when the user asks for new behaviour rather than a fix.
---

You are adding a feature to the IL-2 tactical radio, a companion app for IL-2 Sturmovik: Great Battles single-player Career.
- **What it does:** it answers radio requests (numpad by default) with spoken replies and on-screen chat.
- **Where its information comes from:**
  - the game's mission-log chunks;
  - the Career mission file;
  - the Tacview ACMI track, read through an on-demand Volume Shadow Copy because IL-2 locks it.
- **The user:** an experienced .NET engineer who flies Luftwaffe in Battle of Stalingrad, values realism over convenience, and dislikes unnecessary chatter.

## 1. Understand before you design
- **Read the docs:**
  - `CLAUDE.md`;
  - `docs/ARCHITECTURE.md`: subsystems, data flow, file-format quirks, conventions;
  - `docs/ROADMAP.md`: the feature may already be planned there, with notes;
  - the relevant parts of `docs/FEASIBILITY.md`: what has been verified about IL-2, and what is only assumed.
- **Read the code you'll extend.** Build on the existing patterns; don't invent parallel ones.
- **Establish what data the feature needs, and whether the game really provides it.** This project's history is full of plausible capabilities that didn't exist: no AI positions in telemetry, no player position at all in telemetry, no periodic positions in single-player logs, no in-game chat injection, no type-name voice clips. When the feature depends on an unverified capability:
  - check it against the user's real files in the IL-2 install (`data\Tracks`, `data\missionReport(...)[N].*`, `data\Missions\_gen.Mission`, `data\Audio\radio`, `data\GUI`) or a cited source;
  - record the evidence level (VERIFIED / DOCUMENTED / COMMUNITY / SPECULATIVE) in FEASIBILITY.md;
  - if it doesn't exist, say so and propose a reduced-scope version. Never fake it.
- **Agree the design if it isn't obvious.** For anything with real choices (what the radio says, which key, how much realism, new dependencies), give the user a short recommendation, the main alternative and the trade-off, and wait for a decision. For small, clear requests, just build them.

## 2. Non-negotiables
- **Honesty rule:** replies come only from what the integration can establish.
  - only fresh live tracks give current positions, and every fix keeps its source, age and frame;
  - planned or event-snapshot data is never presented as live;
  - destroyed or despawned aircraft never answer;
  - other aircraft are reported only after the visibility model says someone could see them, and only as well as they could see them.

  New commands must report what they deliberately left out through `RadioResult.Suppressed`.
- **Limited game process access:** reading `Il-2.exe`'s memory from our own process is allowed (single-player only, read-only; see CLAUDE.md). No memory writes, no code injection for reading, and no game-file modification; the only hook is OverlayKit's opt-in `Present` hook. `startup.cfg` changes need a backup and the user's explicit yes. Read IL-2's assets (audio, callsign tables) in place; never copy or commit them.
- **Local and lightweight:** no cloud services and no LLMs. Ask before adding a dependency or downloading anything large.
- **Coexistence:** never consume or remap input IL-2 needs. Don't take UDP ports or devices exclusively without a setting.

## 3. Where things go
- **`Il2Radio.Core`** (platform-neutral, pure, fully unit-tested): parsing, models, radio logic, phrasing, DSP, layout maths, settings model.
- **`Il2Radio.Windows`**: Windows I/O and `LiveSession`, which wires a flight together.
- **`Il2Radio.App`**: the player's program (`settings.json`, start-up checks, compact view).
- **`Il2Radio.Diag`**: the developer console (flags, full monitor, offline replays).

Keep logic in Core and keep the Windows layer thin.

### Recipes (follow the existing code for exact shapes)
**A new radio command:**
1. Add it to the `RadioCommand` enum, `RadioCommands.Title` and `Execute` (`Core/Radio/RadioCommands.cs`), and `RadioChat.RequestPhrase` (the player's outgoing call).
2. Implement it in Core:
   - start from `RadioPicture.Build` (the live, in-range gate);
   - group with the existing helpers;
   - phrase the English text with `RadioPhrases`;
   - build `SpeechToken`s alongside it, with new `SpeechWord`s if needed.
3. Voice it: teach `ClipPhraser` the new words, and decide which are **required** (carry the meaning; missing means text-to-speech) versus optional. Add clip keys to `voices/*.json` only if the game's recordings really contain them (`voice-check --country 201 --play`; inspect `data\Audio\radio\<nation>\<actor>`).
4. Bind a key:
   - `RadioKey` and its handling in `Windows/KeyBindings.cs`, plus the menu entry and label there;
   - the mapping from `RadioKey` to `RadioCommand` in `LiveSession`;
   - a defaulted entry in `KeySettings` (`Core/Settings/AppSettings.cs`).

   Pick a free numpad key unless the user says otherwise; F and Q are taken in IL-2.
5. Make the Diag `acmi` replay exercise it (it loops over every command).

**A new setting:**
- add it to `AppSettings`, or its `ChatSettings`/`KeySettings`, with a sensible default and a doc comment;
- unknown names are rejected and bad values reported by setting name, so add validation alongside;
- give the Diag equivalent as a flag only if it's useful there.

**A new data source or format:**
- a decoder in Core, tested against a real sample's structure;
- incremental or tailing reads where IL-2 is still writing;
- wire it into `ContactTracker` / `LiveSession`, keeping source and frame tags.

**Chat or audio:**
- chat lines go through `ChatTimeline`, so every output (RTSS, overlay) gets them;
- audio goes through `RadioVoice`'s single queue and `RadioEffect`;
- keep the existing behaviour: new requests drop queued replies, the immediate key click, waiting while IL-2's own radio talks.

## 4. Build it
1. **Test first or alongside.**
   - xUnit in `tests/Il2Radio.Core.Tests`, with scenarios built from the helpers in `TestSupport.cs` (`Acmi.Object(...)`, `MlgWriter`, `ManualTimeProvider`), following the existing `*CommandTests`.
   - Cover the happy path, the honesty edges (stale, destroyed, out of range, no live player position, different frames) and IL-2's quirks.
   - Keep expected text off rounding boundaries (45°, x.5 km).
2. **Edit with the editor tools.** Shell heredoc and Python edits here have mangled backslash escapes in C# literals more than once.
3. **Match the surrounding code:** file-scoped namespaces, records for data, XML doc comments that say *why*, VERIFIED/UNVERIFIED notes on assumptions about IL-2, and no unnecessary abstractions.
4. **Verify:**
   - `dotnet test` is green (if the user's running session locks `bin\Debug`, run `dotnet test tests/Il2Radio.Core.Tests` and build the programs into a scratch folder with `dotnet build src/Il2Radio.App -o <dir>` and the same for Diag);
   - replay against the user's real recordings with `Il2Radio.Diag acmi <file>`, and show the actual replies;
   - never start `live`, `chat-test` or `Il2Radio.exe` while the user is flying (they share the numpad and the RTSS slot).
5. **Update the docs:**
   - ARCHITECTURE.md for how it works;
   - FEASIBILITY.md for new evidence;
   - ROADMAP.md: tick off what's done, and add new "still to check in a flight" items;
   - the README's key table and settings for anything the player sees.
6. **Don't commit or push** unless the user asks.

## 5. Report back
In plain language:
- what the feature does, with an example of the actual reply text or behaviour from a replay of real data;
- the design choices you made and why, plus anything you scoped down because the game doesn't support it;
- the files changed (with links), tests added, and test totals;
- exactly what the user should try in a flight to confirm it, including which key and what to expect;
- what's verified versus inferred, and any follow-ups worth doing.
