using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using CSCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using ClientObjectManager = FFXIVClientStructs.FFXIV.Client.Game.Object.ClientObjectManager;
using ObjectKind = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind;
using BattleNpcSubKind = FFXIVClientStructs.FFXIV.Client.Game.Object.BattleNpcSubKind;
using ObjectTargetableFlags = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectTargetableFlags;
using CopyFlags = FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterSetupContainer.CopyFlags;

namespace HDM;

/// <summary>
/// The first time HDM touches an actor that ISN'T the local player: spawn a standalone puppet the DM
/// can disguise and place as a set-piece (a behemoth looming over the party, an imp on a ledge). Until
/// now HDM was strictly self-apply; this is the "spawn actors and disguise them" seed.
///
/// The native spawn primitive is FFXIVClientStructs' <see cref="ClientObjectManager"/> —
/// <c>CreateBattleCharacter</c> creates the actor; <c>DeleteObjectByIndex</c> takes it back. This is the
/// exact, battle-tested path Brio (ActorSpawnService) and ARealmRepopulated (NpcServices) use; we
/// deliberately re-implement it native rather than take a Brio dependency, matching HDM's self-contained
/// ethos (see the project note on duplication-over-coupling).
///
/// <b>TWO INDEX SPACES — the trap that broke the first in-game spawn (0.8.41).</b> ClientObjectManager
/// keeps its OWN small table of the objects IT created; <c>CreateBattleCharacter</c> returns a <i>COM
/// index</i> into that table (0, 1, 2, …), and <c>GetObjectByIndex</c>/<c>DeleteObjectByIndex</c>/
/// <c>GetIndexByObject</c> all speak that COM index. But the puppet's <i>global object-table index</i>
/// — what Dalamud's <see cref="IObjectTable"/>, <c>actor.ObjectIndex</c>, and every objectIndex-keyed
/// service (<see cref="GuiseService"/>/<see cref="HumanGuise"/>) use — is DIFFERENT: a spawned BattleNpc
/// lands in the GPose/cutscene reserved range (~200–244; COM #1 was observed as global #201). So:
///  - <b>Track the GLOBAL index</b> (<c>actor.ObjectIndex</c>) — that is the key the guise pipeline and
///    the UI share. <see cref="_spawned"/> holds global indices.
///  - <b>Delete via the COM index</b>, resolved fresh at despawn from the live object with
///    <c>com-&gt;GetIndexByObject(gameObject)</c> (Brio's DestroyObject pattern) — never store a COM index
///    and reuse it later; the fresh lookup is staleness-proof.
///  - Conflating the two (the 0.8.41 bug) produced a false "returned the local player's index (0)" refusal
///    — COM #0 is a valid slot, not the player — and a false "index mismatch (tracked 1, actor reports
///    201)" abort. Both guards were removed; the surviving player-safety guards below compare GLOBAL to
///    GLOBAL, which is meaningful.
///
/// Render division of labour: this service brings a puppet into the world as a CLONE of the local player
/// (classify → position → clone → draw-when-ready), then the CALLER applies a guise through
/// <see cref="GuiseService"/> / <see cref="HumanGuise"/> — the SAME objectIndex-keyed routines that
/// disguise the local player, just pointed at the puppet (Principle 1: a spawned actor is a puppet the
/// game does NOT pilot; every bit of its appearance is driven, exactly what those services already do).
/// No second appearance path.
///
/// <b>Why clone the player instead of <c>SetupBNpc(0)</c> (the 0.8.42 bug: "c_ on a dummy spawns only the
/// blank").</b> A bare <c>SetupBNpc(0)</c> BattleNpc is not a drawable human and Glamourer never registers
/// it, so the Human (c-skeleton) guise — which paints through Glamourer's ApplyState — silently no-ops on
/// it (<see cref="HumanGuise"/> bails when <c>GetState</c> is null), and a blank puppet renders invisible.
/// Brio's fix (ActorSpawnService.CloneCharacter) is to seed the puppet from the LOCAL PLAYER with a DOUBLE
/// <c>CharacterSetup.CopyFromCharacter</c> (source→new, then new→new with <c>CopyFlags.None</c> — "needed
/// for Penumbra/Glamourer"): the puppet is now a real drawn human that Glamourer sees, so ApplyState lands.
/// The Monster/Demihuman paths are unaffected — their <c>ModelCharaId</c> swap + redraw is ObjectKind-
/// agnostic (it's the exact write that disguises the local player, who is a Pc) and simply rebuilds the
/// clone into the mob model. A blank puppet (no guise) now shows a visible copy of the DM instead of nothing.
///
/// <b>A drawn human still needs a VALID GLAMOURER IDENTITY, or the Human guise can't paint it (the 0.8.44
/// bug: "spawning M'naago clones the DM, not the NPC").</b> Cloning makes the puppet drawable, but Glamourer
/// keys its state on an ActorIdentifier that Penumbra.GameData builds in <c>ActorIdentifierFactory.FromObject</c>
/// — reached from the GetState/ApplyState IPC via <c>Actor.GetIdentifier</c> (<c>allowPlayerNpc: true</c>). For
/// a BattleNpc with <c>NameId == 0</c> and an EMPTY name that factory returns <c>CreateNpc(BattleNpc, 0)</c> →
/// <c>VerifyNpcData</c> fails (there is no BNpc id 0) → <b>Invalid</b> → <c>GetState</c> returns null →
/// <see cref="HumanGuise"/> bails and the puppet stays a bare DM-clone. Its rescue branch, though, identifies a
/// <c>NameId</c>-0 BattleNpc that has a NON-EMPTY, SE-valid player NAME as a PLAYER (name + home world). So
/// <see cref="TrySpawn"/> stamps every puppet with <c>NameId</c> 0, the DM's (valid) home world, and a UNIQUE
/// "Forename Surname" name — a Player identifier is (name, world) with NO object index, so the name must differ
/// per puppet and never equal the DM, or two actors alias onto one Glamourer state. Brio proves the stamped
/// name persists past draw, so one stamp at spawn covers both the spawn-time guise and any later UI apply.
///
/// <b>Draw-when-ready (why the guise apply is DEFERRED, in TWO phases).</b> A freshly-cloned actor isn't
/// drawable the same frame, and — critically — Glamourer returns valid state only once the body is actually
/// VISIBLE, not merely once draw is enabled. So we mirror Brio's two-step redraw settle exactly:
///  1. poll <c>IsReadyToDraw()</c> for a couple of warmup ticks, then <c>EnableDraw()</c> (Brio's
///     ActorRedrawService.DrawWhenReady);
///  2. poll the draw object until <c>DrawObject != null &amp;&amp; DrawObject-&gt;IsVisible</c> (Brio's
///     WaitForDrawing) — and only THEN fire the caller's <c>onReady</c> continuation (the guise apply).
/// Firing after phase 1 alone lands a Human paint on a not-yet-visible body, where <c>GetState</c> can still
/// return null — so waiting for VISIBLE is the complement to the identity stamp above (the identity is what
/// lets Glamourer register the puppet at all; visibility is what makes its customize read back complete). The poll rides one
/// persistent <see cref="OnUpdate"/> handler (like GuiseService's redraw jobs), re-resolving the object each
/// tick so it never holds a stale pointer, and aborts if the puppet is despawned mid-poll.
///
/// Teardown discipline (the CTD-sensitive half — spawning is easy, leaking indexes is what crashes):
///  - Track EVERY created puppet by its global index; delete exactly that set. Never guess.
///  - <b>TerritoryChanged → drop tracking, do NOT delete.</b> A zone change frees every spawned actor
///    with the old zone and the new zone recycles those indexes; deleting a now-recycled index would nuke
///    whatever the new zone put there. This is Brio's most important guard for a synthetic-zone workflow.
///  - Logout → same drop. Dispose → delete all still-live puppets so a plugin unload leaves nothing.
///  - On despawn, FORGET the puppet in GuiseService/HumanGuise first (drop their per-index tracking with
///    no native calls) so a recycled index can't inherit a stale draw-offset / hide / redraw job.
///
/// Everything here runs on the framework thread (UI Draw / command dispatch), like the rest of HDM.
/// </summary>
public sealed unsafe class SpawnService : IDisposable
{
    private readonly IFramework _framework;
    private readonly IObjectTable _objects;
    private readonly IClientState _clientState;
    private readonly GuiseService _guise;
    private readonly HumanGuise _humanGuise;
    private readonly AnimationService _anim;
    private readonly IPluginLog _log;

