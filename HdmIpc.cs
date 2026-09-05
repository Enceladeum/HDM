using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;

namespace HDM;

/// <summary>
/// The disguise atom — the minimal, patch-consistent description of ONE actor's disguise that crosses the
/// wire between HDM instances (via HMS as the courier). It is deliberately tiny: a peer reconstructs the
/// full NpcEquip / BNpcCustomize from its OWN game sheets keyed by <see cref="BaseId"/>, so we never ship
/// a 10-slot equipment array or a 26-byte customize block the receiver would only rebuild anyway. Layout is
/// frozen against the HMS-side brief (docs/hms-disguise-sync-brief.md §3) — HMS keeps a structurally
/// identical mirror and (de)serializes this exact JSON, so property NAMES here are the contract. Additive
/// fields are safe (an older mirror ignores unknown names, and JSON from an older sender leaves new fields
/// at their defaults), exactly like HMoniker's NameData / SimpleHeels' IpcCharacterConfig.
/// </summary>
public sealed class DisguiseAtom
{
    /// <summary>Monotonic per-source counter, bumped on EVERY atom change across the DM's own body and all
    /// their puppets (one counter for the whole HDM instance — see <see cref="HdmIpc"/>). The receiver
    /// applies an atom only if its Epoch ≥ the last it applied for that subject, and drops a one-shot whose
    /// Epoch is older than the current atom. A given subject's atoms still arrive strictly increasing even
    /// when interleaved with other subjects', which is all the ordering guarantee the receiver needs.</summary>
    public uint Epoch { get; set; }

    /// <summary>McType — 1 Human / 2 Demihuman / 3 Monster — selects the receiver's apply path, exactly as
    /// it selects HDM's own. <b>0 = revert</b> (no disguise): the actor should be restored to its real body.</summary>
    public byte Kind { get; set; }

    /// <summary>The BNpcBase (or 1,000,000+ ENpcBase) id. The receiver resolves NpcEquip / BNpcCustomize
    /// LOCALLY from this — every peer has identical game sheets, so this is enough to run the SAME apply.</summary>
    public uint BaseId { get; set; }

    /// <summary>The ModelChara render key. Authoritative for Monster (a bare swap of this id disguises the
    /// actor); carried for Demi/Human too so the receiver never needs a catalog lookup to get it.</summary>
    public int ModelCharaId { get; set; }

    /// <summary>Resolved ABSOLUTE scale multiplier (already through the sender's scale mode) — applied by
    /// the same mechanism HDM uses for this Kind.</summary>
    public float Scale { get; set; }

    /// <summary>Vertical draw offset in world units (GameObject draw offset, re-asserted per frame).</summary>
    public float VOffset { get; set; }

    /// <summary>Held animation loop timeline (Timeline.BaseOverride); 0 = none. This is STATE, not an event:
    /// a peer who starts rendering the actor mid-loop must pick up the current loop from the snapshot.</summary>
    public ushort LoopId { get; set; }

    /// <summary>True when this atom means "take the disguise off" rather than "put one on".</summary>
    [JsonIgnore] public bool IsRevert => Kind == 0;
}

/// <summary>
/// One spawned puppet as HMS sees it: the stable per-source <see cref="Slot"/> (namespaced under the DM's
/// ContentId), the LOCAL <see cref="ObjectIndex"/> (valid only on the spawning client), the puppet's
/// <see cref="Atom"/>, and its world transform. Serialized as the PuppetSpawned event payload and as each
/// element of the GetPuppets snapshot array. Position is flattened to Px/Py/Pz so the JSON stays a flat,
/// version-tolerant record (no nested Vector3 whose shape could drift between serializers).
/// </summary>
public sealed class PuppetInfo
{
    public int Slot { get; set; }
    public int ObjectIndex { get; set; }
    public DisguiseAtom Atom { get; set; } = new();
    public float Px { get; set; }
    public float Py { get; set; }
    public float Pz { get; set; }
    public float Rot { get; set; }

    /// <summary>Whether this puppet's animation is currently FROZEN (playback speed pinned to 0). Like
    /// <see cref="Atom"/> this is STATE, not an event: it rides the GetPuppets snapshot so a peer that starts
    /// mirroring mid-session begins the puppet frozen. Toggled outbound on the FreezeChanged lane and applied
    /// on the receiver via SetFrozen. Additive (MinorVersion ≥ 4); a JSON payload from an older sender leaves
    /// it false. Defaults false — a fresh puppet plays normally.</summary>
    public bool Frozen { get; set; }
}

/// <summary>
/// HDM's cross-plugin IPC surface — the Penumbra/Glamourer-style provider that lets HMS (a) OBSERVE this
/// DM's disguise + puppet state so it can sync it to other players, and (b) DRIVE this client's actors to
/// mirror what a remote DM is showing. Both plugins stay independently loadable: HMS binds these labels by
/// string and version-gates on <c>HDM.ApiVersion</c>; neither hard-references the other (same contract as
/// GlamourerIpc consumes Glamourer). All structured payloads cross as JSON strings — a struct defined here
/// is NOT type-identical to HMS's mirror across the ALC boundary, so we serialize, exactly as HMoniker and
/// SimpleHeels do.
///
/// <b>Two cleanly separated state stores — do not conflate them:</b>
///  - <see cref="_ownBody"/> + <see cref="_puppets"/> = THIS DM's OWN disguise and OWN puppets. Populated by
///    the <c>Report*</c> methods (called from MainWindow when the DM acts on themselves / their puppets),
///    emitted OUTBOUND to HMS, and handed to late-joiners via the snapshot getters. This is the sync SOURCE.
///  - <see cref="_lastApplied"/> = MIRRORS this client drives on behalf of OTHER DMs (populated by the
///    receiver methods HMS calls — ApplyDisguise / SpawnPuppet …). Used only to DIFF successive applies so a
///    loop-only change skips the model redraw. A mirror is never re-reported outbound (that would loop).
/// The own/mirror split is what stops a received SpawnPuppet from echoing back a PuppetSpawned: the puppet
/// lifecycle events (Ready/Despawned) fire outbound ONLY for slots present in <see cref="_puppets"/>.
///
/// <b>Threading:</b> every method here runs on the framework thread — HMS calls the receiver methods from
/// its framework-thread update, and the Report* callers are on the UI/framework thread. No locking; all
/// game-state access is already where it must be. SendMessage invokes HMS's subscribers synchronously on
/// this thread too, which is fine (they marshal as needed).
///
/// <b>0.8.54 dependency:</b> ApplyDisguise on a cold, freshly-sighted peer leans on HumanGuise's non-self
/// draw-object rebuild (0.8.54) to render the correct race on first paint. The IPC SURFACE does not depend
/// on that fix — it routes through the same HumanGuise/GuiseService entry points HDM uses on itself, so if
/// 0.8.54's internals change, this layer is unaffected.
/// </summary>
public sealed class HdmIpc : IDisposable
{
    // Bump Minor for additive changes (new label / new atom field), Major only for a breaking change to an
    // existing label's signature or semantics. HMS gates on Major and treats Minor as capability discovery.
    public const uint MajorVersion = 1;
    public const uint MinorVersion = 4;   // 1.4: + FreezeChanged / GetFrozenOwnBody / SetFrozen + PuppetInfo.Frozen (freeze-animation sync: edge event + late-join snapshot + receiver, cloned from the OwnBodyHidden trio but HDM-applied on the receiver). 1.3: + SanitizeSelf (own-body exit sanitiser — revert model/scale/elevation/hidden + release possession on a logout / session-teardown edge). 1.2: + SpawnPuppetAt (atomic position-carrying mirror spawn — fixes the spawn-in-front race). 1.1: + OwnBodyHidden / GetOwnBodyHidden (DM own-body hide during possession)
    public const string NameSpace = "HDM";

