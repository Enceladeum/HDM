using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using CSCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using CSVector3 = FFXIVClientStructs.FFXIV.Common.Math.Vector3;
using CharacterBase = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase;
using EquipmentModelId = FFXIVClientStructs.FFXIV.Client.Game.Character.EquipmentModelId;
using WeaponModelId = FFXIVClientStructs.FFXIV.Client.Game.Character.WeaponModelId;
using WeaponSlot = FFXIVClientStructs.FFXIV.Client.Game.Character.DrawDataContainer.WeaponSlot;

namespace HDM;

/// <summary>
/// The disguise core: write <c>Character.ModelContainer.ModelCharaId</c> (+
/// optionally the mob's native scale) and redraw. This is exactly Brio's
/// appearance path (ActorAppearanceService.SetCharacterAppearance): compare,
/// write the id, redraw. Client-side only — nothing is sent to the server;
/// other players never see it (until HMS syncs the (modelCharaId, scale) atom).
///
/// Safety envelope (per the Dalamud RE rules):
///  - Every native touch happens on the framework thread. UI Draw runs there,
///    and the redraw state machine rides IFramework.Update.
///  - Redraw = DisableDraw, wait ≥2 framework ticks, then poll IsReadyToDraw and
///    EnableDraw (Brio's ActorRedrawService does DisableDraw → DrawWhenReady →
///    Enable; never disable+enable in the same tick — the draw object teardown
///    must settle first).
///  - Originals are tracked per object index and restored on revert/Dispose.
///  - On territory change / logout the object table is rebuilt: tracked state
///    is CLEARED, never written back — the pointers are stale and the server
///    respawn restores true appearance anyway.
///
/// Re-apply rebuild (two cases, one mechanism): re-applying over an existing
/// guise sometimes can't rebuild in place, so we revert to the ORIGINAL model
/// first, let that redraw complete, then apply the new guise as a second cycle —
/// the manual revert-then-apply that works in-game, automated as a two-phase
/// redraw job (see <see cref="Apply"/> / <see cref="OnUpdate"/>). It triggers when
/// (1) a demihuman is involved (the "switching d→d only changes the outfit" fix —
/// a d-skeleton only swaps its equipment slots on an in-place redraw), or (2) the
/// target ModelChara equals the one already shown (writing an unchanged
/// ModelCharaId sets no dirty flag, so the redraw rebuilds the player's own model
/// instead — the "same-model re-apply restores the enlarged original" fix).
/// Different-id Monster↔Monster keeps the fast single-redraw path (no flicker).
///
/// Scale-only changes NEVER redraw. A resize doesn't touch ModelCharaId, so a
/// DisableDraw/EnableDraw would hit that same unchanged-id trap and rebuild the
/// player's own body (dropping the disguise and resizing the real character — the
/// "scale button reverts to the real model" bug). Instead <see cref="SetScale"/> /
/// <see cref="Resize"/> write the transform live via <see cref="WriteScaleLive"/>:
/// the logical <c>GameObject.Scale</c> plus the rendered draw-object scale, then the
/// game's own <c>NotifyTransformChanged</c> so it shows the same frame.
///
/// Vertical placement: <see cref="SetVerticalOffset"/> dials a draw-only elevation through the
/// game's <c>GameObject.SetDrawOffset</c> (re-asserted per frame like a heel or mount float), so a
/// DM can sink a hovering boss to the ground or lift a floor-clipping flyer without moving the real
/// character — the collision/logical position is untouched.
/// </summary>
public sealed unsafe class GuiseService : IDisposable
{
    private readonly IFramework _framework;
    private readonly IObjectTable _objects;
    private readonly IClientState _clientState;
    private readonly NpcData _npc;
    private readonly IPluginLog _log;

    // Wait at least this many ticks after DisableDraw before EnableDraw (let the
    // old draw object tear down), then poll IsReadyToDraw up to the cap so a heavy
    // equipped demihuman model has time to load before we re-enable it.
    private const int MinDrawWaitTicks = 2;
    private const int MaxDrawWaitTicks = 60;
    // After a demihuman re-apply's revert redraw completes, hold the reverted model
    // on screen this many ticks before starting the new guise's redraw, so the draw
    // object fully rebuilds as the original before it's torn down again.
    private const int SettleTicks = 3;

    // Captured equipment is only taken for demihuman guises (McType 2), whose
    // body/armor we overwrite; null for monster guises (a bare ModelChara swap
    // touches no equipment, so there is nothing to restore). HatHidden snapshots the
    // player's own "Hide Head Gear" preference (DrawData.IsHatHidden): a demihuman's
    // HEAD is equipment slot 0, which that flag hides, so we force it visible for the
    // guise and restore the captured value on revert (the "headless Loporrit" fix).
    private readonly record struct Original(int ModelCharaId, float Scale, EquipmentModelId[]? Equipment, bool HatHidden);

    // objectIndex -> pre-guise state (the TRUE original; preserved across re-applies
    // so revert always restores the real body, never an intermediate guise).
    private readonly Dictionary<int, Original> _originals = [];
    // objectIndex -> the actor's pre-guise MAIN/OFF weapon models, captured when a Monster/Demihuman guise
    // overwrites them with the disguised NPC's OWN weapon (WriteNpcWeapons). A spawned puppet is a clone of
    // the DM (SpawnService CopyFromCharacter copies the DM's _weaponData), so without this it was left holding
    // the DM's blade. Parallel to _originals (seeded together in ApplyNow, kept across re-applies, dropped on
    // the same paths) rather than a field ON Original, so the Original seed sites that never touch weapons —
    // Resize / SetScale — stay untouched. Restored on Revert.
    private readonly Dictionary<int, OrigWeapons> _originalWeapons = [];
    private readonly record struct OrigWeapons(WeaponModelId Main, WeaponModelId Off);
    // objectIndex -> McType of the guise CURRENTLY applied (1/2/3). Lets Apply spot a
    // demihuman-involved re-apply, which needs the clean revert-first rebuild.
    private readonly Dictionary<int, int> _currentKind = [];
    // objectIndex -> the Monster/Demihuman model guise (row + scale) currently applied, so
    // ReassertModel can re-write it after a competing writer resets ModelCharaId (or rebuilds the draw
    // object) out from under a DRIVEN actor: a freshly spawned PUPPET whose deferred guise must land over a
    // bare clone, or (on a peer) the DM's MIRROR that a Penumbra/Glamourer re-sync rebuilds as the real body
    // while the id stays ours. Both are NON-LOCAL indices; the LOCAL PLAYER is excluded in ReassertModel (its
    // own client holds a self-disguise after one redraw — self is never a shed victim). Mirrors the
    // _vOffsets/_hidden drift-reassert. Human (Glamourer) guises never land here — that path owns its own re-assert.
    private readonly Dictionary<int, AppliedGuise> _appliedGuises = [];
    private readonly record struct AppliedGuise(MobRow Row, float? Scale);
    // Re-drift telemetry (peer-shed diagnosis, docs/hms-galatea-peer-shed.md). If the SAME index keeps drifting
    // right after we heal it, a competing writer — on a peer, Mare/Glamourer re-applying the synced player a few
    // seconds after it comes into range — is out-racing the heal. Logged DISTINCTLY (Warning) from a one-shot
    // drift so a joint peer test reads "fight" (streak climbs) vs "silent". The "silent" case — the field says the
    // guise but the draw shows the real body — turned out to be the Galatea peer-shed itself, so it is no longer just
    // telemetry: ReassertModel now ALSO watches the drawn model type and re-asserts on that (see below), bounded so a
    // draw-hook we can't out-race is diagnosed rather than flicker-warred.
    private readonly Dictionary<int, long> _lastDriftMs = [];
    private readonly Dictionary<int, int> _driftStreak = [];
    private const long DriftStreakWindowMs = 2000; // re-drift within this window = same fight, not a fresh event
    private const int DriftStreakWarn = 3;         // escalate to Warning once an index has re-drifted this many times
    // A DRAW-LEVEL shed (id intact, draw object rebuilt as the real body by a peer's Penumbra/Glamourer re-sync) can't
    // be won by out-writing it forever: every re-assert is another DisableDraw, which is exactly what invites Penumbra's
    // rebuild (HMS abandoned DisableDraw for RenderFlags for this very reason). So re-assert a bounded number of times —
    // enough to win once the mob model is cached locally (a 2nd, fast redraw beats Penumbra's ~500ms rebuild) — then STOP
    // and log a verdict instead of flicker-warring a hook a raw ModelChara swap cannot out-race.
    private const int DrawShedReassertCap = 4;
    // For a MONSTER guise (self-contained ModelChara, e.g. Galatea) the peer showing the real body PROVES the id
    // drifted 3723->0 — a running heal would catch that and log. So a SILENT shed means the heal was disabled for
    // that index: its tracking was dropped (see DropApplied — names the path) or a gate skipped it (see the gate
    // notes in ReassertModel). This pair of traces makes the next peer test name the cause outright.
    private readonly Dictionary<int, long> _lastGateLogMs = []; // throttle the "drifted but gated" note, per index
    private long _lastScaleProbeMs; // throttle the "scale had no effect — not tracked" probe (fires per drag frame otherwise)
    // objectIndex -> in-flight redraw job (DisableDraw → wait → EnableDraw), plus an
    // optional queued guise to apply once a revert redraw settles (two-phase re-apply).
    private readonly Dictionary<int, RedrawJob> _redraws = [];