    // In-flight draw-when-ready polls: a freshly-cloned puppet becomes drawable a few ticks after spawn,
    // and its guise must not be applied until then (Glamourer only registers a DRAWN human — see the
    // class note). One job per pending puppet; processed by OnUpdate on the framework thread. A List (not
    // per-spawn Update subscription) because unsubscribing a per-spawn local-function delegate is the
    // classic "-= makes a different delegate instance, leak the handler" trap; one persistent handler
    // draining a job list — GuiseService's proven redraw-job pattern — sidesteps it entirely.
    // DrawEnabled tracks the two-phase handoff: false = still waiting for IsReadyToDraw (phase 1), true =
    // draw enabled, now waiting for DrawObject->IsVisible before firing onReady (phase 2).
    private sealed class ReadyJob
    {
        public ushort GlobalIndex;
        public int Ticks;
        public bool DrawEnabled;
        public Action<ICharacter>? OnReady;
        // A position/rotation requested BEFORE the puppet is draw-ready (e.g. a peer's mirror gets its single
        // HMS-relayed MovePuppet the frame after spawn, long before the draw object exists). That early write
        // races the not-yet-built object and is lost ~1/3 of the time, stranding a STATIC mirror at the spawn
        // spot (a step in front of the RECEIVER) with no later correction. Stash the last such request here and
        // re-apply it once the puppet is confirmed drawn+visible (see OnUpdate) so the placement always lands.
        public Vector3? PendingPos;
        public float? PendingRot;
    }
    private readonly List<ReadyJob> _readyJobs = [];

    // Two-phase draw settle (Brio's DrawWhenReady + WaitForDrawing). Warm up a couple ticks (Brio's
    // dontStartFor:2 — the cloned draw object must settle), then poll IsReadyToDraw and EnableDraw; THEN poll
    // the draw object until it is actually VISIBLE before firing the guise apply. Phase 2 is the fix for
    // "spawning c_ mobs shows the player-clone": Glamourer returns valid state only for a drawn, visible
    // body, so a Human guise applied at EnableDraw time (one phase early) no-ops and the clone stays a copy
    // of the DM. The cap spans BOTH phases; a puppet that never settles still gets applied rather than
    // stranded forever (200 ≈ 3.3s at 60fps — a clone is normally ready+visible within a handful of ticks).
    private const int ReadyWarmupTicks = 2;
    private const int MaxReadyTicks = 200;