    private readonly IDalamudPluginInterface _pi;
    private readonly MobIndex _index;
    private readonly GuiseService _guise;
    private readonly HumanGuise _humanGuise;
    private readonly AnimationService _anim;
    private readonly SpawnService _spawn;
    private readonly IObjectTable _objects;
    private readonly IPluginLog _log;

    // ---- Provider gates -------------------------------------------------------------------------------
    private readonly ICallGateProvider<(uint, uint)> _apiVersion;
    // Outbound (HDM -> HMS): fired via SendMessage; HMS .Subscribe()s.
    private readonly ICallGateProvider<string, object?> _disguiseChanged;   // JSON {Slot:int?, Atom}
    private readonly ICallGateProvider<int, uint, object?> _actionFired;    // (slot: -1 = own body, playId)
    private readonly ICallGateProvider<string, object?> _puppetSpawned;     // JSON PuppetInfo
    private readonly ICallGateProvider<int, object?> _puppetReady;          // (objectIndex)
    private readonly ICallGateProvider<int, object?> _puppetDespawned;      // (slot)
    private readonly ICallGateProvider<int, float, float, float, float, object?> _puppetMoved; // (slot,x,y,z,rot)
    private readonly ICallGateProvider<bool, object?> _ownBodyHidden;       // (hidden) — hide the DM's own-body mirror on peers
    private readonly ICallGateProvider<int, bool, object?> _freezeChanged;  // (slot: -1 = own body / N = puppet, frozen) — animation-freeze edge (MinorVersion>=4)
    // Snapshot getters (HMS pull).
    private readonly ICallGateProvider<string> _getDisguise;                // JSON atom, or "" if none
    private readonly ICallGateProvider<string> _getPuppets;                 // JSON PuppetInfo[]
    private readonly ICallGateProvider<bool> _getOwnBodyHidden;             // true if the DM's own body is hidden right now
    private readonly ICallGateProvider<bool> _getFrozenOwnBody;             // true if the DM's own body is animation-frozen right now (MinorVersion>=4)
    // Receiver methods (HMS -> HDM).
    private readonly ICallGateProvider<int, string, object?> _applyDisguise;// (objectIndex, atomJson)
    private readonly ICallGateProvider<int, object?> _revertDisguise;       // (objectIndex)
    private readonly ICallGateProvider<int, uint, object?> _playAction;     // (objectIndex, playId)
    private readonly ICallGateProvider<string, int> _spawnPuppet;           // (atomJson) -> objectIndex, -1 fail
    private readonly ICallGateProvider<string, float, float, float, float, int> _spawnPuppetAt; // (atomJson,x,y,z,rot) -> objectIndex, -1 fail (MinorVersion>=2)
    private readonly ICallGateProvider<int, float, float, float, float, object?> _movePuppet; // (idx,x,y,z,rot)
    private readonly ICallGateProvider<int, object?> _despawnPuppet;        // (objectIndex)
    private readonly ICallGateProvider<bool, object?> _sanitizeSelf;        // (restoreVisual) — strip the DM's OWN disguise on an exit edge (logout / session teardown) (MinorVersion>=3)
    private readonly ICallGateProvider<int, bool, object?> _setFrozen;      // (objectIndex, frozen) — freeze/unfreeze a specific local mirror actor; HDM re-holds it (MinorVersion>=4)
    // Lifecycle broadcasts (so a plugin that loads in either order can (re)bind).
    private readonly ICallGateProvider<object?> _ready;
    private readonly ICallGateProvider<object?> _disposing;

    // ---- State ---------------------------------------------------------------------------------------
    private DisguiseAtom _ownBody = new() { Kind = 0 };        // the DM's own current disguise (Kind 0 = none)
    private readonly Dictionary<int, PuppetInfo> _puppets = new();  // slot -> the DM's own puppet
    private readonly Dictionary<int, DisguiseAtom> _lastApplied = new(); // objectIndex -> last atom we drove (mirrors)
    private uint _epoch;
    // The DM's own body is suppressed on peers if EITHER source wants it: possession (the client-side Alpha=0
    // fade during a pilot) OR a manual Catalog "Hide" toggle (a "here but not physically present" DM switch).
    // Each source is a separate intent; _ownHidden is the effective OR that actually rides the OwnBodyHidden lane
    // and is served for late-join. Keeping them separate stops the two writers from clobbering each other — a
    // possession-release must NOT reveal a body the DM hid by hand, and a manual unhide must NOT reveal a body
    // possession is still fading. See ReportOwnBodyHidden / ReportManualHidden / RecomputeOwnBodyHidden.
    private bool _ownHidden;         // effective (possession OR manual) — the bit on the wire + served for late-join
    private bool _possessionHidden;  // source: possession is fading the DM to Alpha=0
    private bool _manualHidden;      // source: the DM toggled the Catalog "Hide" button / '/hdm hide'
    private bool _ownFrozen;   // is the DM's own body currently animation-frozen (speed pinned 0)?
    private bool _disposed;

