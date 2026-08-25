# HDM

> **Formerly `HGuise`** — renamed at 0.8.54 to the planned DM / mob-spawn module name.
> The command is now `/hdm` (`/hguise` still works as a hidden alias); the built plugin
> is `HDM.dll` with Dalamud `InternalName` `HDM`. To migrate an existing dev install:
> re-point your Dev Plugin Location at `bin\Debug\HDM.dll`, and copy
> `%AppData%\XIVLauncher\pluginConfigs\HGuise.json` → `HDM.json` to keep your favourites.

Client-side mob disguises with a searchable catalog and animation triggers.
Apply **any** NPC's model to yourself or your target — monster, demihuman, or
human — trigger its action-timeline theatrics, resize it, and revert cleanly.
Built for DM/event use: "grab the face and wear it." Everything is client-side —
nothing is sent to the server, nobody else sees it.

Sister plugin to **HOutfits** (which owns gear/NPC glamour). HDM owns the
*whole-NPC model* surface. Command: `/hdm`.

## Status

**Builds clean (Release, 0/0). All three model families render.** The
Monster path is verified in-game; the Demihuman and Human paths are freshly
implemented and pending the in-game test pass (see [Testing](#testing)). Every
native mutation reverts on revert / zone-change / plugin-unload.

## What it does

- **Catalog** of every mob in the game — 16,243 BNpcBase rows, 15,526 named —
  searchable, favouritable, filterable by model family.
- **Disguise** self or current target with one click.
- **Scale** the disguise: off / the mob's authored native size / a custom value.
- **Animations** tab: play a timeline once, loop it (without locking the actor),
  set playback speed, and a red "unstick" button that always returns the actor to
  a clean idle.
- **Revert** and full cleanup on unload — the plugin never leaves mutated state
  behind.

## The three render paths (the core problem, and the solution)

The render outcome splits **exactly** on `ModelChara.Type` (the catalog's
`McType`). A bare `ModelCharaId` swap only works for one of the three families;
the other two each need something extra. HDM now does all three itself,
self-contained:

| Family | `McType` / skel | Renders from | How HDM does it |
|---|---|---|---|
| **Monster** | 3 / `mNNNN` | a bare model swap — self-contained | `Character.ModelContainer.ModelCharaId = id` + redraw. |
| **Demihuman** | 2 / `dNNNN` | model swap **+ an equipment set** (the d-skeleton is invisible alone) | swap, then write the NPC's `BNpcBase.NpcEquip` set into `DrawData.EquipmentModelIds` before the redraw, so the rebuilt body picks it up in one cycle. |
| **Human** | 1 / `cNNNN` | customize (face/body) **+ gear** — a swap alone leaves a T-posed default body, and a direct customize write is scrubbed by the game every redraw | **not a model swap.** Paint it through **Glamourer** `ApplyState`: the NPC's customize block + its gear as `CustomItemId`s. |

Why the split matters: the c-body is the player's own skeleton, so "swapping" to
it does nothing useful — what makes it *look* like the NPC is customize + gear,
and that surface belongs to Glamourer (the game's `FilterCustomizeData` strips
NPC-only customize off a player actor on every redraw, so a direct write can't
stick). Monster and Demihuman, by contrast, are genuinely different skeletons and
must be swapped.

### Redraw

`DisableDraw()` → wait ≥2 framework ticks for teardown, then poll
`IsReadyToDraw()` (cap 60 ticks) → `EnableDraw()`. This is Brio's
`ActorRedrawService` recipe (Disable → DrawWhenReady → Enable); the poll lets a
heavy equipped demihuman finish loading before it's shown, instead of a fixed
delay that could re-enable an unready model. The Human path doesn't use this —
Glamourer's `ApplyState` triggers its own redraw.

### Animations

- **One-shot:** `TimelineSequencer.PlayTimeline(id)` — blends once, game blends
  back. Never touches Mode.
- **Loop:** `Timeline.BaseOverride = id` while **staying in Normal mode** (not
  AnimLock). BaseOverride forces the base animation in Normal or AnimLock, so the
  actor keeps full agency — it can still move and log out. Trade-off: the override
  may drop if the actor moves; that self-healing miss is far preferable to a
  locked character.
- **Speed:** best-effort `Timeline.OverallSpeed` (no pinning hook in v1).
- **`Sanitize` (unstick):** clears BaseOverride, resets speed, forces
  `CharacterModes.Normal`, blends timeline 3 back to idle. Tracking-independent,
  so it recovers a character stuck by an earlier build too. Every stop / revert /
  dispose / zone-change routes through it.

## Architecture

```
Plugin.cs            entry point, DI wiring, config migration, /hdm command
MainWindow.cs        ImGui catalog + animations UI; routes Apply/Revert by family
├─ MobIndex.cs       loads Data/mob-model-index.csv → MobRow list (the catalog)
├─ TimelineIndex.cs  loads Data/timeline-index.csv → animation-name lookup
├─ NpcData.cs        live Lumina reads keyed by BNpcBase id:
│                      TryGetEquipment → NpcEquip set (Demihuman body + Human gear)
│                      TryGetCustomize → 26-byte BNpcCustomize array (Human face/body)
├─ GuiseService.cs   Monster + Demihuman: ModelCharaId + scale + equipment write,
│                      redraw state machine, per-index revert tracking
├─ HumanGuise.cs     Human: builds a Glamourer state (CustomItemIds + customize)
│                      and applies it via ApplyState; tracks + reverts its own
│  └─ GlamourerIpc.cs   thin Glamourer IPC wrapper (Available/GetState/ApplyState/Revert)
└─ AnimationService.cs  timeline play/loop/speed + the Sanitize unstick
Configuration.cs     persisted settings (filter, scale mode, favourites)
```

**Apply routing** (`MainWindow.ApplyGuise`): Human rows go to `HumanGuise`
(Glamourer); Monster/Demihuman rows go to `GuiseService` (model swap). Either way
the *other* family's guise is cleared first, so switching families never leaves a
monster skeleton wearing a Glamourer face. Revert calls both (each no-ops if it
didn't guise the actor) after `Sanitize`.

## Product & design choices

- **DM/event tool, not a mechanics sim.** It renders a face and plays an
  animation. No hitboxes, no server state, no "become the boss and fight."
- **Self-contained, no dependency on HOutfits.** The Human path duplicates
  HOutfits' Glamourer logic (CustomItemId encode, the 36-key customize map) rather
  than depending on it. Deliberate: HOutfits has a slightly different job (gear
  glamour) and exposes no IPC, and a little duplication is cheaper than coupling
  two plugins' release cycles. The one hard dependency is **Glamourer** itself,
  and only for the Human path.
- **Graceful degradation.** If Glamourer isn't installed, Human rows log-and-no-op
  and the UI shows an amber "needs Glamourer" note; Monster/Demihuman are
  unaffected (zero third-party dependency — they go through FFXIVClientStructs,
  which Dalamud provides).
- **Separate plugin from HOutfits on purpose.** Timeline/redraw writes on
  non-human CharacterBases are the CTD-prone surface; isolating them limits blast
  radius, and the boundary maps 1:1 onto a future **DMS** module's IPC
  (`Apply(objectIndex, modelCharaId, scale)`, `PlayTimeline`, `Revert`).
- **Catalog filter is a convenience, not a gate.** It used to default to
  Monster-only because c/d couldn't render; now that they can, the default is
  **All** (a v2→v3 config migration flips the old default). Narrow to
  Monster/Human/Demihuman from the combo if you want.
- **Client-side only, forever.** No packets, no persistent character writes. The
  eventual HMS sync atom is `(modelCharaId, scale)` (+ optionally a looping
  timeline id) — appearance-only, opt-in, and not wired yet.

## Build & install

`Dalamud.NET.Sdk/15.0.0`, `net10.0-windows`, x64. `dotnet build -c Release`.
Load `bin\Release\HDM.dll` via Dalamud's Dev Plugin Locations, or install the
packaged `bin\Release\HDM\latest.zip`.

**Glamourer.Api** is the one NuGet package (2.x). Its DLL ships next to
`HDM.dll` (each plugin loads in its own AssemblyLoadContext and can't inherit
Glamourer's copy; IPC crosses the boundary by string label, so the two copies
never clash). FFXIVClientStructs / Lumina / ImGui come from Dalamud — never
bundle those.

## Data

`Data/mob-model-index.csv` — 16,243 BNpcBase rows with models, 15,526 named.
Generated offline (`xivtool bnpc index`). Columns:
`BaseId,NameId,Name,ModelCharaId,McType,McModel,McBase,McVariant,Scale`
(`McType` 1=Human `c`, 2=Demihuman `d`, 3=Monster `m`). Reference row: Galatea
Magna = Base 14705, ModelChara **3723**, m0333, scale 2.0.

`Data/timeline-index.csv` — 2,145 action-timeline rows (461 common + 1,684
monster specials across 201 skeletons) → names for the Animations tab.

Regenerate both per game patch.

## Testing

Ready-made target lists live in `_testing/` (generated from the catalog):

- `_testing/human-c-entities.md` — 396 named Human NPCs (80 distinct models).
- `_testing/demihuman-d-entities.md` — 1,578 named Demihuman NPCs (168 distinct
  models).

Each file has a **quick-test set** (one base per distinct model) plus the full
named list, with `BaseId / Name / ModelChara / Skel / Scale`. Good starting
targets: **2B** (base 12712, Human `c0201`), **9S**, Alphinaud, Alisaie;
**Adamant Weapon** (base 18071, Demihuman `d1068`), Abharamu, Aegaeon of the Bone.

Test checklist per family: apply → redraw → revert on **self**; then on **target**;
then plugin-unload mid-guise (must revert); then zone-change mid-guise (must clear
cleanly). For Human, confirm Glamourer paints customize + gear; for Demihuman,
confirm the body isn't blank.

## Open risks & roadmap

1. **Demihuman on a player actor.** If the NPC equipment doesn't stick on a
   *player* (as opposed to a real NPC), the fix is to hook
   `EnforceKindRestrictions` (Brio's ApplyNPCHack). Deferred until the test says
   it's needed.
2. **Human scale.** Glamourer doesn't scale; the Human path is appearance-only for
   now. A `GameObject.Scale` write could be added if wanted.
3. **Weapons.** Both NPC paths exclude weapons (separate draw object; Glamourer may
   refuse a class-mismatched weapon). Opt-in experimental path possible later.
4. **Speed pinning.** If the best-effort `OverallSpeed` write gets stomped, hook
   `CalculateAndApplyOverallSpeed` (Brio's `ActionTimelineService`).
5. **Animation-name dropdown.** The per-skeleton timeline catalog exists; pruning
   the blank/single-frame entries into a clean dropdown is queued.
6. **IPC for DMS/HMS** (later): `Apply(objectIndex, modelCharaId, scale)`,
   `PlayTimeline(objectIndex, id)`, `Revert(objectIndex)`.
7. **Runtime mob→territory harvester** (deferred, spec'd): passively log live
   BattleNpcs' `BaseId → TerritoryType + name` as the DM plays content, to recover
   the *instanced* roster (dungeon/trial/raid — e.g. Chort) that no offline source
   has, and the 717 unnamed names, patch-proof. The overworld slice already ships
   (`Data/mob-territory-index.csv`); this fills the rest.

## Safety envelope (do not regress)

- **Framework thread only** for every native write. UI Draw runs there;
  GuiseService's redraw state machine rides `IFramework.Update`.
- **Never DisableDraw + EnableDraw in the same tick** — teardown must settle.
  Never leave an object hidden on unload.
- **Originals tracked per object index, restored on revert and Dispose.** Plugin
  unload leaves zero mutated game state.
- **Territory change / logout: clear tracking, never write back** — the object
  table is rebuilt, pointers are stale, and server respawn restores truth. (The
  animation service additionally cleans the *local* player with raw field writes,
  since it persists across the transition.)

## Constraints

- Client-side rendering only. No server-visible state, no packets.
- HMS integration is over IPC only; HDM runs standalone and never depends on
  HMS at runtime.

License: AGPL-3.0.