    // GLOBAL object-table indices of the puppets WE created — the key the guise pipeline and UI share, and
    // the tracked set teardown deletes exactly. A List (not a HashSet) so the UI lists them in spawn order;
    // membership tests are cheap at these sizes. NOTE: these are NOT the COM indices CreateBattleCharacter
    // returns (see the two-index-spaces note above) — the COM index is resolved fresh at despawn.
    private readonly List<ushort> _spawned = [];

    // GLOBAL object index → the puppet's STABLE per-source SLOT (its mint-time _nameSerial). The slot is the
    // cross-client identity HMS namespaces under the DM's ContentId ("<cid>:<slot>") to sync a synthetic
    // puppet that has no server identity of its own (HDM-sync contract §B4). Unlike the global index — which
    // the zone recycles — the slot is drawn from the never-reset _nameSerial, so it is unique for the whole
    // session and safe to key a peer's puppet on. Populated at spawn, dropped on every _spawned removal.
    private readonly Dictionary<ushort, int> _slotByIndex = new();

    // GLOBAL object indices of the puppets THIS client ORIGINATED — spawned as a local DM action, NOT the
    // mirrors HdmIpc reproduces from a peer's broadcast (those TrySpawn with originated:false). A subset of
    // _spawned, kept in lockstep with it (added on an originated spawn, dropped on every _spawned removal). It's
    // the possession-ownership gate: on the DM's client their puppet is originated and possessable; on every
    // OTHER client the same puppet is a mirror (absent here), so by default only its originator can drive it.
    private readonly HashSet<ushort> _originated = [];

    // Lifecycle signals for the IPC layer (HdmIpc subscribes). Kept as plain C# events so SpawnService stays
    // self-contained — it never references the provider. PuppetReadyEvent fires once a puppet is drawn+guised
    // (the onReady settle completed); PuppetRemovedEvent fires on EVERY path that drops a tracked puppet —
    // explicit despawn, zone change, logout, dispose — carrying the slot so the provider can emit the
    // matching "peer's puppet is gone" without a second lookup.
    public event Action<ushort>? PuppetReadyEvent;
    public event Action<ushort, int>? PuppetRemovedEvent;

    // Monotonic serial → each puppet's UNIQUE, SE-valid Glamourer name (see NextPuppetName / the identity
    // stamp in TrySpawn). NEVER reset — not on despawn, not on zone change: a Glamourer Player identifier is
    // (name, homeWorld) with NO object index, so two concurrently-live puppets sharing a name would alias
    // onto one Glamourer state. Monotonic guarantees distinct names for the session (wraps at 676, far past
    // any realistic live-puppet count). This same serial is retained per puppet as its sync SLOT (above).
    private int _nameSerial;

    // A short step in front of the DM so the puppet doesn't spawn inside their body (z-fighting), close
    // enough to read as "placed here". The DM repositions the scene by walking; this is only the seed.
    private const float SpawnDistance = 1.5f;

    public SpawnService(IFramework framework, IObjectTable objects, IClientState clientState,
                        GuiseService guise, HumanGuise humanGuise, AnimationService anim, IPluginLog log)
    {
        _framework = framework;
        _objects = objects;
        _clientState = clientState;
        _guise = guise;
        _humanGuise = humanGuise;
        _anim = anim;
        _log = log;

        _framework.Update += OnUpdate;
        _clientState.TerritoryChanged += OnTerritoryChanged;
        _clientState.Logout += OnLogout;
    }

    /// <summary>The GLOBAL object indices of every puppet currently spawned (spawn order). The UI lists these.</summary>
    public IReadOnlyList<ushort> Spawned => _spawned;

    /// <summary>How many puppets are currently spawned.</summary>
    public int Count => _spawned.Count;

    /// <summary>True if this GLOBAL object index is one of our spawned puppets (guards UI actions).</summary>
    public bool IsSpawned(int objectIndex) =>
        objectIndex is >= 0 and <= ushort.MaxValue && _spawned.Contains((ushort)objectIndex);

    /// <summary>The stable per-source SLOT for a spawned puppet's GLOBAL index, or -1 if it isn't one of
    /// ours. The IPC layer maps this slot ↔ objectIndex and HMS namespaces it under the DM's ContentId.</summary>
    public int SlotOf(ushort globalIndex) => _slotByIndex.TryGetValue(globalIndex, out var s) ? s : -1;

    /// <summary>True if this GLOBAL index is a puppet THIS client ORIGINATED (a local DM spawn), as opposed to a
    /// mirror reproduced from a peer's broadcast. The possession gate: by default only an originated puppet may
    /// be driven, so control of a spawn is exclusive to the client that made it (every peer sees a mirror).</summary>
    public bool IsOriginated(ushort globalIndex) => _originated.Contains(globalIndex);

