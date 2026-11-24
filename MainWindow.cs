using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Textures;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace HDM;

/// <summary>
/// Four tabs:
///  - Catalog: search every mob with a model; a single click on a row name
///    selects AND applies the disguise (Ctrl+click selects WITHOUT wearing it, to
///    line up a mob for "Spawn puppet"). Scale is a live multiplier — Native (the
///    mob's own authored scale) or a custom size — that lands immediately and
///    travels with every apply. Revert (on the detail strip) restores your look;
///    an opt-in toggle renames your nameplate to the disguise via Moniker.
///  - Animations: named, per-skeleton timeline buttons (heuristic names from the
///    key sheet, raw key on hover), a speed/freeze control, a draw-elevation
///    slider, the human-emote catalog, and a prominent "Reset to Normal (unstick)"
///    that force-clears any stuck animation state.
///  - Spawn: the set-piece workshop — drop non-targetable puppets, then per-puppet
///    disguise / move / rotate / freeze / animate them.
///  - Favourites: a curated library of starred mobs with per-mob scale, draw
///    elevation, and one-tap animations.
///
/// Draw runs on the framework thread, so service calls are made directly from
/// click handlers — no queueing needed for v1.
/// </summary>
public sealed partial class MainWindow : Window, IDisposable
{
    private const int AnimMaxRows = 512; // cap rendered animation buttons per section (safety valve only —
                                         // "All" now shows ~353 playable rows; a single provenance category is
                                         // <100, so this never bites in normal use, it just bounds pathologies)

    // Plugin version, read from the assembly (stamped by the csproj <Version>). Shown
    // in the window title so the loaded build is identifiable at a glance.
    private static readonly string Version =
        typeof(MainWindow).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";

#if HDM_TESTING
    // Testing-build provenance tag (HDMT lineage). The window header reads "HDMT v<Version> b<InternalBuild>".
    // InternalBuild is an ABSOLUTE, monotonic ordinal that NEVER resets across version bumps — every test
    // build gets the next integer, so any screenshot or in-game report is traceable to one exact source
    // state. This mirrors the HMST<->HMS convention (b186..b194 promoting into 4-part prod releases such as
    // v1.0.1.2 / v1.0.1.3): the b<N> counter is the internal build number, the vX.Y.Z is the prod lineage it
    // is based on. b1 = the FIRST HDMT build, based on live prod 1.0.0.0 (its forward hotfix target is 1.0.1).
    // Bump by 1 for every test build cut.
    //
    // The whole apparatus is gated behind HDM_TESTING (defined only on a Debug build — see HDM.csproj), so
    // the public Release build compiles the plain "HDM v<Version>" header with no b-tag. The
    // testing identity therefore auto-normalizes on promotion; there is nothing to strip by hand.
    private const int InternalBuild = 45;
#endif

    private readonly MobIndex _index;
    private readonly TimelineIndex _timeline;
    private readonly PoseLibrary _poses;   // b18: grouped NPC poses + job skills (human subjects)
    private readonly ContentIndex _content;
    private readonly TerritoryIndex _territory;
    private readonly ManualLocationIndex _manual;
    private readonly DungeonStemIndex _stems;
    private readonly InstanceRosterIndex _instanced;
    private readonly MobLoreIndex _lore;
    private readonly LevelPlacementIndex _level;
    private readonly WebLocIndex _webloc;
    private readonly CompanionIndex _companion;
    private readonly EnpcLocationIndex _enpcLoc;
    private readonly MobHarvester _harvest;
    private readonly GuiseService _guise;
    private readonly HumanGuise _humanGuise;
    private readonly AnimationService _anim;
    private readonly SpawnService _spawn;
    private readonly PossessionService _possession; // Task #40: drive a spawned puppet (Spawn tab toggle/Release + overlay)
    private readonly HdmIpc _ipc;
    private readonly MonikerIpc _moniker;
    private readonly AccentPalette _accent; // theme accent engine (Config tab; syncs to HM-Sync when detected)
    private readonly Configuration _config;
    private readonly IDalamudPluginInterface _pi;
    private readonly IObjectTable _objects;
    private readonly ITargetManager _targets;
    private readonly IClientState _clientState;
    private readonly ITextureProvider _textures;
    private readonly IPluginLog _log;

    private string _filter = string.Empty;
    private string _animFilter = string.Empty;
    private bool _loopMode;            // Animations tab: named-timeline buttons loop (true) vs play once (false)
    // Animation tab: the animation SUBJECT — false = YOU, true = the Spawn tab's focused puppet. The
    // target-generic funnels let one selection drive either; forced back to self when no puppet is spawned.
    // Surfaces a spawned humanoid puppet's emotes.
    private bool _animApplyToPuppet;
    // Possession auto-focus (Problem B): the possessed index we've already pointed the UI at, so the sync fires
    // once on the possess-start EDGE (not every frame) — the DM can still retarget by hand afterward, and a new
    // possession re-focuses. null = not currently auto-focused. See SyncPossessionFocus.
    private int? _autoFocusedFor;
    // Animations tab (b21 library browser): the rail section, each section's dropdown choice, the prop search.
    private string _animSection = "";                                   // b21 rail selection
    private readonly Dictionary<string, string> _animDrop = new();      // b21 per-section dropdown choice
    private string _propSearch = "";                                    // b21 "In hand" picker search
    private LibEntry? _lastPose;                                        // b21 last pose played (re-dress on picker change)
    private bool _hideUnnamed;
    private MobRow? _selected;
    // batch-3 (item #6): what the LOCAL PLAYER is actually WEARING, decoupled from _selected (which is the
    // catalog browse-cursor — a plain click moves it). Set ONLY at the ApplyGuise self choke-point and
    // cleared in RevertGuise; null = real model (no disguise). The Animations tab scopes to THIS, so browsing
    // the catalog while disguised no longer drifts the animation list off the model you're wearing.
    private MobRow? _wornGuise;
    // Nameplate-rename bookkeeping for the ApplyName toggle (Moniker). True while THIS session has an
    // active local nameplate that WE set to a disguise name, so Revert clears exactly the name we wrote and
    // never stomps a name the user set in Moniker directly. Self-only — puppets never touch the nameplate.
    // Deliberately NOT reset on Dispose (matches HOutfits): a Moniker name is the user's own persisted
    // config and HMoniker owns its lifecycle across plugin reloads.
    private bool _appliedNameThisSession;
    private int _timelineId = 3;   // normal/idle; raw advanced input
    private float _speed = 1.0f;

    // Puppet state (Stage tab, b43).
    //  _spawnFilter    — the Stage's Spawn / Look picker search box.
    //  _puppetTid      — each puppet's OWN raw timeline id (Stage > Placement & playback).
    //  _puppetSpeed    — each puppet's OWN animation speed for its slider (AnimationService has no getter;
    //                    Freeze pins 0, this mirrors it). Both dicts keyed by GLOBAL puppet index.
    //  _puppetScale    — each puppet's DIALED scale override. PRESENCE is the flag: a key here means "the DM
    //                    dialed a custom size for this puppet", so its Apply re-guise forces that size instead
    //                    of the global scale mode (the live GameObject.Scale is the value, this is the intent).
    //  _puppetScaleAll — the aggregate slider's own value ("set every puppet to this size"); not per-puppet.
    //  _puppetGuise    — what each puppet WEARS (last mob applied), for the list. The per-index dicts are
    //                    pruned to live puppets each draw (PrunePuppetLabels).
    private string _spawnFilter = string.Empty;
    private readonly Dictionary<ushort, MobRow> _puppetGuise = new();
    private readonly Dictionary<ushort, int> _puppetTid = new();
    private readonly Dictionary<ushort, float> _puppetSpeed = new();
    private readonly Dictionary<ushort, float> _puppetScale = new();
    private float _puppetScaleAll = 1.0f;
    // The puppet the Stage is working on (with _animApplyToPuppet). ushort.MaxValue = none; FocusedPuppet()
    // falls back to the first live puppet when the pick is stale (despawn / zone change).
    private ushort _selectedPuppet = ushort.MaxValue;
    private List<MobRow>? _cachedFilter;
    private string _cachedFilterKey = "\0";

    // Live-name sync watermark. The harvester captures real nameplates while walking duties (Tier A3);
    // we fold those into the catalog rows' LiveName (top DisplayName priority) whenever its name count
    // grows, then invalidate the filter cache so DisplayName-keyed search + the zone tree show the
    // corrected label. -1 forces the first Draw to fold in whatever the harvester loaded from disk.
    private int _lastSyncedNameCount = -1;

    // Harvested-territory watermark, paired with _lastSyncedNameCount. TryLocateAll consults the harvester
    // (Tier A3), so a newly-placed base changes where a row buckets in the location tree — but that shift
    // touches neither row names nor search, so the name-sync above won't notice it. The harvester's distinct
    // -base count (MobHarvester.Count) ticks up when a new base is placed; DrawCatalogTab watches it and
    // forces a rebuild, keeping placement live without the old per-frame rebuild. -1 forces the first Draw to
    // reconcile against whatever loaded from disk. Inert while the harvester is off (the default): Count never moves.
    private int _lastHarvestBaseCount = -1;

    // Cached Location-tree STRUCTURE (bucketed + sorted zone/NPC/minion/unknown nodes). The tree is a pure
    // function of the filtered row set, so it's rebuilt only when that set changes — NOT every frame. Before
    // this, DrawLocationTree re-bucketed EVERY catalog row (each through TryLocateAll's 10-tier cascade) and
    // re-ran several LINQ sorts on every Draw while the Catalog tab was open; that ~halved FPS purely from the
    // window being visible. _treeRows holds the exact filtered-list instance the cache was built from —
    // Filtered() returns a NEW list instance on every filter invalidation, so a reference compare catches every
    // change (search, category, family, hide-unnamed, star, live-name sync) with no extra invalidation sites.
    private List<MobRow>? _treeRows;
    private List<LocNode>? _treeLocated, _treeEventNodes, _treeUnknown;
    private LocNode? _treeMinionNode, _treeMountNode;
    private int _treeMountCount;
    private int _treeMinionCount, _treeEventCount, _treeUnknownCount;

    // Which Location-tree zone nodes are expanded (persisted only in-memory — expand state is
    // ephemeral view state, not a saved preference). Keyed by the node's stable id: a real
    // TerritoryType id for a located zone, or a synthetic UnknownNodeKey(ex) for an ~expansion
    // "Unknown location" bucket. Nodes are collapsed by default; a live text filter force-opens
    // all of them (see DrawLocationTree) without touching this set.
    private readonly HashSet<uint> _expandedDuties = new();

    // Which Location-tree SECTION dividers (expansion / Minions / Unknown) are COLLAPSED. Sections are
    // expanded by default (a divider absent from this set is open), preserving the previous always-open
    // behavior; clicking a divider toggles it, so a DM can fold whole expansions away while browsing.
    // Keyed by the ExVersion byte for a located-expansion divider, or MinionSectionKey /
    // UnknownSectionKey for the two non-expansion fences. A live text filter force-opens everything
    // (autoExpand) without touching this set. In-memory only (ephemeral view state), like _expandedDuties.
    private readonly HashSet<uint> _collapsedSections = new();

    // Per-row category SET, precomputed once at load so the category chips + the chip filter don't
    // re-run the 10-tier TryLocate chain for every row on each keystroke. Every Duty-Finder category the
    // row's zones span, so a mob resident across several maps — a roamer, a game-wide striking dummy —
    // answers the chip filter for EACH of those categories, not just a single primary home (the tree
    // groups by the same multi-zone resolver, TryLocateAll). Keyed by BNpcBase id; only renderable rows
    // are stored (the only rows the chips/grouping touch); minions -> {"Minion"}, an unplaceable row ->
    // {"Unknown"}. A miss falls back to on-demand compute, so lookups are always safe. The tree derives
    // per-node expansion/located itself inline, so no separate single-home metadata table is kept.
    private readonly Dictionary<uint, HashSet<string>> _categories = new();

    // Per-row location-NAME search blob: the lowercased duty/place/region words of every zone a row is
    // placed in, joined into one string and precomputed at load beside _categories. Lets the catalog
    // search box match a LOCATION ("mistwake", "haukke", "mor dhona") and surface that zone's roster,
    // without re-running the 10-tier TryLocate chain per keystroke. Keyed by BNpcBase id; only rows with
    // a located home appear (an Unknown-tail row keeps answering name/id search only, exactly as before).
    private readonly Dictionary<uint, string> _locSearch = new();

    // Category chips actually populated in this data drop — the static CategoryChips minus the empties
    // (e.g. Inn / Housing / Gold Saucer carry no roster'd combat mobs, so their chips would filter to
    // nothing). Built once at load; "All" is always kept. Self-maintaining: a later data drop that
    // populates an empty category brings its chip back with no code change.
    private IReadOnlyList<(string text, string key)> _visibleCategoryChips = CategoryChips;

    public MainWindow(MobIndex index, TimelineIndex timeline, PoseLibrary poses, ContentIndex content, TerritoryIndex territory,
                      ManualLocationIndex manual, DungeonStemIndex stems, InstanceRosterIndex instanced, MobLoreIndex lore, LevelPlacementIndex level, WebLocIndex webloc, CompanionIndex companion, EnpcLocationIndex enpcLoc, MobHarvester harvest, GuiseService guise,
                      HumanGuise humanGuise, AnimationService anim, SpawnService spawn, PossessionService possession, HdmIpc ipc, MonikerIpc moniker,
                      Configuration config, IDalamudPluginInterface pi, IObjectTable objects, ITargetManager targets,
                      IClientState clientState, ITextureProvider textures, IPluginLog log, AccentPalette accent)
#if HDM_TESTING
        // Testing build's ImGui window-id (the token after ###) is deliberately DISTINCT from prod's.
        // ImGui shares one global context across every loaded plugin, so if a co-loaded prod HDM and a
        // testing HDMT both Begin("…###HDMMain"), ImGui resolves them to the SAME window and conjoins
        // their content (shared move/collapse/close — the "crams both plugins into a single window"
        // report). The visible label (HDMT v… b…) is irrelevant to identity; only the ###id is. Never
        // promoted: HDM_TESTING is Debug-only, so the prod build keeps "###HDMMain".
        : base($"HDMT v{Version} b{InternalBuild}###HDMMainTesting")
#else
        : base($"HDM v{Version}###HDMMain")
#endif
    {
        _index = index; _timeline = timeline; _poses = poses; _content = content; _territory = territory; _manual = manual; _stems = stems; _instanced = instanced; _lore = lore; _level = level; _webloc = webloc; _companion = companion; _enpcLoc = enpcLoc; _harvest = harvest;
        _guise = guise; _humanGuise = humanGuise; _anim = anim; _spawn = spawn; _possession = possession; _ipc = ipc; _moniker = moniker; _config = config;
        _anim.SelfOneShotEnded += OnSelfOneShotEnded;            // b24: settle peers when the DM's own one-shot ends
        _anim.SelfReleasedByMovement += OnSelfReleasedByMovement;  // b24: moving releases the DM's held pose
        _pi = pi; _objects = objects; _targets = targets; _clientState = clientState; _textures = textures; _log = log; _accent = accent;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        BuildCategoryIndex();
        BuildCategoryChips();
        LogLocationCoverage();
    }

    /// <summary>The guise subject: always the local player. HDM is self-apply only — a disguise
    /// (like a Glamourer glam) is worn by YOU and propagates to others through HMS sync; it is never
    /// enforced unilaterally on another actor. The identity inspector still READS your hard target to
    /// identify a mob (target it → become it), but the model always lands on self.</summary>
    private ICharacter? Self() => _objects.LocalPlayer;

    /// <summary>b37: the row selected in the Catalog / Spawn list (the model preview follows it).</summary>
    public MobRow? SelectedRow => _selected;

    /// <summary>b38: wired by Plugin to the preview window (the Catalog's "Preview" checkbox).</summary>
    public Func<bool>? PreviewIsOpen { get; set; }
    public Action<bool>? SetPreview { get; set; }

    /// <summary>True iff <paramref name="actor"/> is a legitimate self-apply subject: the LOCAL PLAYER or an
    /// HDM-owned spawned puppet. HDM is SELF-APPLY-ONLY — a disguise is never enforced on another player (the
    /// same safety model as Glamourer), so every manual GUI/chat apply is gated on this. The HMS-driven IPC
    /// mirror path (disguising a consenting peer) deliberately does NOT route through here — that's the sync
    /// feature, gated by HMS's own opt-in instead.</summary>
    private bool IsSelfOrOwnPuppet(ICharacter actor)
        => (Self() is { } self && actor.ObjectIndex == self.ObjectIndex) || _spawn.IsSpawned(actor.ObjectIndex);

    /// <summary>Absolute scale to write for a given row per the current scale mode (null = leave as-is).</summary>
    private float? ResolveScale(MobRow? row) => _config.ScaleMode switch
    {
        1 => row?.Scale,          // native
        2 => _config.ScaleCustom, // custom absolute
        _ => (float?)null,        // off
    };

    // ---- HMS sync emit funnels (see HdmIpc) ----------------------------------
    // These mirror every self/puppet action out to HMS so it can replicate this DM's disguise onto peers.
    // They are the ONLY coupling to the IPC layer from the UI; each is a no-op when the actor isn't a sync
    // subject, so sprinkling them at the render funnels never changes local behaviour.

    /// <summary>Resolve an actor to its HMS sync subject: the DM's own body (slot null), one of their own
    /// puppets (slot ≥ 0 = the puppet's <see cref="SpawnService.SlotOf"/>), or NOT-ours (returns false —
    /// HDM only syncs the DM's own body and own puppets, never an arbitrary target the DM animates). Every
    /// emit funnel gates on this so an action on someone else's actor is silently not reported.</summary>
    private bool TrySyncSubject(ICharacter? actor, out int? slot)
    {
        slot = null;
        if (actor == null) return false;
        if (Self() is { } self && actor.ObjectIndex == self.ObjectIndex) return true; // slot null = own body
        var s = _spawn.SlotOf(actor.ObjectIndex);
        if (s >= 0) { slot = s; return true; }
        return false;
    }

    /// <summary>Emit a disguise APPLY for whatever subject <paramref name="target"/> resolves to. Reports the
    /// SAME resolved scale/elevation the apply used, so a peer renders the identical size + draw offset; a
    /// fresh apply always clears any held loop (the redraw resets BaseOverride), so LoopId is 0 here — a
    /// later loop change re-emits via <see cref="ReportHeldLoop"/>. Human guises aren't HDM-scaled (Glamourer
    /// sizes them), so they report scale 1.0.</summary>
    private void ReportApply(ICharacter target, MobRow row, float? forcedScale, float? forcedElevation)
    {
        if (!TrySyncSubject(target, out var slot)) return;
        var scale = row.McType == 1 ? 1f : (forcedScale ?? ResolveScale(row) ?? 1f);
        _ipc.ReportDisguise(slot, row, scale, forcedElevation ?? 0f, 0);
    }

    /// <summary>Emit a disguise REVERT (a null-row atom → Kind 0) for the subject.</summary>
    private void ReportRevert(ICharacter target)
    {
        if (TrySyncSubject(target, out var slot)) _ipc.ReportDisguise(slot, null, 0f, 0f, 0);
    }

    /// <summary>Emit a one-shot animation EVENT (PlayOnce) for the subject. Never stored/snapshotted.</summary>
    private void ReportOneShot(ICharacter actor, ushort id)
    {
        if (!TrySyncSubject(actor, out var slot)) return;
        _ipc.ReportAction(slot, id);
        // b24: on the DM's OWN body the game ends a looping one-shot by itself; a peer's copy would loop it forever
        // (e.g. "Seat eat sequence" stayed seated on peers). Watch it here and settle peers when it ends.
        if (slot is null) _anim.WatchSelfOneShot(actor, id);
    }

    // b24: the DM's own one-shot ended locally (and nothing newer replaced it) -> tell peers to settle to idle. Sent
    // as a plain one-shot of the idle timeline, which every HDM version already plays (no wire change).
    private const ushort IdleTimeline = 3;
    private void OnSelfOneShotEnded(ICharacter self)
    {
        if (TrySyncSubject(self, out var slot) && slot is null) _ipc.ReportAction(null, IdleTimeline);
    }

    // b24: the DM moved out of a held pose (AnimLock released) -> give the hand back and release it on peers.
    private void OnSelfReleasedByMovement(ICharacter self)
    {
        HandRestore(self);
        ReportHeldLoop(self, 0);
    }

    /// <summary>Emit a held-loop STATE change for the subject (0 clears). Works disguised OR undisguised (b22:
    /// an undisguised subject's held pose rides a Kind-0 atom; it used to be dropped). Both a BaseOverride hold and a full-timeline
    /// replay report their id here — the wire atom carries only LoopId (§3), so a replay-loop currently
    /// renders as a hold on the receiver; exact replay fidelity would need a loop-kind bit (deferred).</summary>
    private void ReportHeldLoop(ICharacter actor, ushort id, ushort intro = 0)
    {
        if (TrySyncSubject(actor, out var slot)) _ipc.ReportLoop(slot, id, intro);
    }

    /// <summary>Put a prop (or, with null, nothing) in the subject's hand for a pose AND report it, so peers and
    /// late joiners see the same cup / pipe / book (IPC Minor 6). The single hand-change funnel with
    /// <see cref="HandRestore"/>.</summary>
    private void HandSet(ICharacter target, FFXIVClientStructs.FFXIV.Client.Game.Character.WeaponModelId? prop, bool offHand = false)
    {
        if (offHand && prop is { } o) _guise.SetPoseHand(target, default, o);
        else _guise.SetPoseHand(target, prop ?? default);
        // Hand: 1 empty, 2 prop in the main hand, 3 (b29) prop in the off hand.
        if (TrySyncSubject(target, out var slot))
            _ipc.ReportHand(slot, prop is null ? (byte)1 : offHand ? (byte)3 : (byte)2, prop?.Value ?? 0);
    }

    /// <summary>Give the subject its real weapon back after a pose, and tell peers (no-op if no pose took it).</summary>
    private void HandRestore(ICharacter target)
    {
        if (_guise.RestorePoseHand(target) && TrySyncSubject(target, out var slot)) _ipc.ReportHand(slot, 0, 0);
    }

    /// <summary>THE single "stop the held/looping animation" funnel (Rule 1). Blends the actor back to idle
    /// AND clears the held-loop state on peers (ReportHeldLoop 0 → receiver Sanitises). Every Stop button —
    /// Overview "Stop all", the per-actor list Stop, the Favourites Stop, and the Advanced raw Stop — routes
    /// through here so none can drift out of sync again (the Favourites Stop used to omit the peer report,
    /// leaving peers stuck in the last Special).</summary>
    private void StopAnim(ICharacter target)
    {
        _anim.Stop(target);
        HandRestore(target); // give back the weapons a pose took (and tell peers)
        ReportHeldLoop(target, 0);
    }

    /// <summary>Emit a scale-only STATE change for the subject. No-op on a non-sync actor and (inside HdmIpc)
    /// on a blank/Human subject. Called on slider RELEASE only: the receiver drives a scale delta LIVE through
    /// GuiseService.Resize (a 0x70 transform write, NO redraw), so a per-frame emit would spam epochs at every
    /// peer for no visual gain (see <see cref="HdmIpc.ReportScale"/>).</summary>
    private void ReportScaleChange(ICharacter actor, float scale)
    {
        if (TrySyncSubject(actor, out var slot)) _ipc.ReportScale(slot, scale);
    }

    /// <summary>Emit an elevation-only STATE change for the subject (vertical draw offset). Mirror of
    /// <see cref="ReportScaleChange"/>: no-op on a non-sync actor and (inside HdmIpc) on a blank subject, but —
    /// unlike scale — it applies to ANY guise kind, Human included (a draw offset lifts any body). Called on
    /// slider RELEASE only; the receiver drives it live through GuiseService.SetVerticalOffset, no redraw.</summary>
    private void ReportElevationChange(ICharacter actor, float voffset)
    {
        if (TrySyncSubject(actor, out var slot)) _ipc.ReportVOffset(slot, voffset);
    }

    /// <summary>Emit a freeze STATE change (animation-hold) for the subject. Mirror of
    /// <see cref="ReportElevationChange"/>: no-op on a non-sync actor and (inside HdmIpc, which dedupes) on an
    /// unchanged value. Unlike scale/elev — which are HMS-applied field writes — freeze is HDM-applied on the
    /// receiver (HdmIpc.SetFrozen pins speed 0/1 through AnimationService, the mechanism HMS can't reach), so the
    /// wire only carries the edge and the peer re-drives it. Own body → slot null; a driven puppet → slot N.</summary>
    private void ReportFreezeChange(ICharacter actor, bool frozen)
    {
        if (TrySyncSubject(actor, out var slot)) _ipc.ReportFrozen(slot, frozen);
    }

    /// <summary>Report a puppet the DM just spawned (or a blank clone dummy when <paramref name="row"/> is
    /// null). Reads the puppet's slot + spawn transform; no-op if the slot isn't tracked yet (shouldn't
    /// happen — SpawnService registers it inside TrySpawn before returning).</summary>
    private void ReportPuppetSpawn(ICharacter puppet, MobRow? row)
    {
        var slot = _spawn.SlotOf(puppet.ObjectIndex);
        if (slot < 0) return;
        var scale = row is null ? 1f : (row.McType == 1 ? 1f : (ResolveScale(row) ?? 1f));
        _ipc.ReportPuppetSpawned(slot, puppet.ObjectIndex, row, scale, 0f, puppet.Position, puppet.Rotation);
    }

    /// <summary>Report a puppet transform change (position/rotation) for its slot. Guards the slot lookup.</summary>
    private void ReportPuppetMove(ushort puppetIndex, Vector3 pos, float rot)
    {
        var slot = _spawn.SlotOf(puppetIndex);
        if (slot >= 0) _ipc.ReportPuppetMoved(slot, pos, rot);
    }

