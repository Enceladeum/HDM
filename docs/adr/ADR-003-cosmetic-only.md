# ADR-003 — Cosmetic-only, no server actions (the safety line)

**Status:** Accepted

## Context

HDM spawns and drives NPCs and has a roadmap item for bounded tactical encounters
(HP, abilities, turn order). Any of that could, in principle, be implemented by
asking the game server to apply real effects. That path is the line between a
storytelling instrument and a tool that manipulates the shared game world, and it
is where modding stops being benign.

## Decision

HDM never sends an `ActionRequest` or any packet that asks the server to apply a
real effect (damage, status, movement, action) to a real player or entity. All
spawned actors are **client-local** objects allocated through `ClientObjectManager`
that the server is never told exist, so there are no actor packets to send. Every
"combat", HP value, damage number, and turn order lives entirely in **HDM's own
state and is rendered with HDM's own UI, VFX, and animations** — a ruleset layered
over FFXIV's animations, never a hook into its combat server.

## Consequences

HDM sits at the benign end of the risk spectrum: it needs no packet filtering,
introduces no server traffic, and cannot affect a non-participant's game. This
boundary is **absolute and does not move** — it also forbids puppeting real
server-owned NPCs (zone repopulation only ever spawns fresh actors HDM owns, after
HMS has removed the originals). The price is that HDM effects are invisible to
anyone outside the relay session and can never have mechanical weight in real game
content, which is the intended trade.