    /// <summary>
    /// Create a non-targetable puppet a short step in front of the local player as a CLONE of that player,
    /// bring it into the render, and track its GLOBAL index. Returns the Dalamud <see cref="ICharacter"/>
    /// immediately (so the caller can record/track it) but DEFERS the disguise: <paramref name="onReady"/>,
    /// when supplied, fires once the puppet is drawable — a fresh clone isn't drawable the same frame, and
    /// the Human (Glamourer) guise path needs a DRAWN human to paint onto (see the class note). Callers
    /// that just want a bare puppet pass <c>null</c>. Call from the framework thread.
    ///
    /// The actor is classified as a non-targetable <see cref="BattleNpcSubKind.Player"/> BattleNpc (a
    /// player-style skeleton with idle/walk/emote support — what a disguise puppet needs, and what the
    /// clone from the local player already is), so the party can't click or attack the set-piece.
    ///
    /// <paramref name="at"/>/<paramref name="facing"/> override the default "a step in front of the DM"
    /// placement with an ABSOLUTE world position/yaw. This is the atomic-placement path for a peer mirror
    /// (SpawnPuppetAt): the puppet is born at the DM's real coords in ONE call, instead of spawning a step
    /// in front of the RECEIVER and then being relocated by a separate MovePuppet — which raced the
    /// not-yet-drawn object and stranded ~1/3 of mirrors at the wrong spot (the spawn-in-front bug). Left
    /// null, placement is unchanged (the DM's own spawns and legacy SpawnPuppet+MovePuppet peers).
    ///
    /// <paramref name="originated"/> (default true) marks this as a LOCAL DM spawn — the client owns it and may
    /// possess it. HdmIpc passes false for the mirrors it reproduces from a peer's broadcast, so those aren't
    /// possessable by default (see <see cref="IsOriginated"/> / the possession gate).
    /// </summary>
    public bool TrySpawn(out ICharacter? actor, Action<ICharacter>? onReady = null,
                         Vector3? at = null, float? facing = null, bool originated = true)
    {
        actor = null;
        if (_objects.LocalPlayer is not { } me)
        {
            _log.Warning("Spawn: no local player — cannot place a puppet.");
            return false;
        }

        var com = ClientObjectManager.Instance();
        if (com == null)
        {
            _log.Warning("Spawn: ClientObjectManager unavailable.");
            return false;
        }

        // Auto-assign the next free COM slot (default args: index 0xFFFFFFFF, no reserved companion slot).
        // This is a COM index (0, 1, 2, …) into ClientObjectManager's own table — NOT a global object-table
        // index. We use it only for GetObjectByIndex/DeleteObjectByIndex below; the puppet's global index
        // (for tracking/guise) comes from actor.ObjectIndex further down.
        var comId = com->CreateBattleCharacter();
        if (comId == 0xFFFFFFFF)
        {
            _log.Warning("Spawn: CreateBattleCharacter returned no index (object table full?).");
            return false;
        }
        var comIdx = (ushort)comId;

        var native = com->GetObjectByIndex(comIdx);
        if (native == null)
        {
            _log.Warning($"Spawn: GetObjectByIndex(COM#{comIdx}) null right after create — reclaiming the slot and aborting.");
            com->DeleteObjectByIndex(comIdx, 0);
            return false;
        }

        // Seed the puppet as a CLONE of the local player, NOT a blank SetupBNpc(0) — the fix for "c_ on a
        // dummy spawns only the blank". A bare BattleNpc isn't a drawable human, so Glamourer never
        // registers it and the Human guise (which paints through Glamourer's ApplyState) silently no-ops;
        // a blank one also renders invisible. Brio's ActorSpawnService.CloneCharacter does a DOUBLE copy —
        // first the real source, then the new actor onto itself with CopyFlags.None ("needed for some tools
        // like Penumbra/Glamourer") — which leaves the puppet a real drawn human that those tools see. The
        // Monster/Demihuman paths are unaffected: their ModelCharaId swap + redraw is the exact write that
        // disguises the local player (a Pc), so it rebuilds this clone into the mob model just the same.
        // WeaponHiding matches Brio's base flag; we deliberately DON'T copy Position (we place it ourselves).
        var meNative = (CSCharacter*)me.Address;
        native->CharacterSetup.CopyFromCharacter(meNative, CopyFlags.WeaponHiding);
        native->CharacterSetup.CopyFromCharacter(native, CopyFlags.None);

        // Start the clone from the DM's TRUE body, not their current disguise. CopyFromCharacter copies the
        // DM's LIVE model state, so if the DM is self-disguised (their own ModelCharaId swapped to a mob) the
        // clone is born as THAT mob and EnableDraw builds a MONSTER draw object. A Monster guise would overwrite
        // it, but a Human (Glamourer) guise paints via ApplyState, which only registers a HUMAN draw object:
        // GetState returns null on the monster clone and the paint no-ops, so the puppet keeps the DM's disguise
        // instead of the selected row (the "first spawn is the mob I'm disguised as / a previously-summoned mob"
        // report). Reset to the player baseline (ModelCharaId 0, scale 1) so every puppet draws as a clean human
        // the guise then builds from — a blank puppet becomes a copy of the DM's REAL body, and the guise apply
        // (Monster OR Human) starts from the same clean skeleton an un-disguised clone would give. No-op when the
        // DM isn't disguised (already 0/1). Customize/equipment stay the DM's (correct for a human baseline); a
        // rare demihuman self-disguise leaves transient odd gear that the puppet's own guise immediately rewrites.
        native->ModelContainer.ModelCharaId = 0;
        native->GameObject.Scale = 1.0f;

        // Weapon SHOWN by default (Issue 2 "weapon should be shown by default"). The clone copied the DM's
        // weapon-hiding state (CopyFlags.WeaponHiding above), so a DM playing with /displayarms off would spawn
        // every puppet holding an INVISIBLE weapon. Clear the inherited hide bit so puppets are born weapon-
        // visible independent of the DM's own setting; the Spawn-tab "Show weapon" toggle drives it per-puppet
        // after. A field write (not the HideWeapons game call) because the draw object doesn't exist yet — the
        // flag is simply read when EnableDraw + LoadWeapon build the weapon a few ticks later.
        native->DrawData.IsWeaponHidden = false;

        // Classify: a non-targetable, player-skeleton BattleNpc — a set-piece the DM drives, not a mob.
        native->GameObject.ObjectKind = ObjectKind.BattleNpc;
        native->GameObject.BattleNpcSubKind = BattleNpcSubKind.Player;
        native->GameObject.TargetableStatus &= ~ObjectTargetableFlags.IsTargetable;

        // Stamp a VALID, UNIQUE, player-format Glamourer IDENTITY — WITHOUT this the Human (c-skeleton) guise
        // can't paint the puppet and it stays a bare clone of the DM (the 0.8.44 "spawning M'naago clones the
        // player" bug). Glamourer keys state on an ActorIdentifier from Penumbra.GameData's
        // ActorIdentifierFactory.FromObject, reached from the GetState/ApplyState IPC via Actor.GetIdentifier
        // (allowPlayerNpc: true). For a BattleNpc that factory takes NameId 0 + EMPTY name → CreateNpc(
        // BattleNpc, 0) → VerifyNpcData fails (no BNpc id 0) → Invalid → GetState null → HumanGuise bails. Its
        // rescue branch instead identifies a NameId-0 BattleNpc with a NON-EMPTY, SE-valid player NAME as a
        // PLAYER (name + homeWorld). So — AFTER the clone copy, so nothing overwrites it — we set:
        //   • NameId 0                  — stay on the player-rescue branch (a fresh clone is already 0; force it).
        //   • HomeWorld = the DM's       — a real world, so Penumbra's VerifyWorld passes.
        //   • a UNIQUE VerifyPlayerName-valid name — the Player identifier is (name, homeWorld) with NO index,
        //     so every puppet needs a DIFFERENT name (and none equal to the DM) or two actors share one
        //     Glamourer state. Brio proves the name persists past draw, so this one stamp is enough.
        native->NameId = 0;
        native->HomeWorld = meNative->HomeWorld;
        // Capture the slot (this puppet's stable per-source id) BEFORE NextPuppetName consumes the serial —
        // NextPuppetName post-increments _nameSerial, so the value it encodes into the name is exactly this.
        var slot = _nameSerial;
        var puppetName = NextPuppetName();
        native->GameObject.SetName(puppetName);

        // Place the puppet. Default: a short step ahead of the DM, facing the same way (FFXIV forward from
        // yaw = (sin,0,cos)). When the caller supplies an ABSOLUTE placement (a peer mirror via SpawnPuppetAt,
        // which already carries the DM's world coords), spawn straight there with no forward nudge — the
        // puppet is born at its final spot in one call instead of a step in front of the RECEIVER and then
        // relocated by a separate MovePuppet (the spawn-in-front race). Position and rotation both honour the
        // override; a null override keeps the legacy in-front seed.
        var rot = facing ?? me.Rotation;
        var forward = new Vector3(MathF.Sin(rot), 0f, MathF.Cos(rot));
        var pos = at ?? (me.Position + forward * SpawnDistance);
        native->GameObject.SetPosition(pos.X, pos.Y, pos.Z);
        native->GameObject.SetRotation(rot);

        // A fresh actor can be fully transparent; ARealmRepopulated sets Alpha to 1 before draw, so we do
        // too. Draw itself is DEFERRED to the readiness poll (DrawWhenReady) below — a just-cloned actor
        // isn't drawable this frame, and enabling too early both renders nothing and races Glamourer's
        // registration. The guise apply is handed to that poll as onReady so it lands on a drawn human.
        native->Alpha = 1.0f;

        // Resolve the puppet's GLOBAL object-table index — the key everything downstream shares. A spawned
        // BattleNpc lands in the GPose/cutscene reserved range (~201), which is DIFFERENT from comIdx.
        actor = _objects.CreateObjectReference((nint)native) as ICharacter;
        if (actor == null)
        {
            // Created but unusable from managed code — reclaim the COM slot so we don't leak an orphan we
            // can't drive. Delete by COM index directly (we never tracked a global index for it).
            _log.Warning($"Spawn: COM#{comIdx} created but no ICharacter reference; reclaiming the slot to avoid an orphan.");
            com->DeleteObjectByIndex(comIdx, 0);
            return false;
        }

        var globalIdx = (ushort)actor.ObjectIndex;

        // Player-safety guard, now on the CORRECT index space: a puppet must NEVER share the local player's
        // GLOBAL index. If it somehow did, a guise/Despawn keyed on it would strip the DM's own tracking and
        // try to delete the player object — the "stuck after despawn, nothing to revert to" catastrophe.
        // A spawned BattleNpc lands in the reserved range, never 0, so this should never fire — but if it
        // did, reclaim the COM slot and refuse rather than hand back a player-colliding puppet.
        if (globalIdx == me.ObjectIndex)
        {
            _log.Error($"Spawn: puppet resolved to the local player's global index ({globalIdx}) — reclaiming COM#{comIdx} and refusing.");
            com->DeleteObjectByIndex(comIdx, 0);
            actor = null;
            return false;
        }

        _spawned.Add(globalIdx);
        if (originated) _originated.Add(globalIdx); // a local DM spawn — possessable here; a mirror (originated:false) is not
        _slotByIndex[globalIdx] = slot; // retain the mint-time serial as this puppet's stable sync slot

        // Draw when ready, then fire the caller's guise apply. The clone is drawable within a few ticks;
        // OnUpdate polls IsReadyToDraw, EnableDraws, and only then runs onReady (so a Human guise paints
        // onto a registered, drawn human — not a half-built draw object).
        _readyJobs.Add(new ReadyJob { GlobalIndex = globalIdx, Ticks = 0, OnReady = onReady });

        _log.Information($"Spawn: puppet global#{globalIdx} (COM#{comIdx}) cloned from local player as \"{puppetName}\" at {pos.X:0.0},{pos.Y:0.0},{pos.Z:0.0} (rot {rot:0.00}). Total puppets: {_spawned.Count}.");
        return true;
    }