    // objectIndex -> vertical DRAW offset (world units) the DM has dialed in to place a guise on
    // the ground: flying mobs spawn clipping the floor, and a hovering idle sits too high for a
    // "still on the ground" ambush. Written through the game's own GameObject.SetDrawOffset (so it
    // propagates to the render exactly like a heel or mount float) and RE-ASSERTED on drift each
    // frame, because the game rewrites DrawOffset on mount/emote/float-height changes. Present only
    // while a non-zero offset is dialed in. SimpleHeels-style tolerance so it coexists with a heel
    // plugin (a few-cm difference is left alone; a full reset toward 0 is re-applied).
    private readonly Dictionary<int, float> _vOffsets = [];
    private const float OffsetDrift = 0.05f;

    // objectIndex -> hidden: the DM has pulled their actor out of the render entirely ("I'm running
    // this scene but I'm not physically here"). Implemented as a held DisableDraw — the same teardown
    // the redraw machine uses — and RE-ASSERTED per frame in ReassertHidden, because the game re-draws
    // an actor on movement/zone/emote events; a one-shot DisableDraw wouldn't stay down. Toggling off
    // (or dispose) EnableDraws it so we never leave the character invisible. Self-apply like the rest.
    private readonly HashSet<int> _hidden = [];

    public GuiseService(IFramework framework, IObjectTable objects, IClientState clientState,
                        NpcData npc, IPluginLog log)
    {
        _framework = framework;
        _objects = objects;
        _clientState = clientState;
        _npc = npc;
        _log = log;

        _framework.Update += OnUpdate;
        _clientState.TerritoryChanged += OnTerritoryChanged;
        _clientState.Logout += OnLogout;
    }

    public bool IsGuised(int objectIndex) => _originals.ContainsKey(objectIndex);

    /// <summary>Optional gate the owner sets so <see cref="ReassertModel"/> skips an actor whose draw is
    /// being driven elsewhere — specifically the puppet PossessionService is piloting, whose animation
    /// reads the very Timeline a re-assert's redraw would null. Returns true to SUPPRESS the re-assert for
    /// that index. Null (default) never suppresses. Wired in Plugin to the possessed index.</summary>
    public Func<int, bool>? SuppressReassert { get; set; }

    /// <summary>Runtime mirror of <see cref="Configuration.ClearDisguisesOnMapChange"/> (seeded at startup,
    /// written back when the Config checkbox flips). When true, <see cref="OnTerritoryChanged"/> strips the
    /// local DM's own disguise at a zone line instead of keeping it across the zone. Default false preserves
    /// the intentional cross-zone persistence (the "stuck m_ across a zone" fix). The logout revert is
    /// unconditional regardless of this flag.</summary>
    public bool ClearDisguiseOnMapChange { get; set; }

    /// <summary>Late-wired in Plugin to <c>HumanGuise.Revert</c>. Called by <see cref="SanitizeLocalPlayer"/>
    /// on the redraw path (restoreVisual = true) so an exit that strips the DM's own disguise also drops a
    /// Human (Glamourer) guise's appearance — which does NOT live in <c>_originals</c> and so is invisible to
    /// <see cref="RevertLocalPlayerHard"/>. A no-op for a Monster/Demihuman guise (nothing Glamourer-side to
    /// revert), so calling it unconditionally on that path is safe. Null until wired; never called on the cheap
    /// logout path, where the Glamourer look self-heals on relog.</summary>
    public Action<int>? RevertHumanAppearance { get; set; }

    /// <summary>
    /// A one-line, READ-ONLY snapshot of an actor's live draw/identity state — the diagnostic
    /// <see cref="HumanGuise"/> dumps when a Glamourer cold-registration retry times out. It answers the
    /// question that localizes the Human-peer-sync gap in a single test: when Glamourer refuses to return
    /// state for a spawned puppet, is the puppet actually drawn + visible with a valid player identity
    /// (⇒ Glamourer is rejecting the identifier) or is it half-built / hidden / world-less (⇒ the spawn
    /// settle is the culprit)? No writes, no redraw — safe on any index, and null-guards a despawned actor.
    /// Lives here (not in HumanGuise) because this service already owns the <c>unsafe</c> CS access.
    /// </summary>
    public string DescribeActor(int objectIndex)
    {
        var go = _objects[objectIndex];
        if (go is null) return $"obj#{objectIndex} not in object table";
        if (go.Address == nint.Zero) return $"obj#{objectIndex} null address";
        var native = (CSCharacter*)go.Address;
        var draw = native->GameObject.DrawObject;
        var drawState = draw == null ? "null" : (draw->IsVisible ? "visible" : "hidden");
        return $"obj#{objectIndex} '{go.Name.TextValue}' kind={native->GameObject.ObjectKind} " +
               $"nameId={native->NameId} homeWorld={native->HomeWorld} " +
               $"readyToDraw={native->GameObject.IsReadyToDraw()} drawObject={drawState} " +
               $"modelChara={native->ModelContainer.ModelCharaId}";
    }

    /// <summary>
    /// Apply a mob's model to a character. <paramref name="scale"/> is the absolute
    /// scale to write, or null to leave the actor's current size untouched. Call
    /// from the framework thread (UI Draw is fine).
    /// </summary>
    public void Apply(ICharacter chara, MobRow row, float? scale)
    {
        var native = (CSCharacter*)chara.Address;
        if (native == null) return;
        var idx = chara.ObjectIndex;
        int currentModel = native->ModelContainer.ModelCharaId;

        // A re-apply over an existing guise needs the clean revert-first rebuild when
        // EITHER of two conditions holds:
        //  1. a demihuman is involved (on either side) — a d-skeleton doesn't fully
        //     re-rig from a single in-place redraw, only its equipment changes; or
        //  2. the target ModelChara equals the one already displayed. Writing the SAME
        //     ModelCharaId sets no "changed" dirty flag, so the redraw rebuilds from the
        //     underlying character (the player's OWN model) instead of the disguise —
        //     the "1975 restores the enlarged original" bug (1974/1975/2319 all share
        //     ModelChara 307; a non-Off scale then sizes that reverted player up).
        // Restoring the ORIGINAL first makes BOTH the revert (id→original) and the
        // re-apply (original→id) genuine changes, so each dirties and rebuilds cleanly.
        // Different-id Monster↔Monster still takes the fast single-redraw path.
        if (_originals.TryGetValue(idx, out var orig))
        {
            var involvesDemihuman = _currentKind.GetValueOrDefault(idx) == 2 || row.McType == 2;
            var sameModel = currentModel == row.ModelCharaId;
            if (involvesDemihuman || sameModel)
            {
                native->ModelContainer.ModelCharaId = orig.ModelCharaId;
                native->GameObject.Scale = orig.Scale;
                if (orig.Equipment != null)
                    WriteEquipment(native, orig.Equipment);
                BeginRedraw(idx, native, row, scale); // revert now; the queued row applies after it settles
                _log.Information($"Guise: obj#{idx} re-apply (demihuman={involvesDemihuman}, sameModel={sameModel}) -> revert to original first, then '{row.DisplayName}'.");
                return;
            }
        }

        // A FRESH demihuman apply (no prior guise to revert) needs the SAME settle-then-apply
        // two-phase the re-apply path above uses. A single in-place redraw swaps the NpcEquip but
        // does NOT re-rig the d-skeleton, so the body renders invisible until something forces a
        // skeleton init — which is why a spawned demihuman only appears once possession's per-frame
        // timeline write kicks it. Redraw the current (clean, pre-guise) skeleton to a settled state
        // first (ThenRow carries the demihuman), then the queued ApplyNow writes the model+equipment
        // onto the freshly-rebuilt skeleton — the second genuine rebuild that actually rigs it. The
        // model is unchanged through this first redraw, so ApplyNow still captures the true original.
        // Monster (McType 3) is self-contained in its ModelChara and rigs from one redraw — untouched.
        if (row.McType == 2)
        {
            BeginRedraw(idx, native, row, scale);
            _log.Information($"Guise: obj#{idx} fresh demihuman '{row.DisplayName}' -> settle-then-apply (re-rig d-skeleton).");
            return;
        }

        ApplyNow(native, idx, row, scale);
    }