    /// <summary>Late-wired in Plugin to <c>PossessionService.Release</c>. Invoked by <see cref="SanitizeSelf"/>
    /// so an exit that strips the DM's own disguise also ends any in-progress possession — necessary because an
    /// HMS session teardown is NOT a Dalamud logout, so PossessionService's own Release edges (territory / logout
    /// / puppet-removed / dispose) may not fire on that path. Null until wired; PossessionService is built after
    /// this IPC, so it cannot be a constructor dependency (same late-wire idiom as GuiseService.SuppressReassert).</summary>
    public Action? ReleasePossession { get; set; }

    public HdmIpc(IDalamudPluginInterface pi, MobIndex index, GuiseService guise, HumanGuise humanGuise,
                  AnimationService anim, SpawnService spawn, IObjectTable objects, IPluginLog log)
    {
        _pi = pi;
        _index = index;
        _guise = guise;
        _humanGuise = humanGuise;
        _anim = anim;
        _spawn = spawn;
        _objects = objects;
        _log = log;

        _apiVersion = pi.GetIpcProvider<(uint, uint)>($"{NameSpace}.ApiVersion");
        _apiVersion.RegisterFunc(() => (MajorVersion, MinorVersion));

        _disguiseChanged = pi.GetIpcProvider<string, object?>($"{NameSpace}.DisguiseChanged");
        _actionFired = pi.GetIpcProvider<int, uint, object?>($"{NameSpace}.ActionFired");
        _puppetSpawned = pi.GetIpcProvider<string, object?>($"{NameSpace}.PuppetSpawned");
        _puppetReady = pi.GetIpcProvider<int, object?>($"{NameSpace}.PuppetReady");
        _puppetDespawned = pi.GetIpcProvider<int, object?>($"{NameSpace}.PuppetDespawned");
        _puppetMoved = pi.GetIpcProvider<int, float, float, float, float, object?>($"{NameSpace}.PuppetMoved");
        _ownBodyHidden = pi.GetIpcProvider<bool, object?>($"{NameSpace}.OwnBodyHidden");
        _freezeChanged = pi.GetIpcProvider<int, bool, object?>($"{NameSpace}.FreezeChanged");

        _getDisguise = pi.GetIpcProvider<string>($"{NameSpace}.GetDisguise");
        _getDisguise.RegisterFunc(GetDisguiseJson);
        _getPuppets = pi.GetIpcProvider<string>($"{NameSpace}.GetPuppets");
        _getPuppets.RegisterFunc(GetPuppetsJson);
        _getOwnBodyHidden = pi.GetIpcProvider<bool>($"{NameSpace}.GetOwnBodyHidden");
        _getOwnBodyHidden.RegisterFunc(() => !_disposed && _ownHidden);
        _getFrozenOwnBody = pi.GetIpcProvider<bool>($"{NameSpace}.GetFrozenOwnBody");
        _getFrozenOwnBody.RegisterFunc(() => !_disposed && _ownFrozen);

        _applyDisguise = pi.GetIpcProvider<int, string, object?>($"{NameSpace}.ApplyDisguise");
        _applyDisguise.RegisterAction((idx, json) => Guard(() => ApplyDisguise(idx, json)));
        _revertDisguise = pi.GetIpcProvider<int, object?>($"{NameSpace}.RevertDisguise");
        _revertDisguise.RegisterAction(idx => Guard(() => RevertDisguise(idx)));
        _playAction = pi.GetIpcProvider<int, uint, object?>($"{NameSpace}.PlayAction");
        _playAction.RegisterAction((idx, playId) => Guard(() => PlayAction(idx, playId)));
        _spawnPuppet = pi.GetIpcProvider<string, int>($"{NameSpace}.SpawnPuppet");
        _spawnPuppet.RegisterFunc(json => { try { return SpawnPuppet(json); } catch (Exception e) { _log.Error(e, "HDM IPC: SpawnPuppet threw."); return -1; } });
        _spawnPuppetAt = pi.GetIpcProvider<string, float, float, float, float, int>($"{NameSpace}.SpawnPuppetAt");
        _spawnPuppetAt.RegisterFunc((json, x, y, z, rot) => { try { return SpawnPuppetAt(json, x, y, z, rot); } catch (Exception e) { _log.Error(e, "HDM IPC: SpawnPuppetAt threw."); return -1; } });
        _movePuppet = pi.GetIpcProvider<int, float, float, float, float, object?>($"{NameSpace}.MovePuppet");
        _movePuppet.RegisterAction((idx, x, y, z, rot) => Guard(() => MovePuppet(idx, x, y, z, rot)));
        _despawnPuppet = pi.GetIpcProvider<int, object?>($"{NameSpace}.DespawnPuppet");
        _despawnPuppet.RegisterAction(idx => Guard(() => _spawn.Despawn((ushort)idx)));
        _sanitizeSelf = pi.GetIpcProvider<bool, object?>($"{NameSpace}.SanitizeSelf");
        _sanitizeSelf.RegisterAction(rv => Guard(() => SanitizeSelf(rv)));
        _setFrozen = pi.GetIpcProvider<int, bool, object?>($"{NameSpace}.SetFrozen");
        _setFrozen.RegisterAction((idx, frozen) => Guard(() => SetFrozen(idx, frozen)));

        _ready = pi.GetIpcProvider<object?>($"{NameSpace}.Ready");
        _disposing = pi.GetIpcProvider<object?>($"{NameSpace}.Disposing");

        // Own-puppet lifecycle: SpawnService owns the "drawn+guised" and "gone" moments across ALL paths
        // (despawn / zone / logout / dispose); we forward them outbound but ONLY for the DM's own puppets.
        _spawn.PuppetReadyEvent += OnPuppetReady;
        _spawn.PuppetRemovedEvent += OnPuppetRemoved;

        // Announce presence so an HMS that loaded first rebinds now.
        _ready.SendMessage();
        _log.Information($"HDM IPC: provider up (v{MajorVersion}.{MinorVersion}, namespace \"{NameSpace}\").");
    }