    /// <summary>Resolve a name-substring-or-BaseId query to a renderable catalog row (shared by the
    /// /hdm ipc dev harness; mirrors CommandApply's resolution rule).</summary>
    private MobRow? ResolveRow(string query)
    {
        query = query.Trim();
        if (query.Length == 0) return null;
        return uint.TryParse(query, out var baseId) && _index.TryGetByBase(baseId, out var byId) && IsRenderable(byId)
            ? byId
            : _index.Rows.FirstOrDefault(r => IsRenderable(r) && r.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Auto-focus the UI on the puppet the DM is POSSESSING (Problem B: "auto focus the npc we're controlling
    /// rather than manually assign control to it in the relevant menus"). On the possess-start edge, point the
    /// Spawn roster selection (<see cref="_selectedPuppet"/>) at the possessed puppet AND flip the Animations tab's
    /// subject toggle (<see cref="_animApplyToPuppet"/>) to it, so the animation chips + every "focused puppet"
    /// control drive the controlled NPC with no manual selection. Edge-gated via <see cref="_autoFocusedFor"/> so
    /// it fires once per possession — the DM can still retarget by hand afterward; a fresh possession re-focuses.
    /// </summary>
    private void SyncPossessionFocus()
    {
        if (_possession.IsPossessing)
        {
            var idx = _possession.PossessedIndex;
            if (idx >= 0 && _autoFocusedFor != idx)
            {
                _selectedPuppet = (ushort)idx;   // FocusedPuppet() now resolves to the possessed puppet
                _animApplyToPuppet = true;        // the Stage drives the puppet, not the DM
                _stageAll = false;
                _autoFocusedFor = idx;
            }
        }
        else if (_autoFocusedFor != null)
        {
            _autoFocusedFor = null;              // released -> re-possessing (even the same index) re-focuses
        }
    }

    public override void Draw()
    {
        // Auto-focus the possessed puppet before any tab draws, so this frame's target resolution (Animations tab,
        // "focused puppet" controls) already sees the possessed NPC as the subject (Problem B). No ImGui here —
        // pure UI-state sync — so it's safe above the accent push.
        SyncPossessionFocus();

        // Window-scope accent: tint the semantic "accent" ImGui slots (tab highlight, tree/collapsing headers,
        // checkmarks, slider grabs, text-selection) from the live palette so the whole window speaks ONE hue —
        // HM-Sync's when syncing, else HDM's own. Plain Buttons stay neutral on purpose (accent is for
        // selection/action STATE, per HMS), so ImGuiCol.Button is deliberately NOT pushed here. Pushed BEFORE
        // BeginTabBar (it reads Tab*), popped in the finally so an early return can't leak the colour stack.
        // [Accent feature 0.8.70 — kept as two one-line calls (Push/Pop) so a MainWindow layout redesign can
        //  re-home them trivially; the engine lives in AccentPalette, the pushes in PushWindowAccent below.]
        PushWindowAccent();
        try
        {
            if (!ImGui.BeginTabBar("##hgtabs")) return;

            if (ImGui.BeginTabItem("Catalog"))
            {
                DrawCatalogTab();
                ImGui.EndTabItem();
            }
            // b43: Stage = the old Animations + Spawn tabs merged (cast strip, subject, placement, library).
            if (ImGui.BeginTabItem("Stage"))
            {
                DrawStageTab();
                ImGui.EndTabItem();
            }
            // Config LAST: theme accent (+ HM-Sync sync) and any future settings. Rightmost, out of the
            // disguise workflow's way.
            if (ImGui.BeginTabItem("Config"))
            {
                DrawConfigTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
        finally
        {
            PopWindowAccent();
        }
    }

    // ---- Catalog tab ---------------------------------------------------------

    private void DrawCatalogTab()
    {
        // Fold any freshly-harvested nameplates into the rows before drawing (cheap no-op unless the
        // harvester's name count moved). This is the payoff of walking a duty: a mis-labelled or blank
        // base shows its true, first-hand name the instant it's sighted.
        if (_harvest.NameCount != _lastSyncedNameCount) SyncLiveNames();
        // Harvested-territory changes (a newly-placed base) shift tree bucketing without touching row names or
        // search, so the name-sync above won't catch them. Force a filter+tree rebuild when the harvester's
        // base count moves (inert while it's off — Count never ticks). One int compare per frame.
        if (_harvest.Count != _lastHarvestBaseCount) { _lastHarvestBaseCount = _harvest.Count; _cachedFilterKey = "\0"; }
        DrawHeader();          // controls block: Category/Type + Disguise·You/Scale panels + Disguise/Spawn + search + despawn + pills
        DrawDetailStrip();     // contextual readouts (identity / active-disguise) — usually empty
        ImGui.Separator();
        DrawTable();
    }

    private void DrawHeader()
    {
        // The header reads as labelled zones so related controls group together instead of one
        // dense row: Browse (search + toggle) · Filter (family chips) · Category (Duty-Finder
        // chips) · Apply (target + scale). Chips replace the old combos — single-select pills are
        // more discoverable than a hidden dropdown. The catalog body is always the Location tree
        // now (the old Group-by chips were removed — see DrawLocationTree).

        // ── Browse controls (search + Hide-unnamed toggle + column legend) live together on ONE prominent
        // row down in the action cluster (below Disguise/Spawn), per the DM's batch-1.1 layout — no lonely
        // toggle line up here. The header therefore leads straight into the filter panels below.

        // Shared once for the panels below: the accent, the local player, and whether it's disguised (the
        // DISGUISE·YOU group lights Revert while guised, and the rename toggle acts on the live disguise).
        var acc = _accent.Primary;
        var self = Self();
        var guised = self != null && (_guise.IsGuised(self.ObjectIndex) || _humanGuise.IsGuised(self.ObjectIndex));

        // ── CATEGORY: content-family chips (the Duty-Finder tabs), single-select + wrapping. Leads the
        // pair now (batch-1 swap — Category above Type).
        using (HmUi.Panel("Category",
            "Narrow the catalog to one content family, the way the in-game Duty Finder tabs do:\n" +
            "World / City / Housing / Dungeon / Trial / Raid / … resolved from each mob's home\n" +
            "territory. \"Unknown\" = no home territory yet (the instanced/uncovered tail); \"All\"\n" +
            "clears the filter. The catalog body groups every match under its home zone."))
        {
            if (WrappedChips(_visibleCategoryChips, _config.CategoryFilter, "cat") is { } catKey)
                SetCategory(catKey);
        }

        // ── TYPE: model-family filter (Monster / Demihuman / Human) — keys on the SKELETON family (the
        // Skel-prefix), the lever for isolating player-similar rows (a Human (c) guise takes native player
        // emotes; m/d skeletons carry none). "All" clears it. Labelled "Type" (batch-1 rename of "Family").
        using (HmUi.Panel("Type",
            "Filter the catalog by model family (the Skel-prefix):\n" +
            "  Monster (m) · Demihuman (d) · Human (c).\n" +
            "Human rows are painted on the player skeleton via Glamourer, so player\n" +
            "emotes like /sit work on them natively. Monster/Demihuman skeletons don't\n" +
            "carry the player emote set, so /sit won't render on them. \"All\" clears it."))
        {
            if (Chip("All",       "fam_all", _config.FamilyFilter == "All"))       SetFamily("All");
            ImGui.SameLine(0, 6); if (Chip("Monster",   "fam_m",   _config.FamilyFilter == "Monster"))   SetFamily("Monster");
            ImGui.SameLine(0, 6); if (Chip("Demihuman", "fam_d",   _config.FamilyFilter == "Demihuman")) SetFamily("Demihuman");
            ImGui.SameLine(0, 6); if (Chip("Human",     "fam_c",   _config.FamilyFilter == "Human"))     SetFamily("Human");
        }

        // ── DISGUISE | SCALE — side by side (mockup 1a, batch-1 swap). The disguise group (the
        // "things you apply to yourself" quick-actions) leads on the left; Scale, which sizes the active
        // disguise live, sits on the right. Both were lifted out of the old detail strip so every control
        // sits together above the tree.
        ImGui.Columns(2, "##catsd", false);

        using (HmUi.Panel("Disguise"))
        {
            if (self != null)
            {
                // Wisp | Hide — two equal-width accent buttons on one row. Revert now lives in the primary CTA
                // grid below (under Disguise, batch-3 item #4); Random was removed (batch-3 item #5 — random
                // mobs often glitched into just an upscaled DM). Hide is a persistent toggle (accented while
                // invisible); Wisp is a one-shot preset.
                const float gap = 6f;
                float bw = (ImGui.GetContentRegionAvail().X - HmUi.PanelPad - gap) / 2f;

                if (HmUi.AccentButton("Wisp", "wisp", false, acc, bw)) ApplyWisp(self);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Become a small territorial wisp (x0.50, raised off the floor).\n" +
                                     "An unobtrusive marker for a DM who isn't physically in the scene.");
                ImGui.SameLine(0, gap);
                var hidden = _guise.IsHidden(self.ObjectIndex);
                // Local draw-disable (self only) + the HMS OwnBodyHidden peer lane (mirror suppressed on peers,
                // lobby-wide, in or out of a loaded map session) so "Hide" reads as absent to everyone, not just us.
                if (HmUi.AccentButton(hidden ? "Unhide" : "Hide", "hide", hidden, acc, bw)) { _guise.SetHidden(self, !hidden); _ipc.ReportManualHidden(!hidden); }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Pull your character out of the render entirely: a DM 'here but not\n" +
                                     "physically present' switch. Click again to reappear.");

                // batch-4: pad the Disguise panel down by one control row so its height matches the taller Scale
                // panel (Scale = chips + Custom box + slider = 3 rows; Disguise = Wisp/Hide + rename = 2). The
                // spacer sits ABOVE the rename checkbox so the checkbox settles near the bottom edge, lining its
                // border up with Scale's.
                ImGui.Dummy(new Vector2(0f, ImGui.GetFrameHeight()));

                // Nameplate rename toggle (opt-in, self-only) — lifted from HOutfits' NPC "Apply name". When on,
                // disguising YOURSELF also sets your nameplate to the disguise's name via Moniker, which HMoniker
                // syncs to nearby players through HMS; Revert restores it. Greyed out when Moniker (HMoniker
                // v2.1+) isn't installed. Toggling takes effect immediately on the CURRENT disguise.
                var monikerAvailable = _moniker.Available;
                var applyName = _config.ApplyName;
                using (new Disabled(!monikerAvailable))
                {
                    if (ImGui.Checkbox("Rename nameplate to disguise", ref applyName))
                    {
                        _config.ApplyName = applyName;
                        _pi.SavePluginConfig(_config);

                        // Take effect now rather than waiting for the next disguise: ON pushes the active
                        // disguise's name immediately (when guised); OFF clears a name we set this session.
                        if (applyName)
                        {
                            if (guised && _selected is { } cur && _moniker.SetLocalName(cur.DisplayName))
                                _appliedNameThisSession = true;
                        }
                        else if (_appliedNameThisSession)
                        {
                            _moniker.ClearLocalName();
                            _appliedNameThisSession = false;
                        }
                    }
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(monikerAvailable
                        ? "Also set your nameplate to the disguise's name via Moniker,\nsynced to nearby players through HMS. Revert clears it."
                        : "Requires the Moniker (HMoniker) plugin, v2.1 or newer, installed and enabled.\n" +
                          "If you have it and this is still disabled, its IPC version may be older than 2.1.");
            }
            else
            {
                ImGui.TextDisabled("Log in to disguise yourself.");
            }
        }

        ImGui.NextColumn();

        using (HmUi.Panel("Scale",
            "Sizes the active disguise, applied immediately.\n" +
            "Native = the selected mob's authored scale (per-mob; tracks the model, e.g. x2.0 for Galatea Magna).\n" +
            "x0.5 / x1 = quick multipliers; the box and slider set any custom size (0.1 … 10)."))
        {
            // Native is auto-per-mob: it resolves each apply to the current row's Scale, so its chip shows
            // the SELECTED mob's real value rather than a frozen number.
            var nativeScale = _selected?.Scale ?? 1.0f;
            // batch-2: pressing Native also snaps the custom slider to the mob's native scale (the x0.5 / x1
            // chips already move the slider; Native didn't). The slider is bound to ScaleCustom, so seed it
            // with nativeScale for display while keeping ScaleMode == 1 so an apply still resolves per-mob to
            // the row's authored Scale. Set inline rather than via SetScaleMode(1) because that early-returns
            // when already in Native mode (so it wouldn't re-seed the slider on a repeat press).
            // Full-width segmented preset row (Native | x0.5 | x1) so the chips share the panel's left edge and its
            // right edge (-PanelPad) with the Custom box and slider below — a clean grid instead of a ragged
            // natural-width run. Weighted 2:1:1: the "Native x…" label is the longest (up to "Native x0.75"), so it
            // takes the double cell and never clips at the 560px min window; the two multipliers split the rest.
            // AccentButton is identical in look to Chip but width-capable (filled when active), matching the Wisp|Hide
            // split in the Disguise panel beside it.
            const float chipGap = 6f;
            float chipUnit = (ImGui.GetContentRegionAvail().X - HmUi.PanelPad - 2f * chipGap) / 4f;
            if (HmUi.AccentButton($"Native x{nativeScale:0.##}", "sc_nat", _config.ScaleMode == 1, acc, chipUnit * 2f))
            {
                _config.ScaleMode = 1;
                _config.ScaleCustom = nativeScale;
                _pi.SavePluginConfig(_config);
                ApplyScaleLive(commit: true);
            }
            ImGui.SameLine(0, chipGap); if (HmUi.AccentButton("x0.5", "sc_half", _config.ScaleMode == 2 && Approx(_config.ScaleCustom, 0.5f), acc, chipUnit)) SetCustom(0.5f);
            ImGui.SameLine(0, chipGap); if (HmUi.AccentButton("x1",   "sc_one",  _config.ScaleMode == 2 && Approx(_config.ScaleCustom, 1.0f), acc, chipUnit)) SetCustom(1.0f);

            // batch-3 (item #3): a Custom text box for an exact scale, clamped to 10 (anything above resets to
            // 10). Shares ScaleCustom with the slider below; committing on edit-end (IsItemDeactivatedAfterEdit,
            // not per keystroke) matches the slider's one-redraw-on-release behaviour. Typing 15 and tabbing away
            // snaps the box back to x10.00 next frame, since it re-reads the clamped ScaleCustom.
            // batch-4: vertically centre the "Custom" label against its framed input box (a bare
            // TextUnformatted sits at the line top, misaligned with the taller InputFloat beside it).
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Custom");
            ImGui.SameLine();
            // Fill to -PanelPad so the box's right edge is flush with the segmented chip row and the slider (all
            // three rows now end on the same grid line); the inline "Custom" label occupies the left indent.
            ImGui.SetNextItemWidth(-HmUi.PanelPad);
            var scBox = _config.ScaleCustom;
            ImGui.InputFloat("##scalebox", ref scBox, 0f, 0f, "x%.2f");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Type an exact scale (0.1 to 10). Anything above 10 clamps to 10.");
            if (ImGui.IsItemDeactivatedAfterEdit())
                SetCustom(Math.Clamp(scBox, 0.1f, 10f));

            // Full-width custom multiplier under the presets. Dragging implies custom mode; the size lands LIVE
            // as you drag (WriteScaleLive is a transform write with NO redraw, so it's cheap per-frame — matches
            // the Favourites slider) and the config persists once on release (no per-frame disk storm).
            // -PanelPad keeps a symmetric right margin.
            ImGui.SetNextItemWidth(-HmUi.PanelPad);
            var sc = _config.ScaleCustom;
            if (ImGui.SliderFloat("##scaleval", ref sc, 0.1f, 10f, "x%.2f"))
            {
                _config.ScaleMode = 2;
                _config.ScaleCustom = sc;
                ApplyScaleLive(commit: false); // resize the active disguise WHILE dragging (local only)
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                _pi.SavePluginConfig(_config);  // persist once, on release
                ApplyScaleLive(commit: true);   // live write + sync to peers on release (no redraw, no real-body flash)
            }
        }

        ImGui.Columns(1);

        // ── Primary CTA grid (2×2). Row 1: Disguise | Spawn — the batch-1 split of the old single "Spawn
        // puppet from selection" button; both act on the SELECTED catalog row (a plain click selects one).
        // Row 2: Revert | Despawn — Revert relocated here under Disguise (batch-3 item #4), lit while guised;
        // Despawn sits under Spawn but goes inert until puppets are live (batch-4).
        //
        // Layout: the grid REUSES the Disguise|Scale boxes' columns id ("##catsd"), so the four buttons inherit
        // pixel-identical column boundaries from the boxes directly above them — Disguise/Revert track the
        // Disguise box, Spawn/Despawn track the Scale box. The old hand-split (`(avail-6)/2` + a 6px SameLine
        // gap) did NOT match: ImGui's column gutter is wider than 6px and its last-column inset trims the right
        // edge, so the buttons read wider-in-the-middle and off from the boxes. Sharing the columns id is the
        // idiom for aligning two separate blocks; per-cell width = that column's GetContentRegionAvail — the
        // exact span HmUi.Panel captures for its border. Don't "simplify" this back to a hand-computed width.
        // Row 1 stays clickable while nothing's selected so the tooltip can say what's missing.
        var canAct = _selected is not null && self != null;
        ImGui.Columns(2, "##catsd", false);

        if (HmUi.PrimaryButton("Disguise", "selfdisguise", acc, ImGui.GetContentRegionAvail().X, canAct)
            && self is { } ds && _selected is { } dsel)
            ApplyGuise(ds, dsel);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(_selected is { } dps
                ? $"Wear {dps.DisplayName} yourself. Revert (below) drops it."
                : "Pick a catalog row first (a plain click selects it), then Disguise wears it.");

        ImGui.NextColumn();
        if (HmUi.PrimaryButton("Spawn", "spawnpuppet", acc, ImGui.GetContentRegionAvail().X, canAct))
            SpawnSelected();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(_selected is { } ps
                ? $"Spawn a set-piece puppet a step ahead of you, disguised as {ps.DisplayName}.\n" +
                   "Non-targetable: the party can't click or attack it. Despawn it below."
                : "Pick a catalog row first (a plain click selects it), then Spawn drops a puppet wearing it.");

        // Row 2: Revert (under Disguise) lights while a disguise is active; Despawn all (under Spawn) stays
        // in place but goes inert when no puppets are live. RevertGuise no-ops safely when nothing's worn.
        ImGui.NextColumn();
        if (HmUi.AccentButton("Revert", "revert", guised, acc, ImGui.GetContentRegionAvail().X) && self is { } rvt)
            RevertGuise(rvt);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Restore your real model + size and un-stick any animation.");

        // batch-4: Despawn stays UNHIDDEN under Spawn at all times (keeps the 2×2 grid square) but goes inert
        // when no puppets are live, rather than vanishing. Disabled dims the red fill; the tooltip still shows
        // through AllowWhenDisabled so the empty state can explain itself.
        ImGui.NextColumn();
        var anyPuppets = _spawn.Count > 0;
        using (new Disabled(!anyPuppets))
        {
            PushRed();
            if (ImGui.Button($"Despawn all ({_spawn.Count})##puppets", new Vector2(ImGui.GetContentRegionAvail().X, 0f)) && anyPuppets)
            { _spawn.DespawnAll(); _puppetGuise.Clear(); }
            PopColors();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(anyPuppets
                ? "Despawn every live puppet. A zone change clears them too."
                : "No live puppets to despawn. Spawn one first.");

        ImGui.Columns(1);

        // ── Disguise the FOCUSED PUPPET from the same selection, WITHOUT possession. Spawned puppets are
        // non-targetable (incorporeal), so they can't be clicked/focused in-world — this re-skins the Spawn-tab
        // roster's focused puppet through the shared ApplyGuise funnel, the very path the per-puppet row's Apply
        // uses (no new mechanism; peer-synced for free via ReportApply inside the funnel). Only appears once a
        // puppet is live so the common self-only flow stays uncluttered, and it's the discoverable answer to
        // "how do I disguise an incorporeal puppet without possessing it" (previously buried in the Spawn tab). ──
        if (anyPuppets)
        {
            var fpup = FocusedPuppet();
            bool canPup = _selected is not null && fpup is not null;
            string pupName = fpup is { } fpc && _puppetGuise.TryGetValue(fpc.ObjectIndex, out var pwl)
                ? pwl.DisplayName : "the focused puppet";
            ImGui.Spacing();
            if (HmUi.PrimaryButton("Disguise focused puppet", "disguisepup", acc, ImGui.GetContentRegionAvail().X, canPup)
                && fpup is { } pup && _selected is { } prow)
            {
                // Preserve a dialed puppet size across the re-guise (matches the Spawn-tab per-puppet Apply).
                var forced = _puppetScale.TryGetValue(pup.ObjectIndex, out var ps) ? (float?)ps : null;
                ApplyGuise(pup, prow, forcedScale: forced);
                _puppetGuise[pup.ObjectIndex] = prow; // keep the Spawn roster chip in sync
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(canPup
                    ? $"Wear {_selected!.DisplayName} on {pupName} — no possession needed.\n" +
                       "That's the puppet selected on the Stage tab; pick another cast chip there to retarget.\n" +
                       "Spawned puppets are non-targetable, so this is how you re-skin one in place."
                    : _selected is null
                        ? "Pick a catalog row first (a plain click selects it), then disguise the focused puppet."
                        : "Spawn a puppet first (Stage > + Spawn); its cast chip picks which one is focused.");
        }

        // ── Browse row (batch-1.1): a prominent, near-full-width search sits between the Disguise/Spawn
        // buttons and the despawn line, with the Hide-unnamed toggle tucked right-aligned onto the SAME
        // row (it no longer earns a lonely line at the top). The input stretches to fill whatever the
        // Hide-unnamed toggle and the conditional Clear button don't use. (batch-2: the (?) column legend
        // that used to sit beside Hide-unnamed was dropped — the DM asked that Hide-unnamed not carry a (?),
        // and the tree's own column headers already document the columns.)
        // batch-3 (item #2): give the search row more breathing room and a faint accented border so it stops
        // blending into the panel. A bumped FramePadding wraps the whole row (input + Clear + Hide-unnamed) so
        // their heights stay consistent; the border is scoped to the input itself.
        ImGui.Spacing();
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10f, 6f));
        var brStyle = ImGui.GetStyle();
        float brAvail = ImGui.GetContentRegionAvail().X;
        float brGroupW = ImGui.GetFrameHeight() + brStyle.ItemInnerSpacing.X + ImGui.CalcTextSize("Hide unnamed").X;
#if HDM_PREVIEW
        brGroupW += brStyle.ItemSpacing.X + ImGui.GetFrameHeight() + brStyle.ItemInnerSpacing.X + ImGui.CalcTextSize("Preview").X;
#endif
        float brClearW = _filter.Length > 0 ? ImGui.CalcTextSize("Clear").X + brStyle.FramePadding.X * 2f + brStyle.ItemSpacing.X : 0f;
        float brSearchW = brAvail - brGroupW - brClearW - brStyle.ItemSpacing.X;
        if (brSearchW < 140f) brSearchW = 140f;

        ImGui.SetNextItemWidth(brSearchW);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(acc.X, acc.Y, acc.Z, 0.55f));
        ImGui.InputTextWithHint("##filter", "Search catalog…", ref _filter, 64);
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Search by name, home location, BaseId, ModelChara id, or m-code.");
        if (_filter.Length > 0)
        {
            ImGui.SameLine(0, brStyle.ItemSpacing.X);
            if (ImGui.Button("Clear##clearfilter")) { _filter = string.Empty; _cachedFilterKey = "\0"; }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Clear the search.");
        }

        // Hide-unnamed toggle, right-aligned so it hugs the row's end. (batch-2: the (?) column legend that
        // used to sit beside it was removed at the DM's request; the tree's column headers cover that ground.)
        ImGui.SameLine();
        float brGroupX = ImGui.GetContentRegionMax().X - brGroupW;
        if (ImGui.GetCursorPosX() < brGroupX) ImGui.SetCursorPosX(brGroupX);
#if HDM_PREVIEW
        // b38: the model-preview side window toggle lives with the search (DM). b41: parked, testing builds only.
        var previewOn = PreviewIsOpen?.Invoke() ?? false;
        if (ImGui.Checkbox("Preview", ref previewOn)) SetPreview?.Invoke(previewOn);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show a side window that renders whatever you select here or in the Spawn list.\n" +
                             "Nothing is spawned; drag to turn it, scroll to zoom.");
        ImGui.SameLine();
#endif
        if (ImGui.Checkbox("Hide unnamed", ref _hideUnnamed)) _cachedFilterKey = "\0";
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Hide the rows with no name at all: the model-ID-only entries that read\n" +
                             "\"(unnamed) <skel> #<BaseId>\". They're shown by default (searchable by\n" +
                             "#id or m-code). Boss bases and instanced roster names (shown in slate) are\n" +
                             "kept; they count as named.");
        ImGui.PopStyleVar();
        ImGui.Spacing();

        // ── Meta pills — the endless count string becomes compact facts. "matches" tracks the live filter
        // (what the tree shows); mobs / NPCs / minions are catalog totals (computed once, cached).
        EnsureCatalogCounts();
        HmUi.MetaPills(
            (Filtered().Count.ToString("N0"), "matches"),
            (_catalogZones.Count.ToString("N0"), "zones"),
            (_countMobs!.Value.ToString("N0"), "mobs"),
            (_countNpcs!.Value.ToString("N0"), "NPCs"),
            (_countMinions!.Value.ToString("N0"), "minions"));
    }

    // Catalog composition totals for the meta pills — computed once (they don't move with the filter),
    // then cached. mobs = Battle-source rows; NPCs = Event-source (ENpcBase humanoids); minions = the
    // summonable companions. Renderable-gated so the numbers match what the catalog can actually become.
    private int? _countMobs, _countNpcs, _countMinions;

    // Distinct home territories the LOCATED tree spans — the "zones" meta pill (deferred from Phase 1
    // because it needs the tree's TryLocateAll grouping, not a raw row count). Filled ONCE at load inside
    // BuildCategoryIndex (piggybacks the TryLocateAll pass that method already pays for — no per-frame cost;
    // the pill just reads .Count). Mirrors DrawLocationTree's bucketing: Event NPCs and minions are fenced
    // into their own sections with no home zone, so they don't count here.
    private readonly HashSet<uint> _catalogZones = new();
    private void EnsureCatalogCounts()
    {
        if (_countMobs.HasValue) return;
        int mobs = 0, npcs = 0, minions = 0;
        foreach (var r in _index.Rows)
        {
            if (!IsRenderable(r)) continue;
            if (r.Source == NpcSource.Event) npcs++; else if (r.Source != NpcSource.Mount) mobs++;
            if (_companion.IsMinion(r.BaseId)) minions++;
        }
        _countMobs = mobs;
        _countNpcs = npcs;
        _countMinions = minions;
    }

    /// <summary>Float near-equality for lighting the active scale chip (slider values never land
    /// exactly on a preset after a round-trip through the config).</summary>
    private static bool Approx(float a, float b) => MathF.Abs(a - b) < 0.001f;

    // Chip mutators — each no-ops when unchanged, persists config, and invalidates the filter
    // cache where the change affects which rows are listed (category) vs only how they're
    // presented (group) or applied (target/scale).
    private void SetCategory(string v)
    {
        if (_config.CategoryFilter == v) return;
        _config.CategoryFilter = v;
        _pi.SavePluginConfig(_config);
        _cachedFilterKey = "\0"; // category narrows WHICH rows are listed → invalidate the filter cache
    }

    private void SetFamily(string v)
    {
        if (_config.FamilyFilter == v) return;
        _config.FamilyFilter = v;
        _pi.SavePluginConfig(_config);
        _cachedFilterKey = "\0"; // family narrows WHICH rows are listed → invalidate the filter cache
    }

    private void SetScaleMode(int v)
    {
        if (_config.ScaleMode == v) return;
        _config.ScaleMode = v;
        _pi.SavePluginConfig(_config);
        ApplyScaleLive(commit: true); // land the new mode on the active disguise now + sync
    }

    private void SetCustom(float v)
    {
        _config.ScaleMode = 2;
        _config.ScaleCustom = v;
        _pi.SavePluginConfig(_config);
        ApplyScaleLive(commit: true); // preset taps land on the active disguise immediately + sync
    }

    // ---- THE scale / elevation control funnels ------------------------------------------------------------
    // One write-live-then-sync path per transform, called from EVERY surface (Catalog, Favourites, per-puppet,
    // aggregate, Animations) — no per-tab duplication of "resize + maybe report". Drag calls with commit:false
    // (live local write only); release / preset / button calls with commit:true (same write + a one-shot HMS
    // report). The receiver drives both deltas live (no redraw), so commit-on-release is cheap for peers.

    /// <summary>Write <paramref name="scale"/> live on <paramref name="target"/> (a 0x70 draw-object transform,
    /// NO redraw) and, when <paramref name="commit"/>, sync it to HMS. The CALLER owns the guise gate: Resize is
    /// UNGATED and would size the REAL body of an un-guised actor, so only call for an actor confirmed
    /// Monster/Demi-guised (a Human sizes through Glamourer, never here). The per-puppet read-back cache
    /// (_puppetScale) stays a puppet-UI concern, updated by those callers — not here.</summary>
    private void ApplyScaleTo(ICharacter target, float scale, bool commit)
    {
        _guise.Resize(target, scale);
        if (commit) ReportScaleChange(target, scale);
    }

    /// <summary>Write <paramref name="voffset"/> live on <paramref name="target"/> (a draw offset, NO redraw)
    /// and, when <paramref name="commit"/>, sync it to HMS. Symmetric with <see cref="ApplyScaleTo"/>; a draw
    /// offset lifts ANY body (Human included) and ~0 clears it, so — unlike scale — it needs no guise gate.</summary>
    private void ApplyElevationTo(ICharacter target, float voffset, bool commit)
    {
        _guise.SetVerticalOffset(target, voffset);
        if (commit) ReportElevationChange(target, voffset);
    }

    /// <summary>THE single self-Freeze funnel the Animations tab calls (for self or the driven puppet) — pins
    /// <see cref="_speed"/> to 0 (hold) or 1 (resume) and pushes it live; AnimationService re-asserts
    /// OverallSpeed every frame so the hold sticks. Also reports the edge to HMS so the hold mirrors across a
    /// session (peers re-drive it via HdmIpc.SetFrozen). Puppets keep their OWN freeze (per-idx
    /// <see cref="_puppetSpeed"/>), a distinct state holder, and report through their own checkbox site.</summary>
    private void ApplyFreeze(ICharacter target, bool frozen)
    {
        _speed = frozen ? 0f : 1f;
        _anim.SetSpeed(target, _speed);
        ReportFreezeChange(target, frozen);
    }

    /// <summary>
    /// Self convenience over <see cref="ApplyScaleTo"/>: push the active Catalog scale setting onto the local
    /// body live, so the size knob behaves like a live control, not a next-apply setting. Gated on
    /// <see cref="GuiseService.IsGuised"/> (Monster/Demi only) so dragging the knob while undisguised — or while
    /// wearing a Human guise (Glamourer sizes those) — can never resize the real body. <paramref name="commit"/>
    /// (release / preset tap) also syncs to HMS. THE self-scale entry every Catalog scale surface calls.
    /// </summary>
    private void ApplyScaleLive(bool commit = false)
    {
        if (Self() is not { } t || !_guise.IsGuised(t.ObjectIndex)) return;
        ApplyScaleTo(t, ResolveScale(_selected) ?? _selected?.Scale ?? 1f, commit);
    }

    private void DrawDetailStrip()
    {
        // Contextual readouts only. The self-actions (Revert / Wisp / Hide / Random / Rename) and the scale
        // controls now live in the header's DISGUISE·YOU / SCALE panels, and the spawn button moved up with
        // them (mockup 1a). What's left is "what am I looking at, and what am I wearing?" — the target
        // identity inspector and the active-disguise line, both conditional, so this strip is usually empty.

        // Target identity inspector. A live actor's BaseId IS its BNpcBase id (ENpcBase for
        // event NPCs) — exactly the catalog's Base key — and it stays the actor's TRUE id even
        // while guised (the guise only swaps ModelCharaId). Cross-reference the FULL index so
        // "what is this mob, is it in the catalog?" is one glance. This is the direct answer to
        // "I couldn't find Chort": target it and read its Base here. A hit the table doesn't list
        // is a family the filter hides (Object/Part/placeholder); a miss is an Event NPC or
        // content newer than the shipped data drop.
        // Read the hard target INDEPENDENTLY of the apply subject. Self-apply means the model always
        // lands on you, but the inspector still needs the thing you clicked on to identify it — target
        // a mob → read its Base here → become it. A player target has BaseId 0 and drops through.
        if (_targets.Target is ICharacter tgt && tgt.BaseId != 0)
        {
            if (_index.TryGetByPair(tgt.BaseId, tgt.NameId, out var known)) // exact nameplate pair, else the body's primary row
            {
                var hidden = !IsRenderable(known);
                // Show a real or roster-inferred name; for a still-nameless base, fall back to a name the
                // harvester saw live (Tier A3) before collapsing to "(unnamed)" — the direct payoff of
                // walking a duty: the white YoRHa androids read their real name the moment they're sighted.
                var label = known.Name.Length > 0 || known.NameIsHeuristic
                    ? known.DisplayName
                    : _harvest.TryGetName(tgt.BaseId, out var liveName) ? $"{MobRow.Cap(liveName)} (seen live)" : "(unnamed)";
                ImGui.TextDisabled(
                    $"Identity: Base {tgt.BaseId} → {label}  ·  {known.Kind} {known.SkeletonCode}  ·  " +
                    $"ModelChara {known.ModelCharaId}{(hidden ? "  (hidden by filter)" : "")}");

                // One-click apply straight off the identity line — the "target it → become it"
                // loop (the direct answer to "I saw this thing in a duty, make me it"). Offered
                // only for an APPLICABLE identity (a renderable family McType 1/2/3 with a real
                // skeleton); a hidden family (Object/Part/placeholder) can't render from a swap,
                // so it stays Select-only. Applies to YOU (self-apply only) — you inspect the
                // target to identify it, then wear it, exactly like a catalog-row click.
                if (!hidden)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Apply##ident") && Self() is { } it)
                    {
                        _selected = known;
                        ApplyGuise(it, known);
                    }
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Select##ident"))
                {
                    _selected = known;
                    // Surface it in the table when it's a family the view shows; otherwise just
                    // select it (Apply/Animations still work) without a filter that lists nothing.
                    if (!hidden)
                    {
                        // Filter by the visible label (crowdsourced OR roster-inferred name); fall back
                        // to the BaseId handle only for a truly nameless base, so Select always lands a
                        // search that actually lists the row.
                        _filter = known.Name.Length > 0 || known.NameIsHeuristic ? known.DisplayName : known.BaseId.ToString();
                        _cachedFilterKey = "\0";
                    }
                }

                // Where this mob lives, resolved through the full tier chain (harvester included) and shown
                // on the identity line itself: target an unknown mob and read its home zone at a glance —
                // the "I saw this in a duty, where was it?" answer, now first-hand for instanced rosters.
                if (TryLocate(known, out var homeTerr) && _content.TryGet(homeTerr, out var home))
                {
                    // A curated tag carries a provenance note the bare zone can't convey — a Tier M
                    // per-base tag ("YoRHa: Dark Apocalypse cutscene prop") or, failing that, the Tier N
                    // dungeon-stem label ("Tower of Babil — dungeon trash, name-stem rule") that explains
                    // why a director-spawned mob resolves to a duty it was never observed in.
                    var note = _manual.TryGetNote(known.BaseId, out var mn) ? $"  ·  {mn}"
                             : known.Name.Length > 0 && _stems.TryGetNote(known.Name, out var sn) ? $"  ·  {sn}"
                             : "";
                    ImGui.TextDisabled($"Resident of: {home.Summary}{note}");
                }

                // Minion provenance: a summonable minion has no home zone, so in place of a "Resident of"
                // line it names its source class — "target it → confirm it's the okuri chochin minion".
                if (_companion.TryGetMinion(known.BaseId, out var minionName))
                    ImGui.TextDisabled($"Minion: {minionName}");

                // Event NPC provenance: an ENpcBase humanoid likewise has no home zone; name its class so a
                // targeted town/quest character reads as a catalog entry you can apply or spawn a copy of.
                if (known.Source == NpcSource.Event)
                    ImGui.TextDisabled("NPC · humanoid character (apply, or spawn a copy)");
            }
            else
            {
                ImGui.TextDisabled($"Identity: Base {tgt.BaseId}, not in catalog (NPC, or newer than the data drop)");
            }
        }

        if (_selected is { } sel)
        {
            // The active-disguise indicator, on its own line under the Revert chip (the user's "the
            // active disguise is a good indicator"): what model you're wearing, its skeleton + size.
            var minionTag = sel.Source == NpcSource.Event ? "  ·  NPC"
                          : _companion.IsMinion(sel.BaseId) ? "  ·  minion" : "";
            ImGui.TextDisabled($"Active: {sel.DisplayName}  ModelChara {sel.ModelCharaId}  {sel.SkeletonCode}  x{sel.Scale:0.##}{minionTag}");

            // Human (McType 1) guises paint through Glamourer; warn if it's absent
            // so Apply doing nothing isn't a mystery.
            if (sel.McType == 1 && !_humanGuise.Available)
                ImGui.TextColored(new Vector4(1f, 0.75f, 0.25f, 1f),
                    "Human guise needs Glamourer, not detected; Apply will no-op.");
        }

    }

    /// <summary>
    /// Route a guise to the right renderer by model family. Monster (McType 3) and
    /// Demihuman (McType 2) render from a ModelCharaId swap (+ equipment) via
    /// GuiseService. Human (McType 1) can't — a swap leaves it T-posed — so it goes
    /// through Glamourer (HumanGuise: customize + NPC gear). Either way we first
    /// clear a guise of the OTHER family on the target, so switching families never
    /// leaves a monster skeleton wearing a Glamourer face (or vice-versa). Each
    /// clear is a no-op when the actor wasn't guised that way.
    /// </summary>
    private void ApplyGuise(ICharacter target, MobRow row, float? forcedScale = null, float? forcedElevation = null)
    {
        // SELF-APPLY-ONLY safety gate. Every GUI/chat apply funnels through here, so this one check makes it
        // structurally impossible to disguise another player by click OR command — refuse anything that isn't
        // YOU or an HDM-owned puppet. (The HMS-driven IPC mirror path does not come through ApplyGuise; it is
        // the consented sync feature and is gated by HMS's opt-in, not by this guard.)
        if (!IsSelfOrOwnPuppet(target))
        {
            _log.Warning($"HDM: refused to apply '{row.DisplayName}' to obj#{target.ObjectIndex} — self-apply-only (not you or an HDM puppet).");
            return;
        }
        HandRestore(target);   // a pose's prop/empty hand goes back before the disguise rebuilds the weapons

        if (row.McType == 1)
        {
            // Restore the real c-skeleton, THEN paint the human face — but the revert is an ASYNC
            // redraw (GuiseService tears the draw object down and rebuilds it over several frames).
            // Painting immediately, as we used to, lands the Glamourer ApplyState on a half-rebuilt
            // draw object: the torso + gear vanish and only a SECOND click (after the redraw had
            // finished) rendered right. Hand the paint to GuiseService as a continuation so it fires
            // once the skeleton is drawable again; when nothing was guised it runs immediately.
            // (A Human guise sizes through Glamourer, not GuiseService, so forcedScale is moot here.)
            var idx = target.ObjectIndex;
            var baseId = row.BaseId;
            var name = row.DisplayName;
            var source = row.Source; // Battle (BNpcBase) vs Event (ENpcBase) — selects the customize/gear sheet
            _guise.Revert(target, () => _humanGuise.Apply(idx, baseId, name, source));
        }
        else
        {
            // forcedScale overrides the global scale mode for canned presets (Wisp) and a
            // per-puppet re-guise's forced scale; null falls back to the current scale mode.
            _humanGuise.Revert(target.ObjectIndex); // drop any Glamourer face before swapping the model
            _guise.Apply(target, row, forcedScale ?? ResolveScale(row));
        }

        // Sanitise the draw-elevation on EVERY apply: assert the requested lift, or clear to the floor
        // when none is asked. Without this a preset's raise (the Wisp's +2.80) or a favourite's dialed
        // elevation stuck around and floated the NEXT disguise — the Monster apply path never touches the
        // offset, only Revert did. SetVerticalOffset treats ~0 as "clear", so null/0 removes any managed
        // lift and drops the offset from the re-assert set. Family-agnostic (a draw offset), so it also
        // lands the Human path once its deferred redraw settles (re-asserted per frame).
        _guise.SetVerticalOffset(target, forcedElevation ?? 0f);

        // Mirror to HMS: this is the single apply funnel for self AND puppets (spawn onReady + spawn-tab
        // re-guise route here), so one emit covers every apply path. No-op for a non-sync-subject actor.
        ReportApply(target, row, forcedScale, forcedElevation);

        // Nameplate rename (opt-in, self-only). When the ApplyName toggle is on and Moniker is available,
        // push the disguise's name to YOUR nameplate via Moniker; HMoniker then syncs it to nearby players
        // through HMS (HDM does no nameplate sync itself). Gated to the LOCAL PLAYER only: ApplyGuise also
        // drives HDM puppets, but Moniker's SetLocalName only ever renames the local player, so a puppet
        // apply must never touch the nameplate. Remembering that we set it lets Revert clear exactly the
        // name we wrote — never a name the user set in Moniker directly.
        if (_config.ApplyName && _moniker.Available && Self() is { } me && target.ObjectIndex == me.ObjectIndex)
        {
            if (_moniker.SetLocalName(row.DisplayName))
                _appliedNameThisSession = true;
        }

        // batch-3 (item #6): remember what the LOCAL PLAYER is now wearing so the Animations tab tracks the
        // WORN model, not the catalog browse-cursor (_selected drifts on a plain click). Self-only — a puppet
        // apply routes through here too, but the tab is about your own guise. Cleared in RevertGuise.
        if (Self() is { } wornSelf && target.ObjectIndex == wornSelf.ObjectIndex)
            _wornGuise = row;
    }

    /// <summary>Un-stick animation, then revert whichever guise family is on the target (both no-op if absent),
    /// and clear the disguise bookkeeping. <see cref="_wornGuise"/> (what you're WEARING) is nulled so the
    /// Animations tab falls back to the neutral human-playables view: it scopes its "This mob" specials and
    /// caps-trimmed "playable by {skel}" sections to the worn model. <see cref="_selected"/> (the catalog
    /// browse-cursor) is nulled too, so the row simply un-highlights; re-applying is one click. The animation
    /// itself already stopped (Sanitize cleared BaseOverride + the replay loop), so this only drops the
    /// identity — nothing keeps advertising the mob you just took off.</summary>
    private void RevertGuise(ICharacter target)
    {
        _anim.Sanitize(target);
        HandRestore(target); // a pose's prop / emptied hands go back before the revert rebuilds weapons
        // Self-apply only (every caller passes the local player), so use the HARD revert: it force-clears
        // a stuck NPC model even when tracking was lost (e.g. a disguise that rode across a zone line and
        // stranded), guaranteeing Revert / "/hdm revert" can always put the DM back to their real body.
        _guise.RevertLocalPlayerHard(target);
        _humanGuise.Revert(target.ObjectIndex);
        _wornGuise = null;
        _selected = null;

        ReportRevert(target); // mirror the revert to HMS (peers drop the puppet copy)

        // Clear the Moniker nameplate rename if THIS session set one (self-only; every RevertGuise caller
        // passes the local player). Guarded by _appliedNameThisSession so we only restore a name we wrote,
        // never one the user set in Moniker directly. HMoniker propagates the clear to peers through HMS.
        if (_appliedNameThisSession && _moniker.Available)
        {
            _moniker.ClearLocalName();
            _appliedNameThisSession = false;
        }
    }

    // The territorial-wisp preset: a small, slightly-raised will-o'-the-wisp (BNpcBase 2512, ModelChara
    // 79). The DM wears this as an unobtrusive "I'm running the scene but not physically present" marker
    // — a faint mote that still shows the party where the DM's token is. Scale + elevation are FORCED
    // regardless of the current scale mode so the preset always lands the same recognisable size.
    private const uint WispBaseId = 2512;
    private const float WispScale = 0.50f;
    private const float WispElevation = 2.80f;

    /// <summary>Apply the territorial-wisp preset to self (see <see cref="WispBaseId"/>) — forced x0.50 and
    /// a +2.80 draw elevation. No-ops with a log line if the wisp base isn't in the shipped catalog.</summary>
    private bool ApplyWisp(ICharacter self)
    {
        if (!_index.TryGetByBase(WispBaseId, out var row))
        {
            _log.Warning($"HDM: wisp base {WispBaseId} not in catalog — cannot apply preset.");
            return false;
        }
        _selected = row;
        // Elevation goes THROUGH ApplyGuise now (forcedElevation) so it uses the same single assert path
        // every apply does — the preset always lands at exactly x0.50 / +2.80, and switching away from the
        // wisp sanitises the lift back to the floor instead of stranding it on the next disguise.
        ApplyGuise(self, row, WispScale, WispElevation);
        return true;
    }

    /// <summary>Spawn a non-targetable set-piece puppet a step ahead of the local player and immediately
    /// disguise it as the selected catalog row — self-apply's sibling for placed actors. The disguise
    /// runs through the SAME objectIndex-keyed <see cref="ApplyGuise"/> path used on you, just pointed at
    /// the puppet (Principle 1). No-op (with a log line) when nothing is selected or the native spawn
    /// fails — SpawnService already logs the failure reason (no local player / object table full).</summary>
    private void SpawnSelected()
    {
        if (_selected is not { } row)
        {
            _log.Information("HDM: Spawn — pick a catalog row first (the puppet wears the selected mob).");
            return;
        }
        if (SpawnPuppetAs(row) is { } puppet)
            _log.Information($"HDM: spawned puppet obj#{puppet.ObjectIndex} as {row.DisplayName} (Base {row.BaseId}).");
    }

    /// <summary>Spawn a puppet and immediately disguise it as <paramref name="row"/>, recording what it
    /// wears for the Spawn Management list (<see cref="_puppetGuise"/>). The disguise runs through the SAME
    /// objectIndex-keyed <see cref="ApplyGuise"/> path used on the local player, just pointed at the puppet
    /// (Principle 1). Returns the puppet, or null if the native spawn failed (SpawnService logged why).</summary>
    private ICharacter? SpawnPuppetAs(MobRow row)
    {
        // The disguise is DEFERRED to onReady: SpawnService clones the local player and the puppet only
        // becomes drawable (and Glamourer-visible) a few ticks later, so applying the guise now would land
        // a Human paint on a not-yet-registered actor (the "c_ on a dummy spawns only the blank" bug) or a
        // Monster redraw on a half-built draw object. SpawnService fires onReady on the framework thread
        // once the clone is drawn — the correct, safe moment. We still record what it wears right away.
        if (!_spawn.TrySpawn(out var puppet, onReady: p => ApplyGuise(p, row)) || puppet is null)
            return null;
        _puppetGuise[(ushort)puppet.ObjectIndex] = row;
        // Announce the new puppet to HMS with its target atom + spawn transform (the disguise lands later
        // via the onReady ApplyGuise → DisguiseChanged; that's diffed to a no-op against this atom).
        ReportPuppetSpawn(puppet, row);
        return puppet;
    }

    /// <summary>Spawn a BLANK puppet (no guise) for the Spawn Management tab — a dummy the DM dresses,
    /// moves, and animates in its row. Since SpawnService now seeds puppets as a clone of the local player,
    /// a blank one renders as a visible copy of the DM (no longer invisible) until a guise is applied.
    /// No-op (logged) if the native spawn failed.</summary>
    private void SpawnBlankPuppet()
    {
        if (!_spawn.TrySpawn(out var puppet) || puppet is null)
            return; // SpawnService logged why
        _log.Information($"HDM: spawned blank puppet obj#{puppet.ObjectIndex} (clone of you, no guise yet).");
        ReportPuppetSpawn(puppet, null); // announce the blank dummy (Kind 0); its face arrives when disguised
    }

    // ---- Spawn Management tab -------------------------------------------------
    // The DM's set-piece workshop. Spawn blank puppets (or the current catalog pick), then manage each one
    // in its own row: give it a disguise, move/rotate it, and play its timelines. Puppets are the same
    // non-targetable BattleNpcs SpawnService brings into the world; a zone change clears them all (their
    // object indexes are recycled by the new zone — see SpawnService's teardown discipline).

    // ---- Command surface (shared by the buttons and the /hdm subcommands) --------------------
    // Each returns a short status line the command handler echoes to chat. They run on the framework
    // thread (both a UI click and a command dispatch are on it), so direct game access is safe.

    /// <summary>Chat/command: revert self to the real model. </summary>
    public string CommandRevert()
    {
        if (Self() is not { } self) return "no local player.";
        RevertGuise(self);
        return "reverted to your real model.";
    }

    /// <summary>Chat/command: toggle hiding your character entirely.</summary>
    public string CommandHide()
    {
        if (Self() is not { } self) return "no local player.";
        var hide = !_guise.IsHidden(self.ObjectIndex);
        _guise.SetHidden(self, hide);
        _ipc.ReportManualHidden(hide);   // + HMS peer lane, matching the Catalog Hide button
        return hide ? $"hidden, you're invisible now. '{Plugin.Command} hide' again to show." : "shown.";
    }

    /// <summary>Chat/command: apply the territorial-wisp preset.</summary>
    public string CommandWisp()
    {
        if (Self() is not { } self) return "no local player.";
        return ApplyWisp(self) ? "applied the territorial wisp." : $"wisp base {WispBaseId} isn't in this data drop.";
    }

    /// <summary>Chat/command: apply a mob by BaseId (exact) or by a name substring (first renderable match).</summary>
    public string CommandApply(string query)
    {
        if (Self() is not { } self) return "no local player.";
        query = query.Trim();
        if (query.Length == 0) return $"usage: {Plugin.Command} apply <name or BaseId>";

        MobRow? match = uint.TryParse(query, out var baseId) && _index.TryGetByBase(baseId, out var byId) && IsRenderable(byId)
            ? byId
            : _index.Rows.FirstOrDefault(r => IsRenderable(r) && r.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (match is null) return $"no renderable match for '{query}'.";

        _selected = match;
        ApplyGuise(self, match);
        return $"applied: {match.DisplayName} (Base {match.BaseId}, {match.SkeletonCode}).";
    }

    /// <summary>Chat/command: spawn a puppet disguised as the current selection, or as an explicit
    /// name/BaseId argument (resolved exactly like apply). The puppet is placed a step ahead of you.</summary>
    public string CommandSpawn(string query)
    {
        query = query.Trim();
        // With an argument, resolve + select that mob first (same rules as apply); else use the selection.
        if (query.Length > 0)
        {
            MobRow? match = uint.TryParse(query, out var baseId) && _index.TryGetByBase(baseId, out var byId) && IsRenderable(byId)
                ? byId
                : _index.Rows.FirstOrDefault(r => IsRenderable(r) && r.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));
            if (match is null) return $"no renderable match for '{query}' to spawn.";
            _selected = match;
        }
        if (_selected is not { } row) return $"select a mob first, or '{Plugin.Command} spawn <name|BaseId>'.";
        if (SpawnPuppetAs(row) is not { } puppet) return "spawn failed (no local player, or the object table is full).";
        return $"spawned a {row.DisplayName} puppet (obj#{puppet.ObjectIndex}). Total puppets: {_spawn.Count}.";
    }

    /// <summary>Chat/command: despawn every puppet.</summary>
    public string CommandDespawnAll()
    {
        if (_spawn.Count == 0) return "no puppets spawned.";
        var n = _spawn.Count;
        _spawn.DespawnAll();
        return $"despawned {n} puppet(s).";
    }

    /// <summary>Chat/command: DEV harness for the §3 sync IPC. HMS is the real consumer over CallGate; this
    /// exercises the receiver-side path (deserialize → epoch-gate → resolve → apply/revert/play/spawn) on a
    /// LOCAL target so the whole pipeline is testable before HMS exists. Not in HelpText — dev-only. The
    /// subject is your own puppet if you've targeted one, else the local player (never another player —
    /// self-apply-only). All routes funnel through the same
    /// private receiver internals HMS will drive, so a green run here proves the wire contract end to end.</summary>
    public string CommandIpc(string rest)
    {
        rest = rest.Trim();
        var sp = rest.IndexOf(' ');
        var sub = (sp < 0 ? rest : rest[..sp]).ToLowerInvariant();
        var arg = sp < 0 ? "" : rest[(sp + 1)..].Trim();
        // SELF-APPLY-ONLY, de-frictioned: the receiver path CAN drive any actor (that's the HMS mirror
        // feature), but a DM poking this harness must never disguise another PLAYER. Rule: your own PUPPET if
        // you've explicitly targeted one, otherwise ALWAYS yourself — a non-owned target (player or NPC) is
        // silently IGNORED, never disguised. So it never refuses / never makes you deselect (swap mid-emote
        // without clearing the target). NB this is the LOCAL mirror path and does NOT sync outbound; the
        // self-disguise-that-SYNCS command is `/hdm apply <name>` (routes through ApplyGuise → ReportApply).
        ICharacter? Subject()
        {
            var self = Self();
            var t = _targets.Target as ICharacter;
            if (t is not null && (self is null || t.ObjectIndex != self.ObjectIndex) && _spawn.IsSpawned(t.ObjectIndex))
                return t;    // your own puppet — drive it
            return self;     // anything else (non-owned target, or none) → you
        }
        switch (sub)
        {
            case "" or "help":
                return $"ipc dev (LOCAL mirror path, no outbound sync; use {Plugin.Command} apply to disguise+sync): ver · snap · apply <name|BaseId> · revert · play <timelineId> · spawn <name|BaseId> · despawn <objIndex>. Subject = your own puppet if targeted, else you.";
            case "ver":
                var v = _ipc.DevVersion;
                return $"HDM.ApiVersion = {v.major}.{v.minor}.";
            case "snap":
                _log.Information($"[HDM IPC snapshot]\n{_ipc.DevSnapshot()}");
                return "snapshot (own disguise + puppets JSON) written to /xllog.";
            case "apply":
            {
                if (Subject() is not { } t) return "ipc apply: no local player.";
                if (ResolveRow(arg) is not { } row) return $"ipc apply: no renderable match for '{arg}'.";
                var scale = row.McType == 1 ? 1f : (ResolveScale(row) ?? 1f);
                _ipc.DevApply(t.ObjectIndex, _ipc.DevAtomFor(row, scale, 0f, 0));
                return $"ipc apply → obj#{t.ObjectIndex} as {row.DisplayName} (Base {row.BaseId}).";
            }
            case "revert":
            {
                if (Subject() is not { } t) return "ipc revert: no local player.";
                _ipc.DevRevert(t.ObjectIndex);
                return $"ipc revert → obj#{t.ObjectIndex}.";
            }
            case "play":
            {
                if (Subject() is not { } t) return "ipc play: no local player.";
                if (!ushort.TryParse(arg, out var pid) || pid == 0) return $"ipc play: usage {Plugin.Command} ipc play <timelineId>";
                _ipc.DevPlay(t.ObjectIndex, pid);
                return $"ipc play → obj#{t.ObjectIndex} timeline {pid}.";
            }
            case "spawn":
            {
                if (ResolveRow(arg) is not { } row) return $"ipc spawn: no renderable match for '{arg}'.";
                var scale = row.McType == 1 ? 1f : (ResolveScale(row) ?? 1f);
                var idx = _ipc.DevSpawn(_ipc.DevAtomFor(row, scale, 0f, 0));
                return idx < 0 ? "ipc spawn: native spawn failed." : $"ipc spawn → mirror puppet obj#{idx} as {row.DisplayName}.";
            }
            case "despawn":
            {
                // Clean up a mirror puppet spawned by `ipc spawn` (a receiver-path puppet isn't in HDM's own
                // _puppets, but IS in SpawnService's tracked set, so it lists in the Spawn tab and despawns
                // here by its global object index). Guarded by IsSpawned so this can only ever remove an actor
                // HDM brought into the world — never a player or a game NPC.
                if (!int.TryParse(arg, out var di) || di < 0) return $"ipc despawn: usage {Plugin.Command} ipc despawn <objIndex>";
                if (!_spawn.IsSpawned(di)) return $"ipc despawn: obj#{di} isn't a live HDM puppet.";
                _spawn.Despawn((ushort)di);
                return $"ipc despawn → obj#{di}.";
            }
            default:
                return $"ipc: unknown subcommand '{sub}'. Try {Plugin.Command} ipc help.";
        }
    }

    /// <summary>Chat/command: dump a one-shot animation + movement-intent snapshot of the local player to
    /// the log — the walk-regression diagnostic ("demihuman mob slides, then sticks in a walk loop"). Fire
    /// it WHILE the loop is stuck, then read the two "Anim[…]" lines in /xllog. The tag stamps the guise
    /// identity (skeleton + ModelChara) that AnimationService can't see, so a single line ties the runtime
    /// slot/BaseOverride/move-intent state to the specific mob being worn.</summary>
    public string CommandDiag()
    {
        if (Self() is not { } self) return "no local player.";
        var tag = _selected is { } s ? $"{s.SkeletonCode} mc{s.ModelCharaId} {s.DisplayName}" : "no-guise";
        _anim.DumpTimelineState(self, tag);
        return "dumped animation + move-intent state to the log (/xllog → 'Anim[…]' lines).";
    }

    private List<MobRow> Filtered()
    {
        var key = $"{_filter}|{_hideUnnamed}|{_config.CategoryFilter}|{_config.FamilyFilter}";
        if (_cachedFilter != null && key == _cachedFilterKey) return _cachedFilter;

        IEnumerable<MobRow> q = _index.Rows;
        // Renderable gate, applied once up front in EVERY filter mode (see IsRenderable — the gate is
        // FAMILY-DEPENDENT). Monster/Demihuman (McType 2/3) render by swapping ModelCharaId onto this
        // BNpcBase's skeleton, so they need a real skeleton (McModel > 0) that isn't a 999x proxy.
        // Human (McType 1) renders through Glamourer (customize+equip; ModelCharaId never swapped), so
        // its model id is irrelevant and McModel==0 is normal & renderable — only the 999x proxy block
        // is excluded there. This drops (a) the empty-skeleton placeholders — McType 0 entries pointing
        // at ModelChara 480 / model 0 (e.g. "the Ultima Weapon" base 410, blank Skel column) and any
        // *0000 garbage — and (b) the 999x proxy/empty family (9993/9994/9995/9998) whose real
        // appearance is instance-supplied and so only hides the actor on a bare swap. Nothing
        // recoverable is lost; the McType-1 model-0 humans (13846 "tempered imperial" &co.) now pass.
        q = q.Where(IsRenderable);
        // (The Monster/Demihuman/Human family filter was removed — all three families render, so it
        // only narrowed the view; the Skel column + search cover it.)
        // Content-category chip filter (the Duty-Finder tabs). "All" = no-op; every other value
        // keys on the precomputed per-row category, or on the absence of a home territory ("Unknown").
        if (_config.CategoryFilter != "All")
            q = q.Where(r => CategoryMatches(r, _config.CategoryFilter));
        // Family (skeleton-prefix) filter — Kind is "Monster"/"Demihuman"/"Human", so an exact match
        // on the chip label narrows to one family. "All" is the no-op sentinel (skipped here).
        if (_config.FamilyFilter != "All")
            q = q.Where(r => r.Kind == _config.FamilyFilter);
        if (_hideUnnamed)
            q = q.Where(r => !r.IsUnnamed);
        var f = _filter.Trim();
        if (f.Length > 0)
        {
            if (uint.TryParse(f, out var id))
                q = q.Where(r => r.BaseId == id || r.ModelCharaId == id
                              || r.DisplayName.Contains(f, StringComparison.OrdinalIgnoreCase));
            else
            {
                // Name / skeleton OR location: the precomputed _locSearch blob is lowercased, so match a
                // lowercased needle against it. Typing a dungeon/zone word ("mistwake", "haukke", "mor
                // dhona") now surfaces that location's whole roster, not just mobs with the word in their name.
                var fl = f.ToLowerInvariant();
                q = q.Where(r => r.DisplayName.Contains(f, StringComparison.OrdinalIgnoreCase)
                              || r.SkeletonCode.Equals(f, StringComparison.OrdinalIgnoreCase)
                              || (_locSearch.TryGetValue(r.RowKey, out var loc) && loc.Contains(fl, StringComparison.Ordinal)));
            }
        }

        _cachedFilter = q.ToList();
        _cachedFilterKey = key;
        return _cachedFilter;
    }

    /// <summary>Push harvested live nameplates (Tier A3) into the catalog rows' <see cref="MobRow.LiveName"/>
    /// — the top DisplayName priority. A first-hand in-game reading is the most authoritative label there
    /// is, so this both FILLS a catalog-blank base (the YoRHa androids read their real name) and CORRECTS
    /// a mis-paired one (base 19218 flips from the mis-joined "North Shroud lemur" to its true "lone
    /// swordsman"). Runs only when the harvester's name count moved (see the DrawCatalogTab guard), so the
    /// full-row walk is paid at most once per newly-sighted base. Rows are shared references with the
    /// index's by-base map, so the target inspector sees the corrected name too. Invalidating the filter
    /// key forces Filtered() to recompute — DisplayName-keyed search and the Hide-unnamed toggle both
    /// depend on the label, and folding the live name into search was the other half of this feature.</summary>
    private void SyncLiveNames()
    {
        _lastSyncedNameCount = _harvest.NameCount;
        // A shared body (one BaseId, several named rows) gets ONE harvested name, so stamp it only on the row
        // that carries that name — or, when no row does, on the primary row (the mis-pair correction case).
        // Stamping every row of the body would overwrite all its alias labels with one name.
        var sharedNames = new Dictionary<uint, HashSet<string>>();
        foreach (var r in _index.Rows)
            if (r.SharedBase)
            {
                if (!sharedNames.TryGetValue(r.BaseId, out var set))
                    sharedNames[r.BaseId] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(r.Name);
            }
        foreach (var r in _index.Rows)
        {
            if (!_harvest.TryGetName(r.BaseId, out var n)) continue;
            n = MobRow.Cap(n);
            if (r.SharedBase && !r.Name.Equals(n, StringComparison.OrdinalIgnoreCase)
                && (r.IsAlias || sharedNames[r.BaseId].Contains(n)))
                continue;
            if (r.LiveName != n) r.LiveName = n;
        }
        _cachedFilterKey = "\0";
    }

    // Catalog body: the HMS-style Location tree is the sole view now — expansion dividers →
    // collapsible zone nodes → indented mob rows, a pure view transform over the Filtered() rows.
    // (The old None / Family / Named group modes were removed — search + the category chips cover
    // what they did, and a zone tree is what a DM actually browses by.)
    private void DrawTable() => DrawLocationTree(Filtered());

    private static void SetupMobColumns()
    {
        ImGui.TableSetupColumn("★", ImGuiTableColumnFlags.WidthFixed, 24);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Base", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("Model", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("Skel", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("Scl", ImGuiTableColumnFlags.WidthFixed, 45);
    }

    /// <summary>One catalog row: fav toggle + click-to-apply name + Base/Model/Skel/Scale.
    /// PushID(BaseId) namespaces the widgets so rows that share a Name don't collide. When
    /// <paramref name="indented"/> the name is pushed right to nest under its Location-tree zone
    /// node (the full-row highlight still spans all columns).</summary>
    private void DrawMobRow(MobRow r, bool indented = false)
    {
        ImGui.TableNextRow();
        ImGui.PushID(unchecked((int)r.RowKey)); // per ROW: alias rows share a BaseId

        ImGui.TableNextColumn();
        var fav = _config.Favorites.Contains(r.RowKey);
        // Bare star toggle (no button frame — the mockup's clean gold ★): a filled accent star when
        // favourited, a dim hollow star otherwise. Transparent Button bg turns SmallButton into a frameless
        // glyph; the accent tint ties the "on" state to the suite palette (gold by default).
        var favToggled = FavStarButton(fav);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(fav ? "Unfavourite" : "Add to Favourites");
        if (favToggled)
        {
            if (fav) _config.Favorites.Remove(r.RowKey);
            else _config.Favorites.Add(r.RowKey);
            _pi.SavePluginConfig(_config);
            _cachedFilterKey = "\0";
        }

        ImGui.TableNextColumn();
        // Nest under the zone node: a one-arrow-width gap before the name aligns the leaf with the
        // node label above it. SpanAllColumns still highlights the whole row.
        if (indented) { ImGui.Dummy(new Vector2(ImGui.GetFrameHeight() + 4f, 0f)); ImGui.SameLine(0f, 4f); }
        // Leading marker: a gender glyph for a humanoid NPC, a portrait icon for a minion, nothing for a
        // plain mob (so the dense monster list stays unshifted). See DrawRowMarker.
        DrawRowMarker(r);
        // Plain click SELECTS the row (batch-1) — apply/spawn now happen via the header's Disguise / Spawn
        // buttons, so browsing no longer forces a disguise onto you. A roster-inferred name renders in a
        // slate tint with a provenance tooltip, so an inferred label reads distinctly from a crowdsourced
        // one without adding a column.
        if (r.NameIsHeuristic)
            ImGui.PushStyleColor(ImGuiCol.Text, HeuristicNameTint);
        var clicked = ImGui.Selectable(r.DisplayName, _selected?.RowKey == r.RowKey,
                                       ImGuiSelectableFlags.SpanAllColumns);
        if (r.NameIsHeuristic)
        {
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Name inferred from the instanced encounter roster (BossMod); this base is\n" +
                                 "unnamed in the crowdsourced catalog; its home instance names its boss.");
        }
        else if (r.NameIsInferred && ImGui.IsItemHovered())
            ImGui.SetTooltip(ModelNameTip(r)); // the asterisked model approximation explains itself
        // Home-zone orientation label for a humanoid NPC ("add location where they live next to name").
        // Inline after the name, dimmed so it reads as secondary; "(+N)" flags an NPC placed in multiple
        // zones (distinct-outfit variants are already separate rows, each with its own home). Event rows
        // only — a Battle mob/minion has no ENpc placement.
        if (r.Source == NpcSource.Event && _enpcLoc.TryGetLabel(r.BaseId, out var homeZone, out var extraZones))
        {
            ImGui.SameLine(0f, 6f);
            ImGui.TextDisabled(extraZones > 0 ? $"· {homeZone} (+{extraZones})" : $"· {homeZone}");
        }
        if (clicked)
            _selected = r; // select only — apply/spawn happen via the header's Disguise / Spawn buttons

        // Muted numeric columns (TextDisabled) so the bright Name reads as primary and the Base/Model/Skel/
        // Scale ids sit back as the mono reference the mockup shows in a dimmer tone.
        ImGui.TableNextColumn(); ImGui.TextDisabled(r.BaseId.ToString());
        ImGui.TableNextColumn(); ImGui.TextDisabled(r.ModelCharaId.ToString());
        ImGui.TableNextColumn(); ImGui.TextDisabled(r.SkeletonCode);
        ImGui.TableNextColumn(); ImGui.TextDisabled(r.Scale.ToString("0.##"));

        ImGui.PopID();
    }