    // Synthesize the next UNIQUE, SE-valid "Forename Surname" name for a puppet's Glamourer identity (see the
    // identity stamp in TrySpawn). The surname encodes the monotonic serial as capital+lowercase letters —
    // 676 distinct names before it wraps, far past any live-puppet count. VerifyPlayerName is satisfied: 5–21
    // chars, exactly one space, each part capitalised then lowercase ("Hdm Aa"). The forename is
    // deliberately synthetic so it won't collide with a real player on the DM's home world (same name+world
    // would share a Glamourer state).
    private string NextPuppetName()
    {
        var n  = _nameSerial++;
        var hi = (char)('A' + n / 26 % 26);
        var lo = (char)('a' + n % 26);
        return $"Hdm {hi}{lo}";
    }

    // Drain in-flight draw-when-ready polls on the framework thread, in two phases (see the class note).
    // Each puppet enqueued by TrySpawn is held here: phase 1 waits for IsReadyToDraw then EnableDraws; phase
    // 2 waits for the draw object to become VISIBLE, then hands it to its onReady guise apply. Applying only
    // once VISIBLE is what lets the Human (Glamourer) path see a registered body. Re-resolves the object
    // every tick (never a stale pointer) and drops a job whose puppet was despawned or zone-recycled
    // mid-poll. Cheap when idle (the list is empty the moment spawns settle).
    private void OnUpdate(IFramework _)
    {
        if (_readyJobs.Count == 0) return;
        for (var i = _readyJobs.Count - 1; i >= 0; i--)
        {
            var job = _readyJobs[i];
            job.Ticks++;

            // Aborted: despawned, or a zone change dropped tracking — stop polling this one.
            if (!_spawned.Contains(job.GlobalIndex)) { _readyJobs.RemoveAt(i); continue; }
            // Warmup: let the draw object settle before the first readiness check (Brio's dontStartFor:2).
            if (job.Ticks <= ReadyWarmupTicks) continue;

            var obj = _objects[job.GlobalIndex];
            if (obj is not ICharacter chara || chara.Address == nint.Zero)
            {
                if (job.Ticks >= MaxReadyTicks)
                {
                    _readyJobs.RemoveAt(i);
                    _log.Warning($"Spawn: puppet obj#{job.GlobalIndex} never resolved to an ICharacter before draw-ready timeout.");
                }
                continue;
            }

            var native = (CSCharacter*)chara.Address;

            // Phase 1 — draw not yet enabled: wait until the cloned model is loaded (IsReadyToDraw), then
            // EnableDraw (Brio's DrawWhenReady). Give the draw object a frame to build before phase 2.
            if (!job.DrawEnabled)
            {
                var ready = native->GameObject.IsReadyToDraw();
                if (!ready && job.Ticks < MaxReadyTicks) continue; // keep waiting until ready or the cap
                native->GameObject.EnableDraw();
                job.DrawEnabled = true;
                if (!ready)
                    _log.Warning($"Spawn: puppet obj#{job.GlobalIndex} never reported draw-ready after {job.Ticks} ticks — enabled draw anyway.");
                continue;
            }

            // Phase 2 — draw enabled: wait until the draw object exists AND is visible before firing onReady
            // (Brio's WaitForDrawing). Glamourer only returns valid state for a visible body, so applying a
            // Human guise here — not at EnableDraw time — is what lands the NPC face on the puppet instead
            // of leaving it a player-clone.
            var draw = native->GameObject.DrawObject;
            var visible = draw != null && draw->IsVisible;
            if (!visible && job.Ticks < MaxReadyTicks) continue; // keep waiting until visible or the cap

            _readyJobs.RemoveAt(i);
            if (!visible)
                _log.Warning($"Spawn: puppet obj#{job.GlobalIndex} draw object never became visible after {job.Ticks} ticks — applying guise anyway.");
            // Re-apply any placement that was requested while this puppet was still building (the spawn-position
            // race fix): now that it's drawn+visible the write is guaranteed to stick. Do it BEFORE onReady so the
            // guise redraw rebuilds the model at the final spot. No-op for a puppet that was never moved early
            // (DM's own puppets are placed once at spawn and need no relocation).
            if (job.PendingPos is { } pp) native->GameObject.SetPosition(pp.X, pp.Y, pp.Z);
            if (job.PendingRot is { } pr) native->GameObject.SetRotation(pr);
            try { job.OnReady?.Invoke(chara); }
            catch (Exception e) { _log.Error(e, $"Spawn: draw-ready onReady for puppet obj#{job.GlobalIndex} threw."); }
            // Signal the IPC layer AFTER the guise apply — the puppet is now drawn+guised, the moment HMS
            // gates "the puppet is visibly the mob" on. Fired even on the timeout path (guise applied anyway).
            try { PuppetReadyEvent?.Invoke(job.GlobalIndex); }
            catch (Exception e) { _log.Error(e, $"Spawn: PuppetReadyEvent for puppet obj#{job.GlobalIndex} threw."); }
        }
    }