    // ======================================================================================================
    //  Outbound reporting — called from MainWindow at the SAME funnel points it already drives the services.
    //  Each stamps the shared epoch and updates the sync-source state, then emits.
    // ======================================================================================================

    /// <summary>Report an apply OR a revert of the DM's own body (<paramref name="slot"/> null) or one of
    /// their puppets (slot = the puppet's <see cref="SpawnService.SlotOf"/>). A null <paramref name="row"/>
    /// means "reverted" (Kind 0). Loop is carried in the atom; use <see cref="ReportLoop"/> for a
    /// loop-only change so the receiver can take the cheap no-redraw path.</summary>
    public void ReportDisguise(int? slot, MobRow? row, float scale, float voffset, ushort loopId)
    {
        if (_disposed) return;
        var atom = row is null ? new DisguiseAtom { Kind = 0 } : AtomFor(row, scale, voffset, loopId);
        atom.Epoch = ++_epoch;
        StoreOwn(slot, atom);
        EmitDisguiseChanged(slot, atom);
    }

    /// <summary>Report a loop-only change (started, replaced, or cleared with 0) on an already-disguised
    /// own-body/puppet subject. Mutates the stored atom's <see cref="DisguiseAtom.LoopId"/>, bumps the epoch,
    /// and re-emits — the receiver diffs it to a loop drive without a model redraw. No-op if the subject
    /// currently holds no disguise (a loop with nothing to attach it to isn't synced).</summary>
    public void ReportLoop(int? slot, ushort loopId)
    {
        if (_disposed) return;
        var atom = slot is null ? _ownBody : (_puppets.TryGetValue(slot.Value, out var p) ? p.Atom : null);
        if (atom is null || atom.Kind == 0) return;
        atom.LoopId = loopId;
        atom.Epoch = ++_epoch;
        EmitDisguiseChanged(slot, atom);
    }

    /// <summary>Report a scale-only change on an already-disguised own-body/puppet subject. Mutates the stored
    /// atom's <see cref="DisguiseAtom.Scale"/>, bumps the epoch, and re-emits — the receiver's ApplyDisguise
    /// diffs it to a scale-only delta and drives it LIVE through GuiseService.Resize (a draw-object transform
    /// write, NO redraw), the same cheap path as a loop change. Call on slider RELEASE only: not for redraw
    /// cost (there is none now) but so a per-frame drag doesn't spam epochs/IPC at every peer. No-op unless the
    /// subject holds a Monster/Demihuman disguise: a bare clone (Kind 0) is hidden on peers and has no atom to
    /// rescale, and a Human guise (Kind 1) sizes through Glamourer — its atom Scale is canonically 1.0
    /// (ReportApply forces it) and the receiver's Human path ignores it, so we never corrupt it here.</summary>
    public void ReportScale(int? slot, float scale)
    {
        if (_disposed) return;
        var atom = slot is null ? _ownBody : (_puppets.TryGetValue(slot.Value, out var p) ? p.Atom : null);
        if (atom is null || atom.Kind is 0 or 1) return;
        atom.Scale = scale;
        atom.Epoch = ++_epoch;
        EmitDisguiseChanged(slot, atom);
    }

    /// <summary>Report an elevation-only change (vertical draw offset) on an already-disguised own-body/puppet
    /// subject. Symmetric with <see cref="ReportScale"/>: mutates the stored atom's
    /// <see cref="DisguiseAtom.VOffset"/>, bumps the epoch, and re-emits — the receiver diffs it to a
    /// VOffset-only delta and drives it LIVE through GuiseService.SetVerticalOffset (a draw-offset write, NO
    /// redraw), the same cheap path as a scale/loop change. Call on slider RELEASE only (a per-frame re-emit
    /// would spam epochs at every peer). Applies to ANY non-blank Kind — a draw offset lifts any body, Human
    /// included (unlike scale, which Glamourer owns for Humans) — so this is gated only on Kind 0 (a bare
    /// clone has no atom to offset).</summary>
    public void ReportVOffset(int? slot, float voffset)
    {
        if (_disposed) return;
        var atom = slot is null ? _ownBody : (_puppets.TryGetValue(slot.Value, out var p) ? p.Atom : null);
        if (atom is null || atom.Kind == 0) return;
        atom.VOffset = voffset;
        atom.Epoch = ++_epoch;
        EmitDisguiseChanged(slot, atom);
    }

    /// <summary>Report a one-shot animation (PlayOnce) fired on the DM's own body (<paramref name="slot"/>
    /// null) or a puppet. One-shots are EVENTS — never stored, never snapshotted to late-joiners.</summary>
    public void ReportAction(int? slot, ushort playId)
    {
        if (_disposed) return;
        // A one-shot supersedes any held pose (PlayOnce now clears BaseOverride), so the subject no longer
        // holds a loop. Clear the stored atom's LoopId WITHOUT re-emitting the atom (the action event itself
        // drives peers): this stops a later scale/voffset re-emit — or a late-join snapshot — from
        // resurrecting the now-gone Special. The receiver clears its mirror symmetrically in PlayAction.
        var atom = slot is null ? _ownBody : (_puppets.TryGetValue(slot.Value, out var p) ? p.Atom : null);
        if (atom is not null) atom.LoopId = 0;
        _actionFired.SendMessage(slot ?? -1, playId);
    }

    /// <summary>Record + announce a puppet the DM just spawned. Called synchronously right after TrySpawn
    /// returns (the objectIndex is real immediately); the disguise itself lands later and is confirmed by the
    /// outbound PuppetReady. Adds the puppet to the sync-source set so its Ready/Despawn fire outbound. A null
    /// <paramref name="row"/> announces a BLANK puppet (Kind 0) — the DM's own-clone dummy; its real face
    /// arrives later as a DisguiseChanged when they disguise it (HMS may ignore Kind-0 spawns).</summary>
    public void ReportPuppetSpawned(int slot, int objectIndex, MobRow? row, float scale, float voffset, Vector3 pos, float rot)
    {
        if (_disposed) return;
        var atom = row is null ? new DisguiseAtom { Kind = 0 } : AtomFor(row, scale, voffset, 0);
        atom.Epoch = ++_epoch;
        _puppets[slot] = new PuppetInfo { Slot = slot, ObjectIndex = objectIndex, Atom = atom, Px = pos.X, Py = pos.Y, Pz = pos.Z, Rot = rot };
        _puppetSpawned.SendMessage(JsonConvert.SerializeObject(_puppets[slot]));
    }

