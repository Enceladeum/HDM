# ADR-001 — Command layer as the single control abstraction

**Status:** Accepted

## Context

HDM offers several ways to drive an NPC: **Possession** (the DM hides their body
and steers one NPC directly), **Command / RTS** (a party-style heads-up that
issues discrete orders to any selected unit), and **relay-sync** (orders arriving
from the DM's client over HMS). A naive design would build each as its own
codebase, which triples the surface area for spawn, locomotion, animation, and
teardown bugs and guarantees the three modes drift apart.

## Decision

An NPC is modelled as a **driven actor** with no native input loop, controlled
solely by consuming a small **command set** per tick: `Spawn`, `Despawn`,
`MoveTo` / `Stop`, `FacePoint` / `SetRotation`, `SetTransform`, `PlayEmote` /
`HoldAnimationAt`, `PlayActionTimeline`, `SetWeaponDrawn`, `Say` (extensible).
Possession, RTS, and relay-received input are **front-ends that all produce the
same commands**; nothing drives an NPC except by emitting a command into this one
layer.

## Consequences

Every feature in the scope fence becomes a thin producer or consumer of one
abstraction, so a fix to locomotion or teardown lands once for all three modes.
New capabilities (`SetVfx`, `Tether`, path patrols) are added as commands, not as
new control paths. The cost is discipline: no mode may reach past the command
layer to mutate actor state directly, or the single-authority and sync guarantees
in [ADR-002](ADR-002-intent-sync.md) break.