    /// <summary>Despawn one puppet by its GLOBAL object index: forget its guise tracking (no native calls —
    /// the object is about to vanish), resolve the COM index fresh from the live object, delete it, and
    /// untrack. Safe if the slot is already empty (GetIndexByObject returns 0xFFFFFFFF and we skip the
    /// delete — so we never delete a recycled index).</summary>
    public void Despawn(ushort globalIndex)
    {
        // Last-ditch guard: NEVER forget/delete the local player. A bookkeeping slip that let global index 0
        // in here would strip the DM's own guise tracking (Forget) and delete the player object. Untrack and
        // bail without touching guise state or the object.
        if (_objects.LocalPlayer is { } me && globalIndex == me.ObjectIndex)
        {
            _log.Error($"Spawn: refused to despawn the local player's global index ({globalIndex}).");
            _spawned.Remove(globalIndex);
            _originated.Remove(globalIndex); // belt-and-braces (a player index is never originated)
            _slotByIndex.Remove(globalIndex); // never a real puppet slot — drop without notifying
            return;
        }

        // Ownership guard: Despawn is destructive (Forget strips guise heal-tracking; the COM delete frees the
        // object). It must ONLY ever touch a puppet WE spawned — every legit caller (DespawnAll, the UI, and a
        // real mirror-puppet DespawnPuppet) passes an index in _spawned. The prior local-player guard is not
        // enough: on a PEER the DM is a REMOTE actor (its own-body disguise mirror sits at a remote index, not
        // the local player), so an inbound DespawnPuppet that resolves to that index would slip past and
        // _guise.Forget it — silently killing the DM's self-heal, which then sheds to the real body at the
        // disguise's persisted scale on the next Mare/Glamourer redraw (the Galatea peer-shed regression:
        // spawn/despawn sync is what first made a peer call Despawn at all). Untracked index => nothing of ours.
        if (!_spawned.Contains(globalIndex))
        {
            _log.Warning($"Spawn: ignored Despawn for obj#{globalIndex} — not a puppet we spawned; refusing to Forget/delete an actor we don't own (would have shed a disguise we're mirroring).");
            _slotByIndex.Remove(globalIndex); // clear any stray slot mapping without notifying peers
            return;
        }

        // Cancel any pending draw-when-ready poll for this puppet so its queued guise apply can't fire on a
        // recycled index (OnUpdate would also self-drop it next tick via the _spawned check, but a despawn
        // this frame plus an immediate re-spawn into the same slot could otherwise race — drop it now).
        _readyJobs.RemoveAll(j => j.GlobalIndex == globalIndex);

        // Drop ALL per-index tracking BEFORE freeing the slot, so a recycled index can't inherit a stale
        // model / offset / hide / in-flight redraw (guise) or a stuck speed-pin / replay loop (anim) from
        // those services' per-frame loops. No native calls in any Forget — the object is about to vanish.
        _guise.Forget(globalIndex);
        _humanGuise.Forget(globalIndex);
        _anim.Forget(globalIndex);

        // Resolve the COM index FRESH from the live object (Brio's DestroyObject pattern). We hold the
        // puppet's global index; ask the game which COM slot that live GameObject occupies, then delete by
        // COM index. GetIndexByObject → 0xFFFFFFFF means "not COM-managed" (already freed / recycled) — skip.
        var com = ClientObjectManager.Instance();
        var obj = _objects[globalIndex];
        if (com != null && obj != null)
        {
            var comIdx = com->GetIndexByObject((CSGameObject*)obj.Address);
            if (comIdx != 0xFFFFFFFF)
                com->DeleteObjectByIndex((ushort)comIdx, 0);
            else
                _log.Warning($"Spawn: obj#{globalIndex} not COM-managed at despawn (GetIndexByObject 0xFFFFFFFF) — already freed?");
        }

        _spawned.Remove(globalIndex);
        _originated.Remove(globalIndex); // keep the ownership set in lockstep (no-op for a mirror)
        // Drop the slot and tell the IPC layer the puppet is gone (so peers despawn their copy). Capture the
        // slot BEFORE removing it; skip the notify if we somehow held no slot for this index.
        if (_slotByIndex.Remove(globalIndex, out var goneSlot))
        {
            try { PuppetRemovedEvent?.Invoke(globalIndex, goneSlot); }
            catch (Exception e) { _log.Error(e, $"Spawn: PuppetRemovedEvent for puppet obj#{globalIndex} threw."); }
        }
        _log.Information($"Spawn: despawned puppet obj#{globalIndex}. Remaining: {_spawned.Count}.");
    }