    /// <summary>Report the DM moved one of their own puppets (Spawn Management tab). Updates the stored
    /// transform (so a late-join snapshot places it right) and emits on the HOT-style transform lane.</summary>
    public void ReportPuppetMoved(int slot, Vector3 pos, float rot)
    {
        if (_disposed) return;
        if (_puppets.TryGetValue(slot, out var p)) { p.Px = pos.X; p.Py = pos.Y; p.Pz = pos.Z; p.Rot = rot; }
        _puppetMoved.SendMessage(slot, pos.X, pos.Y, pos.Z, rot);
    }

    /// <summary>Report the POSSESSION source of own-body hide — true when possession fades the DM to Alpha=0
    /// (that fade is client-side only, so a peer's HMS-driven mirror of the DM keeps rendering the frozen body
    /// next to the moving puppet unless HMS is told to suppress it). One of two independent sources (the other
    /// is <see cref="ReportManualHidden"/>); both fold into the effective OR that rides the wire — see
    /// <see cref="RecomputeOwnBodyHidden"/>. PossessionService dedupes on its own transition, but we guard the
    /// stored source here too so a redundant call is a cheap no-op.</summary>
    public void ReportOwnBodyHidden(bool hidden)
    {
        if (_disposed || _possessionHidden == hidden) return;
        _possessionHidden = hidden;
        RecomputeOwnBodyHidden();
    }

    /// <summary>Report the MANUAL source of own-body hide — the Catalog "Hide" toggle (and <c>/hdm hide</c>), a
    /// DM "here but not physically present" switch. Distinct from the local <see cref="GuiseService.SetHidden"/>
    /// draw-disable (which only pulls the body from the DM's OWN client): this drives the same peer-suppression
    /// lane possession uses, so HMS peers stop rendering the DM's mirror lobby-wide, in or out of a loaded map
    /// session. One of two independent sources (the other is <see cref="ReportOwnBodyHidden"/>); both fold into
    /// the effective OR — see <see cref="RecomputeOwnBodyHidden"/>. Guarded so a redundant toggle is a no-op.</summary>
    public void ReportManualHidden(bool hidden)
    {
        if (_disposed || _manualHidden == hidden) return;
        _manualHidden = hidden;
        RecomputeOwnBodyHidden();
    }

    /// <summary>Fold the two independent hide sources (<see cref="_possessionHidden"/>, <see cref="_manualHidden"/>)
    /// into the effective <see cref="_ownHidden"/> and emit an EDGE on the OwnBodyHidden lane only when that OR
    /// actually flips. The OR is the correct semantics: the DM's body stays suppressed on peers while EITHER
    /// source wants it, so releasing one source never reveals a body the other still hides. The stored effective
    /// bit is what <see cref="_getOwnBodyHidden"/> serves for late-join.</summary>
    private void RecomputeOwnBodyHidden()
    {
        var effective = _possessionHidden || _manualHidden;
        if (_ownHidden == effective) return;
        _ownHidden = effective;
        _ownBodyHidden.SendMessage(effective);
    }

    /// <summary>Report an animation-FREEZE toggle on the DM's own body (<paramref name="slot"/> null) or one of
    /// their puppets (slot = the puppet's <see cref="SpawnService.SlotOf"/>). Freeze is a per-actor visual STATE
    /// (a pinned playback speed of 0), so it is stored for late-join — own body in <see cref="_ownFrozen"/>
    /// (served by <see cref="_getFrozenOwnBody"/>), a puppet in its <see cref="PuppetInfo.Frozen"/> (served by the
    /// GetPuppets snapshot) — and emitted on the FreezeChanged lane as an EDGE. Deduped on the stored state so a
    /// redundant toggle is a cheap no-op (mirrors <see cref="ReportOwnBodyHidden"/>). A puppet slot not in
    /// <see cref="_puppets"/> is not one of the DM's own puppets (no mirror exists on peers), so it neither
    /// stores nor emits — matching the scale/voffset reporters that no-op on an untracked subject.</summary>
    public void ReportFrozen(int? slot, bool frozen)
    {
        if (_disposed) return;
        if (slot is null)
        {
            if (_ownFrozen == frozen) return;
            _ownFrozen = frozen;
        }
        else
        {
            if (!_puppets.TryGetValue(slot.Value, out var p) || p.Frozen == frozen) return;
            p.Frozen = frozen;
        }
        _freezeChanged.SendMessage(slot ?? -1, frozen);
    }

    // SpawnService lifecycle → outbound, but ONLY for the DM's OWN puppets (those in _puppets). A puppet
    // this client spawned on HMS's behalf (a mirror) is NOT in _puppets, so it never echoes back.
    private void OnPuppetReady(ushort globalIndex)
    {
        if (_disposed) return;
        var slot = _spawn.SlotOf(globalIndex);
        if (slot >= 0 && _puppets.ContainsKey(slot))
            _puppetReady.SendMessage((int)globalIndex);
    }

    private void OnPuppetRemoved(ushort globalIndex, int slot)
    {
        if (_disposed) return;
        if (_puppets.Remove(slot))
            _puppetDespawned.SendMessage(slot);   // own puppet — tell peers to drop their copy
        _lastApplied.Remove(globalIndex);          // mirror bookkeeping (harmless if this wasn't a mirror)
    }

    // ======================================================================================================
    //  Snapshot getters (HMS pull, for late-join / first-sight).
    // ======================================================================================================

    private string GetDisguiseJson()
    {
        if (_disposed || _ownBody.Kind == 0) return string.Empty;
        try { return JsonConvert.SerializeObject(_ownBody); }
        catch (Exception e) { _log.Error(e, "HDM IPC: GetDisguise serialize failed."); return string.Empty; }
    }

    private string GetPuppetsJson()
    {
        if (_disposed || _puppets.Count == 0) return "[]";
        try { return JsonConvert.SerializeObject(_puppets.Values.ToList()); }
        catch (Exception e) { _log.Error(e, "HDM IPC: GetPuppets serialize failed."); return "[]"; }
    }

