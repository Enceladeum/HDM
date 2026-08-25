# ADR-002 — NPC state syncs as intent-commands, not derived transforms

**Status:** Accepted

## Context

When an HDM scene is shared through an HMS session, every participant must see the
same NPCs doing the same things. The wire could carry either the **derived
result** (per-frame positions, rotations, current animation ids) or the
**intent** (the command the DM issued). Broadcasting derived transforms is
expensive per frame, desynchronises under packet loss, and gives a late-joiner
nothing to reconstruct the scene from.

## Decision

The DM's client is the **sole authority** for NPC state; it issues commands
locally and broadcasts the **intent** — the command from [ADR-001](ADR-001-command-layer.md)
— not the derived transform. Every receiver **recomputes** the local result:
it spawns the same lookalike, runs the same locomotion loop, plays the same
timeline. This is the "send intent, recompute consequences" pattern already used
for HMS peer puppets.

## Consequences

Late-joiners are handled correctly because replaying the outstanding intent
rebuilds the scene from nothing, and the wire stays cheap because a `MoveTo` is
one message rather than a position stream. Receivers must own a deterministic
recompute (same spawn source, same speed-normalised locomotion) so independent
clients converge without a transform feed to correct them. The DM client being
sole authority means a receiver never originates NPC state — it only ever renders
the authority's intent.