    /// <summary>Despawn every tracked puppet (UI "Despawn all" and dispose).</summary>
    public void DespawnAll()
    {
        foreach (var idx in new List<ushort>(_spawned))
            Despawn(idx);
        _spawned.Clear(); // belt-and-braces; each Despawn already removed its entry
        _originated.Clear(); // ditto — the ownership set trails _spawned
    }

    // ---- Puppet manipulation (the Spawn Management tab: reposition / rotate a placed set-piece) --------
    // All guarded by IsSpawned so the UI can NEVER move/turn a non-puppet — a set/read only ever touches
    // an actor WE brought into the world. Native GameObject writes on the framework thread, like the rest.

    /// <summary>Resolve the live native GameObject for a tracked puppet's GLOBAL index, or null if it isn't
    /// one of ours or the slot is gone. The single guarded gate every transform call funnels through.</summary>
    private CSGameObject* ResolvePuppet(ushort globalIndex)
    {
        if (!IsSpawned(globalIndex)) return null;
        var obj = _objects[globalIndex];
        return obj == null ? null : (CSGameObject*)obj.Address;
    }

    /// <summary>Read a puppet's current world position + facing yaw (radians). False if it isn't a live
    /// puppet — the UI seeds its drag/slider from this each frame so the controls track external moves.</summary>
    public bool TryGetTransform(ushort globalIndex, out Vector3 pos, out float rot)
    {
        pos = default;
        rot = 0f;
        var go = ResolvePuppet(globalIndex);
        if (go == null) return false;
        pos = go->Position;
        rot = go->Rotation;
        return true;
    }