    // ======================================================================================================
    //  Receiver methods (HMS -> HDM) — drive a MIRROR of a remote DM's disguise/puppet on THIS client. Thin
    //  wrappers over the same GuiseService / HumanGuise / AnimationService / SpawnService entry points HDM
    //  uses on itself, so a peer renders identically to how the remote DM sees themselves.
    // ======================================================================================================

    /// <summary>Apply (or, with a Kind-0 atom, revert) a disguise onto a local actor that mirrors a remote
    /// DM. Idempotent and diffing: a delta where only the live transforms moved — <see cref="DisguiseAtom.Scale"/>,
    /// <see cref="DisguiseAtom.VOffset"/>, and/or the held <see cref="DisguiseAtom.LoopId"/> — is driven live with
    /// no model redraw; anything touching the model/customize takes the full apply + redraw. Epoch-gated so a
    /// stale atom that overtakes a newer one is dropped.</summary>
    private void ApplyDisguise(int objectIndex, string atomJson)
    {
        var atom = Deserialize(atomJson);
        if (atom is null) return;

        _lastApplied.TryGetValue(objectIndex, out var prev);
        if (prev is not null && atom.Epoch < prev.Epoch)
        {
            _log.Debug($"HDM IPC: ApplyDisguise obj#{objectIndex} dropped stale atom (epoch {atom.Epoch} < {prev.Epoch}).");
            return;
        }

        var chara = ResolveChara(objectIndex);
        if (chara is null) { _log.Debug($"HDM IPC: ApplyDisguise obj#{objectIndex} — no live actor."); return; }

        if (atom.Kind == 0)
        {
            RevertInternal(chara);
            _lastApplied[objectIndex] = atom;
            return;
        }

        // Cheap path: same Kind/base/model — only the live transforms moved (SCALE, vertical OFFSET,
        // and/or the held loop), none of which needs a model redraw. Each is driven LIVE through the exact
        // GuiseService entry the DM's own slider uses on this client — NOT the full apply+redraw. This is the
        // fix for "scale/elevation are local-only": ApplyAtomFull's redraw path writes only the logical
        // GameObject.Scale and leans on the rebuild to re-init the rendered draw-object scale, which does NOT
        // resize a built Monster (only a direct draw-object 0x70 write does — the b0889 finding). So the DM saw
        // their live 0x70 write resize the puppet while the mirror rebuilt at stock size. Driving the identical
        // live Resize / SetVerticalOffset on the mirror renders it identically, by construction, and skips a peer
        // redraw. Offset rides this path for EVERY Kind (Human included — a draw offset lifts any body); scale is
        // Monster/Demi only (Glamourer owns a Human's size).
        if (prev is not null && prev.Kind == atom.Kind && prev.BaseId == atom.BaseId
            && prev.ModelCharaId == atom.ModelCharaId)
        {
            if (!Approx(prev.Scale, atom.Scale) && atom.Kind != 1) // Human sizes through Glamourer, never HDM-scaled
                _guise.Resize(chara, atom.Scale);
            if (!Approx(prev.VOffset, atom.VOffset))
                _guise.SetVerticalOffset(chara, atom.VOffset); // a draw offset lifts any body, Human included
            if (prev.LoopId != atom.LoopId)
            {
                if (atom.LoopId == 0) _anim.Sanitize(chara);
                else _anim.Loop(chara, atom.LoopId);
            }
            _lastApplied[objectIndex] = atom;
            return;
        }

        ApplyAtomFull(chara, atom);
        _lastApplied[objectIndex] = atom;
    }

    /// <summary>The full model/customize apply + redraw, mirroring MainWindow.ApplyGuise: Human (Kind 1)
    /// goes through Glamourer (revert the real skeleton, then paint on the settle continuation); Monster/Demi
    /// swap the ModelChara. Vertical offset and any held loop are asserted after, matching the self path.</summary>
    private void ApplyAtomFull(ICharacter chara, DisguiseAtom atom)
    {
        // Reveal a mirror that was spawned hidden as a blank (Kind-0) clone now that it's getting a real face.
        // IsHidden is a precise signal on the receiver: HDM's _hidden set only ever holds the DM's own /hide body
        // (never an IPC-receiver target) or a hidden blank mirror — so this un-hides nothing else. Drop it from
        // _hidden BEFORE the guise redraw so ReassertHidden stops re-issuing DisableDraw and doesn't fight the apply.
        if (_guise.IsHidden(chara.ObjectIndex))
            _guise.SetHidden(chara, false);

        var row = RowForAtom(atom);
        if (atom.Kind == 1)
        {
            var idx = chara.ObjectIndex;
            var baseId = atom.BaseId;
            var name = row.DisplayName;
            var source = row.Source;
            var loopId = atom.LoopId;
            // Paint on the redraw continuation (HumanGuise's 0.8.54 non-self rebuild lands the race), then
            // re-assert the loop on the freshly-drawn body. Re-resolve the actor inside the closure so a
            // few-frame-late continuation never touches a stale pointer.
            _guise.Revert(chara, () =>
            {
                _humanGuise.Apply(idx, baseId, name, source);
                if (loopId != 0 && ResolveChara(idx) is { } c) _anim.Loop(c, loopId);
            });
        }
        else
        {
            _humanGuise.Revert(chara.ObjectIndex);   // drop any Glamourer face before the model swap
            _guise.Apply(chara, row, atom.Scale);
            if (atom.LoopId != 0) _anim.Loop(chara, atom.LoopId);
        }
        _guise.SetVerticalOffset(chara, atom.VOffset);
    }

    private void RevertDisguise(int objectIndex)
    {
        var chara = ResolveChara(objectIndex);
        if (chara is null) { _lastApplied.Remove(objectIndex); return; }
        RevertInternal(chara);
        _lastApplied.Remove(objectIndex);
    }

    // Peer/puppet revert (NOT the self-only hard revert): un-stick animation, revert whichever guise family
    // is on the actor (both no-op if absent), and restore the Human snapshot — which on a Mare-fork peer is
    // that peer's actual Mare-synced look (HumanGuise.Revert restores the exact pre-disguise Glamourer bytes).
    private void RevertInternal(ICharacter chara)
    {
        _anim.Sanitize(chara);
        _guise.Revert(chara, null);
        _humanGuise.Revert(chara.ObjectIndex);
    }