    /// <summary>The actual model write + optional demihuman equipment + redraw. Captures
    /// the original on first touch. Used for a first apply and for the second phase of a
    /// demihuman re-apply (after the revert redraw has settled).</summary>
    private void ApplyNow(CSCharacter* native, int idx, MobRow row, float? scale)
    {
        // Demihuman (McType 2) bodies are invisible from a bare skeleton swap; they
        // need their NpcEquip set written into the equipment slots too.
        var isDemihuman = row.McType == 2;

        // Remember the pre-guise state once; re-applying keeps the ORIGINAL original.
        // Capture equipment only for demihuman (the only path that overwrites it).
        if (!_originals.ContainsKey(idx))
            _originals[idx] = new Original(
                native->ModelContainer.ModelCharaId,
                native->GameObject.Scale,
                isDemihuman ? CaptureEquipment(native) : null,
                native->DrawData.IsHatHidden);

        native->ModelContainer.ModelCharaId = row.ModelCharaId;
        // Scale SANITISATION between disguises: a null scale ("Off"/human rows) must RESET to the
        // tracked original size, not silently leave the PREVIOUS disguise's runtime GameObject.Scale
        // on the freshly-swapped model (the size-leak — a big mob's x2 bleeding onto the next guise).
        // _originals[idx] was populated just above on first touch and is KEPT across re-applies, so it
        // always holds the true pre-guise size.
        native->GameObject.Scale = scale ?? _originals[idx].Scale;

        if (isDemihuman)
        {
            // The DrawData equipment array survives the DisableDraw/EnableDraw cycle
            // (it lives on the Character, not the transient DrawObject), so writing it
            // before the redraw makes the rebuilt demihuman pick it up in a single
            // cycle — the same state the game sets when it first draws this NPC.
            var equip = _npc.TryGetEquipment(row.BaseId);
            if (equip != null)
                WriteEquipment(native, equip);
            else
                _log.Warning($"Guise: demihuman base {row.BaseId} has no NpcEquip set — body may render invisible.");

            // A demihuman's HEAD lives in equipment slot 0, and the player's own "Hide Head
            // Gear" glamour preference (DrawData.IsHatHidden, the /displayhead flag) hides slot
            // 0 — so a player who hides their headgear renders the guise HEADLESS (the Loporrit
            // bug). Force the head visible for the duration of the guise; Revert restores the
            // captured original flag. (Diagnostic demoted to Debug now the fix is confirmed.)
            var head = native->DrawData.EquipmentModelIds[0];
            _log.Debug($"Guise[head] obj#{idx} demihuman '{row.DisplayName}': IsHatHidden={native->DrawData.IsHatHidden}, head slot Id={head.Id}/var{head.Variant}");
            if (native->DrawData.IsHatHidden)
                native->DrawData.IsHatHidden = false;
        }

        // Give the puppet the disguised NPC's OWN weapon instead of the DM's inherited one (thread #1). Runs
        // for both Monster (McType 3) and Demihuman (McType 2) — every actor that reaches ApplyNow; Human goes
        // through HumanGuise. Resolve + capture now, but LOAD the weapon in the post-redraw continuation: the
        // body redraw rebuilds the body draw object, NOT the separate weapon draw object (Weapon* at
        // DrawObjectData+0x08), and the game's LoadWeapon only attaches once the body draw object is present.
        var (mainW, offW) = ResolveNpcWeapons(native, idx, row);

        _currentKind[idx] = row.McType;
        _appliedGuises[idx] = new AppliedGuise(row, scale); // remember it so ReassertModel can heal a drift
        BeginRedraw(idx, native, null, null, thenApply: () => LoadWeaponsNow(idx, mainW, offW));
        _log.Information($"Guise: obj#{idx} -> ModelChara {row.ModelCharaId} ({row.DisplayName}), {row.Kind}, scale {(scale?.ToString("0.##") ?? "unchanged")}");
    }

    /// <summary>Snapshot the 10 draw-data equipment slots so a demihuman revert can restore them.</summary>
    private static EquipmentModelId[] CaptureEquipment(CSCharacter* native)
    {
        var arr = new EquipmentModelId[10];
        var span = native->DrawData.EquipmentModelIds;
        for (var i = 0; i < 10 && i < span.Length; i++)
            arr[i] = span[i];
        return arr;
    }

    /// <summary>Write up to 10 equipment model ids into the draw-data equipment slots.</summary>
    private static void WriteEquipment(CSCharacter* native, EquipmentModelId[] equip)
    {
        var span = native->DrawData.EquipmentModelIds;
        for (var i = 0; i < equip.Length && i < span.Length; i++)
            span[i] = equip[i];
    }

    /// <summary>
    /// Resolve the disguised NPC's OWN main+off-hand weapon models (from the SAME NpcEquip source the body
    /// uses) and capture the puppet's pre-guise weapon once so Revert can restore it. Does NOT touch the game
    /// object — the actual build happens later in <see cref="LoadWeaponsNow"/>, after the body redraw settles,
    /// because the weapon draw object (Weapon* at DrawObjectData+0x08) is SEPARATE from the ModelId spec and is
    /// only (re)built by the game's LoadWeapon, which needs the body draw object present to attach to. (The
    /// earlier field-write of DrawData.Weapon(slot).ModelId + a body redraw did NOT rebuild the weapon — the
    /// clone-inherited DM weapon persisted; that was the "spawns inherit DM's weapon" report.) When the NPC has
    /// no weapon in a slot the model is <c>default</c> (empty) so LoadWeapon CLEARS the inherited DM weapon
    /// rather than leaving a weaponless beast holding the DM's sword. Human (McType 1) puppets take the parallel
    /// <see cref="LoadNpcWeapon"/> entry (Glamourer's cross-class gate rejects a weapon write, so the model is
    /// driven natively there too, not through Glamourer — #79).
    /// </summary>
    private (WeaponModelId Main, WeaponModelId Off) ResolveNpcWeapons(CSCharacter* native, int idx, MobRow row)
    {
        var (main, off) = _npc.TryGetWeapons(row.BaseId);

        // Capture the TRUE pre-guise weapon once (kept across re-applies, like _originals) so Revert restores it.
        if (!_originalWeapons.ContainsKey(idx))
            _originalWeapons[idx] = new OrigWeapons(
                native->DrawData.Weapon(WeaponSlot.MainHand).ModelId,
                native->DrawData.Weapon(WeaponSlot.OffHand).ModelId);

        var mainModel = main is { } m ? ToWeaponModel(m) : default;
        var offModel  = off  is { } o ? ToWeaponModel(o) : default;
        _log.Information($"Guise[weapon] obj#{idx} '{row.DisplayName}' (base {row.BaseId}): " +
                         $"main {(main is { } mm ? $"{mm.Set}/{mm.Type}/{mm.Variant}" : "none -> cleared")}, " +
                         $"off {(off is { } oo ? $"{oo.Set}/{oo.Type}/{oo.Variant}" : "none -> cleared")}");
        return (mainModel, offModel);
    }

    /// <summary>
    /// Build the resolved weapon(s) onto a puppet/actor via the game's <c>LoadWeapon</c> — the ONLY thing that
    /// (re)creates the weapon draw object (a body redraw does not). Fired from the post-redraw continuation so
    /// the body draw object exists to attach to; re-fetches the actor from its index because the continuation
    /// runs a few ticks later and the object may be gone. <c>skipGameObject=0</c> writes the weapon into the
    /// game object too, so the puppet's stored weapon state matches what's drawn (survives draw/sheathe
    /// re-derivation and any external redraw) — safe here because a puppet has no authoritative weapon to
    /// protect, unlike Glamourer's human targets. Other params match Glamourer's proven call
    /// (redrawOnEquality=1). An empty (<c>default</c>) model clears the slot.
    /// </summary>
    private void LoadWeaponsNow(int idx, WeaponModelId main, WeaponModelId off)
    {
        if (_objects[idx] is not ICharacter c || c.Address == nint.Zero) return;
        var native = (CSCharacter*)c.Address;
        native->DrawData.LoadWeapon(WeaponSlot.MainHand, main, 1, 0, 0, 0, false);
        native->DrawData.LoadWeapon(WeaponSlot.OffHand,  off,  1, 0, 0, 0, false);
        _log.Debug($"Guise[weapon] obj#{idx}: LoadWeapon main {main.Id}/{main.Type}/{main.Variant}, off {off.Id}/{off.Type}/{off.Variant}");
    }

    /// <summary>
    /// Public parallel to <see cref="ResolveNpcWeapons"/>+<see cref="LoadWeaponsNow"/> for a HUMAN (McType 1)
    /// puppet, whose weapon CANNOT be driven through Glamourer: Glamourer's cross-class gate silently DROPS a
    /// MainHand write whose type differs from the actor's true (DM-clone) weapon, so the model is loaded NATIVELY
    /// here via the game's <c>LoadWeapon</c> — the same gate-free path the Monster/Demihuman guise uses. Called
    /// from HumanGuise's post-redraw continuation (puppet-only), AFTER the body redraw settles so the weapon draw
    /// object can attach and AFTER the cloned Glamourer weapon slots have been un-managed (Apply flags cleared) so
    /// they defer to this native drive instead of re-asserting the DM weapon every redraw. Captures the pre-guise
    /// weapon once (like <see cref="ResolveNpcWeapons"/>) so Revert restores it; a <c>default</c> model CLEARS the
    /// slot when the NPC has no weapon there.
    /// </summary>
    public void LoadNpcWeapon(int objectIndex, uint baseId, NpcSource source)
    {
        if (_objects[objectIndex] is not ICharacter c || c.Address == nint.Zero) return;
        var native = (CSCharacter*)c.Address;
        var (main, off) = _npc.TryGetWeapons(baseId, source);

        // Capture the TRUE pre-guise weapon once (kept across re-applies) so Revert restores it.
        if (!_originalWeapons.ContainsKey(objectIndex))
            _originalWeapons[objectIndex] = new OrigWeapons(
                native->DrawData.Weapon(WeaponSlot.MainHand).ModelId,
                native->DrawData.Weapon(WeaponSlot.OffHand).ModelId);

        var mainModel = main is { } m ? ToWeaponModel(m) : default;
        var offModel  = off  is { } o ? ToWeaponModel(o) : default;
        LoadWeaponsNow(objectIndex, mainModel, offModel);
        _log.Information($"Guise[weapon] obj#{objectIndex} human puppet base {baseId} ({source}): " +
                         $"main {(main is { } mm ? $"{mm.Set}/{mm.Type}/{mm.Variant}" : "none -> cleared")}, " +
                         $"off {(off is { } oo ? $"{oo.Set}/{oo.Type}/{oo.Variant}" : "none -> cleared")}");
    }

    /// <summary>Map an <see cref="NpcData.NpcWeapon"/> (Set/Type/Variant + 2 dyes) to the native
    /// <see cref="WeaponModelId"/> a draw-data slot holds (1:1: Set-&gt;Id, Type-&gt;Type, Variant-&gt;Variant,
    /// Dye-&gt;Stain0, Dye2-&gt;Stain1).</summary>
    private static WeaponModelId ToWeaponModel(NpcData.NpcWeapon w) => new()
    {
        Id      = w.Set,
        Type    = w.Type,
        Variant = w.Variant,
        Stain0  = w.Dye,
        Stain1  = w.Dye2,
    };

    /// <summary>
    /// Read whether an actor's weapon is currently DRAWN/visible — the inverse of the DrawData
    /// <c>IsWeaponHidden</c> bit. The LIVE actor is the source of truth, so the Spawn-tab checkbox reflects
    /// reality each frame without a shadow flag that could drift. False for a missing/undrawable actor.
    /// </summary>
    public bool GetWeaponDrawn(int objectIndex)
    {
        if (_objects[objectIndex] is not ICharacter c || c.Address == nint.Zero) return false;
        return !((CSCharacter*)c.Address)->DrawData.IsWeaponHidden;
    }

    /// <summary>
    /// Toggle whether an actor's weapon is DRAWN/visible — the /displayarms axis (<c>HideWeapons</c> /
    /// <c>IsWeaponHidden</c>). This is weapon VISIBILITY, NOT the weapon MODEL (Issue 1's
    /// <see cref="LoadWeaponsNow"/>) nor the combat STANCE (the BtlIdle "Draw weapon" animation). Calls the
    /// game's <c>HideWeapons</c> so the change re-derives on the drawn body immediately. Puppets are born
    /// weapon-shown (SpawnService clears the clone's inherited hide bit); this drives per-puppet changes after.
    /// </summary>
    public void SetWeaponDrawn(int objectIndex, bool drawn)
    {
        if (_objects[objectIndex] is not ICharacter c || c.Address == nint.Zero) return;
        ((CSCharacter*)c.Address)->DrawData.HideWeapons(!drawn);
        _log.Debug($"Guise[weapon] obj#{objectIndex}: weapon {(drawn ? "shown" : "hidden")}");
    }

    /// <summary>
    /// Change only the actor's scale, LIVE (a transform write, no redraw — see
    /// <see cref="WriteScaleLive"/> for why a redraw is actively wrong for a resize).
    /// Tracks an original if this is the first touch, so Revert restores the pre-guise
    /// size too. UN-gated (unlike <see cref="SetScale"/>): it self-seeds the original,
    /// so it lands on a bare, never-guised puppet (a blank clone) as well as an
    /// already-guised actor — the per-puppet scale slider drives this. A built Monster's
    /// size is baked at draw-build, so the GameObject.Scale write here survives to the
    /// next redraw but doesn't resize the live monster (H3), same as the self path.
    /// </summary>
    public void Resize(ICharacter chara, float scale)
    {
        var native = (CSCharacter*)chara.Address;
        if (native == null) return;

        if (!_originals.ContainsKey(chara.ObjectIndex))
            _originals[chara.ObjectIndex] = new Original(
                native->ModelContainer.ModelCharaId,
                native->GameObject.Scale,
                null,
                native->DrawData.IsHatHidden);

        // Keep the HEALABLE guise record's scale in step with the live write. ReassertModel heals a
        // drift by re-Apply-ing at applied.Scale, so a stale entry would silently revert this resize on
        // the next Penumbra/Glamourer re-sync heal — frequent on a peer mirror (the Galatea peer-shed),
        // making a synced resize flicker back to the size it was guised at. Self is excluded from the
        // heal so it doesn't need this, but a puppet / peer mirror does. No-op on a bare (un-guised) clone.
        if (_appliedGuises.TryGetValue(chara.ObjectIndex, out var applied))
            _appliedGuises[chara.ObjectIndex] = applied with { Scale = scale };

        WriteScaleLive(native, scale);
        _log.Information($"Guise: obj#{chara.ObjectIndex} resized to {scale:0.##} (live, no redraw)");
    }

    /// <summary>The actor's current logical size (GameObject.Scale) — the per-puppet scale slider seeds its
    /// initial read-out from this so the knob reflects the puppet's REAL size (native/custom/blank-1.0) rather
    /// than a hardcoded default. 1.0 for a null actor.</summary>
    public float GetScale(ICharacter chara)
    {
        var native = (CSCharacter*)chara.Address;
        return native == null ? 1f : native->GameObject.Scale;
    }

    /// <summary>
    /// Live-set the scale of an actor we've ALREADY guised (no model change), so the UI's
    /// scale controls land on the presently-active disguise immediately instead of waiting
    /// for a re-apply. <paramref name="scale"/> null restores the tracked original size (the
    /// "Off" setting). No-op if this actor isn't one we guised — we only own the size of
    /// actors we disguised, and a human (Glamourer) guise scales through Glamourer, not here.
    /// </summary>
    public void SetScale(ICharacter chara, float? scale)
    {
        if (!_originals.TryGetValue(chara.ObjectIndex, out var orig))
        {
            // DIAGNOSTIC (galatea-peer-shed / "scale slider does nothing" cue): the size knobs and the
            // disguise heal share this exact tracking (_originals is set beside _appliedGuises in ApplyNow).
            // A live scale write that no-ops here while the actor is VISIBLY disguised means the self-guise
            // tracking was dropped out from under us — the same drop that would silently kill ReassertModel
            // (peer shed). Throttled so a slider drag can't spam it.
            var nowMs = Environment.TickCount64;
            if (nowMs - _lastScaleProbeMs > 2000)
            {
                _lastScaleProbeMs = nowMs;
                _log.Warning($"Guise: obj#{chara.ObjectIndex} SetScale had NO EFFECT — not in _originals (not tracked as guised). If this actor is currently disguised, its guise tracking was dropped (same tracking ReassertModel heals with).");
            }
            return;
        }
        var native = (CSCharacter*)chara.Address;
        if (native == null) return;

        WriteScaleLive(native, scale ?? orig.Scale);
        _log.Debug($"Guise: obj#{chara.ObjectIndex} scale -> {(scale?.ToString("0.##") ?? $"original {orig.Scale:0.##}")} (live, no redraw)");
    }

    /// <summary>
    /// Set a vertical DRAW offset on the actor — shifts where the model renders relative to its
    /// logical (collision) position, WITHOUT moving the real character. Lets a DM drop a
    /// floor-clipping flyer or a too-high hovering boss onto the ground for an ambush, or lift a
    /// mob that spawned in the floor. Written through the game's own <c>GameObject.SetDrawOffset</c>
    /// (the same entry point heels and mount-float use, so it propagates to the render) and
    /// re-asserted per frame in <see cref="ReassertOffsets"/> against the game's periodic resets. An
    /// offset within <see cref="OffsetDrift"/> of 0 clears management and zeroes our contribution.
    /// Self-apply subject like the rest of HDM; coexists with SimpleHeels within tolerance.
    /// </summary>
    public void SetVerticalOffset(ICharacter chara, float offsetY)
    {
        var native = (CSCharacter*)chara.Address;
        if (native == null) return;
        var idx = chara.ObjectIndex;
        var cur = native->GameObject.DrawOffset;
        if (MathF.Abs(offsetY) < OffsetDrift)
        {
            _vOffsets.Remove(idx);
            native->GameObject.SetDrawOffset(cur.X, 0f, cur.Z); // drop our lift, keep any X/Z (heels etc.)
            _log.Information($"Guise: obj#{idx} vertical offset cleared");
            return;
        }
        _vOffsets[idx] = offsetY;
        native->GameObject.SetDrawOffset(cur.X, offsetY, cur.Z);
        _log.Information($"Guise: obj#{idx} vertical offset -> {offsetY:0.##}");
    }

    /// <summary>The vertical draw offset currently dialed in for this actor (0 if none) — UI slider read-back.</summary>
    public float GetVerticalOffset(int objectIndex) => _vOffsets.GetValueOrDefault(objectIndex);

    /// <summary>True if this actor is currently hidden from the render (see <see cref="SetHidden"/>) — the UI toggle reads this.</summary>
    public bool IsHidden(int objectIndex) => _hidden.Contains(objectIndex);

    /// <summary>True while a guise redraw (DisableDraw→EnableDraw, incl. the settle-then-apply two-phase) is in
    /// flight for this actor. PossessionService reads this to PAUSE its per-frame timeline drive on the puppet it
    /// pilots: an explicit re-guise tears the draw object down and rebuilds it, and a concurrent per-frame
    /// PlayTimeline write fights that rebuild (the same Timeline-vs-redraw conflict that <c>SuppressReassert</c>
    /// guards for the self-heal path) — which stranded the DM's own view on the OLD model while an un-possessed
    /// peer mirror rebuilt cleanly. Letting the redraw run uncontested lands the new model on both.</summary>
    public bool IsRedrawing(int objectIndex) => _redraws.ContainsKey(objectIndex);

    /// <summary>
    /// Hide or show the actor entirely — a DM "I'm here running the scene but not physically present"
    /// switch. Hiding holds a <c>DisableDraw</c> (re-asserted per frame against the game's own redraws);
    /// showing <c>EnableDraw</c>s and drops management. Independent of any guise: you can hide a guised
    /// or a bare character. Self-apply subject like the rest of HDM. Idempotent — a redundant call
    /// is a no-op. Call from the framework thread.
    /// </summary>
    public void SetHidden(ICharacter chara, bool hidden)
    {
        var native = (CSCharacter*)chara.Address;
        if (native == null) return;
        var idx = chara.ObjectIndex;
        if (hidden)
        {
            if (_hidden.Add(idx))
            {
                native->GameObject.DisableDraw();
                _log.Information($"Guise: obj#{idx} hidden");
            }
        }
        else if (_hidden.Remove(idx))
        {
            native->GameObject.EnableDraw();
            _log.Information($"Guise: obj#{idx} shown");
        }
    }

    /// <summary>
    /// Write an actor's scale live — no redraw. A redraw is actively wrong for a resize:
    /// scale doesn't touch ModelCharaId, and writing an UNCHANGED ModelCharaId sets no
    /// dirty flag, so a DisableDraw/EnableDraw cycle rebuilds the actor's underlying model
    /// (the player's own body) and drops the disguise — the "scale button reverts to the
    /// real character and resizes that" bug. Instead we set the logical GameObject.Scale
    /// (so the size survives any LATER, legitimate redraw) AND the rendered draw-object
    /// transform scale, then call the game's own NotifyTransformChanged (the inlined routine
    /// it runs after any transform edit: sets the IsTransformChanged flag and, when the model
    /// is loaded, calls UpdateTransforms) so the new size shows this frame. DrawObject can be
    /// null briefly while a model loads — the GameObject.Scale write still lands and the next
    /// draw initialises the transform from it.
    /// </summary>
    private static void WriteScaleLive(CSCharacter* native, float scale)
    {
        native->GameObject.Scale = scale;
        var draw = native->GameObject.DrawObject;
        if (draw != null)
        {
            draw->Object.Scale = new CSVector3(scale);
            draw->NotifyTransformChanged();
        }
    }

    /// <summary>Restore the character's original model and redraw. <paramref name="onSettled"/>, when
    /// supplied, runs ONCE the actor is a stable, drawable skeleton again (after this revert's async
    /// redraw settles) — used by the Monster/Demihuman→Human transition to defer the Glamourer paint
    /// until the c-skeleton has rebuilt. Painting mid-rebuild lands on a half-torn-down draw object and
    /// drops the torso/gear (the "human guise only applies on the 2nd click after an m_ disguise" bug).
    /// When there is nothing to revert (or the actor is gone) it runs immediately, so a fresh Human
    /// apply — with no prior swap to unwind — is never delayed.</summary>
    public void Revert(ICharacter chara, Action? onSettled = null)
    {
        if (!_originals.Remove(chara.ObjectIndex, out var orig)) { onSettled?.Invoke(); return; }
        _currentKind.Remove(chara.ObjectIndex);
        _appliedGuises.Remove(chara.ObjectIndex); // stop healing a guise we're deliberately reverting
        var native = (CSCharacter*)chara.Address;
        if (native == null) { onSettled?.Invoke(); return; }

        // Drop any dialed-in elevation so a reverted character isn't left floating or sunk.
        if (_vOffsets.Remove(chara.ObjectIndex))
        {
            var cur = native->GameObject.DrawOffset;
            native->GameObject.SetDrawOffset(cur.X, 0f, cur.Z);
        }

        native->ModelContainer.ModelCharaId = orig.ModelCharaId;
        native->GameObject.Scale = orig.Scale;
        if (orig.Equipment != null)
            WriteEquipment(native, orig.Equipment);
        // Restore the actor's own weapon if a Monster/Demihuman guise overwrote it (ResolveNpcWeapons captured
        // it). Like the body, the weapon draw object is only rebuilt by LoadWeapon AFTER the redraw settles — a
        // field-write wouldn't rebuild the separate Weapon* draw object. Only restore when reverting to the DM's
        // true self (onSettled == null); a revert that continues into a Human paint lets HumanGuise/Glamourer
        // set the weapon, so don't fight it here (just drop the tracking).
        var haveWeapon = _originalWeapons.Remove(chara.ObjectIndex, out var ow);
        // Restore the player's own head-gear-visibility preference (a demihuman guise
        // forces it visible so the head mesh shows; put it back on the way out).
        if (native->DrawData.IsHatHidden != orig.HatHidden)
            native->DrawData.IsHatHidden = orig.HatHidden;
        // BeginRedraw overwrites any in-flight job for this index, which cancels a
        // queued re-apply if the user reverts mid-sequence — the desired behaviour.
        int revIdx = chara.ObjectIndex;
        Action? cont = onSettled;
        if (onSettled == null && haveWeapon)
            cont = () => LoadWeaponsNow(revIdx, ow.Main, ow.Off);
        BeginRedraw(chara.ObjectIndex, native, null, null, cont);
        _log.Information($"Guise: obj#{chara.ObjectIndex} reverted to ModelChara {orig.ModelCharaId}{(onSettled != null ? " (then paint human)" : "")}");
    }

    /// <summary>
    /// Revert the LOCAL PLAYER even when we hold no tracked original — the guaranteed un-stick. The
    /// normal <see cref="Revert"/> no-ops when <c>_originals</c> has no entry for the index, which is
    /// exactly how a DM gets permanently stuck: a zone change dropped the tracking while the swapped
    /// <c>ModelCharaId</c> rode along on the (never-destroyed) player object, so nothing knows how to put
    /// it back (the "stuck m_ across a zone, nothing to revert to" report). A real player's true model is
    /// ModelCharaId 0 at scale 1, so when the caller vouches this is the local player and it's still
    /// wearing a non-zero NPC model with nothing tracked, force it back to baseline and redraw. If we DO
    /// hold tracking, the precise <see cref="Revert"/> runs instead. Self-only: forcing 0 is correct for a
    /// player but NOT for an arbitrary NPC, so only ever pass the local player here.
    /// </summary>
    public void RevertLocalPlayerHard(ICharacter localPlayer, Action? onSettled = null)
    {
        if (_originals.ContainsKey(localPlayer.ObjectIndex)) { Revert(localPlayer, onSettled); return; }

        var native = (CSCharacter*)localPlayer.Address;
        if (native == null) { onSettled?.Invoke(); return; }
        if (native->ModelContainer.ModelCharaId == 0) { onSettled?.Invoke(); return; } // already the real player model

        _log.Information($"Guise: obj#{localPlayer.ObjectIndex} FORCE-revert (untracked stuck model {native->ModelContainer.ModelCharaId} -> 0).");
        native->ModelContainer.ModelCharaId = 0;
        native->GameObject.Scale = 1.0f;
        // Drop any managed lift / hidden hold dangling on the player so a forced un-stick leaves them
        // fully normal (visible, grounded), not floating or invisible from a stale re-assert entry.
        var cur = native->GameObject.DrawOffset;
        native->GameObject.SetDrawOffset(cur.X, 0f, cur.Z);
        _vOffsets.Remove(localPlayer.ObjectIndex);
        _hidden.Remove(localPlayer.ObjectIndex);
        _appliedGuises.Remove(localPlayer.ObjectIndex);
        // If a self-disguise had written an NPC weapon, restore the DM's own via LoadWeapon after the rebuild
        // (a field-write wouldn't rebuild the separate Weapon* draw object) so the force-unstick doesn't leave
        // them holding a mob weapon (usually a no-op here — the untracked path means _originalWeapons rarely has
        // the index — but drops any stale capture defensively).
        var haveWeapon = _originalWeapons.Remove(localPlayer.ObjectIndex, out var ow);
        int revIdx = localPlayer.ObjectIndex;
        Action? cont = onSettled;
        if (onSettled == null && haveWeapon)
            cont = () => LoadWeaponsNow(revIdx, ow.Main, ow.Off);
        BeginRedraw(localPlayer.ObjectIndex, native, null, null, cont);
    }

    /// <summary>Return the LOCAL player's own body to clean baseline — the exit-edge sanitiser that
    /// <see cref="OnLogout"/>, the opt-in map-hop revert, and <c>HDM.SanitizeSelf</c> all route through.
    /// Zeroes the PERSISTENT own-body fields that survive a logout — <c>GameObject.Scale</c> and the vertical
    /// draw offset — plus any hidden hold, ALWAYS; those are the fields that stranded a DM downsized + floating
    /// past logout (ModelCharaId is NOT among them: the login rebuild resets it to 0, so the look self-heals).
    /// <paramref name="restoreVisual"/> true additionally reverts the model WITH a redraw (map-hop: the body
    /// persists and the DM must see their real self); false skips the redraw (logout: the screen is fading and
    /// relog rebuilds the model anyway) while still writing the cheap scale/offset fields. Idempotent and
    /// self-only; guarded field writes no-op if the local player isn't resolvable, so it is safe on a teardown
    /// edge before native despawn. Framework thread.</summary>
    public void SanitizeLocalPlayer(bool restoreVisual)
    {
        if (_objects.LocalPlayer is not { } lp || lp.Address == nint.Zero) return;
        int idx = lp.ObjectIndex;
        var native = (CSCharacter*)lp.Address;
        if (native == null) return;

        // Persistent fields FIRST — the ones that leak past logout, and the Human-guise elevation the model
        // path below wouldn't cover (a Glamourer guise lifts through GuiseService with no _originals capture).
        // Zero the managed lift and un-hide unconditionally.
        if (_vOffsets.Remove(idx))
        {
            var cur = native->GameObject.DrawOffset;
            native->GameObject.SetDrawOffset(cur.X, 0f, cur.Z);
        }
        if (_hidden.Remove(idx))
            native->GameObject.EnableDraw();

        if (restoreVisual)
        {
            // Model + scale + equipment + weapon WITH the native redraw (tracked -> precise Revert; untracked-
            // but-stuck -> force baseline). Scale is restored by that path.
            RevertLocalPlayerHard(lp);
            // A Human (Glamourer) guise carries no _originals capture, so RevertLocalPlayerHard leaves its
            // appearance untouched — drop it here too. No-op for a Monster/Demihuman guise.
            RevertHumanAppearance?.Invoke(idx);
        }
        else
        {
            // Cheap exit path: write the persistent Scale field directly (a player's true scale is 1.0 — the
            // same value RevertLocalPlayerHard forces) and drop local tracking WITHOUT queuing a redraw the
            // fading logout frame would waste. The model is left as-is; the login rebuild resets ModelCharaId.
            native->GameObject.Scale = 1.0f;
            _originals.Remove(idx);
            _originalWeapons.Remove(idx);
            _currentKind.Remove(idx);
            DropApplied(idx, "SanitizeLocalPlayer (logout cheap path)");
        }
    }

    /// <summary>
    /// Force a bare native redraw (DisableDraw → settle → EnableDraw) on an actor WITHOUT touching its
    /// model, scale, or equipment — the rebuild the Human (Glamourer) path needs on a freshly spawned
    /// puppet. A cold first-spawn puppet's draw object accepts a Glamourer ApplyState into STORED state but
    /// doesn't RENDER a customize/Race change: the equipment swaps in place, but the skeleton never rebuilds,
    /// so the first puppet keeps the DM's race until a 2nd spawn warms Glamourer's pipeline (the "first spawn
    /// caches the last disguise" report — race half). Tearing the draw object down and rebuilding it makes
    /// Glamourer re-apply its stored NPC state (customize + Race) as the new skeleton is built — the same
    /// mechanism that makes the MONSTER puppet path (which always redraws via <see cref="Apply"/>) correct on
    /// its first spawn, and exactly what Penumbra's redraw does for Glamourer, minus the Penumbra dependency.
    /// <paramref name="onSettled"/> (when supplied) runs once the rebuild has settled into a drawable
    /// skeleton — <see cref="HumanGuise"/> passes a re-assert of the NPC state there, so the guise lands even
    /// if Glamourer doesn't auto-re-apply on a native redraw. No <c>_originals</c> capture (the Human path
    /// owns its own revert); this only drives the teardown/rebuild machine <see cref="OnUpdate"/> already
    /// runs. Fires onSettled immediately if the index isn't a live character. Framework thread.
    /// </summary>
    public void Redraw(int objectIndex, Action? onSettled = null)
    {
        var go = _objects[objectIndex];
        if (go is not ICharacter c || c.Address == nint.Zero) { onSettled?.Invoke(); return; }
        var native = (CSCharacter*)c.Address;
        BeginRedraw(objectIndex, native, null, null, onSettled);
    }

    /// <summary>
    /// Drop ALL tracking for an object index WITHOUT touching game state — for an actor that is being
    /// destroyed (a despawned puppet), where a Revert's model-restore + redraw would be wasted on a
    /// doomed object and, worse, the redraw job could poke whatever the game recycles into that slot.
    /// The object index is freed the instant its owner deletes it, so the only correct cleanup is to
    /// forget it: any stale <c>_vOffsets</c>/<c>_hidden</c>/<c>_redraws</c> entry left behind would make
    /// this per-frame loop re-assert an offset or a DisableDraw on the NEXT object handed that index.
    /// Distinct from <see cref="Revert"/> (restore the real model on a LIVE actor) and from the
    /// TerritoryChanged/Logout clear (drop EVERYTHING because the whole table rebuilt).
    /// </summary>
    public void Forget(int objectIndex)
    {
        _originals.Remove(objectIndex);
        _originalWeapons.Remove(objectIndex);
        _currentKind.Remove(objectIndex);
        DropApplied(objectIndex, "Forget (puppet despawn/cleanup)");
        _redraws.Remove(objectIndex);
        _vOffsets.Remove(objectIndex);
        _hidden.Remove(objectIndex);
    }

    /// <summary>Revert every tracked character whose object is still live.</summary>
    public void RevertAll()
    {
        foreach (var idx in new List<int>(_originals.Keys))
        {
            var go = _objects[idx];
            if (go is ICharacter c && c.Address != nint.Zero)
                Revert(c);
            else
            {
                _originals.Remove(idx);
                _originalWeapons.Remove(idx);
                _currentKind.Remove(idx);
                DropApplied(idx, "RevertAll (actor gone)");
            }
        }
    }

    /// <summary>Zero every dialed-in vertical offset on still-live actors, then drop tracking.
    /// Covers offset-only actors that <see cref="RevertAll"/> misses (an elevation set without a
    /// model guise leaves no entry in <c>_originals</c>). Called on dispose so no floating body is
    /// left behind.</summary>
    private void ClearAllOffsets()
    {
        foreach (var idx in new List<int>(_vOffsets.Keys))
        {
            if (_objects[idx] is ICharacter c && c.Address != nint.Zero)
            {
                var native = (CSCharacter*)c.Address;
                var cur = native->GameObject.DrawOffset;
                native->GameObject.SetDrawOffset(cur.X, 0f, cur.Z);
            }
        }
        _vOffsets.Clear();
    }

    private void BeginRedraw(int objectIndex, CSCharacter* native, MobRow? thenRow, float? thenScale, Action? thenApply = null)
    {
        native->GameObject.DisableDraw();
        _redraws[objectIndex] = new RedrawJob
        {
            Ticks = 0,
            Phase = RedrawPhase.WaitEnable,
            ThenRow = thenRow,
            ThenScale = thenScale,
            ThenApply = thenApply,
        };
    }

    private void OnUpdate(IFramework _)
    {
        DiagnoseSelf();
        ReassertOffsets();
        ReassertHidden();
        ReassertModel();
        if (_redraws.Count == 0) return;
        foreach (var idx in new List<int>(_redraws.Keys))
        {
            var job = _redraws[idx];
            job.Ticks++;

            var go = _objects[idx];
            if (go == null || go.Address == nint.Zero) { _redraws.Remove(idx); continue; }
            var native = (CSCharacter*)go.Address;

            switch (job.Phase)
            {
                case RedrawPhase.WaitEnable:
                    // Let the teardown settle for a couple ticks (never disable+enable
                    // in the same frame), then wait until the model is ready to draw —
                    // capped so a model that never reports ready still re-enables rather
                    // than staying invisible forever.
                    if (job.Ticks < MinDrawWaitTicks) continue;
                    if (!native->GameObject.IsReadyToDraw() && job.Ticks < MaxDrawWaitTicks) continue;

                    native->GameObject.EnableDraw();
                    if (job.ThenRow is not null || job.ThenApply is not null)
                    {
                        // Revert redraw done, but a continuation is queued — either a monster
                        // re-apply row (ThenRow) or a Human Glamourer paint (ThenApply). Hold the
                        // reverted model a few frames so the draw object fully rebuilds as the
                        // original, THEN run the continuation (the second half of the fix).
                        job.Phase = RedrawPhase.SettleThenApply;
                        job.Ticks = 0;
                    }
                    else
                    {
                        _redraws.Remove(idx);
                    }
                    continue;

                case RedrawPhase.SettleThenApply:
                    if (job.Ticks < SettleTicks) continue;
                    if (!native->GameObject.IsReadyToDraw() && job.Ticks < MaxDrawWaitTicks) continue;
                    _redraws.Remove(idx);
                    if (job.ThenRow is not null)
                        ApplyNow(native, idx, job.ThenRow, job.ThenScale); // monster re-apply: starts its own WaitEnable redraw
                    else
                        job.ThenApply?.Invoke();                            // Human paint: the actor is a stable c-skeleton now
                    continue;
            }
        }
    }

    // ── DIAGNOSTIC (self flicker + "dead slider" regression hunt, b0889) ────────────────────────────
    // HGuise ("fork one") resized a MONSTER (Galatea Magna) INSTANTLY through the SAME WriteScaleLive
    // transform write and never flickered — so both symptoms are a REGRESSION, not a monster limit
    // (that kills the earlier "monsters only resize on redraw" theory). ReassertModel already skips self,
    // so OUR per-frame loop isn't touching the local player; something else fires at the flicker cadence.
    // Watch the LOCAL PLAYER's disguise each tick and log ONLY on change, to reveal: (a) a periodic draw
    // REBUILD (drawPtr changes → an external Penumbra/Glamourer/Mare/HMS redraw = the flicker), (b) whether
    // a live scale write lands (objScale) AND can propagate (NotifyTransformChanged only fires
    // UpdateTransforms when the draw object's LoadState byte @0x89 == 3 — if self sits below 3 the slider
    // silently no-ops), and (c) which scale field the render actually reads (obj @0x70 vs vfx/glob/mdl).
    private nint _dbgDraw;
    private int _dbgModel = int.MinValue;
    private int _dbgType = -1;
    private float _dbgGo = float.NaN;
    private float _dbgObj = float.NaN;
    private byte _dbgLoad = 0xFF;
    private long _dbgAtMs;
    private void DiagnoseSelf()
    {
        var lp = _objects.LocalPlayer;
        if (lp == null || lp.Address == nint.Zero) return;
        if (!_appliedGuises.TryGetValue(lp.ObjectIndex, out var applied)) { _dbgModel = int.MinValue; return; }
        var native = (CSCharacter*)lp.Address;
        var draw = native->GameObject.DrawObject;
        var drawPtr = (nint)draw;
        var cb = (CharacterBase*)draw;
        var model = native->ModelContainer.ModelCharaId;
        var go = native->GameObject.Scale;
        var obj = draw != null ? draw->Object.Scale.X : float.NaN;
        var type = draw != null ? (int)cb->GetModelType() : 0;
        var load = draw != null ? *(byte*)((byte*)draw + 0x89) : (byte)0;

        // Gate on STRUCTURAL change only (draw ptr / model id / model type / load state) — NOT on scale. A
        // scale drag moves goScale/objScale every frame; gating on them printed a line per drag frame and
        // buried any real redraw. Now that scale-commit is a live transform write (no redraw), the only thing
        // that should move these four is an ACTUAL rebuild, so a line here means a genuine self redraw/flicker,
        // captured clean. goScale/objScale/glob/mdl are still PRINTED (the size-lever read-out) — just not a trigger.
        if (drawPtr == _dbgDraw && model == _dbgModel && type == _dbgType && load == _dbgLoad)
            return;

        var nowMs = Environment.TickCount64;
        var dt = _dbgAtMs == 0 ? 0 : nowMs - _dbgAtMs;
        float vfx = draw != null ? cb->VfxScale : float.NaN;
        float glob = draw != null ? *(float*)((byte*)draw + 0x2A0) : float.NaN;
        float mdl = draw != null ? *(float*)((byte*)draw + 0x2A4) : float.NaN;
        _log.Information($"SELF-DBG obj#{lp.ObjectIndex} +{dt}ms: draw {_dbgDraw:X}->{drawPtr:X} | model {_dbgModel}->{model} | type {_dbgType}->{type} | load {_dbgLoad}->{load} ready={native->GameObject.IsReadyToDraw()} | goScale {_dbgGo:0.###}->{go:0.###} | objScale {_dbgObj:0.###}->{obj:0.###} | vfx {vfx:0.###} glob {glob:0.###} mdl {mdl:0.###} | applied {applied.Row.ModelCharaId}('{applied.Row.DisplayName}') McType {applied.Row.McType}");
        _dbgDraw = drawPtr; _dbgModel = model; _dbgType = type; _dbgLoad = load; _dbgGo = go; _dbgObj = obj; _dbgAtMs = nowMs;
    }

    // Re-apply dialed-in vertical offsets the game has reset past OffsetDrift. The game rewrites
    // DrawOffset on mount/emote/float-height changes, so a one-shot SetDrawOffset doesn't hold;
    // this pins it. Gated on drift so we don't fight a coexisting heel plugin every frame (a
    // few-cm difference is left alone; a full reset toward 0 is re-applied). Cheap when idle.
    private void ReassertOffsets()
    {
        if (_vOffsets.Count == 0) return;
        foreach (var (idx, want) in _vOffsets)
        {
            if (_objects[idx] is not ICharacter c || c.Address == nint.Zero) continue;
            var native = (CSCharacter*)c.Address;
            var cur = native->GameObject.DrawOffset;
            if (MathF.Abs(cur.Y - want) > OffsetDrift)
                native->GameObject.SetDrawOffset(cur.X, want, cur.Z);
        }
    }

    // Keep a held-hidden actor down against the game's periodic redraws. DisableDraw nulls the draw
    // object, so we only re-issue it once the game has rebuilt one (DrawObject non-null) — cheap and
    // flicker-free when idle. Gated on the set being non-empty (the common case is empty).
    private void ReassertHidden()
    {
        if (_hidden.Count == 0) return;
        foreach (var idx in _hidden)
        {
            if (_objects[idx] is not ICharacter c || c.Address == nint.Zero) continue;
            var native = (CSCharacter*)c.Address;
            if (native->GameObject.DrawObject != null)
                native->GameObject.DisableDraw();
        }
    }

    // Re-assert a Monster/Demihuman disguise a competing writer has reset out from under a DRIVEN actor. Two
    // victims, both NON-LOCAL: (1) a freshly SPAWNED PUPPET — its deferred guise has to land over a bare
    // player-clone; (2) on a peer, the DM's MIRROR — the peer's Penumbra/Glamourer re-sync rebuilds the real
    // body over our monster while ModelCharaId stays ours (the SILENT draw-level Galatea shed). The LOCAL
    // PLAYER is deliberately EXCLUDED (see the selfIdx skip below): its own client holds a self-disguise after
    // the single apply redraw — the spawn clone copies FROM the DM into the puppet and never writes the DM's
    // own id (SpawnService CopyFromCharacter(meNative) is source->puppet), so self is never a victim here.
    // Same shape as ReassertOffsets/ReassertHidden: fire only on ACTUAL drift, and route through the normal
    // Apply path so a demihuman gets its clean two-phase rebuild and a monster its single redraw — each
    // re-dirties the id (original->disguise is a genuine change, so no unchanged-id trap). Skipped while a
    // redraw is already in flight (a legitimate transition owns the model then) and while the index is being
    // piloted by possession (a re-assert's redraw would null the Timeline its animation reads). Cheap when idle.
    private void ReassertModel()
    {
        if (_appliedGuises.Count == 0) return;
        // ROOT-CAUSE fix for the self "flicker + dead scale slider": the LOCAL PLAYER is piloted by the game's
        // own systems (Principle 1), so a self-disguise is one ModelChara swap + redraw the client then HOLDS
        // on its own — exactly how the first HGuise build ("fork one") worked, applied once and never
        // re-asserted. Healing self instead re-reads the transient underlying-body frame right after the
        // disguise's own EnableDraw as a shed, firing a second redraw (the visible flicker) and re-applying the
        // STALE stored scale over a live WriteScaleLive (the slider that "does nothing"). Neither shed the heal
        // guards can hit self (both are non-local — see method note), so exclude it and let self hold clean.
        var selfIdx = _objects.LocalPlayer?.ObjectIndex ?? -1;
        foreach (var idx in new List<int>(_appliedGuises.Keys))
        {
            if (idx == selfIdx) continue; // native systems keep the local player's model coherent; no heal needed
            if (_redraws.ContainsKey(idx)) { NoteGatedSkip(idx, "redraw in-flight"); continue; }        // a transition/redraw owns the model now
            if (SuppressReassert?.Invoke(idx) == true) { NoteGatedSkip(idx, "possession-suppressed"); continue; } // possession is piloting this puppet
            if (!_appliedGuises.TryGetValue(idx, out var applied)) continue;
            if (_objects[idx] is not ICharacter c || c.Address == nint.Zero) continue;
            var native = (CSCharacter*)c.Address;

            // Two INDEPENDENT sheds, and the field only reveals one:
            //  - FIELD drift: our swapped ModelCharaId was overwritten (a redraw rebuilt the actor from its
            //    underlying model, dropping the id). The classic self/puppet shed; healed unbounded below.
            //  - DRAW-LEVEL shed: the id is INTACT but the RENDERED draw object is the wrong model type. On a
            //    peer, its Penumbra/Glamourer re-sync rebuilds the DM's real (human) body over our monster while
            //    leaving ModelCharaId == 3723, so a field-only check reads "no drift" and the disguise sheds
            //    SILENTLY (the Galatea peer-shed — no log ever fired because the id never moved). Read the drawn
            //    model type: CharacterBase.ModelType shares McType's 1/2/3 numbering, so a Monster/Demihuman guise
            //    whose draw object reports a different type is a shed the id cannot expose.
            var liveId = native->ModelContainer.ModelCharaId;
            var fieldDrift = liveId != applied.Row.ModelCharaId;
            var drawObj = (CharacterBase*)native->GameObject.DrawObject;
            var drawType = drawObj != null ? (int)drawObj->GetModelType() : 0; // 0 = not drawn yet (mid-load) — not a shed
            var drawDrift = drawType != 0 && drawType != applied.Row.McType;
            if (!fieldDrift && !drawDrift) continue; // neither kind of drift

            var nowMs = Environment.TickCount64;
            _driftStreak[idx] = (_lastDriftMs.TryGetValue(idx, out var lastMs) && nowMs - lastMs < DriftStreakWindowMs)
                ? _driftStreak.GetValueOrDefault(idx) + 1 : 1;
            _lastDriftMs[idx] = nowMs;
            var streak = _driftStreak[idx];

            if (fieldDrift)
            {
                if (streak >= DriftStreakWarn)
                    _log.Warning($"Guise: obj#{idx} model RE-DRIFTING (streak {streak}, live {liveId} != applied {applied.Row.ModelCharaId}) — a competing writer (peer Mare/Glamourer re-apply?) is out-racing the heal for '{applied.Row.DisplayName}'.");
                else
                    _log.Information($"Guise: obj#{idx} model drifted (live {liveId} != applied {applied.Row.ModelCharaId}) — re-asserting '{applied.Row.DisplayName}'.");
                Apply(c, applied.Row, applied.Scale);
            }
            else if (streak <= DrawShedReassertCap)
            {
                // Draw-level shed, id still ours: force a genuine rebuild (Apply's same-id path reverts→re-applies)
                // so the freshly-drawn body is our model again. Bounded — wins once the mob model is cached locally.
                _log.Warning($"Guise: obj#{idx} DRAW-LEVEL shed — id intact ({liveId}='{applied.Row.DisplayName}') but the drawn body is model-type {drawType}, not McType {applied.Row.McType}. A peer redraw (Penumbra/Glamourer re-sync) rebuilt the real body over the disguise; re-asserting (attempt {streak}/{DrawShedReassertCap}).");
                Apply(c, applied.Row, applied.Scale);
            }
            else if (streak == DrawShedReassertCap + 1)
            {
                _log.Error($"Guise: obj#{idx} DRAW-LEVEL shed for '{applied.Row.DisplayName}' keeps being RE-OVERRIDDEN after {DrawShedReassertCap} re-asserts (drawn body stays model-type {drawType} while our id holds at {liveId}). A raw ModelChara swap cannot out-race a Penumbra/Glamourer draw rebuild — the peer's disguise must be painted THROUGH Glamourer or that actor's sync suppressed. Halting re-assert to avoid a flicker war.");
            }
            // streak > cap+1: war already diagnosed and halted; stay silent until the streak window resets.
        }
    }

    /// <summary>DIAGNOSTIC (Galatea peer-shed): a tracked guise skipped by a ReassertModel gate is invisible.
    /// If that index has ALSO drifted, the heal is silently losing to the gate rather than to a competing
    /// writer — the "silent shed" cause. Logs the coincidence at Warning, throttled per index. Harmless when
    /// the gated index hasn't drifted (the common, correct case: a real in-flight transition).</summary>
    private void NoteGatedSkip(int idx, string gate)
    {
        if (!_appliedGuises.TryGetValue(idx, out var applied)) return;
        if (_objects[idx] is not ICharacter c || c.Address == nint.Zero) return;
        var native = (CSCharacter*)c.Address;
        if (native->ModelContainer.ModelCharaId == applied.Row.ModelCharaId) return; // not drifted; gate is harmless
        var nowMs = Environment.TickCount64;
        if (_lastGateLogMs.TryGetValue(idx, out var last) && nowMs - last < DriftStreakWindowMs) return;
        _lastGateLogMs[idx] = nowMs;
        _log.Warning($"Guise: obj#{idx} DRIFTED (live {native->ModelContainer.ModelCharaId} != applied {applied.Row.ModelCharaId} '{applied.Row.DisplayName}') but the heal is GATED OFF by [{gate}] — if this persists it IS the shed.");
    }

    /// <summary>DIAGNOSTIC (Galatea peer-shed): drop an index's heal-tracking and NAME the path that did it, so a
    /// peer-side shed of a disguise reveals exactly which caller removed its self-heal. Non-behavioral — the same
    /// Remove as before, plus one Information line when something was actually tracked.</summary>
    private void DropApplied(int idx, string reason)
    {
        if (_appliedGuises.Remove(idx))
            _log.Information($"Guise: obj#{idx} heal-tracking dropped [{reason}].");
    }

    /// <summary>Re-enable draw on every hidden actor still live, then drop tracking. Called on dispose so
    /// a plugin unload never leaves the player invisible (the redraw machine's ClearAllOffsets sibling).</summary>
    private void ShowAllHidden()
    {
        foreach (var idx in new List<int>(_hidden))
        {
            if (_objects[idx] is ICharacter c && c.Address != nint.Zero)
                ((CSCharacter*)c.Address)->GameObject.EnableDraw();
        }
        _hidden.Clear();
    }

    // A territory change frees and RECYCLES every NPC/puppet object index, so tracking for THOSE is stale
    // and must be dropped. But the LOCAL PLAYER (index 0) is NOT destroyed by a zone change — its
    // GameObject persists and CARRIES the swapped ModelCharaId across the zone line. Clearing its tracking
    // here (as this used to) stranded the disguise: still worn, but untracked, so Revert had nothing to
    // restore, and the next apply re-captured the DISGUISED model as the "original" — the "stuck m_ across
    // a zone, nothing to revert to" bug. So keep the local player's disguise state across a zone (it stays
    // valid and revertable) and drop only the freed NPC/puppet indices. Redraw jobs are always dropped —
    // the zone reload redraws every actor, so an in-flight DisableDraw→Enable job is moot.
    // OPT-IN (Config "Clear disguises on map change", off by default): also strip the DM's OWN disguise at the
    // zone line. The local player persists WITH its swapped ModelCharaId here (the model will NOT self-heal,
    // unlike a logout where the login rebuild resets it), so that revert needs a real redraw — it runs AFTER
    // _redraws.Clear() so the revert's own redraw job survives instead of being wiped with the moot NPC jobs.
    private void OnTerritoryChanged(uint _)
    {
        var me = _objects.LocalPlayer?.ObjectIndex ?? 0;
        DropAllExcept(me);
        _redraws.Clear();

        if (ClearDisguiseOnMapChange)
        {
            try { SanitizeLocalPlayer(restoreVisual: true); }
            catch (Exception e) { _log.Error(e, "Guise: OnTerritoryChanged SanitizeLocalPlayer failed"); }
        }
    }

    // A logout resets the local player's MODEL (the login rebuild puts ModelCharaId back to 0, so the look
    // self-heals) but NOT GameObject.Scale or the vertical DrawOffset — those are plain object fields that
    // SURVIVE the round trip, so dropping tracking without reverting stranded the DM downsized + floating (the
    // "wisp still downsized and raised after logout" bug). Sanitise the own body FIRST via the cheap field path
    // (no redraw, safe while the object is live, a guarded no-op if it is already gone) THEN drop the rest of
    // the now-dying actors' tracking. HMS also fires HDM.SanitizeSelf on its own known-body-live logout edge as
    // the reliable belt; this is HDM's best-effort self-trigger.
    private void OnLogout(int type, int code)
    {
        if (_appliedGuises.Count > 0) _log.Information($"Guise: OnLogout sanitising own body + dropping heal-tracking ({_appliedGuises.Count} guise(s)).");
        try { SanitizeLocalPlayer(restoreVisual: false); }
        catch (Exception e) { _log.Error(e, "Guise: OnLogout SanitizeLocalPlayer failed"); }
        _originals.Clear(); _originalWeapons.Clear(); _currentKind.Clear(); _appliedGuises.Clear(); _redraws.Clear(); _vOffsets.Clear(); _hidden.Clear();
    }

    /// <summary>Drop every tracked index EXCEPT <paramref name="keep"/> (the local player) across all the
    /// disguise dictionaries — used on a zone change, where the player persists but every other actor is
    /// freed and its index recycled.</summary>
    private void DropAllExcept(int keep)
    {
        foreach (var k in new List<int>(_originals.Keys))       if (k != keep) _originals.Remove(k);
        foreach (var k in new List<int>(_originalWeapons.Keys)) if (k != keep) _originalWeapons.Remove(k);
        foreach (var k in new List<int>(_currentKind.Keys))     if (k != keep) _currentKind.Remove(k);
        foreach (var k in new List<int>(_appliedGuises.Keys)) if (k != keep) DropApplied(k, "DropAllExcept (zone change)");
        foreach (var k in new List<int>(_vOffsets.Keys))      if (k != keep) _vOffsets.Remove(k);
        _hidden.RemoveWhere(k => k != keep);
    }

    public void Dispose()
    {
        // Plugin unload must leave no mutated game state behind.
        try { RevertAll(); ClearAllOffsets(); ShowAllHidden(); }
        catch (Exception e) { _log.Error(e, "Guise: RevertAll on dispose failed"); }
        _framework.Update -= OnUpdate;
        _clientState.TerritoryChanged -= OnTerritoryChanged;
        _clientState.Logout -= OnLogout;
    }

    private enum RedrawPhase { WaitEnable, SettleThenApply }

    /// <summary>An in-flight redraw. <see cref="ThenRow"/> non-null means this is the revert half of a
    /// demihuman re-apply: once it settles, that row is applied. <see cref="ThenApply"/> non-null means
    /// this is the revert half of a Monster/Demihuman→Human transition: once it settles, that callback
    /// (the Glamourer paint) runs against the now-drawable c-skeleton. At most one is set.</summary>
    private sealed class RedrawJob
    {
        public int Ticks;
        public RedrawPhase Phase;
        public MobRow? ThenRow;
        public float? ThenScale;
        public Action? ThenApply;
    }
}