    // Row-marker sizing/colours. RowIconSize is a touch smaller than the emote icon (20) for a denser
    // catalog line; the gender tints are soft blue/rose so the Mars/Venus glyph reads at a glance without
    // shouting. Vector4 can't be const, hence static readonly.
    private const float RowIconSize = 18f;
    private static readonly Vector4 MaleTint   = new(0.56f, 0.72f, 0.96f, 1f);
    private static readonly Vector4 FemaleTint = new(0.96f, 0.62f, 0.78f, 1f);

    /// <summary>Leading marker in a row's Name cell: a FontAwesome Mars/Venus glyph for a humanoid NPC
    /// (gender at a glance in the Race→Clan lists), or the minion's own game portrait icon for a summonable
    /// minion. Plain catalog mobs get nothing, so the dense monster list stays unshifted. The glyph uses
    /// Dalamud's icon font (always present, so it can't fail like a game-icon lookup); the minion icon
    /// reuses the proven DrawEmoteIcon path. AlignTextToFramePadding keeps the glyph centred on the
    /// Selectable that follows it.</summary>
    /// <summary>A frameless ★/☆ toggle one FRAME tall, so the glyph sits on the same centre line as the row's text.
    /// (It was a SmallButton, which has no vertical padding: one text line tall, pinned to the TOP of a row whose name
    /// is frame-padded to the middle — the star and the name didn't line up.)</summary>
    private bool FavStarButton(bool fav)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 1f, 1f, 0.08f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 1f, 1f, 0.12f));
        ImGui.PushStyleColor(ImGuiCol.Text, fav ? _accent.Primary : StarOff);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(3f, ImGui.GetStyle().FramePadding.Y));
        var clicked = ImGui.Button(fav ? "★" : "☆");
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);
        return clicked;
    }
    private static readonly Vector4 StarOff = new(0.45f, 0.47f, 0.52f, 1f);

    /// <summary>Tooltip for an asterisked, model-approximated name (see <see cref="MobRow.FamilyName"/>).</summary>
    private static string ModelNameTip(MobRow r)
        => $"* No name of its own: approximated from its model ({r.SkeletonCode}, ModelChara {r.ModelCharaId}).\n" +
           $"Also on this model: {r.FamilyHint}";

    private void DrawRowMarker(MobRow r)
    {
        if (r.Source == NpcSource.Event && r.McType == 1)
        {
            var female = r.Gender == 1;
            ImGui.AlignTextToFramePadding();
            ImGui.PushStyleColor(ImGuiCol.Text, female ? FemaleTint : MaleTint);
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextUnformatted((female ? FontAwesomeIcon.Venus : FontAwesomeIcon.Mars).ToIconString());
            ImGui.PopFont();
            ImGui.PopStyleColor();
            ImGui.SameLine(0f, 6f);
        }
        else if (_companion.TryGetIcon(r.BaseId, out var icon))
        {
            if (DrawEmoteIcon(icon, RowIconSize))
                ImGui.SameLine(0f, 6f);
        }
    }

    /// <summary>The puppet the Spawn tab's roster currently focuses (its selection if still live, else the first
    /// spawned), resolved to an ICharacter — or null when nothing is spawned. Lets a control act on "the focused
    /// puppet" through the same target-generic funnels the Spawn surface uses, so there's no duplicate puppet
    /// control (single-control-mechanism rule). Mirrors <see cref="ResolveRosterSelection"/> without its side
    /// effect of writing back <see cref="_selectedPuppet"/>.</summary>
    private ICharacter? FocusedPuppet()
    {
        var idx = _spawn.IsSpawned(_selectedPuppet) ? _selectedPuppet
                : (_spawn.Spawned.Count > 0 ? _spawn.Spawned[0] : (ushort?)null);
        return idx is { } i ? _objects[i] as ICharacter : null;
    }

    // ---- Location tree -------------------------------------------------------

    // A zone node in the Location tree: one home territory (Located) or one ~expansion "Unknown"
    // bucket, holding the mob rows that resolve to it.
    private sealed class LocNode
    {
        public uint Key;            // TerritoryType id (located) or UnknownNodeKey(ex) (unknown)
        public bool Located;
        public byte Expansion;      // real ExVersion (located) or estimated from id range (unknown)
        public int CategoryRank;    // Duty-Finder order within an expansion (located); int.MaxValue for unknown
        public int SortKey;         // CFC sort key for stable per-category ordering (located)
        public string ZoneLabel = "";
        public string Category = "";
        public string Region = "";  // Info tail ("Norvrandt"); "" when open-world region-less or unknown
        public readonly List<MobRow> Items = new();
    }

    // Synthetic node keys for the six ~expansion "Unknown location" buckets. Real TerritoryType
    // ids are small (< ~1300), so keys up near uint.MaxValue can never collide with a located zone.
    private static uint UnknownNodeKey(byte ex) => uint.MaxValue - ex;

    // Synthetic key for the single "Minions & summons" node. Sits well clear of both the real
    // TerritoryType ids (small) and the ~7 Unknown keys clustered at the very top of the range.
    private const uint MinionNodeKey = uint.MaxValue - 100;
    private const uint MountNodeKey  = uint.MaxValue - 101; // b16: the Mount-sheet creatures, drawn under the minions divider

    // Synthetic node keys for the humanoid-NPC Race→Clan buckets. An ENpcBase has no TerritoryType home,
    // so instead of one flat "All NPCs" pile the Event rows group by (Race 1-8, Clan/Tribe 1-16) into
    // collapsible leaves under the single "NPCs" section. race*32+clan ∈ [33, 272], so these keys land in
    // [uint.MaxValue-572 .. uint.MaxValue-333] — clear of the Unknown keys (uint.MaxValue-ex, ex ≤ ~6) and
    // the minion key (-100). Used as the _expandedDuties key so each Race→Clan leaf remembers its own
    // expand state. (32, not 16, per clan slot so the arithmetic can't overflow into a neighbour's key.)
    private static uint RaceClanNodeKey(byte race, byte clan) => uint.MaxValue - 300u - (uint)(race * 32 + clan);

    // Display label for a Race→Clan NPC node ("Hyur · Midlander"). Falls back to the numeric ids if a name
    // didn't intern — shouldn't happen for a ValidHuman row, but keeps the node label non-empty regardless.
    private static string RaceClanLabel(MobRow r)
    {
        var race = r.RaceName.Length > 0 ? r.RaceName : $"Race {r.Race}";
        var clan = r.ClanName.Length > 0 ? r.ClanName : $"Clan {r.Clan}";
        return $"{race} · {clan}";
    }

    // Collapsible-SECTION keys (for _collapsedSections). Located-expansion dividers key on the
    // ExVersion byte (0..5); these fence the non-expansion sections and sit clear of that range.
    private const uint MinionSectionKey   = 200;
    private const uint UnknownSectionKey  = 201;
    private const uint EventNpcSectionKey = 202;

    private static string ExLongSafe(byte ex) =>
        ex < ContentIndex.ExLong.Length ? ContentIndex.ExLong[ex] : $"Expansion {ex}";

    /// <summary>
    /// The headline Location view: a single scrolling table shaped like the in-game Duty Finder /
    /// HMS Zones tab — full-width EXPANSION divider rows, each holding collapsible ZONE nodes (a
    /// disclosure arrow + the duty/zone name + its mob count), each expanding to the INDENTED mob
    /// rows that live there. Located zones come first (grouped ARR→DT, Duty-Finder order within an
    /// expansion); the unplaceable tail is fenced into a final "Unknown location" section of
    /// ~expansion buckets. Zone nodes are collapsed by default (so the big Unknown pile costs
    /// nothing until opened); a live text filter force-opens every node so matches are visible
    /// without clicking. There is no render cap: a collapsed tree costs nothing, and the DM
    /// manages volume by expanding one zone at a time (the reason the old 400-row cap is gone).
    /// </summary>
    // Bucket the filtered rows into the cached zone/NPC/minion/unknown node lists. Runs only on a cache miss
    // (see DrawLocationTree's guard), NOT per frame: this is the pass that walks every catalog row through
    // TryLocateAll's tier cascade and sorts the nodes, so keeping it off the per-frame path is the FPS fix.
    private void RebuildLocationTree(List<MobRow> rows)
    {
        // Bucket rows into zone nodes (located by home territory, else by ~expansion).
        var nodes = new Dictionary<uint, LocNode>();
        foreach (var r in rows)
        {
            // Event NPC precedence (mirrors ComputeCategories): an ENpcBase row has no home zone, so it
            // goes straight to the NPC section ahead of the location tiers (which would all miss it). Group
            // by Race→Clan into collapsible leaves ("Hyur — Midlander") instead of one flat "All NPCs" pile;
            // each leaf's rows are alphabetized at render (DrawZoneNode), the "secondary sort is
            // alphabetical" the DM asked for.
            if (r.Source == NpcSource.Event)
            {
                var rckey = RaceClanNodeKey(r.Race, r.Clan);
                if (!nodes.TryGetValue(rckey, out var enode))
                    nodes[rckey] = enode = new LocNode
                    {
                        Key = rckey, Located = false, Expansion = byte.MaxValue,
                        CategoryRank = int.MaxValue, SortKey = int.MaxValue,
                        ZoneLabel = RaceClanLabel(r), Category = "EventNpc",
                    };
                enode.Items.Add(r);
                continue;
            }

            // Mount precedence (b16): a Mount-sheet creature has no home zone either; one flat node shown
            // under the same "Minions, mounts & summons" divider as the minions.
            if (r.Source == NpcSource.Mount)
            {
                if (!nodes.TryGetValue(MountNodeKey, out var mtnode))
                    nodes[MountNodeKey] = mtnode = new LocNode
                    {
                        Key = MountNodeKey, Located = false, Expansion = byte.MaxValue,
                        CategoryRank = int.MaxValue, SortKey = int.MaxValue,
                        ZoneLabel = "All mounts", Category = "Mount",
                    };
                mtnode.Items.Add(r);
                continue;
            }

            // Minion precedence (mirrors ComputeCategories): a summonable minion goes to its own section
            // ahead of any location — it never has a real home zone.
            if (_companion.TryGetMinion(r.BaseId, out _))
            {
                if (!nodes.TryGetValue(MinionNodeKey, out var mnode))
                    nodes[MinionNodeKey] = mnode = new LocNode
                    {
                        Key = MinionNodeKey, Located = false, Expansion = byte.MaxValue,
                        CategoryRank = int.MaxValue, SortKey = int.MaxValue,
                        ZoneLabel = "All minions", Category = "Minion",
                    };
                mnode.Items.Add(r);
                continue;
            }

            // Multi-location: list the mob under EVERY zone the winning tier places it in (a DM may know
            // a pursuit from one dungeon but not that it lives elsewhere, or want a zone's complete
            // roster). TryLocateAll's set is already distinct, so no single zone gets the same base twice.
            var placedAny = false;
            if (TryLocateAll(r, out var terrs))
            {
                foreach (var terr in terrs)
                {
                    if (!_content.TryGet(terr, out var info)) continue; // engine-limbo territory we can't name — skip; the row may still fall to Unknown
                    if (!nodes.TryGetValue(terr, out var znode))
                    {
                        var name = info.DutyName.Length > 0 ? info.DutyName : info.PlaceName;
                        var lvl = info.Level > 0 ? $" (Lv{info.Level})" : "";
                        nodes[terr] = znode = new LocNode
                        {
                            Key = terr, Located = true, Expansion = info.Expansion,
                            CategoryRank = CategoryRank(info.Category), SortKey = info.SortKey,
                            ZoneLabel = $"{name}{lvl}", Category = info.Category, Region = info.Region,
                        };
                    }
                    znode.Items.Add(r);
                    placedAny = true;
                }
            }

            if (!placedAny)
            {
                var ex = EstimateExpansion(r.BaseId);
                var key = UnknownNodeKey(ex);
                if (!nodes.TryGetValue(key, out var unode))
                    nodes[key] = unode = new LocNode
                    {
                        Key = key, Located = false, Expansion = ex,
                        CategoryRank = int.MaxValue, SortKey = int.MaxValue,
                        ZoneLabel = $"~{ContentIndex.ExShort[ex]} (estimated from id range)",
                        Category = "Unknown",
                    };
                unode.Items.Add(r);
            }
        }

        _treeLocated = nodes.Values.Where(n => n.Located)
            .OrderBy(n => n.Expansion).ThenBy(n => n.CategoryRank).ThenBy(n => n.SortKey)
            .ThenBy(n => n.ZoneLabel, StringComparer.Ordinal).ToList();
        nodes.TryGetValue(MinionNodeKey, out var minionNode);
        _treeMinionNode = minionNode;
        nodes.TryGetValue(MountNodeKey, out var mountNode);
        _treeMountNode = mountNode;
        _treeMountCount = mountNode?.Items.Count ?? 0;
        // The humanoid-NPC set is now many Race→Clan nodes (not one flat node); gather them and order the
        // leaves alphabetically by "Race — Clan" label so the section reads A→Z.
        _treeEventNodes = nodes.Values.Where(n => n.Category == "EventNpc")
            .OrderBy(n => n.ZoneLabel, StringComparer.OrdinalIgnoreCase).ToList();
        _treeUnknown = nodes.Values.Where(n => !n.Located && n.Key != MinionNodeKey && n.Key != MountNodeKey && n.Category != "EventNpc")
            .OrderBy(n => n.Expansion).ToList();

        // Section counts for the dividers below (the pre-header summary line was removed in batch-1 — it
        // duplicated the meta pills). These three feed the NPCs / Minions / Unknown divider metas; the
        // located zones carry their own per-expansion "N zones" counts.
        _treeMinionCount = minionNode?.Items.Count ?? 0;
        _treeEventCount = _treeEventNodes.Sum(n => n.Items.Count);
        _treeUnknownCount = _treeUnknown.Sum(n => n.Items.Count);
    }

    private void DrawLocationTree(List<MobRow> rows)
    {
        // Rebuild the tree STRUCTURE only on a cache miss — Filtered() hands back a fresh list instance on every
        // filter invalidation, so a reference compare catches every change (search/category/family/hide-unnamed/
        // star/live-name sync) with no extra invalidation sites. Per frame this is one ref check; the bucket/sort
        // in RebuildLocationTree used to run every frame and ~halved FPS while the Catalog tab was open. The
        // render walk below stays per-frame — ImGui is immediate-mode — but only submits already-bucketed nodes.
        if (!ReferenceEquals(rows, _treeRows) || _treeLocated is null)
        {
            RebuildLocationTree(rows);
            _treeRows = rows;
        }

        var located = _treeLocated!;
        var eventNodes = _treeEventNodes!;
        var unknown = _treeUnknown!;
        var minionNode = _treeMinionNode;
        var minionCount = _treeMinionCount;
        var eventCount = _treeEventCount;
        var unknownCount = _treeUnknownCount;

        // A live search force-opens every node (matches are already narrow) without disturbing the
        // user's persisted expand set.
        var autoExpand = _filter.Trim().Length > 0;

        // Right edge for the divider text clip, captured before the table (mirrors HMS SectionRow).
        // Batch-1: subtract the ScrollY scrollbar width (always present on the long catalog) as well as a
        // small margin, so right-aligned divider/region text doesn't slide under the scrollbar and clip.
        float rowRightEdge;
        { var tl0 = ImGui.GetCursorScreenPos(); rowRightEdge = tl0.X + ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ScrollbarSize - 8f; }

        if (!ImGui.BeginTable("##loctree", 6,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.PadOuterX))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        SetupMobColumns();
        ImGui.TableHeadersRow();

        // Located region: emit a collapsible expansion divider whenever the expansion changes, and
        // skip that expansion's zone nodes while it's folded. `located` is expansion-contiguous, so
        // one open/closed flag carried across the run fences each section correctly.
        // Zone-count per expansion for the divider's right-aligned meta ("A REALM REBORN … 17 zones").
        var zonesPerEx = new Dictionary<int, int>();
        foreach (var n in located)
            zonesPerEx[n.Expansion] = zonesPerEx.GetValueOrDefault(n.Expansion) + 1;

        int curEx = -1;
        var sectionOpen = true;
        foreach (var n in located)
        {
            if (n.Expansion != curEx)
            {
                curEx = n.Expansion;
                // Expansion band in proper case (batch-1 — was mono-caps) + "N zones" meta on the right. The
                // collapsible triangle stays — folding whole expansions is a real feature the mockup's static
                // frame simply doesn't show.
                sectionOpen = ExpansionDivider(ExLongSafe((byte)curEx), rowRightEdge,
                                               (uint)curEx, autoExpand, $"{zonesPerEx.GetValueOrDefault(curEx)} zones");
            }
            if (sectionOpen)
                DrawZoneNode(n, autoExpand, rowRightEdge);
        }

        // Event NPCs region: the humanoid ENpcBase set (the Glamourer "NPCs" tab), fenced between the
        // located zones and the minion/Unknown tail. Like minions these have no home TerritoryType — a
        // town NPC's spawn point isn't a duty — so they're bracketed off rather than dumped into Unknown.
        // Grouped into Race→Clan leaves (alphabetical), all collapsed by default under one "NPCs" divider.
        if (eventNodes.Count > 0)
        {
            if (ExpansionDivider("NPCs", rowRightEdge, EventNpcSectionKey, autoExpand, $"{eventCount} NPCs"))
                foreach (var n in eventNodes)
                    DrawZoneNode(n, autoExpand, rowRightEdge);
        }

        // Minions & summons region: its own section between the located zones and the Unknown tail.
        // A minion has no home TerritoryType (its "home" is a reward source), so it can never be
        // "located" — but it isn't unidentified either, so it earns its own fence rather than sitting
        // in the Unknown pile. One flat node (id ranges don't track a minion's true era, so an
        // ~expansion split would mislead); collapsed by default like every other node.
        var mountNode = _treeMountNode;
        if (minionNode != null || mountNode != null)
        {
            if (ExpansionDivider("Minions, mounts & summons", rowRightEdge, MinionSectionKey, autoExpand,
                    $"{minionCount} minions · {_treeMountCount} mounts"))
            {
                if (minionNode != null) DrawZoneNode(minionNode, autoExpand, rowRightEdge);
                if (mountNode != null) DrawZoneNode(mountNode, autoExpand, rowRightEdge);
            }
        }

        // Unknown region: one divider, then the ~expansion buckets (kept out of the located sections
        // so those read clean; collapsed by default so the ~89% tail costs nothing).
        if (unknown.Count > 0)
        {
            if (ExpansionDivider("Unknown location", rowRightEdge, UnknownSectionKey, autoExpand, $"{unknownCount} mobs"))
                foreach (var n in unknown)
                    DrawZoneNode(n, autoExpand, rowRightEdge);
        }

        ImGui.EndTable();
    }

    /// <summary>Full-width, COLLAPSIBLE expansion/section divider row: a disclosure triangle + the
    /// section label (DrawList-clipped so a long label overflows the narrow first column), tinted like
    /// HMS's SectionRow. The whole bar is a click target that toggles the section's collapsed state
    /// (<see cref="_collapsedSections"/>), so a DM can fold whole expansions away while browsing; a live
    /// text filter force-opens it (<paramref name="autoExpand"/>). Returns whether the section is OPEN,
    /// so the caller skips the section's child zone nodes when it's collapsed.</summary>
    private bool ExpansionDivider(string label, float rowRightEdge, uint sectionKey, bool autoExpand, string meta = "")
    {
        var open = autoExpand || !_collapsedSections.Contains(sectionKey);

        ImGui.TableNextRow();
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(new Vector4(0.14f, 0.15f, 0.18f, 1f)));
        ImGui.TableSetColumnIndex(0);
        var p = ImGui.GetCursorScreenPos();
        var lh = ImGui.GetTextLineHeight();

        // Whole-bar click target: an invisible Selectable that spans every column (same idiom as the
        // mob rows), so a click anywhere on the divider toggles it. Guarded by !autoExpand so a
        // filtered view — force-opened — can't be collapsed out from under itself.
        ImGui.PushID((int)sectionKey);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.26f, 0.28f, 0.33f, 0.60f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive,  new Vector4(0.30f, 0.34f, 0.40f, 0.70f));
        if (ImGui.Selectable("##sec", false, ImGuiSelectableFlags.SpanAllColumns, new Vector2(0f, lh)) && !autoExpand)
        {
            if (!_collapsedSections.Remove(sectionKey)) _collapsedSections.Add(sectionKey);
            open = !_collapsedSections.Contains(sectionKey);
        }
        ImGui.PopStyleColor(2);
        ImGui.PopID();

        // Disclosure triangle + label, drawn on top and clipped only at the far right edge so a long
        // section label ("Unknown location — target in-game to identify") still shows in full.
        var dl = ImGui.GetWindowDrawList();
        var cmin = dl.GetClipRectMin();
        var cmax = dl.GetClipRectMax();
        dl.PushClipRect(new Vector2(cmin.X, cmin.Y), new Vector2(rowRightEdge, cmax.Y), false);
        var ink = ImGui.GetColorU32(new Vector4(0.66f, 0.70f, 0.78f, 1f));
        var ax = p.X + 6f;
        if (open) // down-pointing triangle
            dl.AddTriangleFilled(new Vector2(ax, p.Y + lh * 0.32f), new Vector2(ax + lh * 0.55f, p.Y + lh * 0.32f), new Vector2(ax + lh * 0.275f, p.Y + lh * 0.64f), ink);
        else      // right-pointing triangle
            dl.AddTriangleFilled(new Vector2(ax, p.Y + lh * 0.24f), new Vector2(ax, p.Y + lh * 0.72f), new Vector2(ax + lh * 0.34f, p.Y + lh * 0.48f), ink);
        dl.AddText(new Vector2(ax + lh * 0.7f + 4f, p.Y), ink, label);
        // Right-aligned dim meta ("17 zones") — the section's headline count, pinned to the table's right
        // edge inside the same wide clip so it can't be eaten by the narrow first-column cell.
        if (!string.IsNullOrEmpty(meta))
        {
            var mSz = ImGui.CalcTextSize(meta);
            dl.AddText(new Vector2(rowRightEdge - mSz.X, p.Y), ImGui.GetColorU32(new Vector4(0.55f, 0.58f, 0.64f, 1f)), meta);
        }
        dl.PopClipRect();

        return open;
    }

    /// <summary>One collapsible zone node: a disclosure arrow + the zone/duty name + mob count in
    /// the Name column, a dim "· Category · Region" tail, and — when open — the indented mob rows.
    /// The arrow toggles the persistent expand set; a live filter forces open (autoExpand).</summary>
    private void DrawZoneNode(LocNode n, bool autoExpand, float rowRightEdge)
    {
        var open = autoExpand || _expandedDuties.Contains(n.Key);

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(1); // the whole node header lives in the Name column
        var rowY = ImGui.GetCursorScreenPos().Y;
        ImGui.PushID((int)n.Key);

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.24f, 0.25f, 0.29f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.24f, 0.25f, 0.29f, 1f));
        var toggled = ImGui.ArrowButton("##exp", open ? ImGuiDir.Down : ImGuiDir.Right);
        ImGui.PopStyleColor(3);
        if (toggled && !autoExpand)
        {
            if (!_expandedDuties.Remove(n.Key)) _expandedDuties.Add(n.Key);
        }
        ImGui.SameLine(0f, 4f);

        // Zone label (bright) + a dimmed count, matching the mockup's "Central Shroud (147)".
        ImGui.AlignTextToFramePadding();
        var headTint = n.Located ? new Vector4(0.86f, 0.90f, 0.98f, 1f) : new Vector4(0.74f, 0.68f, 0.62f, 1f);
        ImGui.PushStyleColor(ImGuiCol.Text, headTint);
        ImGui.TextUnformatted(n.ZoneLabel);
        ImGui.PopStyleColor();
        ImGui.SameLine(0f, 6f);
        ImGui.TextDisabled($"({n.Items.Count})");

        // Region pinned to the table's right edge (mockup: "… The Black Shroud"), drawn on the window draw
        // list under a widened clip so the far-right text isn't eaten by the Name cell. Located zones only —
        // minion/NPC/Unknown leaves have no region. Category dropped from the row (it's the CATEGORY filter
        // panel now); the region is the single orienting fact the mockup keeps on the line.
        if (n.Located && n.Region.Length > 0)
        {
            var dl = ImGui.GetWindowDrawList();
            var cmin = dl.GetClipRectMin();
            var cmax = dl.GetClipRectMax();
            dl.PushClipRect(cmin, new Vector2(rowRightEdge, cmax.Y), false);
            var rSz = ImGui.CalcTextSize(n.Region);
            dl.AddText(new Vector2(rowRightEdge - rSz.X, rowY + ImGui.GetStyle().FramePadding.Y),
                       ImGui.GetColorU32(new Vector4(0.55f, 0.58f, 0.64f, 1f)), n.Region);
            dl.PopClipRect();
        }

        ImGui.PopID();

        if (!open) return;
        // Alphabetize the leaf's rows by the label the DM actually reads (DisplayName). Only expanded nodes
        // reach here, so this per-frame sort touches a handful of small lists at most — and it gives every
        // open zone / minion / NPC leaf the "secondary sort is alphabetical" ordering the DM asked for.
        // BaseId is a TIEBREAKER, not decoration: List.Sort is an unstable introsort, and leaves like "Antling
        // Worker" carry many rows with the IDENTICAL DisplayName. Comparing on name alone leaves those ties in
        // an arbitrary relative order that introsort re-permutes on each per-frame call, so a selected row
        // visibly hops between positions ("jitter"). Adding BaseId makes the comparison a strict total order —
        // no two rows tie, the sorted permutation is unique, and re-sorting the already-sorted list every frame
        // is a fixpoint, so the order (and the selection highlight) holds still.
        n.Items.Sort(static (a, b) =>
        {
            var c = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.BaseId.CompareTo(b.BaseId);
        });
        foreach (var r in n.Items)
            DrawMobRow(r, indented: true);
    }

    /// <summary>
    /// Resolve a catalog row to its home TerritoryType, chaining the location sources most-specific
    /// first: Tier M hand-curated tags (cutscene-script props no automated tier can reach) →
    /// Tier N curated dungeon name-stems (director-spawned dungeon trash placed by the dungeon word
    /// in its name — "Babil slasher" → Tower of Babil — one rule per roster; asserts authority over
    /// the scattered crowdsource telemetry beneath it) → Tier A crowdsource roster (actual spawn
    /// observations) → Tier B game sheets (exact
    /// BNpcBase from hunt marks, then exact BNpcName from Hunting Log / marks, then a leading-word
    /// name-stem match for rank variants) → Tier C web supplement (GamerEscape zone strings resolved
    /// through the PlaceName sheet, for the instanced content the first two structurally miss).
    /// Earlier tiers win where several know a mob, so each later tier only ever FILLS the remaining
    /// Unknown tail — never overrides an observed or sheet-derived zone. False = genuinely unplaceable
    /// (awaits the runtime harvester).
    /// </summary>
    /// <summary>The catalog renderable gate, in ONE place so the grid, the target-identity line and
    /// the coverage diagnostic all agree on the denominator. The renderable test is FAMILY-DEPENDENT
    /// because the two apply paths consume the model field completely differently (see ApplyGuise):
    ///
    ///   • Human (McType 1) renders through Glamourer — HumanGuise paints BNpcCustomize + NpcEquip and
    ///     NEVER swaps ModelCharaId (a human NPC already IS the player's c-skeleton). The model id is
    ///     therefore irrelevant to whether it renders, and McModel==0 is the NORMAL, fully-renderable
    ///     case for a huge slice of human NPCs — 140 rows here, 136 of them named: base 13846 "tempered
    ///     imperial" (Tower of Babil, the tentacle-helm), "zombie sailor", "Tomato King", "Senor
    ///     Sabotender", … Glamourer's own NPC tab lists exactly these (it accepts every BNpcBase whose
    ///     ModelChara.Type==1, gating on customize validity, not on a model id). The old blanket
    ///     `McModel &gt; 0` clause wrongly hid all 136 — you couldn't even find 13846 by exact-ID search,
    ///     because Filtered() applies this gate before the search predicate. So for Human we only still
    ///     exclude the 999x proxy block.
    ///
    ///   • Monster / Demihuman (McType 2/3) render by swapping the player's ModelCharaId onto THIS
    ///     BNpcBase's skeleton, so the skeleton must be real: McModel &gt; 0 AND not a 999x proxy. Models
    ///     9993/9994/9995/9998 are the engine's placeholder/proxy family — their BNpcBases point
    ///     ModelChara at an empty stand-in (e.g. ModelChara 476 = m9994 b1 v1, shared by Leviathan /
    ///     Hades / Wuk Lamat / Howling Blade) whose real appearance is instance-supplied (attached
    ///     models, scripted gear, part actors) and a bare client-side write can't reproduce it — applying
    ///     one just hides the actor (redraw waits on IsReadyToDraw, times out, EnableDraw renders
    ///     nothing). Real monster skeletons are all &lt; 9000, so the cutoff loses no reproducible
    ///     appearance. See #17/#21 for the empty-skeleton and McType-0 exclusions this extends.
    ///
    ///   • McType 0 = empty-skeleton placeholder (ModelChara 480 / model 0) — never renderable.</summary>
    private static bool IsRenderable(MobRow r) => r.McType switch
    {
        1      => r.McModel < 9990,               // Human: model id irrelevant (Glamourer path); only drop 999x proxies
        2 or 3 => r.McModel is > 0 and < 9990,    // Monster/Demihuman: need a real, non-proxy skeleton to swap onto
        _      => false,                          // McType 0: empty-skeleton placeholder
    };

    /// <summary>Tier S — for a row whose BODY carries several names (<see cref="MobRow.SharedBase"/>). Every
    /// BaseId-keyed tier (crowdsource, hunt-mark base, instanced roster, harvest, Level) describes the body,
    /// so it can't say WHICH of its named mobs was where: before pairs, a dungeon add sharing an overworld
    /// body inherited the overworld zone and vanished from its duty category. This consults the evidence
    /// that is specific to THIS row's name first: the instanced roster when its encounter names exactly this
    /// NameId, then the exact-name and name-stem lore tiers, then the web supplement. Falls through (false)
    /// to the normal chain when none applies.</summary>
    private bool TryLocateByOwnName(MobRow r, out uint territoryId)
    {
        if (_instanced.TryGetName(r.BaseId, out var bossName) && bossName == r.NameId
            && _instanced.TryGetPrimary(r.BaseId, out territoryId)) return true;
        if (_lore.TryGetByName(r.NameId, out territoryId)) return true;
        if (r.Name.Length > 0 && _lore.TryGetByNameStem(r.Name, out territoryId)) return true;
        if (r.Name.Length > 0 && _webloc.TryGetTerritory(r.Name, EstimateExpansion(r.BaseId), out territoryId)) return true;
        territoryId = 0;
        return false;
    }

    private bool TryLocate(MobRow r, out uint territoryId)
    {
        if (_manual.TryGetPrimary(r.BaseId, out territoryId)) return true;    // Tier M: hand-curated tag (cutscene-script props) — overrides every estimate/inference
        if (r.Name.Length > 0 && _stems.TryMatch(r.Name, out territoryId)) return true; // Tier N: curated dungeon name-stem (director-spawned trash "Babil slasher" -> Tower of Babil) — asserts authority over the scattered crowdsource telemetry below it
        if (r.SharedBase && TryLocateByOwnName(r, out territoryId)) return true; // Tier S: shared body — this row's own name evidence before the body-wide (ambiguous) tiers
        if (_territory.TryGetPrimary(r.BaseId, out territoryId)) return true; // Tier A: crowdsource
        if (_lore.TryGetByBase(r.BaseId, out territoryId)) return true;       // Tier B: hunt-mark base
        if (_instanced.TryGetPrimary(r.BaseId, out territoryId)) return true; // Tier A2: instanced roster (BossMod-derived) — the dungeon/trial/raid tail
        if (_harvest.TryGetPrimary(r.BaseId, out territoryId)) return true;   // Tier A3: the DM's own runtime sightings — reaches the instanced roster (YoRHa &co.) no offline table has
        if (_lore.TryGetByName(r.NameId, out territoryId)) return true;       // Tier B: exact name
        if (r.Name.Length > 0 && _lore.TryGetByNameStem(r.Name, out territoryId)) return true; // Tier B: stem
        if (_level.TryGetPrimary(r.BaseId, EstimateExpansion(r.BaseId), out territoryId)) return true; // Tier D: deterministic client-file static placement (Level Type 9, keyed on BNpcBase) — preferred over the wiki scrape, never overrides an observed/sheet zone
        if (r.Name.Length > 0 && _webloc.TryGetTerritory(r.Name, EstimateExpansion(r.BaseId), out territoryId)) return true; // Tier C: web supplement (GamerEscape zone + MapMarker sub-area), expansion hint disambiguates recurring sub-area labels
        territoryId = 0;
        return false;
    }

    /// <summary>The multi-location variant of <see cref="TryLocate"/>: returns EVERY territory the
    /// WINNING tier places this mob in, so the Location tree can list it under each zone it lives in
    /// (a roamer known from one field but resident in several; a striking dummy placed game-wide).
    /// <para>Semantics are winning-tier-ONLY, not a union across tiers — the first tier that matches
    /// supplies the whole answer, exactly as <see cref="TryLocate"/> takes the first tier for the
    /// single home. That is deliberate: the override tiers (manual tag, dungeon name-stem) exist to
    /// DEFEAT the crowdsource's model-reuse scatter, so unioning them back in would re-import the very
    /// misfiling they correct. Only the two inherently multi-zone sources — crowdsource sightings
    /// (<see cref="TerritoryIndex.TryGetAll"/>) and static Level placements
    /// (<see cref="LevelPlacementIndex.TryGetAll"/>) — return more than one territory; every keyed or
    /// name tier contributes its single authoritative zone. The returned set always CONTAINS
    /// <see cref="TryLocate"/>'s single home (they resolve the same winning tier); for every tier bar
    /// the static Level placements it is element 0, and for Level it is one member of the set — the
    /// single-home path just applies an extra categorizable/expansion tiebreak the tree doesn't need.</para></summary>
    private bool TryLocateAll(MobRow r, out List<uint> territories)
    {
        territories = new List<uint>(1);
        if (_manual.TryGetPrimary(r.BaseId, out var t)) { territories.Add(t); return true; }               // Tier M
        if (r.Name.Length > 0 && _stems.TryMatch(r.Name, out t)) { territories.Add(t); return true; }       // Tier N
        if (r.SharedBase && TryLocateByOwnName(r, out t)) { territories.Add(t); return true; }              // Tier S: shared body, own name first
        if (_territory.TryGetAll(r.BaseId, out var crowd)) { territories.AddRange(crowd); return true; }     // Tier A: crowdsource (multi-zone)
        if (_lore.TryGetByBase(r.BaseId, out t)) { territories.Add(t); return true; }                       // Tier B: hunt-mark base
        if (_instanced.TryGetPrimary(r.BaseId, out t)) { territories.Add(t); return true; }                 // Tier A2: instanced roster
        if (_harvest.TryGetPrimary(r.BaseId, out t)) { territories.Add(t); return true; }                   // Tier A3: runtime harvest
        if (_lore.TryGetByName(r.NameId, out t)) { territories.Add(t); return true; }                       // Tier B: exact name
        if (r.Name.Length > 0 && _lore.TryGetByNameStem(r.Name, out t)) { territories.Add(t); return true; } // Tier B: stem
        if (_level.TryGetAll(r.BaseId, out var lvls)) { territories.AddRange(lvls); return true; }           // Tier D: static placement (multi-zone)
        if (r.Name.Length > 0 && _webloc.TryGetTerritory(r.Name, EstimateExpansion(r.BaseId), out t)) { territories.Add(t); return true; } // Tier C: web supplement
        return false;
    }

    // ---- content metadata (category · provisional expansion) -----------------

    /// <summary>Precompute <see cref="_categories"/> for every renderable row (see
    /// <see cref="ComputeCategories"/>). Runs once at load so the category chips and the chip filter are
    /// O(1) set lookups instead of re-running the 10-tier TryLocateAll chain per keystroke.</summary>
    private void BuildCategoryIndex()
    {
        foreach (var r in _index.Rows)
        {
            if (!IsRenderable(r)) continue;
            _categories[r.RowKey] = ComputeCategories(r);
            var loc = ComputeLocSearch(r);
            if (loc.Length > 0) _locSearch[r.RowKey] = loc;

            // Zones-pill source: add every distinct, nameable home territory this row locates to. Skip
            // Event NPCs + minions (they're fenced into their own sections with no home zone, exactly as
            // DrawLocationTree buckets them), and skip territories the content sheet can't name (engine
            // limbo — the same TryGet guard DrawLocationTree uses). One-time at load; free at draw.
            if (r.Source == NpcSource.Battle && !_companion.IsMinion(r.BaseId)
                && TryLocateAll(r, out var zterrs))
                foreach (var terr in zterrs)
                    if (_content.TryGet(terr, out _)) _catalogZones.Add(terr);
        }
    }

    /// <summary>Build the lowercased location-name search blob for a row (see <see cref="_locSearch"/>):
    /// the duty name + map place-name + region of EVERY zone <see cref="TryLocateAll"/> places it in,
    /// space-joined. Empty when the row has no located home. Runs once at load, so the per-keystroke
    /// catalog filter stays an O(1) string lookup.</summary>
    private string ComputeLocSearch(MobRow r)
    {
        if (!TryLocateAll(r, out var terrs)) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var terr in terrs)
        {
            if (!_content.TryGet(terr, out var info)) continue;
            if (info.DutyName.Length > 0) { sb.Append(info.DutyName); sb.Append(' '); }
            if (info.PlaceName.Length > 0) { sb.Append(info.PlaceName); sb.Append(' '); }
            if (info.Region.Length > 0) { sb.Append(info.Region); sb.Append(' '); }
        }
        return sb.ToString().ToLowerInvariant();
    }

    /// <summary>Reduce the static category chip set to those actually populated in this data drop
    /// (drop Inn / Housing / Gold Saucer &amp; friends when the roster has no combat mobs there), and
    /// log the per-chip tally to /xllog. Also repairs a persisted CategoryFilter that points at a
    /// now-hidden (empty) category, so the UI can't get stuck showing nothing with no chip to clear.</summary>
    private void BuildCategoryChips()
    {
        var counts = new Dictionary<string, int>();
        foreach (var cats in _categories.Values)
        {
            // Multi-aware tally: a row contributes to EACH chip its zones span (a game-wide dummy shows
            // under Dungeons AND its field categories). Fold raw categories to chip keys and de-dup, so a
            // mob in two Dungeons counts once for Dungeons but one spanning Dungeons+Field counts for both.
            foreach (var k in cats.Select(ChipKey).Distinct())
                counts[k] = counts.GetValueOrDefault(k) + 1;
        }
        _visibleCategoryChips = CategoryChips
            .Where(c => c.key == "All" || counts.GetValueOrDefault(c.key) > 0)
            .ToList();

        // A saved filter that now points at an empty (hidden) category would show nothing with no chip
        // to clear it — fall back to All.
        if (_config.CategoryFilter != "All" && _visibleCategoryChips.All(c => c.key != _config.CategoryFilter))
        {
            _config.CategoryFilter = "All";
            _pi.SavePluginConfig(_config);
        }

        var tally = string.Join(", ", CategoryChips.Where(c => c.key != "All")
            .Select(c => $"{c.text}={counts.GetValueOrDefault(c.key)}"));
        _log.Information($"HDM category tally: {tally}. Visible chips: " +
                        string.Join(" · ", _visibleCategoryChips.Select(c => c.text)) + ".");
    }

    /// <summary>The multi-aware category set for a row: EVERY Duty-Finder category its zones span, so the
    /// chip filter and the chip tallies see a game-wide striking dummy or a multi-zone roamer under each
    /// of its categories. Precedence: a summonable minion is its own class ABOVE any location (it has no
    /// TerritoryType home — its "home" is a reward source — so it gets {"Minion"} and leaves the located
    /// zones + Unknown tail for its own section); otherwise collect the category of each categorizable
    /// zone from <see cref="TryLocateAll"/> (all winning-tier zones), and if none resolve (every zone is
    /// engine-limbo, or the mob has no home) fall to the {"Unknown"} tail — exactly how the tree buckets
    /// the same row. Raw category strings are kept (Solo folded only at query time via
    /// <see cref="ChipKey"/>), so the set joins cleanly against both the chip keys and the content
    /// categories.</summary>
    private HashSet<string> ComputeCategories(MobRow r)
    {
        // Event NPCs (ENpcBase) have no BNpc-keyed home zone and aren't reward-minions; they file into
        // their own category, so short-circuit before the location tiers (which would all miss → Unknown).
        if (r.Source == NpcSource.Event)
            return new HashSet<string> { "EventNpc" };
        if (r.Source == NpcSource.Mount)
            return new HashSet<string> { "Mount" };
        if (_companion.IsMinion(r.BaseId))
            return new HashSet<string> { "Minion" };
        var set = new HashSet<string>();
        if (TryLocateAll(r, out var terrs))
            foreach (var terr in terrs)
                if (_content.TryGet(terr, out var info))
                    set.Add(info.Category);
        if (set.Count == 0)
            set.Add("Unknown");
        return set;
    }

    /// <summary>The category set from the precomputed table, computing on the fly on a miss (a
    /// non-renderable row that somehow reaches here) so callers never need a null check.</summary>
    private HashSet<string> CategoriesOf(MobRow r) => _categories.TryGetValue(r.RowKey, out var s) ? s : ComputeCategories(r);

    /// <summary>Fold a raw content category to its chip key — only the two Solo sub-categories collapse
    /// into one "Solo" chip; every other category (and "Minion"/"Unknown") is already its own chip key.</summary>
    private static string ChipKey(string category) => category is "Solo Instances" or "Solo Duty" ? "Solo" : category;

    /// <summary>Does a row belong to the selected category chip? Multi-aware: a mob spanning several
    /// maps matches the chip for EVERY category its zones cover (see <see cref="ComputeCategories"/>).
    /// "All" is handled by the caller; "Solo" folds the two Solo categories; "Minion"/"Unknown" and every
    /// real category key match by plain set membership — a minion set is {"Minion"} and an unplaceable
    /// set is {"Unknown"}, so neither leaks into a located chip and vice-versa.</summary>
    private bool CategoryMatches(MobRow r, string filter)
    {
        var cats = CategoriesOf(r);
        return filter == "Solo"
            ? cats.Contains("Solo Instances") || cats.Contains("Solo Duty")
            : cats.Contains(filter);
    }

    // Provisional expansion from the BNpcBase id range. BNpcBase ids are assigned roughly in
    // content-creation order, so id brackets track expansion. Cut points are the midpoints between
    // per-expansion median ids measured over the located sample (2026-08 data drop): a pure-threshold
    // classifier reproduces the single-expansion ground truth at ~85% (the misses are mostly ARR's
    // long high-id tail — patch NPCs placed in old zones — plus adjacent-expansion boundary noise).
    // It is an ESTIMATE: always shown with a "~" and never used to claim a home zone. It exists only
    // to give the otherwise-unplaceable tail one sortable denominator (the user's "group the 10k pile
    // by something — expansion?"). Re-measure and nudge the cuts if a future data drop shifts medians.
    private static readonly (uint maxExclusive, byte ex)[] ExpansionCuts =
    {
        (2800, 0), (6000, 1), (9300, 2), (12600, 3), (16200, 4),
    };

    private static byte EstimateExpansion(uint baseId)
    {
        foreach (var (maxExclusive, ex) in ExpansionCuts)
            if (baseId < maxExclusive) return ex;
        return 5; // Dawntrail and anything newer
    }

    /// <summary>One-shot startup diagnostic: authoritative per-tier location yield over the
    /// renderable catalog (same gate as the UI denominator), logged once to /xllog. Lets us
    /// confirm the Tier C web supplement actually lifts coverage above the A+B baseline without
    /// eyeballing the grid. Mirrors <see cref="TryLocate"/>'s chain so the numbers reconcile.</summary>
    private void LogLocationCoverage()
    {
        int m = 0, n = 0, a = 0, inst = 0, harv = 0, b = 0, d = 0, c = 0, cSub = 0, unknown = 0, total = 0;
        int dRescued = 0, lvlGross = 0, lvlAgree = 0;
        int namedTotal = 0, namedUnplaced = 0, unnamedUnplaced = 0;
        foreach (var r in _index.Rows)
        {
            if (!IsRenderable(r)) continue;
            // Event NPCs (ENpcBase) aren't part of the BNpc location problem this diagnostic measures —
            // they have their own section and would otherwise inflate the Unknown tail. Skip them.
            if (r.Source != NpcSource.Battle) continue; // Event NPCs + mounts have their own sections
            total++;
            var named = !r.IsUnnamed && !r.NameIsInferred; // real names only, not the asterisked model approximation
            if (named) namedTotal++;
            var hint = EstimateExpansion(r.BaseId);
            // Independent Level (Tier D) coverage of this base, for the corroboration/net-new diagnostic.
            var lvl = _level.TryGetPrimary(r.BaseId, hint, out _);
            if (lvl) lvlGross++;
            // Same precedence as TryLocate so each row is attributed to its winning tier.
            bool placed = true;
            if (_manual.TryGetPrimary(r.BaseId, out _)) { m++; if (lvl) lvlAgree++; }
            else if (r.Name.Length > 0 && _stems.TryMatch(r.Name, out _)) { n++; if (lvl) lvlAgree++; }
            else if (_territory.TryGetPrimary(r.BaseId, out _)) { a++; if (lvl) lvlAgree++; }
            else if (_lore.TryGetByBase(r.BaseId, out _)) { b++; if (lvl) lvlAgree++; }
            else if (_instanced.TryGetPrimary(r.BaseId, out _)) { inst++; if (lvl) lvlAgree++; }
            else if (_harvest.TryGetPrimary(r.BaseId, out _)) { harv++; if (lvl) lvlAgree++; }
            else if (_lore.TryGetByName(r.NameId, out _)
                     || (r.Name.Length > 0 && _lore.TryGetByNameStem(r.Name, out _))) { b++; if (lvl) lvlAgree++; }
            else if (lvl)
            {
                d++; // Tier D: deterministic client-file placement no earlier tier reached
                // Rescued from Unknown = the web scrape couldn't have placed it either.
                if (!(r.Name.Length > 0 && _webloc.TryGetTerritory(r.Name, hint, out _))) dRescued++;
            }
            else if (r.Name.Length > 0 && _webloc.TryGetTerritory(r.Name, hint, out _, out var viaSub)) { c++; if (viaSub) cSub++; }
            else { unknown++; placed = false; }

            if (!placed) { if (named) namedUnplaced++; else unnamedUnplaced++; }
        }
        var located = m + n + a + inst + harv + b + d + c;
        _log.Information(
            $"HDM location coverage: {located}/{total} " +
            $"({located * 100.0 / Math.Max(1, total):0.0}%) located — " +
            $"Tier M curated {m}, Tier N name-stem {n}, Tier A crowdsource {a}, Tier A2 instanced {inst}, Tier A3 harvest {harv}, Tier B sheets {b}, Tier D game-file {d}, Tier C web {c} (of which {cSub} via MapMarker sub-area); Unknown {unknown}.");
        // Deterministic game-file join (Level Type 9, keyed on BNpcBase) — the decision numbers: how
        // broadly it covers the renderable catalog, how much it CORROBORATES the crowdsource/lore tiers
        // (high agreement = safe to later promote above them), and how much it RESCUES from the Unknown
        // tail that even the wiki scrape can't reach. lvlGross reconciles as lvlAgree + d by construction.
        _log.Information(
            $"HDM game-file placement (Level Type 9): {lvlGross} renderable bases covered — " +
            $"{d} net-new over A/A2/B (of which {dRescued} rescued from Unknown, {d - dRescued} also web-reachable) " +
            $"+ {lvlAgree} corroborating an earlier tier.");
        // Named-vs-unnamed split of the unplaced tail: sizes the addressable target for the web
        // scrape / runtime harvester (a NAMED-but-unplaced row is one a name→zone source could place;
        // an unnamed one needs the naming pull first). This is the authoritative post-resolution count
        // — it runs the real TryLocate chain against live sheets, unlike an offline name-set estimate.
        _log.Information(
            $"HDM nameable coverage: {namedTotal - namedUnplaced}/{namedTotal} named rows placed; " +
            $"{namedUnplaced} named-but-unplaced (web-scrape / harvest target), {unnamedUnplaced} unnamed unplaced.");
    }

    // Duty-Finder-ish category order for Location sections. Mirrors the reading order of the
    // in-game Duty Finder (open world, then towns/housing, then the instanced tiers).
    private static int CategoryRank(string category) => category switch
    {
        "World" => 0,
        "City" => 1,
        "Inn" => 2,
        "Housing" => 3,
        "Dungeon" => 4,
        "Trial" => 5,
        "Raid" => 6,
        "Deep Dungeon" => 7,
        "Variant & Criterion" => 8,
        "Solo Instances" => 9,
        "Solo Duty" => 10,
        "PvP" => 11,
        "Gold Saucer" => 12,
        _ => 99,
    };

    // ---- Animations tab ------------------------------------------------------

    // Compound "intro then hold" gestures for the Animations tab's Combos group. Each entry glues a Codebook
    // intro key to a terminal-pose key; ResolveCombos turns those into playable ids per skeleton (both must be
    // caps-valid or the combo is hidden). "Die" is the flagged example — the death FALL (battle/dead) then the
    // lying dead_pose held — a pair "never used separately." Add rows here to grow the set; the resolver + the
    // PlaySequence funnel handle the rest (Rule 1: one compound-gesture mechanism).
    private static readonly (string Label, string IntroKey, string HoldKey)[] CompoundGestures =
    {
        ("Die", "battle/dead", "battle/dead_pose"),
    };

    /// <summary>Resolve <see cref="CompoundGestures"/> to (label, introId, holdId) triples playable on
    /// <paramref name="skel"/> — a combo is INCLUDED only when BOTH its constituent keys resolve as caps-valid
    /// for this skeleton (<see cref="TimelineIndex.ResolvePlayable"/>), so the Combos group never offers a
    /// gesture the model can't perform. Cheap (a handful of rows); called per-frame while the group draws.</summary>
    private List<(string Label, ushort IntroId, ushort HoldId)> ResolveCombos(string skel)
    {
        var result = new List<(string Label, ushort IntroId, ushort HoldId)>();
        foreach (var (label, introKey, holdKey) in CompoundGestures)
        {
            var intro = _timeline.ResolvePlayable(skel, introKey);
            var hold  = _timeline.ResolvePlayable(skel, holdKey);
            if (intro != 0 && hold != 0) result.Add((label, (ushort)intro, (ushort)hold));
        }
        return result;
    }

    /// <summary>b43: the animation library (rail + pane), acting on the Stage subject (see MainWindow.Stage.cs).</summary>
    private void DrawLibrary(ICharacter? target, MobRow? subjectGuise)
    {
        using var _ = new Disabled(target == null);

        // ── b21: the animation LIBRARY BROWSER (rebuilt to the Marionette-style mockup). A rail on the left picks
        // WHAT KIND of thing to play; the pane on the right is always the same shape — an optional narrowing
        // dropdown (+ the "In hand" prop picker for poses), the search box, then ONE list that fills the rest of
        // the window, so it grows and shrinks with the window instead of the old fixed-height boxes. Rows carry the
        // game's own icon where one exists, the name, and a holds/once tag; ids only appear in tooltips. Each kind
        // still plays through its single funnel (TriggerTimeline / PlayLibEntry / PlaySequence).
        var subjectIsHuman = subjectGuise is { McType: 1 } || (subjectGuise is null && target != null);
        var sections = subjectIsHuman ? HumanSections
                     : subjectGuise != null ? CreatureSections
                     : Array.Empty<(string Name, FontAwesomeIcon Glyph)>();
        if (sections.Length == 0) { ImGui.TextDisabled("Nothing to animate: no subject."); return; }
        var body = PoseLibrary.BodyOf(target);

        // The rail: the fixed sections, with this body's pose groups as indented rows right under "All poses".
        var rail = new List<(string Id, string Label, FontAwesomeIcon Glyph, bool Child)>();
        foreach (var (name, glyph) in sections)
        {
            rail.Add((name, name, glyph, false));
            if (name == "All poses")
                foreach (var g in _poses.PosesFor(body))
                    if (g.Items.Count > 0) rail.Add((PoseSectionPrefix + g.Name, GroupDisplay(g.Name), GroupGlyph(g.Name), true));
        }
        // First visit lands on Favourites once something is bookmarked, else on the first real section.
        if (!rail.Any(r => r.Id == _animSection))
            _animSection = _config.AnimFavourites.Count > 0 ? sections[0].Name : sections[subjectIsHuman ? 3 : 1].Name;

        var paneH = MathF.Max(260f, ImGui.GetContentRegionAvail().Y);
        var lineH = ImGui.GetTextLineHeight();
        var railRowH = lineH + 8f;
        const float childIndent = 14f;
        var railW = rail.Max(r => ImGui.CalcTextSize(r.Label).X + (r.Child ? childIndent : 0f)) + lineH + 34f;

        ImGui.BeginChild("##animrail", new Vector2(railW, paneH), true, ImGuiWindowFlags.None);
        foreach (var (id, label, glyph, child) in rail)
        {
            var start = ImGui.GetCursorPos();
            if (ImGui.Selectable($"##rail_{id}", _animSection == id, ImGuiSelectableFlags.None, new Vector2(0f, railRowH)))
                _animSection = id;
            var after = ImGui.GetCursorPos();
            ImGui.SetCursorPos(new Vector2(start.X + 4f + (child ? childIndent : 0f), start.Y + (railRowH - lineH) / 2f));
            DrawGlyph(glyph, _animSection == id);
            ImGui.SameLine(0f, 8f);
            if (child && _animSection != id) ImGui.TextDisabled(label); else ImGui.TextUnformatted(label);
            ImGui.SetCursorPos(after);
        }
        if (_config.HiddenAnims.Count > 0)
        {
            ImGui.Spacing();
            ImGui.Checkbox($"Show hidden ({_config.HiddenAnims.Count})", ref _showHidden);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Rows you hid with right-click > Hide. Tick to see them (and unhide).");
        }
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("##animpane", new Vector2(0f, paneH), false, ImGuiWindowFlags.None);
        _ctxHuman = subjectIsHuman; _ctxGuise = subjectGuise; _ctxBody = body;
        if (_animSection.StartsWith(PoseSectionPrefix, StringComparison.Ordinal))
            DrawPosesPane(target, body, _animSection[PoseSectionPrefix.Length..]);
        else switch (_animSection)
        {
            case "Favourites": DrawFavouritesPane(target, subjectIsHuman, subjectGuise, body); break;
            case "Search all": DrawSearchAllPane(target, body); break;
            case "Emotes":     DrawEmotesPane(target); break;
            case "All poses":  DrawPosesPane(target, body, null); break;
            case "Job skills": DrawSkillsPane(target); break;
            case "Basics":     DrawBasicsPane(target, subjectIsHuman, subjectGuise); break;
            case "Skills":     if (subjectGuise != null) DrawCreatureSkillsPane(target, subjectGuise); break;
            default:           DrawAllTimelinesPane(target); break;
        }
        ImGui.EndChild();
    }

    // ---- b21 library browser ------------------------------------------------------------------------------

    // b27: the human rail. Pose groups are rail rows of their own (indented under "All poses"), Talking and Entrances
    // are just pose groups now, and the human Basics are gone (DM). Creatures keep Basics: it holds their Die combos.
    private static readonly (string Name, FontAwesomeIcon Glyph)[] HumanSections =
    [
        ("Favourites", FontAwesomeIcon.Star), ("Search all", FontAwesomeIcon.Search),
        ("Emotes", FontAwesomeIcon.Smile), ("All poses", FontAwesomeIcon.StreetView),
        ("Job skills", FontAwesomeIcon.HandRock), ("All timelines", FontAwesomeIcon.Feather),
    ];
    private const string PoseSectionPrefix = "pose:";
    private static string GroupDisplay(string group) => group == "Warp in & out" ? "Entrances" : group;

    private static readonly (string Name, FontAwesomeIcon Glyph)[] CreatureSections =
    [
        ("Favourites", FontAwesomeIcon.Star),
        ("Skills", FontAwesomeIcon.Dragon), ("Basics", FontAwesomeIcon.Walking), ("All timelines", FontAwesomeIcon.Feather),
    ];

    private static readonly string[] EmoteModes = ["All emotes", "Holds", "One-shots"];

    // Human "Basics": the handful of everyday moves worth a button, by Common key, with plain names.
    private static readonly (string Name, string Key)[] HumanBasicKeys =
    [
        ("Draw weapon", "battle/battle_start"), ("Sheathe weapon", "battle/battle_end"), ("Battle stance", "battle/idle"),
        ("Idle", "normal/idle"), ("Walk", "normal/walk"), ("Walk backwards", "normal/walk_b"), ("Run", "normal/run"),
        ("Jump", "normal/jump_start"), ("Flinch", "battle/damage"),
    ];
    private List<TimelineRow>? _humanBasics;
    private bool _showHidden;
    private List<TimelineRow> HumanBasics() => _humanBasics ??= HumanBasicKeys
        .Select(b => _timeline.Common.FirstOrDefault(t => t.Key == b.Key) is { } row ? row with { Name = b.Name } : null)
        .Where(t => t != null).Select(t => t!).ToList();

    private static FontAwesomeIcon GroupGlyph(string group) => group switch
    {
        "Hurt & collapse" => FontAwesomeIcon.UserInjured, "Lying" => FontAwesomeIcon.Bed,
        "Prayer & ceremony" or "Kneeling" => FontAwesomeIcon.Pray, "Sitting" or "On a chair" => FontAwesomeIcon.Chair,
        "On the ground" => FontAwesomeIcon.Campground, "More" => FontAwesomeIcon.EllipsisH,
        "Crouching" => FontAwesomeIcon.PersonFalling, "Sorrow" => FontAwesomeIcon.SadTear, "Fear" => FontAwesomeIcon.Ghost,
        "Anger & provoking" => FontAwesomeIcon.Angry, "Joy" => FontAwesomeIcon.Laugh, "Performing" => FontAwesomeIcon.Music,
        "Talking" => FontAwesomeIcon.Comments, "Thinking & worry" => FontAwesomeIcon.Brain,
        "Reactions" => FontAwesomeIcon.HandPaper, "Eating & drinking" => FontAwesomeIcon.Utensils,
        "Crafting & chores" => FontAwesomeIcon.Hammer, "Animals & mounts" => FontAwesomeIcon.Paw,
        "Battle & weapons" => FontAwesomeIcon.ShieldAlt, "Walking" => FontAwesomeIcon.Walking,
        "Standing & idle" => FontAwesomeIcon.StreetView, "Warp in & out" => FontAwesomeIcon.HatWizard,
        _ => FontAwesomeIcon.Feather,
    };

    /// <summary>What a library row shows: icon (game icon id, else a glyph), name, a dim suffix (e.g. the
    /// auto-prop), a right-aligned tag (holds / once / Lv 50), and the hover text.</summary>
    private readonly record struct RowView(string Name, uint Icon, FontAwesomeIcon Glyph, string Tag, bool Accent, string Tip, string Sub = "", string FavKey = "");

    // b25: animation bookmarks. Keys: emote:<EmoteRowId> · pose:<id> · skill:<id> · tl:<id> (common timeline) ·
    // mob:<skel>:<id> (a creature skill) · combo:<skel>:<intro>:<hold>.
    private bool IsAnimFav(string key) => key.Length > 0 && _config.AnimFavourites.Contains(key);
    private void ToggleAnimFav(string key)
    {
        if (!_config.AnimFavourites.Remove(key)) _config.AnimFavourites.Add(key);
        _pi.SavePluginConfig(_config);
    }

    private void DrawGlyph(FontAwesomeIcon glyph, bool lit)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        if (lit) ImGui.TextColored(_accent.Primary, glyph.ToIconString());
        else ImGui.TextDisabled(glyph.ToIconString());
        ImGui.PopFont();
    }

    /// <summary>The section's narrowing dropdown; remembers its choice per section.</summary>
    private string Dropdown(string id, IReadOnlyList<string> options, string section, float width)
    {
        var cur = _animDrop.GetValueOrDefault(section, "");
        if (options.Count > 0 && !options.Contains(cur)) cur = options[0];
        ImGui.SetNextItemWidth(width);
        if (ImGui.BeginCombo(id, cur, ImGuiComboFlags.HeightLarge))
        {
            foreach (var o in options)
                if (ImGui.Selectable(o, o == cur)) cur = o;
            ImGui.EndCombo();
        }
        _animDrop[section] = cur;
        return cur;
    }

    private float LoopReserve()
    {
        var st = ImGui.GetStyle();
        return ImGui.GetFrameHeight() + st.ItemInnerSpacing.X + ImGui.CalcTextSize("Loop").X + st.ItemSpacing.X;
    }

    private void SearchBox(float reserveRight)
    {
        // b30: a clear (×) button right of the box; it sits inside the reserved width, so callers' layouts don't move.
        var clearW = ImGui.GetFrameHeight();
        var gap = ImGui.GetStyle().ItemInnerSpacing.X;
        ImGui.SetNextItemWidth(MathF.Max(80f, ImGui.GetContentRegionAvail().X - reserveRight - clearW - gap));
        ImGui.InputTextWithHint("##animsearch", "Search", ref _animFilter, 48);
        ImGui.SameLine(0f, gap);
        using (new Disabled(_animFilter.Length == 0))
            if (ImGui.Button("×##animsearchclear", new Vector2(clearW, 0f))) _animFilter = "";
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Clear the search");
    }

    private void LoopToggle()
    {
        ImGui.SameLine();
        ImGui.Checkbox("Loop", ref _loopMode);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Checked: clicking a timeline replays it until Stop.\nUnchecked: it plays once, then blends back to idle.");
    }

    private bool Matches(string name, string key = "")
    {
        var f = _animFilter.Trim();
        return f.Length == 0 || name.Contains(f, StringComparison.OrdinalIgnoreCase)
               || (key.Length > 0 && key.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Draw a list that fills the rest of the pane, one full-width row per item: icon, name (+ dim suffix), tag.
    /// Virtualised by hand — fixed row height, only the rows inside the scroll window are submitted — so the
    /// 1,000-entry pose list costs the same as a short one.
    /// </summary>
    private void DrawRows<T>(string id, IReadOnlyList<T> rows, Func<T, RowView> view, Action<T> click)
    {
        if (_config.HiddenAnims.Count > 0 && !_showHidden)
            rows = rows.Where(r => !_config.HiddenAnims.Contains(view(r).FavKey)).ToList();
        ImGui.BeginChild(id, new Vector2(0f, MathF.Max(80f, ImGui.GetContentRegionAvail().Y)), true, ImGuiWindowFlags.None);
        if (rows.Count == 0) { ImGui.TextDisabled("(no matches)"); ImGui.EndChild(); return; }
        var lineH = ImGui.GetTextLineHeight();
        var iconSz = MathF.Round(lineH * 1.35f);
        var rowH = iconSz + 6f;
        var x0 = ImGui.GetCursorPosX();
        var y0 = ImGui.GetCursorPosY();
        var width = ImGui.GetContentRegionAvail().X;
        // b25: a bookmark star leads each row. It sits on the row's TEXT centre line (same offset as the name), so the
        // stars form a neat column level with the names. Clicking it toggles the bookmark; the rest of the row plays.
        var starW = ImGui.CalcTextSize("★").X;
        var lead = 4f + starW + 6f;
        var first = Math.Max(0, (int)(ImGui.GetScrollY() / rowH) - 1);
        var last = Math.Min(rows.Count, first + (int)(ImGui.GetWindowHeight() / rowH) + 3);
        for (var i = first; i < last; i++)
        {
            var item = rows[i];
            var r = view(item);
            var y = y0 + i * rowH;
            ImGui.PushID(i);
            ImGui.SetCursorPos(new Vector2(x0, y));
            var onStar = r.FavKey.Length > 0 && ImGui.GetMousePos().X - ImGui.GetCursorScreenPos().X < lead - 2f;
            if (ImGui.Selectable("##row", false, ImGuiSelectableFlags.None, new Vector2(width, rowH - 2f)))
            {
                if (onStar) ToggleAnimFav(r.FavKey); else click(item);
            }
            var fav = IsAnimFav(r.FavKey);
            if (ImGui.IsItemHovered())
            {
                if (onStar) ImGui.SetTooltip(fav ? "Remove from Favourites" : "Add to Favourites");
                else if (r.FavKey.Length > 0) ImGui.SetTooltip((r.Tip.Length > 0 ? r.Tip + "\n" : "") + "Right-click: copy as a macro command for the hotbar.");
                else if (r.Tip.Length > 0) ImGui.SetTooltip(r.Tip);
            }
            if (r.FavKey.Length > 0 && ImGui.BeginPopupContextItem("##rowctx"))
            {
                var line = MacroLine(r);
                ImGui.TextDisabled(line);
                ImGui.Separator();
                if (ImGui.Selectable("Copy macro command")) ImGui.SetClipboardText(line);
                if (ImGui.Selectable(fav ? "Remove from Favourites" : "Add to Favourites")) ToggleAnimFav(r.FavKey);
                var hidden = _config.HiddenAnims.Contains(r.FavKey);
                if (ImGui.Selectable(hidden ? "Unhide" : "Hide from lists"))
                {
                    if (!_config.HiddenAnims.Remove(r.FavKey)) _config.HiddenAnims.Add(r.FavKey);
                    _pi.SavePluginConfig(_config);
                }
                ImGui.EndPopup();
            }
            var textY = y + (rowH - lineH) / 2f - 1f;
            if (r.FavKey.Length > 0)
            {
                ImGui.SetCursorPos(new Vector2(x0 + 4f, textY));
                ImGui.TextColored(fav ? _accent.Primary : StarOff, fav ? "★" : "☆");
            }
            ImGui.SetCursorPos(new Vector2(x0 + lead, y + (rowH - iconSz) / 2f - 1f));
            if (!DrawEmoteIcon(r.Icon, iconSz))
            {
                ImGui.SetCursorPos(new Vector2(x0 + lead + (iconSz - lineH) / 2f, textY));
                DrawGlyph(r.Glyph, false);
            }
            ImGui.SetCursorPos(new Vector2(x0 + lead + iconSz + 8f, textY));
            ImGui.TextUnformatted(r.Name);
            if (r.Sub.Length > 0) { ImGui.SameLine(0f, 8f); ImGui.TextDisabled(r.Sub); }
            if (r.Tag.Length > 0)
            {
                var tw = ImGui.CalcTextSize(r.Tag).X;
                ImGui.SetCursorPos(new Vector2(x0 + width - tw - 8f, y + (rowH - lineH) / 2f - 1f));
                if (r.Accent) ImGui.TextColored(_accent.Primary, r.Tag); else ImGui.TextDisabled(r.Tag);
            }
            ImGui.PopID();
        }
        ImGui.SetCursorPos(new Vector2(x0, y0 + rows.Count * rowH));
        ImGui.Dummy(new Vector2(1f, 1f));
        ImGui.EndChild();
    }

    private static string ModeText(PosePlay p) => p switch
    {
        PosePlay.Hold      => "Holds this pose until Stop or Reset to Normal.",
        PosePlay.IntroHold => "Plays its intro, then holds the pose until Stop or Reset to Normal.",
        _                  => "Plays once, then returns to idle.",
    };

    // b29: one-shot poses hold their ending while the "Hold emote" toggle is on (DM: a toggle, like Use chair).
    private bool HoldsEnding(LibEntry e) => e.Play == PosePlay.Once && _config.HoldEmote;

    /// <summary>The pose toolbar's two toggles, after the "In hand" picker.</summary>
    private void PoseToggles()
    {
        ImGui.SameLine();
        var snap = _config.SnapSeatedPoses;
        if (ImGui.Checkbox("Use chair", ref snap)) { _config.SnapSeatedPoses = snap; _pi.SavePluginConfig(_config); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("On: a chair, bench or stool pose played on yourself first does a native /sit, so the game\n" +
                             "snaps you into the nearest seat, then plays the pose there. Already seated: plays in place.\n" +
                             "Off: the pose plays wherever you stand.");
        ImGui.SameLine();
        var hold = _config.HoldEmote;
        if (ImGui.Checkbox("Hold emote", ref hold)) { _config.HoldEmote = hold; _pi.SavePluginConfig(_config); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("On: a pose that plays once stops on its last frame and stays there (a fall stays down)\n" +
                             "until you move or Reset to Normal. Off: it returns to idle when it ends.");
    }

    // b31: which races/sexes ship this pose's animation, and where this body borrows it from (Universal poses).
    private readonly Dictionary<ushort, string> _nativeLine = new();
    private string NativeBodiesLine(LibEntry e)
    {
        if (_poses.Porter is not { } porter || !e.Key.StartsWith("event", StringComparison.Ordinal)) return "";
        if (!_nativeLine.TryGetValue(e.Id, out var line))
        {
            var own = porter.NativeBodies(e.Key);
            line = own.Count == 18 ? "\nAnimation: every race" : $"\nAnimation ships for: {string.Join(", ", own.Select(UniversalPoses.BodyName))}";
            _nativeLine[e.Id] = line;
        }
        var donor = _ctxBody > 0 ? porter.DonorFor(_ctxBody, e.Key) : 0;
        return donor > 0 ? line + $"\nThis body borrows it from {UniversalPoses.BodyName(donor)}" + (porter.Active ? "" : " (Universal poses is off)") : line;
    }

    private RowView PoseView(LibEntry e, string group, bool showGroup)
    {
        var holdEnd = HoldsEnding(e);
        var holds = e.Play != PosePlay.Once || holdEnd;
        var hand = _config.PoseHand == "auto" && e.Prop != null ? e.Prop.Name.ToLowerInvariant() : "";
        var sub = showGroup ? (hand.Length > 0 ? $"{group} · {hand}" : group) : hand;
        var tip = (holdEnd ? "Plays once, then holds its ending until you move or Reset to Normal." : ModeText(e.Play))
                  + (e.Note.Length > 0 ? $"\n{e.Note}" : "")
                  + (e.Prop != null ? $"\nAuto-prop: {e.Prop.Name}" : "") + NativeBodiesLine(e) + $"\n{group} · {e.Key} · {e.Id}";
        return new RowView(e.Name, 0, GroupGlyph(group), holds ? "holds" : "once", holds, tip, sub, $"pose:{e.Id}");
    }

    private static RowView EmoteView(TimelineRow t)
        => new(t.Name, t.Icon, FontAwesomeIcon.Smile, t.EmoteConditionMode != 0 ? "holds" : "once", t.EmoteConditionMode != 0,
               $"{t.Key}\n{(t.EmoteConditionMode != 0 ? "Holds until Stop or Reset to Normal." : "Plays once.")}",
               FavKey: $"emote:{t.EmoteRowId}");

    private static RowView SkillView(LibEntry e, string job, bool showJob)
        => new(e.Name, e.Icon, FontAwesomeIcon.HandRock, e.Note.Replace("Level ", "Lv "), false,
               $"Plays the skill's animation and effects once.\n{job} · {e.Key} · {e.Id}", showJob ? job : "",
               FavKey: $"skill:{e.Id}");

    private void DrawEmotesPane(ICharacter? target)
    {
        var mode = Dropdown("##emotemode", EmoteModes, "Emotes", 120f);
        ImGui.SameLine();
        SearchBox(0f);
        var rows = _timeline.Emotes.Where(t =>
                (mode == "All emotes" || (mode == "Holds") == (t.EmoteConditionMode != 0)) && Matches(t.Name, t.Key)).ToList();
        DrawRows("##emoterows", rows, EmoteView, t => { if (target != null) TriggerTimeline(t, target); });
    }

    /// <summary>A pose group (from the rail), or every pose when <paramref name="group"/> is null. Typing in the
    /// search always looks across EVERY pose group, so the DM never has to guess the group first.</summary>
    private void DrawPosesPane(ICharacter? target, int body, string? group)
    {
        var groups = _poses.PosesFor(body);
        if (groups.Count == 0) { ImGui.TextDisabled("No poses exist for this body."); return; }
        DrawHandPicker(target, 220f);
        PoseToggles();
        SearchBox(0f);
        var searching = _animFilter.Trim().Length > 0;
        var all = searching || group == null;
        var rows = (all
                ? groups.SelectMany(g => g.Items.Select(e => (E: e, G: g.Name))).Where(x => Matches(x.E.Name))
                : (groups.FirstOrDefault(g => g.Name == group)?.Items ?? []).Select(e => (E: e, G: group!))).ToList();
        DrawRows("##poserows", rows, x => PoseView(x.E, x.G, all),
            x => { if (target != null) PlayLibEntry(x.E, target, skill: false); });
    }

    /// <summary>b27: one search over everything a human can play: emotes, every pose group and job skills.</summary>
    private void DrawSearchAllPane(ICharacter? target, int body)
    {
        DrawHandPicker(target, 220f);
        PoseToggles();
        SearchBox(0f);
        if (_animFilter.Trim().Length == 0)
        {
            ImGui.TextDisabled("Type to search emotes, poses, talking, entrances and job skills at once.");
            return;
        }
        var items = new List<FavItem>();
        foreach (var t in _timeline.Emotes)
            if (Matches(t.Name, t.Key)) { var et = t; items.Add(new FavItem(EmoteView(et) with { Sub = "Emote" }, c => TriggerTimeline(et, c))); }
        foreach (var g in _poses.PosesFor(body))
            foreach (var e in g.Items)
                if (Matches(e.Name)) { var pe = e; items.Add(new FavItem(PoseView(pe, GroupDisplay(g.Name), showGroup: true), c => PlayLibEntry(pe, c, skill: false))); }
        foreach (var j in _poses.JobSkills)
            foreach (var e in j.Items)
                if (Matches(e.Name)) { var se = e; items.Add(new FavItem(SkillView(se, j.Name, showJob: true), c => PlayLibEntry(se, c, skill: true))); }
        DrawRows("##searchall", items, f => f.View, f => { if (target != null) f.Play(target); });
    }

    private void DrawSkillsPane(ICharacter? target)
    {
        var jobs = _poses.JobSkills;
        if (jobs.Count == 0) { ImGui.TextDisabled("No job skills loaded."); return; }
        // First visit: open on the subject's current job.
        if (!_animDrop.ContainsKey("Job skills") && target != null
            && jobs.FirstOrDefault(j => j.JobId == target.ClassJob.RowId) is { } mine)
            _animDrop["Job skills"] = mine.Name;
        var pick = Dropdown("##job", jobs.Select(j => j.Name).ToList(), "Job skills", 210f);
        ImGui.SameLine();
        SearchBox(0f);
        var searching = _animFilter.Trim().Length > 0;
        var rows = (searching
                ? jobs.SelectMany(j => j.Items.Select(e => (E: e, G: j.Name))).Where(x => Matches(x.E.Name))
                : jobs.First(j => j.Name == pick).Items.Select(e => (E: e, G: pick))).ToList();
        DrawRows("##skillrows", rows, x => SkillView(x.E, x.G, searching),
            x => { if (target != null) PlayLibEntry(x.E, target, skill: true); });
    }

    private void DrawBasicsPane(ICharacter? target, bool human, MobRow? guise)
    {
        SearchBox(LoopReserve());
        LoopToggle();
        if (human || guise == null) return;   // b27: human Basics retired (DM); old "tl:" bookmarks still resolve
        // Creature: the glued combos (Die = fall, then hold the pose) first, then the base moves this skeleton's
        // animation files actually define (idle / attack / dead / turn / flinch…).
        var skel = guise.SkeletonCode;
        var list = new List<(string Name, TimelineRow? T, ushort Intro, ushort Hold)>();
        foreach (var (label, introId, holdId) in ResolveCombos(skel)) list.Add((label, null, introId, holdId));
        foreach (var t in _timeline.ValidCommonFor(skel)) list.Add((t.Name, t, 0, 0));
        var crows = list.Where(x => Matches(x.Name, x.T?.Key ?? "")).ToList();
        DrawRows("##cbasicrows", crows,
            x => x.T is { } t ? TimelineView(t, FontAwesomeIcon.Walking)
                : new RowView(x.Name, 0, FontAwesomeIcon.Walking, "holds", true,
                              $"Plays the intro once, then holds its final pose until Stop or Reset to Normal.\n{x.Intro} → {x.Hold}",
                              FavKey: $"combo:{skel}:{x.Intro}:{x.Hold}"),
            x =>
            {
                if (target == null) return;
                if (x.T is { } t) TriggerTimeline(t, target);
                else { _anim.PlaySequence(target, x.Intro, x.Hold); ReportHeldLoop(target, x.Hold, x.Intro); }
            });
    }

    /// <summary>A bookmarked row resolved for the CURRENT subject: what to show and how to play it.</summary>
    private readonly record struct FavItem(RowView View, Action<ICharacter> Play);

    /// <summary>Favourites: every bookmark that applies to this subject (human-only kinds are skipped on a creature,
    /// and a creature skill only shows on its own skeleton), sorted by name, played through the usual funnels.</summary>
    private void DrawFavouritesPane(ICharacter? target, bool human, MobRow? guise, int body)
    {
        SearchBox(LoopReserve());
        LoopToggle();
        if (_config.AnimFavourites.Count == 0)
        {
            ImGui.TextDisabled("Nothing bookmarked yet. Click the ☆ at the start of any row to pin it here.");
            return;
        }
        var items = new List<FavItem>();
        foreach (var key in _config.AnimFavourites)
            if (ResolveFav(key, human, guise, body) is { } fi && Matches(fi.View.Name)) items.Add(fi);
        items.Sort((a, b) => string.Compare(a.View.Name, b.View.Name, StringComparison.OrdinalIgnoreCase));
        if (items.Count == 0 && _animFilter.Trim().Length == 0)
            ImGui.TextDisabled(human ? "None of your bookmarks apply to this body." : "None of your bookmarks apply to this creature.");
        DrawRows("##favrows", items, f => f.View, f => { if (target != null) f.Play(target); });
    }

    private FavItem? ResolveFav(string key, bool human, MobRow? guise, int body)
    {
        var parts = key.Split(':');
        if (parts.Length < 2) return null;
        switch (parts[0])
        {
            case "emote" when human && ushort.TryParse(parts[1], out var er):
                return _timeline.Emotes.FirstOrDefault(t => t.EmoteRowId == er) is { } et
                    ? new FavItem(EmoteView(et), c => TriggerTimeline(et, c)) : null;
            case "pose" when human && ushort.TryParse(parts[1], out var pid):
                foreach (var g in _poses.PosesFor(body))
                    if (g.Items.FirstOrDefault(e => e.Id == pid) is { } pe)
                        return new FavItem(PoseView(pe, g.Name, showGroup: true), c => PlayLibEntry(pe, c, skill: false));
                return null;
            case "skill" when human && ushort.TryParse(parts[1], out var sid):
                foreach (var j in _poses.JobSkills)
                    if (j.Items.FirstOrDefault(e => e.Id == sid) is { } se)
                        return new FavItem(SkillView(se, j.Name, showJob: true), c => PlayLibEntry(se, c, skill: true));
                return null;
            case "tl" when uint.TryParse(parts[1], out var tid):
                var row = HumanBasics().FirstOrDefault(t => t.Id == tid) ?? _timeline.Common.FirstOrDefault(t => t.Id == tid);
                return row is null ? null : new FavItem(TimelineView(row, FontAwesomeIcon.Walking), c => TriggerTimeline(row, c));
            case "mob" when !human && guise != null && parts.Length == 3 && parts[1] == guise.SkeletonCode && uint.TryParse(parts[2], out var mid):
                return _timeline.ForSkeleton(guise.SkeletonCode, (uint)guise.ModelCharaId).FirstOrDefault(t => t.Id == mid) is { } mt
                    ? new FavItem(TimelineView(mt, FontAwesomeIcon.Dragon, key), c => TriggerTimeline(mt, c)) : null;
            case "combo" when !human && guise != null && parts.Length == 4 && parts[1] == guise.SkeletonCode
                              && ushort.TryParse(parts[2], out var ci) && ushort.TryParse(parts[3], out var ch):
                foreach (var (label, introId, holdId) in ResolveCombos(guise.SkeletonCode))
                    if (introId == ci && holdId == ch)
                        return new FavItem(new RowView(label, 0, FontAwesomeIcon.Walking, "holds", true,
                                               "Plays the intro once, then holds its final pose until Stop or Reset to Normal.", FavKey: key),
                                           c => { _anim.PlaySequence(c, ci, ch); ReportHeldLoop(c, ch, ci); });
                return null;
        }
        return null;
    }

    // ---- b26: macro / hotbar commands -------------------------------------------------------------------
    // "/hdm play <name>" plays a library row exactly as clicking it would, on the Animations tab's subject (you, or
    // the focused puppet when Drive is set to it). "/hdm play pose:1068" (the bookmark key) also works and never
    // breaks if a name changes. "/hdm stop" = Reset to Normal. Right-click a row to copy its line.
    private bool _ctxHuman = true;
    private MobRow? _ctxGuise;
    private int _ctxBody;

    private void ResetToNormal(ICharacter target)
    {
        _anim.Sanitize(target);
        HandRestore(target);
        ReportHeldLoop(target, 0);
    }

    private string MacroLine(RowView r)
        => $"{Plugin.Command} play " + (FindAnimKey(r.Name, _ctxHuman, _ctxGuise, _ctxBody) == r.FavKey ? r.Name : r.FavKey);

    /// <summary>Name -> bookmark key, searched in the tab's own order. Exact (case-insensitive) names only.</summary>
    private string? FindAnimKey(string q, bool human, MobRow? guise, int body)
    {
        bool Eq(string n) => string.Equals(n, q, StringComparison.OrdinalIgnoreCase);
        if (human)
        {
            if (_timeline.Emotes.FirstOrDefault(t => Eq(t.Name)) is { } em) return $"emote:{em.EmoteRowId}";
            foreach (var g in _poses.PosesFor(body))
                if (g.Items.FirstOrDefault(e => Eq(e.Name)) is { } pe) return $"pose:{pe.Id}";
            foreach (var j in _poses.JobSkills)
                if (j.Items.FirstOrDefault(e => Eq(e.Name)) is { } se) return $"skill:{se.Id}";
            if (HumanBasics().FirstOrDefault(t => Eq(t.Name)) is { } hb) return $"tl:{hb.Id}";
        }
        else if (guise != null)
        {
            var skel = guise.SkeletonCode;
            if (_timeline.ForSkeleton(skel, (uint)guise.ModelCharaId).FirstOrDefault(t => Eq(t.Name)) is { } mt) return $"mob:{skel}:{mt.Id}";
            foreach (var (label, introId, holdId) in ResolveCombos(skel))
                if (Eq(label)) return $"combo:{skel}:{introId}:{holdId}";
            if (_timeline.ValidCommonFor(skel).FirstOrDefault(t => Eq(t.Name)) is { } vc) return $"tl:{vc.Id}";
        }
        return _timeline.Common.FirstOrDefault(t => Eq(t.Name)) is { } c ? $"tl:{c.Id}" : null;
    }

    /// <summary>Chat/macro: play an animation by name or key on the Animations tab's subject; "stop" resets.
    /// Silent on success so a hotbar macro doesn't spam chat.</summary>
    public string CommandPlay(string arg)
    {
        arg = arg.Trim().Trim('"');
        var self = Self();
        var puppet = FocusedPuppet();
        var target = _animApplyToPuppet && puppet != null ? puppet : self;
        if (target == null) return "no one to animate.";
        if (arg.Length == 0) return $"usage: {Plugin.Command} play <animation name>  ·  {Plugin.Command} stop. Right-click a row on the Stage tab to copy one.";
        if (arg is "stop" or "reset" or "normal") { ResetToNormal(target); return ""; }
        var guise = target == puppet && _animApplyToPuppet ? _puppetGuise.GetValueOrDefault(target.ObjectIndex) : _wornGuise;
        var human = guise is { McType: 1 } || guise is null;
        var body = PoseLibrary.BodyOf(target);
        var key = arg.Contains(':') ? arg : FindAnimKey(arg, human, guise, body);
        if (key == null) return $"no animation called \"{arg}\" for {(target == self ? "you" : "the puppet")} right now.";
        if (ResolveFav(key, human, guise, body) is not { } fi) return $"\"{arg}\" doesn't apply to {(target == self ? "your current body" : "the puppet")}.";
        fi.Play(target);
        return "";
    }

    private RowView TimelineView(TimelineRow t, FontAwesomeIcon glyph, string favKey = "")
    {
        var holds = IsResidentSpecialLoop(t.Key);
        var tag = holds ? "holds" : _loopMode ? "loops" : "once";
        var tip = $"{t.Key} · {t.Id}" + (t.IsTemplate
            ? "\nTemplate special: the game resolves it on this skeleton." + (t.HasTmb ? "\nShips its effects (VFX + sound)." : "\nBody animation only.")
            : "");
        return new RowView(t.Name, 0, glyph, tag, holds || _loopMode, tip, FavKey: favKey.Length > 0 ? favKey : $"tl:{t.Id}");
    }

    private void DrawCreatureSkillsPane(ICharacter? target, MobRow guise)
    {
        SearchBox(LoopReserve());
        LoopToggle();
        var specials = _timeline.ForSkeleton(guise.SkeletonCode, (uint)guise.ModelCharaId);
        if (specials.Count == 0)
        {
            ImGui.TextDisabled($"No skills found for {guise.DisplayName} ({guise.SkeletonCode}). Try Basics or All timelines.");
            return;
        }
        var rows = specials.Where(t => Matches(t.Name, t.Key)).ToList();
        DrawRows("##cskillrows", rows, t => TimelineView(t, FontAwesomeIcon.Dragon, $"mob:{guise.SkeletonCode}:{t.Id}"),
            t => { if (target != null) TriggerTimeline(t, target); });
    }

    private void DrawAllTimelinesPane(ICharacter? target)
    {
        var cat = Dropdown("##provcat", AnimGroupChips.Select(c => c.text).ToList(), "All timelines", 110f);
        ImGui.SameLine();
        SearchBox(LoopReserve());
        LoopToggle();
        var rows = _timeline.Common.Where(t => (cat == "All" || ProvenanceCategory(t.Key) == cat) && Matches(t.Name, t.Key)).ToList();
        DrawRows("##allrows", rows, t => TimelineView(t, FontAwesomeIcon.Feather),
            t => { if (target != null) TriggerTimeline(t, target); });
    }

    // ---- "In hand": props for poses ----------------------------------------------------------------------

    private string HandLabel()
    {
        var m = _config.PoseHand;
        if (m == "empty") return "In hand: nothing";
        if (m == "weapon") return "In hand: my weapon";
        if (m.StartsWith("prop:", StringComparison.Ordinal))
            return "In hand: " + (_poses.Props.FirstOrDefault(p => PropKey(p) == m)?.Name ?? "prop");
        return "In hand: auto";
    }

    private static string PropKey(HandProp p) => $"prop:{p.Set},{p.Base},{p.Variant}";

    /// <summary>The "In hand" picker: Auto (the pose's own prop where it has one, else empty hands — the default),
    /// nothing, my weapon, or any of the ~800 named props (searchable). Changing it re-dresses a held pose now.</summary>
    private void DrawHandPicker(ICharacter? target, float width)
    {
        ImGui.SetNextItemWidth(width);
        if (ImGui.BeginCombo("##handpick", HandLabel(), ImGuiComboFlags.HeightLarge))
        {
            if (ImGui.Selectable("Auto: the pose's own prop, else nothing", _config.PoseHand == "auto")) SetHand("auto", target);
            if (ImGui.Selectable("Nothing (empty hands)", _config.PoseHand == "empty")) SetHand("empty", target);
            if (ImGui.Selectable("My weapon", _config.PoseHand == "weapon")) SetHand("weapon", target);
            ImGui.Separator();
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint("##propsearch", "Search props (cup, pipe, book…)", ref _propSearch, 48);
            var q = _propSearch.Trim();
            var shown = 0;
            foreach (var p in _poses.Props)
            {
                if (q.Length > 0 && !p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                if (++shown > 300) { ImGui.TextDisabled("…type to narrow the list"); break; }
                ImGui.PushID(shown);
                if (ImGui.Selectable(p.Name, _config.PoseHand == PropKey(p))) SetHand(PropKey(p), target);
                ImGui.PopID();
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("What the hand holds while posing. Auto gives drinking poses a cup, smoking a pipe,\n" +
                             "reading a book and so on, and empties the hand otherwise (so your weapon never\n" +
                             "appears in it). Reset to Normal gives the weapon back. Visual only, your screen.");
    }

    private void SetHand(string mode, ICharacter? target)
    {
        _config.PoseHand = mode;
        _pi.SavePluginConfig(_config);
        if (target != null && _guise.HasPoseHand(target.ObjectIndex)) ApplyHand(target, _lastPose);
    }

    /// <summary>Dress the hand for a pose per the picker: weapon = give it back; nothing = empty; auto = the
    /// entry's own prop else empty; a specific prop = that prop.</summary>
    private void ApplyHand(ICharacter target, LibEntry? e)
    {
        var m = _config.PoseHand;
        if (m == "weapon") { HandRestore(target); return; }
        HandProp? prop = m == "auto" ? e?.Prop
                       : m.StartsWith("prop:", StringComparison.Ordinal) ? _poses.Props.FirstOrDefault(p => PropKey(p) == m)
                       : null;
        if (prop == null) { HandSet(target, null); return; }
        HandSet(target, new FFXIVClientStructs.FFXIV.Client.Game.Character.WeaponModelId
            { Id = prop.Set, Type = prop.Base, Variant = prop.Variant }, prop.OffHand);
    }

    /// <summary>Play a library entry through the tab's funnels (hold = Loop + held-loop report; intro-hold =
    /// PlaySequence like the Combos; once = PlayOnce + one-shot report), dressing the hand first: a job skill gets
    /// the real weapon back, a pose gets whatever the "In hand" picker says.</summary>
    private void PlayLibEntry(LibEntry e, ICharacter target, bool skill)
    {
        if (BroadcastIfAll(c => PlayLibEntry(e, c, skill))) return;   // b43: "All puppets" on the Stage
        // b26: chair snap. Self only (a puppet can't /sit), and only when not already sitting or riding.
        if (!skill && _config.SnapSeatedPoses && PoseLibrary.IsChairPose(e) && Self() is { } me
            && me.ObjectIndex == target.ObjectIndex && !AnimationService.IsNativelySeated(target) && !IsRidingNow(target)
            && _anim.PlaySelfEmote(SitEmoteId()))
        {
            _anim.AfterSeated(target, () => PlayLibEntryNow(e, target, skill));
            return;
        }
        PlayLibEntryNow(e, target, skill);
    }

    private static unsafe bool IsRidingNow(ICharacter c)
        => c.Address != nint.Zero && AnimationService.IsRiding((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)c.Address);

    // /sit's Emote row, read from the sheet by name (fallback: 50, the long-standing id).
    private ushort _sitEmote;
    private ushort SitEmoteId()
    {
        if (_sitEmote == 0)
            _sitEmote = _timeline.Emotes.FirstOrDefault(t => string.Equals(t.Name, "Sit", StringComparison.OrdinalIgnoreCase)) is { } t
                ? (ushort)t.EmoteRowId : (ushort)50;
        return _sitEmote;
    }

    private void PlayLibEntryNow(LibEntry e, ICharacter target, bool skill)
    {
        // b30 diagnostic (race-glamour bug): which body the pose was played on and where its animation file comes from.
        var body = PoseLibrary.BodyOf(target);
        _log.Information($"Anim[lib] {e.Name} ({e.Key} {e.Id}) on obj#{target.ObjectIndex} body c{body:D4}, chain [{string.Join(",", _poses.BodyChain(body))}], has={_poses.HasAnimation(body, e.Id)}");
        if (skill) HandRestore(target);
        else { _lastPose = e; ApplyHand(target, e); }
        switch (e.Play)
        {
            case PosePlay.Once when HoldsEnding(e) && _poses.ClipSeconds(e) is > 0.1f and var secs:
                _anim.PlayHoldEnd(target, e.Id, secs); ReportHeldLoop(target, e.Id, e.Id); break;   // b34: peers hold it too
            case PosePlay.Hold:      _anim.Loop(target, e.Id);                   ReportHeldLoop(target, e.Id);     break;
            case PosePlay.IntroHold: _anim.PlaySequence(target, e.Id, e.HoldId); ReportHeldLoop(target, e.HoldId, e.Id); break;
            default:                 _anim.PlayOnce(target, e.Id);               ReportOneShot(target, e.Id);      break;
        }
    }

    // Provenance split for the Common timeline pile (single-select, wraps). These 9 buckets (+All, +Other
    // residue) mirror the real key-path clusters the offline survey found, so a chip tells the DM WHERE an
    // animation comes from — Cast (spell poses), Craft (Ishgard-Restoration saw/hammer + Cosmic crafting +
    // farming), Item (use gestures), Interact (world/object gestures + duty gadgets), Combat (weapon swings),
    // Monster (generic mon_sp slots), Idle (freeze/stance poses), Move (locomotion), React (hit reactions).
    // Order fronts the playable, weapon-independent buckets; the two junk buckets (Move/React) sit last and
    // are hidden by default on human guises (see IsPlayable + the "Show all timelines" escape).
    private static readonly (string text, string key)[] AnimGroupChips =
    {
        ("All", "All"), ("Cast", "Cast"), ("Craft", "Craft"), ("Item", "Item"),
        ("Interact", "Interact"), ("Combat", "Combat"), ("Monster", "Monster"),
        ("Idle", "Idle"), ("Move", "Move"), ("React", "React"), ("Other", "Other"),
    };

    /// <summary>Bucket a Common timeline into one of the 9 provenance categories by its key path. Ordering
    /// is FIRST-MATCH-WINS and deliberate: monster generic slots and the carry family are claimed before the
    /// broad "action"/"wks" keywords could steal them, and Craft's wks/hwd_fate check precedes Interact's
    /// "action" so <c>event_action_wks4_end</c> (Cosmic crafting) files under Craft, not Interact. Validated
    /// offline over all 461 Common rows (Other residue = 3: guildleave/dejon/warp). Skeleton-AGNOSTIC — it
    /// describes what the animation IS, independent of whether the guised skeleton can play it (that "works
    /// vs dummy" fact is the separate caps trim via ValidCommonFor).</summary>
    private static string ProvenanceCategory(string key)
    {
        bool Has(string s) => key.Contains(s, StringComparison.Ordinal);
        if (Has("mon_sp")) return "Monster";                       // generic per-monster special slots (false-friend zone)
        if (Has("carry")) return "Interact";                       // keep the whole carry family together (incl. carry_wks)
        if (Has("hwd_fate") || Has("wks") || Has("farm") || Has("craft"))
            return "Craft";                                        // Ishgard Restoration + Cosmic Exploration + farming
        if (Has("magic") || Has("barrier")) return "Cast";         // spell cast poses (the 28 named motifs/casts live here)
        if (Has("item")) return "Item";                            // eat/drink/use + event_item bombs
        if (Has("cannon") || Has("aettouch") || Has("callpet") || Has("decifer")
            || Has("action") || Has("search") || Has("mater") || Has("throw")
            || Has("treasure") || Has("firecracker")) return "Interact";
        if (Has("auto") || Has("attack") || Has("weapon") || Has("attach")) return "Combat";
        if (Has("walk") || Has("run") || Has("sprint") || Has("dash") || Has("turn") || Has("jump")
            || Has("move") || Has("swim") || Has("telepo")) return "Move";
        if (Has("damage") || Has("knockback") || Has("guard") || Has("hit") || Has("blow")
            || Has("fall") || Has("revive") || Has("partsbreak")) return "React";
        if (Has("idle") || Has("battle_start") || Has("battle_end") || Has("dead")
            || Has("pose") || Has("sync") || Has("stand")) return "Idle";
        return "Other";
    }


    /// <summary>
    /// Fire a timeline on the target per the Loop toggle and the row's nature — the single routing point
    /// the Animations tab goes through (self or the driven puppet):
    ///  - a resident-special HOLD pose (mon_sp_X_loop, which only renders while held) always holds via
    ///    <see cref="AnimationService.Loop"/> (BaseOverride); replaying it would flicker a frame at a time;
    ///  - otherwise, with Loop CHECKED, <see cref="AnimationService.LoopReplay"/> replays the WHOLE
    ///    timeline start→end and repeats it (the Galatea open-arms fix), instead of the truncated base-lane
    ///    loop the old path produced;
    ///  - with Loop unchecked, a single <see cref="AnimationService.PlayOnce"/>.
    /// </summary>
    private void TriggerTimeline(TimelineRow t, ICharacter target)
    {
        if (BroadcastIfAll(c => TriggerTimeline(t, c))) return;   // b43: "All puppets" on the Stage
        var id = (ushort)t.Id;

        // An emote plays through the emote-aware path, which threads three things the raw clip id can't carry: the
        // emote's INTRO clip (ActionTimeline[1], so the pose BLENDS in instead of snapping), the weapon stance the
        // Emote sheet performs it with, and its CharacterMode (persistent emotes like /atease carry a ConditionMode
        // that SetMode must hold, or the resolver snaps back to idle after the intro). Three dispatch paths, one per
        // actor kind — the emote must "behave as the char's own" regardless (DM directive):
        //   • SELF (incl. a disguised local player): the game's OWN emote-execution call (native ExecuteEmote via
        //     PlaySelfEmote). The local player HAS a native input loop, so this makes the emote behave EXACTLY as if
        //     the DM typed the slash command — full intro/loop/hold fidelity, disguise reskin rendering over it. No
        //     HDM peer report: HMS observes the DM's own EmoteController and syncs it to peers faithfully (with the
        //     intro), which HDM's lossy one-shot report (a bare timeline id) never could.
        //   • The actively-possessed puppet: folded into the possession drive loop via RequestEmote (a direct
        //     PlayEmote would be stomped by the per-frame DriveLocomotion next frame).
        //   • Any other guised actor: plays directly via PlayEmote.
        if (t.Cat == "emote" && Self() is { } s)
        {
            if (target.ObjectIndex == s.ObjectIndex)
            {
                _anim.PlaySelfEmote(t.EmoteRowId);   // behaves as the char's own; HMS syncs it to peers
                return;
            }
            if (_possession.IsPossessing && _possession.PossessedIndex == target.ObjectIndex)
                _possession.RequestEmote(id, t.EmoteIntro, t.DrawsWeapon, t.EmoteConditionMode, t.EmoteModeParam);
            else
                _anim.PlayEmote(target, id, t.EmoteIntro, t.DrawsWeapon, t.EmoteConditionMode, t.EmoteModeParam);
            ReportOneShot(target, id);
            return;
        }

        if (IsResidentSpecialLoop(t.Key)) { _anim.Loop(target, id);       ReportHeldLoop(target, id); }
        else if (_loopMode)               { _anim.LoopReplay(target, id); ReportHeldLoop(target, id); }
        else                              { _anim.PlayOnce(target, id);   ReportOneShot(target, id); }
    }

    private void DrawTimelineButtons(IReadOnlyList<TimelineRow> rows, ICharacter? target)
    {
        var f = _animFilter.Trim();
        var shown = 0;
        foreach (var t in rows)
        {
            if (f.Length > 0
                && !t.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                && !t.Key.Contains(f, StringComparison.OrdinalIgnoreCase))
                continue;
            if (++shown > AnimMaxRows) break;

            // A generic resident-special LOOP row (battle/mon_sp_X_loop -> cbbm_sp_X_2lp) is a
            // base-type pose that only renders while HELD. Played once it blends in for a frame
            // and the game immediately blends back to idle, so the user sees "only the first
            // frame." Those always HOLD (BaseOverride) regardless of the Loop toggle. Everything
            // else with Loop checked now REPLAYS the full timeline start→end (see TriggerTimeline)
            // rather than forcing its base lane, which fixes the awkward truncated loop on full
            // gestures (Galatea's open-arms). Intro rows and standalone casts stay one-shots.
            var forceHold = IsResidentSpecialLoop(t.Key);

            ImGui.PushID((int)t.Id);
            // One button per row; the global Loop toggle + the row's nature decide the mode (see TriggerTimeline).
            if (ImGui.Button(t.Name) && target != null)
                TriggerTimeline(t, target);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"id {t.Id} · {t.Key}\n" +
                                 (t.IsTemplate
                                     ? $"Template special (mon_sp/[SKL_ID]): the game resolves it on this skeleton.\n" +
                                       (t.HasTmb ? "Ships its effects timeline (VFX + sound).\n" : "No effects timeline: body animation only.\n")
                                     : "") +
                                 (forceHold
                                     ? "Click: hold this looping pose (Stop to end). This special only renders while held."
                                     : _loopMode
                                         ? "Click: loop the full animation start→end (Stop to end)."
                                         : "Click: play once, then blend back to idle."));
            ImGui.PopID();
        }
        if (shown == 0) ImGui.TextDisabled("(no matches)");
    }

    // A generic resident-special LOOP timeline (battle/mon_sp_{a..l}_loop). These map to the
    // model's resident cbbm_sp_X_2lp — a base-type pose that only renders while held. The intro
    // rows (…_start) are one-shots; standalone per-skeleton mon_sp/mNNNN casts don't match either.
    private static bool IsResidentSpecialLoop(string key)
        => key.StartsWith("battle/mon_sp_", StringComparison.Ordinal)
           && key.EndsWith("_loop", StringComparison.Ordinal);

    /// <summary>Draw one in-game icon square at the cursor; returns false (drawing nothing) if the id is 0
    /// or the texture can't be resolved, so the caller can reserve aligned space. Same ITextureProvider
    /// path the in-game emote window uses (proven in HMS).</summary>
    private bool DrawEmoteIcon(uint icon, float size)
    {
        if (icon > 0
            && _textures.TryGetFromGameIcon(new GameIconLookup(icon), out var tex)
            && tex.TryGetWrap(out var wrap, out _))
        {
            ImGui.Image(wrap.Handle, new Vector2(size, size));
            return true;
        }
        return false;
    }

    // ---- helpers -------------------------------------------------------------

    // HDM's chip accent now comes from the shared theme palette (AccentPalette.Primary): HDM's own accent
    // (gold by default), or HM-Sync's when sync is on and HMS exposes it over IPC. Set in the Config tab.
    // (Was a hard-coded slate-blue before 0.8.70, when HDM had no theming config.)

    // Slate tint for a roster-inferred (heuristic) catalog name — distinguishes it at a glance
    // from a crowdsourced name without spending a column.
    private static readonly Vector4 HeuristicNameTint = new(0.62f, 0.74f, 0.92f, 1f);

    // The Duty-Finder-style category chips. Key == the ContentIndex category to match, except:
    // "All" clears the filter, "Minion" matches summonable minions (CompanionIndex), "Unknown" matches
    // rows with no resolved home territory, and "Solo" folds both "Solo Instances" and "Solo Duty".
    // Order mirrors the in-game Duty Finder reading order (CategoryRank), with All at the head and the
    // three home-less classes (Event NPC, Minion, then Unknown) as the tail bookends.
    private static readonly (string text, string key)[] CategoryChips =
    {
        ("All", "All"), ("World", "World"), ("City", "City"), ("Inn", "Inn"), ("Housing", "Housing"),
        ("Dungeon", "Dungeon"), ("Trial", "Trial"), ("Raid", "Raid"), ("Deep Dungeon", "Deep Dungeon"),
        ("V&C", "Variant & Criterion"), ("Solo", "Solo"), ("PvP", "PvP"), ("Gold Saucer", "Gold Saucer"),
        ("NPCs", "EventNpc"), ("Minion", "Minion"), ("Mounts", "Mount"), ("Unknown", "Unknown"),
    };

    /// <summary>Draw a single-select row of <see cref="Chip"/>s that wraps to the next line when the
    /// next chip would overflow the content region (the category row is too wide for one line at the
    /// minimum window width). Returns the clicked key, or null if nothing was clicked this frame.</summary>
    private string? WrappedChips(IReadOnlyList<(string text, string key)> chips, string activeKey, string idPrefix)
    {
        string? clicked = null;
        var style = ImGui.GetStyle();
        var rightEdge = ImGui.GetWindowPos().X + ImGui.GetContentRegionMax().X;
        for (var i = 0; i < chips.Count; i++)
        {
            var (text, key) = chips[i];
            if (Chip(text, $"{idPrefix}_{key}", key == activeKey)) clicked = key;
            if (i + 1 < chips.Count)
            {
                // Keep the next chip on this line only if it fits; otherwise let it fall to the next.
                var nextW = ImGui.CalcTextSize(chips[i + 1].text).X + style.FramePadding.X * 2f;
                var nextX = ImGui.GetItemRectMax().X + style.ItemSpacing.X + nextW;
                if (nextX < rightEdge) ImGui.SameLine();
            }
        }
        return clicked;
    }

    /// <summary>A single-select pill button (HMS-style): filled accent + bright ink when active,
    /// dark + muted when not. <paramref name="id"/> namespaces it so same-labelled chips in
    /// different rows don't collide. Returns true on click.</summary>
    private bool Chip(string label, string id, bool active)
    {
        var acc = _accent.Primary;
        ImGui.PushStyleColor(ImGuiCol.Button,        active ? acc                        : new Vector4(0.15f, 0.16f, 0.19f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, active ? Lighten(acc, 1.15f)         : new Vector4(0.24f, 0.25f, 0.29f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.30f, 0.34f, 0.40f, 1f));
        // Auto-contrast ink on the active chip (dark on a light accent like gold, light on a dark one) so the
        // label stays legible whatever accent HDM/HM-Sync is set to — matches HMS's PrimaryButton.
        ImGui.PushStyleColor(ImGuiCol.Text,          active ? AccentPalette.TextOn(acc)  : new Vector4(0.70f, 0.73f, 0.78f, 1f));
        var clicked = ImGui.Button($"{label}##{id}");
        ImGui.PopStyleColor(4);
        return clicked;
    }

    private static Vector4 Lighten(Vector4 c, float f) =>
        new(Math.Min(c.X * f, 1f), Math.Min(c.Y * f, 1f), Math.Min(c.Z * f, 1f), c.W);

    private static void PushRed()
    {
        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.42f, 0.16f, 0.16f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.72f, 0.22f, 0.22f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.85f, 0.28f, 0.28f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(1f, 0.92f, 0.92f, 1f));
    }

    private static void PopColors() => ImGui.PopStyleColor(4);

    // ── Window-scope accent (0.8.70) ─────────────────────────────────────────────────────────────────────
    // Number of style colours PushWindowAccent pushes; PopWindowAccent MUST pop exactly this many.
    private const int WindowAccentColorCount = 10;

    /// <summary>Tint the window's semantic accent slots from the live palette (called at the top of
    /// <see cref="Draw"/>; pop with <see cref="PopWindowAccent"/>). Tabs use DARKENED accents and
    /// headers/selection use TRANSLUCENT accents so light ImGui text stays legible on them whatever accent is
    /// picked; checkmark / slider grabs (no text sits on them) take the accent directly. Plain Buttons are
    /// deliberately left alone — accent is for selection/action STATE, not every button (mirrors HMS).</summary>
    private void PushWindowAccent()
    {
        var acc = _accent.Primary;
        ImGui.PushStyleColor(ImGuiCol.Tab,              AccentPalette.Darken(acc, 0.30f));
        ImGui.PushStyleColor(ImGuiCol.TabHovered,       AccentPalette.Darken(acc, 0.55f));
        ImGui.PushStyleColor(ImGuiCol.TabActive,        AccentPalette.Darken(acc, 0.70f));
        ImGui.PushStyleColor(ImGuiCol.Header,           AccentPalette.Alpha(acc, 0.28f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered,    AccentPalette.Alpha(acc, 0.45f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive,     AccentPalette.Alpha(acc, 0.65f));
        ImGui.PushStyleColor(ImGuiCol.CheckMark,        AccentPalette.Lighten(acc, 1.10f));
        ImGui.PushStyleColor(ImGuiCol.SliderGrab,       acc);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, AccentPalette.Lighten(acc, 1.20f));
        ImGui.PushStyleColor(ImGuiCol.TextSelectedBg,   AccentPalette.Alpha(acc, 0.35f));
    }

    private static void PopWindowAccent() => ImGui.PopStyleColor(WindowAccentColorCount);

    // ── Config tab (0.8.70) ──────────────────────────────────────────────────────────────────────────────
    /// <summary>Theme accent (+ HM-Sync sync) and future settings. Mirrors HM-Sync's Config→Accents so the HM
    /// tool-suite shares one hue: HDM defaults to the same gold, and when HM-Sync is installed AND exposes its
    /// accent over IPC (see <see cref="HmsIpc"/>), HDM follows it. The picker edits HDM's OWN accent (the sync
    /// fallback), disabled while a live HM-Sync accent is driving.</summary>
    private void DrawConfigTab()
    {
        var swatch = new Vector2(28, 28);   // one size for EVERY accent swatch below (kept identical, Rule 2)

        ImGui.Spacing();
        ImGui.TextDisabled("Possession");
        ImGui.Separator();
        ImGui.Spacing();
        {
            var ov = _possession.OverlayEnabled;
            if (ImGui.Checkbox("Show possess dots over puppets", ref ov)) { _possession.OverlayEnabled = ov; _config.ShowPossessionDots = ov; _pi.SavePluginConfig(_config); }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("A dot over each puppet's head; click one to possess it (WASD drives, Esc releases).\n" +
                                 "The Stage's Possess button and the cast chip's right-click do the same.");
            var hidePilot = _possession.HidePilotWhileDriving;
            if (ImGui.Checkbox("Hide me while driving", ref hidePilot)) _possession.HidePilotWhileDriving = hidePilot;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("While driving a puppet, fade your own frozen body on YOUR screen only.");
            var allowOthers = _possession.AllowPossessOthers;
            if (ImGui.Checkbox("Allow possessing other players' puppets", ref allowOthers))
            {
                _possession.AllowPossessOthers = allowOthers;
                _config.AllowPossessOthersPuppets = allowOthers;
                _pi.SavePluginConfig(_config);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Off by default: you can only possess puppets you spawned. Turn on to help run someone else's event.");
        }
        ImGui.Spacing();

        ImGui.Spacing();
        ImGui.TextDisabled("Animations · experimental");
        ImGui.Separator();
        ImGui.Spacing();
        if (_poses.Porter is { } porter)
        {
            var uni = _config.UniversalPoses;
            if (ImGui.Checkbox("Universal poses: every pose on every race", ref uni))
            {
                porter.SetEnabled(uni);
                _pi.SavePluginConfig(_config);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Many NPC poses ship for only a few races (adjust cap: male Midlander and male Elezen).\n" +
                                 "On: a body without its own animation borrows the closest race's, through an in-memory\n" +
                                 "Penumbra swap, for every character on your screen (you, puppets, peers running HDM).\n" +
                                 "No files are changed; the swap goes away when HDM unloads.\n" +
                                 "Off: only poses this body has its own animation for are listed.");
            ImGui.SameLine();
            ImGui.TextDisabled(porter.Active ? $"active · {porter.Rewrites} borrowed animations" : porter.Status);
        }
        ImGui.Spacing();

        ImGui.Spacing();
        ImGui.TextDisabled("Theme · accent");
        ImGui.Separator();
        ImGui.Spacing();

        var sync = _config.SyncAccentWithHms;
        if (ImGui.Checkbox("Sync accent with HM-Sync", ref sync))
        {
            _config.SyncAccentWithHms = sync;
            _pi.SavePluginConfig(_config);
        }

        // Where the live accent is coming from right now.
        var synced = _accent.SyncedFromHms;
        var live = _accent.Primary;
        ImGui.Spacing();
        if (synced)
        {
            ImGui.ColorButton("##accentLive", live, ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoAlpha, swatch);
            ImGui.SameLine();
            ImGui.TextUnformatted("Synced from HM-Sync");
        }
        else if (sync && _accent.HmsPresent && !_accent.HmsAccentAvailable)
            ImGui.TextDisabled("HM-Sync is installed but this version doesn't expose its accent yet. Update HM-Sync. Using HDM's own accent.");
        else if (sync && !_accent.HmsPresent)
            ImGui.TextDisabled("HM-Sync not detected. Using HDM's own accent. Install HM-Sync to share its accent.");
        else
            ImGui.TextDisabled("Using HDM's own accent.");

        // HDM's own accent (the sync fallback). Editable only when a live HM-Sync accent isn't overriding it.
        ImGui.Spacing();
        ImGui.Spacing();
        var loc = _accent.Local;
        if (synced)
        {
            using (new Disabled(true))
            {
                ImGui.ColorButton("##hdmAccentSwatch", loc, ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoAlpha, swatch);
                ImGui.SameLine();
                ImGui.TextUnformatted("HDM accent (used when not syncing)");
            }
        }
        else
        {
            if (ImGui.ColorButton("##hdmAccentSwatch", loc, ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoAlpha, swatch))
                ImGui.OpenPopup("##hdmAccentPicker");
            ImGui.SameLine();
            ImGui.TextUnformatted("HDM accent");
            ImGui.SameLine();
            if (ImGui.SmallButton("Reset to gold"))
            {
                var g = AccentPalette.DefaultGold;
                _config.AccentColor = new[] { g.X, g.Y, g.Z, 1f };
                _pi.SavePluginConfig(_config);
            }
            if (ImGui.BeginPopup("##hdmAccentPicker"))
            {
                var v = loc;
                if (ImGui.ColorPicker4("##hdmAccentPick", ref v, ImGuiColorEditFlags.NoAlpha))
                {
                    _config.AccentColor = new[] { v.X, v.Y, v.Z, 1f };
                    _pi.SavePluginConfig(_config);
                }
                ImGui.EndPopup();
            }
        }

        // ── Data · monster catalog ────────────────────────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.TextDisabled("Data · monster catalog");
        ImGui.Separator();
        ImGui.Spacing();

        var harvest = _harvest.Enabled;
        if (ImGui.Checkbox("Update monster names from game spawns", ref harvest))
        {
            _harvest.Enabled = harvest;
            _config.HarvestMobNames = harvest;
            _pi.SavePluginConfig(_config);
        }
        ImGui.TextDisabled("Off by default. When on, HDM passively samples live enemies while you're in a duty\nand records their names and home instance to fill the catalog's instanced tail.");

        // ── Disguise · session ────────────────────────────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.TextDisabled("Disguise · session");
        ImGui.Separator();
        ImGui.Spacing();

        var clearOnMap = _guise.ClearDisguiseOnMapChange;
        if (ImGui.Checkbox("Clear disguises on map change", ref clearOnMap))
        {
            _guise.ClearDisguiseOnMapChange = clearOnMap;
            _config.ClearDisguisesOnMapChange = clearOnMap;
            _pi.SavePluginConfig(_config);
        }
        ImGui.TextDisabled("Off by default, so a disguise stays on across a zone line (walk between areas in character).\nWhen on, your own look, scale, and elevation revert at every map change, to re-apply by hand.\nLogging out always reverts either way.");
    }

    public void Dispose()
    {
        _anim.SelfOneShotEnded -= OnSelfOneShotEnded;
        _anim.SelfReleasedByMovement -= OnSelfReleasedByMovement;
    }

    /// <summary>Tiny RAII helper for ImGui.BeginDisabled/EndDisabled.</summary>
    private readonly struct Disabled : IDisposable
    {
        public Disabled(bool disabled) => ImGui.BeginDisabled(disabled);
        public void Dispose() => ImGui.EndDisabled();
    }
}