    private void PlayAction(int objectIndex, uint playId)
    {
        if (ResolveChara(objectIndex) is { } chara)
            _anim.PlayOnce(chara, (ushort)playId);
        // Keep the mirror consistent with the sender: the one-shot cleared the held pose (PlayOnce clears
        // BaseOverride), so drop the tracked LoopId. Otherwise a later cheap-path diff or a full re-apply
        // would re-hold the old Special that the DM's own body no longer shows.
        if (_lastApplied.TryGetValue(objectIndex, out var prev)) prev.LoopId = 0;
    }

    /// <summary>Spawn a local puppet that mirrors a remote DM's puppet and guise it as the atom once drawn.
    /// Returns the LOCAL objectIndex synchronously (SpawnService resolves it the same frame) so HMS can map
    /// its slot → this index at once; -1 if the native spawn failed. NOT recorded in the sync-source set, so
    /// it never echoes a PuppetSpawned/Ready/Despawn back outbound.</summary>
    private int SpawnPuppet(string atomJson)
    {
        var atom = Deserialize(atomJson);
        if (atom is null) return -1;

        // Legacy path (HMS older than HDM MinorVersion 2): spawn a step in front of the RECEIVER, then a
        // separate MovePuppet relocates it to the DM's coords. Prefer SpawnPuppetAt on newer HMS.
        // originated:false — this is a MIRROR of a peer's spawn, so it's not possessable here by default
        // (control of a puppet stays with the client that originated it).
        if (!_spawn.TrySpawn(out var puppet, onReady: MirrorOnReady(atom), originated: false) || puppet is null)
            return -1;

        return puppet.ObjectIndex;
    }

    /// <summary>Position-carrying mirror spawn (MinorVersion≥2): the atomic-placement successor to
    /// SpawnPuppet+MovePuppet. Same atom mirroring as <see cref="SpawnPuppet"/>, but the puppet is born at the
    /// DM's ABSOLUTE world coords (x,y,z,rot) in ONE call — no "spawn in front of the receiver then relocate"
    /// two-step, which raced the not-yet-drawn object and stranded ~1/3 of mirrors in front of the RECEIVER
    /// (the spawn-in-front bug). Placement flows through the SAME TrySpawn funnel (its at/facing override), so
    /// there is exactly one spawn mechanism. Returns the LOCAL objectIndex synchronously, or -1 on failure.</summary>
    private int SpawnPuppetAt(string atomJson, float x, float y, float z, float rot)
    {
        var atom = Deserialize(atomJson);
        if (atom is null) return -1;

        // originated:false — a MIRROR of a peer's spawn; not possessable here by default (see SpawnPuppet).
        if (!_spawn.TrySpawn(out var puppet, onReady: MirrorOnReady(atom),
                             at: new Vector3(x, y, z), facing: rot, originated: false) || puppet is null)
            return -1;

        return puppet.ObjectIndex;
    }

    /// <summary>The shared "this mirror puppet became drawable" continuation for BOTH spawn IPC paths
    /// (<see cref="SpawnPuppet"/> and <see cref="SpawnPuppetAt"/>) — one funnel, never a copy
    /// (single-control-mechanism rule). A Kind-0 (blank) mirror is a bare clone of the RECEIVER's OWN local
    /// player — TrySpawn always clones the local player, and with no guise to paint over it a peer would see a
    /// copy of THEMSELF standing in front of them (the DM's naked dummy has no shared appearance to mirror).
    /// Hide it instead; ApplyAtomFull reveals it (SetHidden false) the moment the DM gives the dummy a real
    /// face. The DM's OWN blank dummies are spawned through MainWindow, never this IPC path, so they stay
    /// visible for the DM. See docs: "HMS may ignore Kind-0 spawns" — we make honouring them safe. Otherwise
    /// paint the atom and seed the diff baseline for a later ApplyDisguise.</summary>
    private Action<ICharacter> MirrorOnReady(DisguiseAtom atom) => p =>
    {
        if (atom.Kind == 0) { _guise.SetHidden(p, true); return; }
        ApplyAtomFull(p, atom);
        if (ResolveChara(p.ObjectIndex) is not null)
            _lastApplied[p.ObjectIndex] = atom; // seed the diff baseline for later ApplyDisguise
    };

    private void MovePuppet(int objectIndex, float x, float y, float z, float rot)
    {
        var gi = (ushort)objectIndex;
        _spawn.SetPosition(gi, new Vector3(x, y, z));   // SpawnService guards IsSpawned internally
        _spawn.SetRotation(gi, rot);
    }

    // ======================================================================================================
    //  Helpers.
    // ======================================================================================================

    private static DisguiseAtom AtomFor(MobRow row, float scale, float voffset, ushort loopId) => new()
    {
        Kind = (byte)row.McType,
        BaseId = row.BaseId,
        ModelCharaId = row.ModelCharaId,
        Scale = scale,
        VOffset = voffset,
        LoopId = loopId,
    };

    // Prefer the receiver's own catalog row (nicer DisplayName for logs, and both sides ship the same CSV so
    // it normally hits). On a miss — newer content, or a catalog the receiver trimmed — synthesize a minimal
    // MobRow straight from the atom: GuiseService.Apply only reads McType / ModelCharaId / BaseId (the live
    // NpcEquip lookup is keyed by BaseId against the game sheet, not the CSV), so the decomposed model coords
    // aren't needed. Source is inferred from the id range (BNpcBase < ENpcBase's 1,000,000+).
    private MobRow RowForAtom(DisguiseAtom atom)
    {
        if (_index.TryGetByBase(atom.BaseId, out var row))
            return row;
        var source = atom.BaseId >= 1_000_000 ? NpcSource.Event : NpcSource.Battle;
        return new MobRow(atom.BaseId, 0, "", atom.ModelCharaId, atom.Kind, 0, 0, 0, atom.Scale) { Source = source };
    }

    private void StoreOwn(int? slot, DisguiseAtom atom)
    {
        if (slot is null) _ownBody = atom;
        else if (_puppets.TryGetValue(slot.Value, out var p)) p.Atom = atom;
    }