    /// <summary>Move a puppet to an absolute world position. No-op unless it's a live puppet.</summary>
    public void SetPosition(ushort globalIndex, Vector3 pos)
    {
        // Stash onto a still-pending draw-ready job FIRST (before the possibly-no-op eager write) so a not-yet-
        // resolvable mirror still records the target and OnUpdate re-applies it at draw-ready. For a live puppet
        // no job exists, the loop finds nothing, and this is a plain immediate move.
        foreach (var job in _readyJobs)
            if (job.GlobalIndex == globalIndex) { job.PendingPos = pos; break; }

        var go = ResolvePuppet(globalIndex);
        if (go == null) return;
        go->SetPosition(pos.X, pos.Y, pos.Z);
    }

    /// <summary>Face a puppet to an absolute yaw (radians). No-op unless it's a live puppet.</summary>
    public void SetRotation(ushort globalIndex, float rot)
    {
        foreach (var job in _readyJobs)
            if (job.GlobalIndex == globalIndex) { job.PendingRot = rot; break; }

        var go = ResolvePuppet(globalIndex);
        if (go == null) return;
        go->SetRotation(rot);
    }

    /// <summary>Teleport a puppet to the DM: a step in front (<paramref name="inFront"/>) or right on top,
    /// facing the DM's way — the "bring/face this set-piece to me" convenience. No-op without a local
    /// player or if it isn't a live puppet.</summary>
    public void MoveToLocalPlayer(ushort globalIndex, bool inFront = true)
    {
        if (_objects.LocalPlayer is not { } me) return;
        var rot = me.Rotation;
        var pos = me.Position;
        if (inFront)
            pos += new Vector3(MathF.Sin(rot), 0f, MathF.Cos(rot)) * SpawnDistance;
        SetPosition(globalIndex, pos);
        SetRotation(globalIndex, rot);
    }

    /// <summary>Turn a puppet to face the DM (yaw toward the local player). No-op without a local player
    /// or if it isn't a live puppet.</summary>
    public void FaceLocalPlayer(ushort globalIndex)
    {
        if (_objects.LocalPlayer is not { } me) return;
        if (!TryGetTransform(globalIndex, out var pos, out _)) return;
        var dx = me.Position.X - pos.X;
        var dz = me.Position.Z - pos.Z;
        if (dx == 0f && dz == 0f) return;          // DM is directly above/below — no meaningful facing
        SetRotation(globalIndex, MathF.Atan2(dx, dz)); // FFXIV yaw: atan2(dx, dz)
    }

    // A zone change frees every spawned actor WITH the old zone, and the new zone recycles those object
    // indices. So DROP tracking without deleting — a delete now would target whatever the new zone put in
    // those slots (Brio's critical guard). GuiseService/HumanGuise clear their own per-index tracking on
    // TerritoryChanged too, so nothing is left dangling.
    private void OnTerritoryChanged(uint _)
    {
        if (_spawned.Count > 0)
            _log.Information($"Spawn: territory change — dropping {_spawned.Count} puppet index(es) (freed with the zone).");
        NotifyAndClearTracking();
        _readyJobs.Clear(); // pending clones are freed with the zone too — never apply a guise across the line
    }

    private void OnLogout(int type, int code) { NotifyAndClearTracking(); _readyJobs.Clear(); }

    // Tell the IPC layer every tracked puppet is gone, then drop ALL tracking. The zone-change and logout
    // drops free the actors WITHOUT a native delete (the game already reclaimed them), so we can't route
    // through Despawn — but peers still need the "puppet gone" signal to despawn their copies. Snapshot the
    // map first so a handler can't perturb the enumeration.
    private void NotifyAndClearTracking()
    {
        foreach (var kv in new List<KeyValuePair<ushort, int>>(_slotByIndex))
        {
            try { PuppetRemovedEvent?.Invoke(kv.Key, kv.Value); }
            catch (Exception e) { _log.Error(e, $"Spawn: PuppetRemovedEvent (bulk) for puppet obj#{kv.Key} threw."); }
        }
        _slotByIndex.Clear();
        _spawned.Clear();
        _originated.Clear(); // zone change / logout frees every puppet — drop ownership tracking with them
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        _clientState.TerritoryChanged -= OnTerritoryChanged;
        _clientState.Logout -= OnLogout;
        _readyJobs.Clear();
        // Plugin unload must leave no puppet behind (unless a zone change already freed them, in which
        // case tracking is empty and this is a no-op).
        try { DespawnAll(); }
        catch (Exception e) { _log.Error(e, "Spawn: DespawnAll on dispose failed."); }
    }
}