    private void EmitDisguiseChanged(int? slot, DisguiseAtom atom)
    {
        try { _disguiseChanged.SendMessage(JsonConvert.SerializeObject(new DisguiseChangeDto { Slot = slot, Atom = atom })); }
        catch (Exception e) { _log.Error(e, "HDM IPC: DisguiseChanged emit failed."); }
    }

    private ICharacter? ResolveChara(int objectIndex)
    {
        if (objectIndex < 0 || objectIndex >= _objects.Length) return null;
        var obj = _objects[objectIndex];
        return obj is ICharacter c && c.Address != nint.Zero ? c : null;
    }

    private DisguiseAtom? Deserialize(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonConvert.DeserializeObject<DisguiseAtom>(json); }
        catch (Exception e) { _log.Error(e, "HDM IPC: atom deserialize failed."); return null; }
    }

    private static bool Approx(float a, float b) => MathF.Abs(a - b) < 0.0001f;

    // Wrap a receiver action so an exception from one bad IPC call can never crash the game or throw back
    // across the plugin boundary into HMS.
    private void Guard(Action a)
    {
        if (_disposed) return;
        try { a(); }
        catch (Exception e) { _log.Error(e, "HDM IPC: receiver call threw."); }
    }

    // HMS -> HDM exit-edge sanitiser (HMS calls this on its known-body-live logout / session-teardown edge as
    // the reliable belt for HDM's own best-effort OnLogout). Ends any in-progress possession FIRST — un-hides +
    // un-pins the DM's own body — because an HMS teardown is not a Dalamud logout, so PossessionService's own
    // Release edges may not fire on this path. THEN strips the DM's own disguise via the shared sanitiser:
    // restoreVisual=false is the cheap logout path (scale/offset/hidden field writes only, the model self-heals
    // on relog); true also reverts the model + Human-guise appearance WITH a redraw (map-hop / teardown where
    // the body persists and would NOT self-heal). Self-only + idempotent; runs on HMS's framework thread.
    private void SanitizeSelf(bool restoreVisual)
    {
        ReleasePossession?.Invoke();
        _guise.SanitizeLocalPlayer(restoreVisual);
    }

    // HMS -> HDM freeze receiver: freeze (frozen=true → speed 0) or resume (false → speed 1) the animation of a
    // SPECIFIC local mirror actor HMS drives for a remote DM (that DM's synced body, or one of their mirror
    // puppets). Routes through the SAME AnimationService.SetSpeed the DM uses on their own actors, so the pin is
    // RE-ASSERTED every frame by the two speed hooks — that is the persistence contract HMS relies on (it re-calls
    // this on rebind rather than per-frame poking). Actor-general: the hooks force the pin for any ObjectIndex in
    // _speedPin, self or not. No-op if the actor isn't resolvable (a mirror not yet drawn / already gone).
    private void SetFrozen(int objectIndex, bool frozen)
    {
        if (ResolveChara(objectIndex) is { } chara)
            _anim.SetSpeed(chara, frozen ? 0f : 1f);
    }

    // Flat JSON discriminator for the DisguiseChanged payload: Slot null = the DM's own body, N = puppet N.
    private sealed class DisguiseChangeDto
    {
        public int? Slot { get; set; }
        public DisguiseAtom Atom { get; set; } = new();
    }

    // ======================================================================================================
    //  Dev harness — drive the RECEIVER path locally, without HMS. A /hdm ipc subcommand calls these on a
    //  real local actor (the DM's target, or a fresh mirror puppet) so the whole deserialize -> epoch-gate ->
    //  apply pipeline is exercisable before HMS's consuming side exists. Each routes through the SAME private
    //  internal the matching CallGate does, so a green dev run means the wire path is green too.
    // ======================================================================================================

    public (uint major, uint minor) DevVersion => (MajorVersion, MinorVersion);
    public string DevSnapshot() => $"disguise={GetDisguiseJson()}\npuppets={GetPuppetsJson()}";
    public void DevApply(int objectIndex, DisguiseAtom atom) => Guard(() => ApplyDisguise(objectIndex, JsonConvert.SerializeObject(atom)));
    public void DevRevert(int objectIndex) => Guard(() => RevertDisguise(objectIndex));
    public void DevPlay(int objectIndex, ushort playId) => Guard(() => PlayAction(objectIndex, playId));
    public int DevSpawn(DisguiseAtom atom)
    {
        if (_disposed) return -1;
        try { return SpawnPuppet(JsonConvert.SerializeObject(atom)); }
        catch (Exception e) { _log.Error(e, "HDM IPC: DevSpawn threw."); return -1; }
    }

    /// <summary>Build a fresh, epoch-stamped atom from a catalog row — the same shape <see cref="AtomFor"/>
    /// produces for the outbound path, exposed so the dev command can hand a live atom to <see cref="DevApply"/>
    /// / <see cref="DevSpawn"/> without the caller reaching into the private helpers.</summary>
    public DisguiseAtom DevAtomFor(MobRow row, float scale, float voffset, ushort loopId)
    {
        var a = AtomFor(row, scale, voffset, loopId);
        a.Epoch = ++_epoch;
        return a;
    }

    public void Dispose()
    {
        _disposed = true;
        try { _disposing.SendMessage(); } catch { /* HMS may already be gone */ }

        _spawn.PuppetReadyEvent -= OnPuppetReady;
        _spawn.PuppetRemovedEvent -= OnPuppetRemoved;

        _apiVersion.UnregisterFunc();
        _getDisguise.UnregisterFunc();
        _getPuppets.UnregisterFunc();
        _getOwnBodyHidden.UnregisterFunc();
        _getFrozenOwnBody.UnregisterFunc();
        _spawnPuppet.UnregisterFunc();
        _spawnPuppetAt.UnregisterFunc();
        _applyDisguise.UnregisterAction();
        _revertDisguise.UnregisterAction();
        _playAction.UnregisterAction();
        _movePuppet.UnregisterAction();
        _despawnPuppet.UnregisterAction();
        _sanitizeSelf.UnregisterAction();
        _setFrozen.UnregisterAction();

        _puppets.Clear();
        _lastApplied.Clear();
    }
}
