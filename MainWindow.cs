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
public sealed class MainWindow : Window, IDisposable
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
    private const int InternalBuild = 10;
#endif

    private readonly MobIndex _index;
    private readonly TimelineIndex _timeline;
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
    private string _animGroup = "All"; // Animations tab: which Common-timeline semantic group is shown
    private bool _loopMode;            // Animations tab: named-timeline buttons loop (true) vs play once (false)
    // Animation tab: the animation SUBJECT — false = YOU, true = the Spawn tab's focused puppet. The
    // target-generic funnels let one selection drive either; forced back to self when no puppet is spawned.
    // Surfaces a spawned humanoid puppet's emotes.
    private bool _animApplyToPuppet;
    private bool _showAllTimelines;    // Animations tab: escape hatch — show the full Common pile instead of
                                       // trimming it to what the selected skeleton's .pap set can actually play
    private int? _animPlayableCount;   // Animations tab: cached count of playable (non-Move/React) Common rows
    // Animations tab (2a redesign): group expand-state, held by us now that the sections use HmUi.GroupHeader
    // (right-aligned meta + custom triangle) instead of ImGui.CollapsingHeader (which persisted its own state).
    private bool _commonOpen = true;
    private bool _specialsOpen = true;
    private bool _emotesOpen = true;   // Animations tab: Emotes group (human guises only) — default open
    private bool _combosOpen = true;   // Animations tab: Combos group (compound intro+pose gestures) — default open
    private bool _advancedOpen;        // raw-timeline-id group: default CLOSED — the prominent red "Reset to
                                       // Normal" card is now the primary unstick, so the raw Stop needn't sit open.
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

    // Spawn Management tab state.
    //  _spawnFilter    — the in-tab mob search box, so the DM can spawn WITHOUT switching to the Catalog.
    //  _puppetTimelineId — the SHARED timeline id the "…all" buttons fan across every puppet (the overhead
    //                    the DM asked to keep alongside per-puppet control).
    //  _puppetTid      — each puppet's OWN timeline id (its per-row Play/Loop), defaulting to the shared id.
    //  _puppetSpeed    — each puppet's OWN animation speed for its slider (AnimationService has no getter;
    //                    Freeze pins 0, this mirrors it). Both dicts keyed by GLOBAL puppet index.
    //  _puppetScale    — each puppet's DIALED scale override. PRESENCE is the flag: a key here means "the DM
    //                    dialed a custom size for this puppet", so its Apply re-guise forces that size instead
    //                    of the global scale mode (the live GameObject.Scale is the value, this is the intent).
    //  _puppetScaleAll — the aggregate slider's own value ("set every puppet to this size"); not per-puppet.
    //  _puppetGuise    — what each puppet WEARS (last mob applied), for the list. The per-index dicts are
    //                    pruned to live puppets each draw (PrunePuppetLabels).
    private string _spawnFilter = string.Empty;
    private int _puppetTimelineId = 3; // 3 = idle/normal — the shared "apply to all" id
    private readonly Dictionary<ushort, MobRow> _puppetGuise = new();
    private readonly Dictionary<ushort, int> _puppetTid = new();
    private readonly Dictionary<ushort, float> _puppetSpeed = new();
    private readonly Dictionary<ushort, float> _puppetScale = new();
    private float _puppetScaleAll = 1.0f;
    // Roster selection: the ONE puppet the live surface edits (mockup 3b "roster + one live surface"). Sentinel
    // ushort.MaxValue = nothing picked yet; ResolveRosterSelection re-homes it to the first live puppet when
    // the pick is stale (despawn / zone change), so the surface never edits a dead index.
    private ushort _selectedPuppet = ushort.MaxValue;
    // Roster height (px). 0 = auto (size to the puppet count, clamped). A drag-splitter below the roster child
    // writes a concrete value here so the DM can grow the list to see many puppets or shrink it to reclaim room
    // for the live surface — the one roster dimension the layout used to forbid (width already stretches with
    // the resizable window). Session-only (not persisted); a re-drag is cheap and the default auto-fit is sane.
    private float _rosterHeight;
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
    private LocNode? _treeMinionNode;
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

    public MainWindow(MobIndex index, TimelineIndex timeline, ContentIndex content, TerritoryIndex territory,
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
        _index = index; _timeline = timeline; _content = content; _territory = territory; _manual = manual; _stems = stems; _instanced = instanced; _lore = lore; _level = level; _webloc = webloc; _companion = companion; _enpcLoc = enpcLoc; _harvest = harvest;
        _guise = guise; _humanGuise = humanGuise; _anim = anim; _spawn = spawn; _possession = possession; _ipc = ipc; _moniker = moniker; _config = config;
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
        if (TrySyncSubject(actor, out var slot)) _ipc.ReportAction(slot, id);
    }

    /// <summary>Emit a held-loop STATE change for the subject (0 clears). No-op on an undisguised subject
    /// (HdmIpc drops a loop with no disguise to attach it to). Both a BaseOverride hold and a full-timeline
    /// replay report their id here — the wire atom carries only LoopId (§3), so a replay-loop currently
    /// renders as a hold on the receiver; exact replay fidelity would need a loop-kind bit (deferred).</summary>
    private void ReportHeldLoop(ICharacter actor, ushort id)
    {
        if (TrySyncSubject(actor, out var slot)) _ipc.ReportLoop(slot, id);
    }

    /// <summary>THE single "stop the held/looping animation" funnel (Rule 1). Blends the actor back to idle
    /// AND clears the held-loop state on peers (ReportHeldLoop 0 → receiver Sanitises). Every Stop button —
    /// Overview "Stop all", the per-actor list Stop, the Favourites Stop, and the Advanced raw Stop — routes
    /// through here so none can drift out of sync again (the Favourites Stop used to omit the peer report,
    /// leaving peers stuck in the last Special).</summary>
    private void StopAnim(ICharacter target)
    {
        _anim.Stop(target);
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

    public override void Draw()
    {
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
            if (ImGui.BeginTabItem("Animations"))
            {
                DrawAnimTab();
                ImGui.EndTabItem();
            }
            // Spawn Management: the DM's set-piece workshop — spawn blank puppets, then per-puppet disguise /
            // move / rotate / animate them. Sits after Animations (the self-apply workflow). Starred favourites
            // are pinned as fixtures at the top of its spawn catalog (the old Favourites tab, merged in).
            if (ImGui.BeginTabItem("Spawn"))
            {
                DrawSpawnTab();
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
                if (HmUi.AccentButton(hidden ? "Unhide" : "Hide", "hide", hidden, acc, bw)) _guise.SetHidden(self, !hidden);
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
                       "That's the Spawn tab's focused puppet; select a different roster row there to retarget.\n" +
                       "Spawned puppets are non-targetable, so this is how you re-skin one in place."
                    : _selected is null
                        ? "Pick a catalog row first (a plain click selects it), then disguise the focused puppet."
                        : "Spawn a puppet first — its Spawn-tab roster row picks which one is focused.");
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
            if (r.Source == NpcSource.Event) npcs++; else mobs++;
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
            if (_index.TryGetByBase(tgt.BaseId, out var known))
            {
                var hidden = !IsRenderable(known);
                // Show a real or roster-inferred name; for a still-nameless base, fall back to a name the
                // harvester saw live (Tier A3) before collapsing to "(unnamed)" — the direct payoff of
                // walking a duty: the white YoRHa androids read their real name the moment they're sighted.
                var label = known.Name.Length > 0 || known.NameIsHeuristic
                    ? known.DisplayName
                    : _harvest.TryGetName(tgt.BaseId, out var liveName) ? $"{liveName} (seen live)" : "(unnamed)";
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

    private void DrawSpawnTab()
    {
        var self = Self();

        // ── Spawn a mob straight from here — no trip to the Catalog tab (the DM's ask). ──
        DrawSpawnList(self);

        // ── Quick spawn: a plain dummy, or the current catalog/favourite selection ──
        // Boxed in a padded panel and drawn with the shared Chip pill (not a bare ImGui.Button) so the
        // spawn actions read with the same rounded, padded gloss as the rest of the suite (UI-tidiness rule).
        using (HmUi.Panel("QUICK SPAWN"))
        {
            using (new Disabled(self == null))
            {
                if (Chip("Spawn blank dummy", "spawnblank", false)) SpawnBlankPuppet();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Drop a plain, non-targetable puppet a step ahead of you,\n" +
                                 "then disguise, move, rotate, freeze and animate it from its row below.");

            if (_selected is { } sel)
            {
                ImGui.SameLine();
                if (Chip($"Spawn as {sel.DisplayName}", "spawnassel", self != null) && self != null)
                    SpawnSelected();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Spawn a puppet already wearing {sel.DisplayName} (Base {sel.BaseId}).");
            }
        }

        if (_spawn.Count == 0)
        {
            DrawSpawnEmptyState();
            return;
        }

        PrunePuppetLabels();

        // ── Broadcast row: ONE timeline id fanned across EVERY puppet (the "overhead" the DM asked to keep),
        // plus Despawn all. Per-puppet rows still carry their OWN id + Play/Loop/Stop below. ──
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("All puppets · timeline:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(70);
        ImGui.InputInt("##allid", ref _puppetTimelineId, 0);
        ImGui.SameLine();
        using (new Disabled(_puppetTimelineId is <= 0 or > ushort.MaxValue))
        {
            if (ImGui.Button("Play all")) BroadcastAnim(a => { _anim.PlayOnce(a, (ushort)_puppetTimelineId); ReportOneShot(a, (ushort)_puppetTimelineId); });
            ImGui.SameLine();
            if (ImGui.Button("Loop all")) BroadcastAnim(a => { _anim.Loop(a, (ushort)_puppetTimelineId); ReportHeldLoop(a, (ushort)_puppetTimelineId); });
        }
        ImGui.SameLine();
        PushRed();
        var stopAll = ImGui.Button("Stop all");
        PopColors();
        if (stopAll) BroadcastAnim(StopAnim); // funnel: blends to idle + clears held loop on peers
        ImGui.SameLine();
        PushRed();
        if (ImGui.Button($"Despawn all ({_spawn.Count})")) { _spawn.DespawnAll(); _puppetGuise.Clear(); }
        PopColors();

        // ── Aggregate scale: fan ONE absolute multiplier across every live puppet through the shared
        // ApplyScaleTo funnel. Live transform write per drag (cheap, no redraw — non-monster puppets resize as
        // you drag; a built monster bakes on its next Apply, same as the self knob). Marks each puppet "dialed"
        // (_puppetScale) so its individual slider + Apply pick the size up. Synced to peers once each on RELEASE,
        // where the receiver drives the delta live (no redraw); a per-frame emit would just spam epochs. ──
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("All puppets · scale:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderFloat("##allscale", ref _puppetScaleAll, 0.1f, 10f, "x%.2f"))
            BroadcastAnim(a => { _puppetScale[a.ObjectIndex] = _puppetScaleAll; ApplyScaleTo(a, _puppetScaleAll, commit: false); });
        if (ImGui.IsItemDeactivatedAfterEdit())
            BroadcastAnim(a => ApplyScaleTo(a, _puppetScaleAll, commit: true));
        ImGui.SameLine();
        if (ImGui.SmallButton("1.0x##allscale"))
        {
            _puppetScaleAll = 1f;
            BroadcastAnim(a => { _puppetScale[a.ObjectIndex] = 1f; ApplyScaleTo(a, 1f, commit: true); });
        }

        ImGui.Separator();

        // ── Possession (Task #40): the world-dot toggle + a compact status line. The per-puppet Possess/Release
        // button now lives in the live surface below (the DM's ask for a menu control, not just the world dot). ──
        DrawPossessionControls();

        ImGui.Separator();

        // ── Roster + ONE live surface (mockup 3b): pick a puppet in the roster, then edit its transform +
        // animation in a single surface whose height never grows with the puppet count — the old design stacked
        // a full control block per puppet and grew unbounded. ResolveRosterSelection keeps the pick valid. ──
        _selectedPuppet = ResolveRosterSelection();
        DrawRoster();
        ImGui.Separator();
        if (_spawn.IsSpawned(_selectedPuppet))
            DrawLiveSurface(_selectedPuppet);
        else
            ImGui.TextDisabled("Pick a puppet from the roster to edit its transform and animation here.");
    }

    /// <summary>Keep the roster selection valid: the current pick if it's still a live puppet, else the first
    /// spawned puppet (so the live surface auto-focuses something after a despawn / zone change), else the
    /// sentinel when nothing is spawned. Called once per Spawn-tab draw before the roster + surface render.</summary>
    private ushort ResolveRosterSelection()
    {
        if (_spawn.IsSpawned(_selectedPuppet)) return _selectedPuppet;
        var s = _spawn.Spawned;
        return s.Count > 0 ? s[0] : ushort.MaxValue;
    }

    /// <summary>The roster (mockup 3b): one selectable row per live puppet — a status dot (gold = being driven,
    /// green = idle), the puppet's name + obj#, and its worn-guise chip. Clicking a row selects it as the one
    /// puppet the live surface below edits. A drag-resizable scrolling child (grip below; auto-fits the puppet
    /// count until the DM drags it), so a dozen puppets never grows the tab uninvited. Snapshots the index list
    /// — a despawn from elsewhere can't invalidate the walk.</summary>
    private void DrawRoster()
    {
        var spawned = _spawn.Spawned;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"Roster · {spawned.Count}");

        // A bit more breathing room inside the roster list than the compact default (the DM's ask): roomier
        // interior padding + row spacing. Pushed only around the child so the "Roster · N" header keeps its
        // default metrics; lineH is read AFTER the push so the auto-height accounts for the wider row spacing.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 8f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,   new Vector2(8f, 6f));

        var lineH = ImGui.GetTextLineHeightWithSpacing();
        var rows  = Math.Clamp(spawned.Count, 1, 6);
        var padY  = ImGui.GetStyle().WindowPadding.Y;              // reflects the push above
        var autoH = rows * lineH + padY * 2f + 4f;                 // auto-fit the puppet count (1..6 rows)
        var minH  = lineH * 2f + padY * 2f;                        // never collapse below ~2 rows
        var maxH  = lineH * 16f;                                   // nor eat the whole tab
        var h     = _rosterHeight > 0f ? Math.Clamp(_rosterHeight, minH, maxH) : autoH;
        ImGui.BeginChild("##roster", new Vector2(0, h), true);
        foreach (var idx in spawned.ToList())
        {
            var obj = _objects[idx];
            var name = obj?.Name.TextValue;
            var worn = _puppetGuise.TryGetValue(idx, out var w) ? w.DisplayName : null;
            var possessed = _possession.IsPossessing && _possession.PossessedIndex == idx;

            ImGui.PushID(idx);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(possessed ? new Vector4(1f, 0.75f, 0.20f, 1f) : new Vector4(0.35f, 0.85f, 0.40f, 1f), "●");
            ImGui.SameLine();
            var label = string.IsNullOrEmpty(name) ? $"puppet  ·  obj#{idx}" : $"{name}  ·  obj#{idx}";
            if (!string.IsNullOrEmpty(worn)) label += $"   ·  {worn}";
            if (possessed) label += "   (driving)";
            if (ImGui.Selectable(label, _selectedPuppet == idx))
                _selectedPuppet = idx;
            ImGui.PopID();
        }
        ImGui.EndChild();
        ImGui.PopStyleVar(2);

        // Drag-splitter: resize the roster HEIGHT by dragging this thin grip. The roster's width already
        // stretches with the freely-resizable window; height was the one dimension the layout pinned (a hard
        // 6-row clamp), so the DM can now grow the list to eyeball many puppets or shrink it to give the live
        // surface below more room. Accumulates the per-frame vertical mouse delta into _rosterHeight (seeded
        // from the auto height on first drag, clamped to [minH, maxH]).
        ImGui.InvisibleButton("##rosterSplit", new Vector2(ImGui.GetContentRegionAvail().X, 6f));
        var gripHot = ImGui.IsItemHovered() || ImGui.IsItemActive();
        if (gripHot) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNs);
        if (ImGui.IsItemActive())
        {
            var basis = _rosterHeight > 0f ? _rosterHeight : autoH;
            _rosterHeight = Math.Clamp(basis + ImGui.GetIO().MouseDelta.Y, minH, maxH);
        }
        var rmin = ImGui.GetItemRectMin();
        var rmax = ImGui.GetItemRectMax();
        var cy   = (rmin.Y + rmax.Y) * 0.5f;
        var grip = ImGui.GetColorU32(gripHot ? ImGuiCol.SeparatorActive : ImGuiCol.Separator);
        ImGui.GetWindowDrawList().AddLine(new Vector2(rmin.X + 4f, cy), new Vector2(rmax.X - 4f, cy), grip, 2f);
    }

    /// <summary>Task #40 possession controls on the Spawn tab: the blue-dot overlay toggle, the current
    /// possession status + a Release button, and a one-line key hint. All the heavy lifting (camera retarget,
    /// movement freeze, WASD drive, overlay draw + click-to-possess) lives in <see cref="PossessionService"/>;
    /// this is the thin, re-homeable UI surface (collision-safe against the concurrent MainWindow redesign).</summary>
    private void DrawPossessionControls()
    {
        var ov = _possession.OverlayEnabled;
        if (ImGui.Checkbox("Show possess dots", ref ov)) { _possession.OverlayEnabled = ov; _config.ShowPossessionDots = ov; _pi.SavePluginConfig(_config); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Float a blue dot over each puppet's head. Click a dot to possess that puppet:\n" +
                             "the camera orbits onto it and WASD drives it. The dot hides while you drive it;\n" +
                             "press Esc, or hit Release (here or on the puppet's surface), to let go.");

        ImGui.SameLine();
        var hidePilot = _possession.HidePilotWhileDriving;
        if (ImGui.Checkbox("Hide me while driving", ref hidePilot)) _possession.HidePilotWhileDriving = hidePilot;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("While driving, fade your own frozen body to invisible — on YOUR screen only — so you\n" +
                             "don't see yourself running in place. Your body reappears the instant you release.\n" +
                             "The puppet moves either way; this is purely what you see locally.");

        var allowOthers = _possession.AllowPossessOthers;
        if (ImGui.Checkbox("Allow possessing others' puppets", ref allowOthers))
        {
            _possession.AllowPossessOthers = allowOthers;
            _config.AllowPossessOthersPuppets = allowOthers;
            _pi.SavePluginConfig(_config);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Off by default: you can only possess puppets YOU spawned. A puppet another player\n" +
                             "spawned appears in your world as a mirror, and its control stays with them.\n" +
                             "Turn this on to drive someone else's puppet too — useful when helping run their event.");

        if (_possession.IsPossessing)
        {
            var pi = (ushort)_possession.PossessedIndex;
            var label = _puppetGuise.TryGetValue(pi, out var g) ? $"{g.DisplayName} (obj#{pi})" : $"obj#{pi}";
            PushRed();
            var release = ImGui.Button("Release##possess");
            PopColors();
            if (release) _possession.Release();
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.20f, 1f), $"Driving {label}.  WASD move · Space up · Ctrl down · Esc release.");
        }
        else
        {
            ImGui.TextDisabled("not possessing");
        }
    }

    /// <summary>In-tab mob picker: a search box + a compact scrolling result list, each row a one-click
    /// Spawn — so the DM never has to switch to the Catalog to place a set-piece. Clicking a result also
    /// SELECTS it (sets <see cref="_selected"/>), keeping the per-puppet "Apply" and the Catalog in sync
    /// with the last mob picked here. Matches renderable rows on name or BaseId, capped for a light list.</summary>
    private void DrawSpawnList(ICharacter? self)
    {
        if (!ImGui.CollapsingHeader("Spawn a mob (favourites + search)", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("##spawnfilter", "search name or BaseId…", ref _spawnFilter, 64);
        if (_spawnFilter.Length > 0)
        {
            ImGui.SameLine(0, 4);
            if (ImGui.Button("X##clearspawnfilter")) _spawnFilter = string.Empty;
        }

        // Starred favourites, pinned as permanent fixtures at the top of the spawn catalog (the merged-away
        // Favourites tab). Resolve the saved ids to renderable rows and order them by a strict total key —
        // name, then BaseId — so a curated shelf reads A→Z and never reshuffles (the same tiebreaker the
        // Location tree now uses). Shown always, independent of the search box.
        var favRows = new List<MobRow>();
        foreach (var id in _config.Favorites)
            if (_index.TryGetByBase(id, out var fr) && IsRenderable(fr)) favRows.Add(fr);
        favRows.Sort(static (a, b) =>
        {
            var c = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.BaseId.CompareTo(b.BaseId);
        });

        const int cap = 60;
        var q = _spawnFilter.Trim();
        // Search matches EXCLUDING anything already pinned above, so a starred mob shows once (pinned), not twice.
        var matches = MatchSpawnRows(q, cap + 1, _config.Favorites);
        var truncated = matches.Count > cap;
        if (truncated) matches.RemoveAt(matches.Count - 1);

        ImGui.BeginChild("##spawnresults", new Vector2(0, favRows.Count > 0 ? 240 : 160), true);

        if (favRows.Count > 0)
        {
            ImGui.TextDisabled($"Favourites · {favRows.Count}");
            foreach (var r in favRows)
                DrawFavouriteChip(r, self);
            ImGui.Separator();
        }

        if (matches.Count == 0)
        {
            ImGui.TextDisabled(q.Length == 0
                ? (favRows.Count > 0 ? "Type to search the rest of the catalog." : "Type to search the catalog.")
                : "No renderable mob matches.");
        }
        else
        {
            foreach (var r in matches)
                DrawSpawnRow(r, self);
            if (truncated)
                ImGui.TextDisabled($"…more than {cap} matches. Refine the search.");
        }
        ImGui.EndChild();
    }

    /// <summary>One SEARCH-MATCH row in the Spawn list (favourites render as pretty chips via
    /// <see cref="DrawFavouriteChip"/>; a 60-row search stays compact here rather than a wall of tall chips): a ★
    /// star toggle (curate the pinned favourites shelf without leaving this tab — same add/remove + save +
    /// filter-invalidate as the Catalog's star, so both stay in sync), the Spawn button (clones a puppet already
    /// wearing this row via the shared SpawnPuppetAs funnel), and a click-to-select label that sets the browse
    /// cursor for the per-puppet "Apply {name}" action. PushID(BaseId) namespaces the widgets so rows that share a
    /// Name don't collide — and since favourites are excluded from the search matches, a given BaseId appears at
    /// most once per frame, so no id clash.</summary>
    private void DrawSpawnRow(MobRow r, ICharacter? self)
    {
        ImGui.PushID((int)r.BaseId);

        var fav = _config.Favorites.Contains(r.BaseId);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 1f, 1f, 0.08f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 1f, 1f, 0.12f));
        ImGui.PushStyleColor(ImGuiCol.Text, fav ? _accent.Primary : new Vector4(0.45f, 0.47f, 0.52f, 1f));
        var favToggled = ImGui.SmallButton(fav ? "★" : "☆");
        ImGui.PopStyleColor(4);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(fav ? "Unpin from favourites" : "Pin to favourites");
        if (favToggled)
        {
            if (fav) _config.Favorites.Remove(r.BaseId);
            else _config.Favorites.Add(r.BaseId);
            _pi.SavePluginConfig(_config);
            _cachedFilterKey = "\0"; // catalog star state changed (the Catalog tab shares this set)
        }
        ImGui.SameLine();

        using (new Disabled(self == null))
        {
            if (ImGui.SmallButton("Spawn")) { _selected = r; SpawnPuppetAs(r); }
        }
        ImGui.SameLine();
        if (ImGui.Selectable($"{r.DisplayName}  ·  {r.SkeletonCode} mc{r.ModelCharaId} #{r.BaseId}", _selected?.BaseId == r.BaseId))
            _selected = r;

        ImGui.PopID();
    }

    /// <summary>One favourite in the Spawn tab's starred shelf, rendered as the accent name-chip the old
    /// Favourites tab had (restored on the consolidated tab) PLUS the two per-row actions the shelf lost when it
    /// merged in — the DM's ask, "control distinction between disguise self from favs or spawn from favs":
    ///   ★ unpin · [name chip → click SELECTS] · Disguise · Spawn.
    /// Disguise wears the mob on YOU via the shared <see cref="ApplyGuise"/> funnel; Spawn drops a puppet wearing
    /// it via <see cref="SpawnPuppetAs"/> — the exact funnels the Catalog uses, so scale/elevation and the HMS
    /// report ride along unchanged (this is pure UI; no IPC touched). The appearance/animation levers the release
    /// Favourites tab carried are deliberately NOT restored — the Animations tab owns those now. Every element is
    /// drawn at the chip height (<c>GetFrameHeight()+6</c>, matching RowNameButton) so the row aligns on one line;
    /// PushID(BaseId) namespaces the widgets. Search matches stay compact on <see cref="DrawSpawnRow"/>.</summary>
    private void DrawFavouriteChip(MobRow r, ICharacter? self)
    {
        ImGui.PushID((int)r.BaseId);

        float rowH   = ImGui.GetFrameHeight() + 6f;          // == RowNameButton's internal height
        var   style  = ImGui.GetStyle();
        float gap    = style.ItemSpacing.X;
        float pad2   = style.FramePadding.X * 2f;
        float starW  = ImGui.GetFrameHeight();
        float disgW  = ImGui.CalcTextSize("Disguise").X + pad2 + 6f;
        float spawnW = ImGui.CalcTextSize("Spawn").X + pad2 + 6f;
        float chipW  = ImGui.GetContentRegionAvail().X - (starW + disgW + spawnW + gap * 3f);
        if (chipW < 60f) chipW = 60f;

        // ── ★ unpin (frameless glyph, rowH-tall for line alignment) — same add/remove + save + filter-invalidate
        // as the Catalog's star, so both shelves stay in sync. Rows here are all favourites, so it always reads ★.
        var fav = _config.Favorites.Contains(r.BaseId);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 1f, 1f, 0.08f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 1f, 1f, 0.12f));
        ImGui.PushStyleColor(ImGuiCol.Text, fav ? _accent.Primary : new Vector4(0.45f, 0.47f, 0.52f, 1f));
        var favToggled = ImGui.Button(fav ? "★" : "☆", new Vector2(starW, rowH));
        ImGui.PopStyleColor(4);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(fav ? "Unpin from favourites" : "Pin to favourites");
        if (favToggled)
        {
            if (fav) _config.Favorites.Remove(r.BaseId);
            else _config.Favorites.Add(r.BaseId);
            _pi.SavePluginConfig(_config);
            _cachedFilterKey = "\0"; // catalog star state changed (the Catalog tab shares this set)
        }

        // ── Name chip: a plain click SELECTS (sets the browse cursor the per-puppet "Apply {name}" reads); the
        // chip fills accent while selected. Meta rides a tooltip so the chip stays clean, like the release Library.
        ImGui.SameLine();
        if (HmUi.RowNameButton(r.DisplayName, _selected?.BaseId == r.BaseId, _accent.Primary, chipW, "favchip"))
            _selected = r;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"{r.DisplayName}\n{r.SkeletonCode} · mc{r.ModelCharaId} · #{r.BaseId}\nClick to select for the per-puppet Apply.");

        // ── Disguise: wear this mob YOURSELF — the action the merged shelf dropped. Same funnel as the Catalog's
        // Disguise, so scale/elevation and the HMS report all match.
        ImGui.SameLine();
        using (new Disabled(self == null))
        {
            if (ImGui.Button("Disguise", new Vector2(disgW, rowH)) && self is { } s)
            { _selected = r; ApplyGuise(s, r); }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(self == null
                ? "Be present in the world to disguise."
                : $"Wear {r.DisplayName} yourself. Revert (Catalog tab) drops it.");

        // ── Spawn: drop a puppet already wearing this mob (unchanged funnel). ──
        ImGui.SameLine();
        using (new Disabled(self == null))
        {
            if (ImGui.Button("Spawn", new Vector2(spawnW, rowH)))
            { _selected = r; SpawnPuppetAs(r); }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(self == null
                ? "Be present in the world to spawn."
                : $"Spawn a non-targetable puppet wearing {r.DisplayName}, a step ahead of you.");

        ImGui.PopID();
    }

    /// <summary>Renderable catalog rows matching the Spawn-tab search (name or BaseId, case-insensitive),
    /// capped, minus any pinned-favourite ids (those show once, pinned above). Empty query returns nothing (the
    /// list prompts the DM to type). A cheap linear scan — the cap keeps the child list light without needing
    /// the Catalog's filter cache.</summary>
    private List<MobRow> MatchSpawnRows(string query, int cap, HashSet<uint>? exclude = null)
    {
        var results = new List<MobRow>();
        if (query.Length == 0) return results;
        var byId = uint.TryParse(query, out var qid);
        foreach (var r in _index.Rows)
        {
            if (!IsRenderable(r)) continue;
            if (exclude != null && exclude.Contains(r.BaseId)) continue;
            if (!(r.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                  || (byId && r.BaseId == qid)
                  || r.BaseId.ToString().Contains(query)))
                continue;
            results.Add(r);
            if (results.Count >= cap) break;
        }
        return results;
    }

    /// <summary>Run an animation action on every live puppet (the "…all" broadcast buttons). Resolves each
    /// puppet's ICharacter fresh; skips any index without a character handle this frame.</summary>
    private void BroadcastAnim(Action<ICharacter> act)
    {
        foreach (var idx in _spawn.Spawned.ToList())
            if (_objects[idx] is ICharacter c)
                act(c);
    }

    /// <summary>The ONE live surface (mockup 3b): the full control suite for the roster-selected puppet —
    /// identity, the Possess/Release menu control + despawn, the worn guise + apply-selection, position drag
    /// (+ bring-to-me), yaw slider (+ face-me), Freeze + speed + draw-elevation, and this puppet's OWN timeline
    /// id with Play/Loop/Stop. Rendered for a SINGLE puppet (not once per puppet), so the tab height is fixed
    /// no matter how many are spawned. Every transform write goes through the IsSpawned-guarded SpawnService;
    /// freeze/speed/anim through the objectIndex-keyed AnimationService; elevation through GuiseService's
    /// draw-offset — so nothing here can touch a non-puppet. Same levers as self-apply (Principle 1: a driven
    /// actor).</summary>
    private void DrawLiveSurface(ushort idx)
    {
        ImGui.PushID(idx);

        var obj = _objects[idx];
        var chara = obj as ICharacter;
        var name = obj?.Name.TextValue;
        var worn = _puppetGuise.TryGetValue(idx, out var w) ? w.DisplayName : null;

        // Header: identity, the Possess/Release menu control (the DM's ask — a secondary path to the world
        // dot), and Despawn. Defer the ACTUAL despawn to the end of this block so it can't invalidate the
        // widgets we draw for this same puppet this frame.
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(string.IsNullOrEmpty(name) ? $"puppet obj#{idx}" : $"{name}  ·  obj#{idx}");

        var possessingThis = _possession.IsPossessing && _possession.PossessedIndex == idx;
        ImGui.SameLine();
        if (possessingThis)
        {
            PushRed();
            if (ImGui.SmallButton("Release")) _possession.Release();
            PopColors();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Drive this puppet with WASD from an orbit camera.\nSpace up · Ctrl down · Esc release.");
        }
        else
        {
            // Only a puppet's originator may possess it; a mirror of a peer's spawn is disabled here unless the DM
            // opted into "Allow possessing others' puppets" (Spawn tab). Same authoritative gate PossessionService
            // enforces — the button just reflects it, with a tooltip that explains the disabled state.
            var canPossess = _possession.CanPossess(idx);
            using (new Disabled(!canPossess))
            {
                if (ImGui.SmallButton("Possess")) _possession.Possess(idx);
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(canPossess
                    ? "Drive this puppet with WASD from an orbit camera.\nSpace up · Ctrl down · Esc release."
                    : "A mirror of another player's puppet — only its originator can possess it.\n" +
                      "Enable \"Allow possessing others' puppets\" in the Spawn tab to override.");
        }

        ImGui.SameLine();
        PushRed();
        var despawn = ImGui.SmallButton("Despawn");
        PopColors();

        if (chara != null)
        {
            // ── Disguise: what it wears now + apply the current selection to THIS puppet ──
            ImGui.TextUnformatted($"Wearing: {(string.IsNullOrEmpty(worn) ? "(blank)" : worn)}");
            ImGui.SameLine();
            using (new Disabled(_selected == null))
            {
                var applyLabel = _selected is { } s ? $"Apply {s.DisplayName}" : "Apply (pick a row)";
                if (ImGui.SmallButton(applyLabel) && _selected is { } row)
                {
                    // Preserve a dialed size across the re-guise: a key in _puppetScale means the DM sized this
                    // puppet, so force that scale (which also bakes it into a Monster's redraw); otherwise null
                    // falls back to the global scale mode, unchanged from before.
                    var forced = _puppetScale.TryGetValue(idx, out var ps) ? (float?)ps : null;
                    ApplyGuise(chara, row, forcedScale: forced);
                    _puppetGuise[idx] = row;
                }
            }
            if (ImGui.IsItemHovered() && _selected == null)
                ImGui.SetTooltip("Search above (or Ctrl+click a Catalog row / Favourite) to select a mob, then Apply it here.");

            // ── Position + rotation. Seed from the LIVE values each frame so the controls track any external
            // move (the DM walking the puppet, a re-place, possession). ──
            if (_spawn.TryGetTransform(idx, out var pos, out var rot))
            {
                // While THIS puppet is being driven, the possession loop rewrites its position + facing every
                // frame from the DM's input (Principle 1: one writer wins), so these manual transform levers can't
                // stick — grey them out with a hint rather than let the DM fight the drive. The animation controls
                // below still work; Release to regain hand-placement.
                using (new Disabled(possessingThis))
                {
                    var p = pos;
                    ImGui.SetNextItemWidth(240);
                    if (ImGui.DragFloat3("##pos", ref p, 0.05f)) { _spawn.SetPosition(idx, p); ReportPuppetMove(idx, p, rot); }
                    if (ImGui.IsItemHovered() && !possessingThis)
                        ImGui.SetTooltip("Drag each axis to slide the puppet (X / Y-height / Z). Ctrl+click to type exact world coords.");
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Bring to me")) { _spawn.MoveToLocalPlayer(idx); if (_spawn.TryGetTransform(idx, out var bp, out var br)) ReportPuppetMove(idx, bp, br); }

                    var yaw = rot;
                    ImGui.SetNextItemWidth(240);
                    if (ImGui.SliderFloat("##yaw", ref yaw, -MathF.PI, MathF.PI, "yaw %.2f")) { _spawn.SetRotation(idx, yaw); ReportPuppetMove(idx, p, yaw); }
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Face me")) { _spawn.FaceLocalPlayer(idx); if (_spawn.TryGetTransform(idx, out var fp, out var fr)) ReportPuppetMove(idx, fp, fr); }
                }
                if (possessingThis)
                    ImGui.TextDisabled("Driving — WASD moves & steers this puppet. Release to place it by hand.");
            }

            // ── Freeze + speed + draw-elevation (the Animations-tab levers, per puppet) ──
            var frozen = _anim.IsFrozen(idx);
            if (ImGui.Checkbox("Freeze", ref frozen)) { var s = frozen ? 0f : 1f; _puppetSpeed[idx] = s; _anim.SetSpeed(chara, s); ReportFreezeChange(chara, frozen); }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Hold the puppet on a still frame (pins animation speed 0 every frame). Toggle off to resume.");
            ImGui.SameLine();
            var spd = _puppetSpeed.TryGetValue(idx, out var sv) ? sv : 1f;
            ImGui.SetNextItemWidth(120);
            if (ImGui.SliderFloat("##spd", ref spd, 0f, 3f, "speed %.2f")) { _puppetSpeed[idx] = spd; _anim.SetSpeed(chara, spd); }
            ImGui.SameLine();
            if (ImGui.SmallButton("1.0x")) { _puppetSpeed[idx] = 1f; _anim.SetSpeed(chara, 1f); }

            var voff = _guise.GetVerticalOffset(idx);
            ImGui.SetNextItemWidth(160);
            if (ImGui.SliderFloat("##elev", ref voff, -20f, 20f, "elevation %.2f")) ApplyElevationTo(chara, voff, commit: false);
            if (ImGui.IsItemDeactivatedAfterEdit()) ApplyElevationTo(chara, voff, commit: true); // sync on release
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Raise/lower where the model is DRAWN (a draw offset; the actor doesn't move).\n" +
                                 "Sink a hovering flyer to the floor, or lift a mob that spawned in the ground.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Reset##elev")) ApplyElevationTo(chara, 0f, commit: true);

            // ── Live scale (absolute multiplier). Reads the puppet's REAL size until the DM dials it, then the
            // dialed value takes over (its presence in _puppetScale also makes Apply below preserve the size).
            // Live transform write, no redraw: non-monster puppets resize as you drag; a built monster's size is
            // baked at draw-build (H3) so it changes only on the next Apply/redraw — same as the self knob. Peers
            // sync on RELEASE via the shared ApplyScaleTo funnel; the receiver drives the delta live (no redraw). ──
            var scl = _puppetScale.TryGetValue(idx, out var scv) ? scv : _guise.GetScale(chara);
            ImGui.SetNextItemWidth(160);
            if (ImGui.SliderFloat("##pupscale", ref scl, 0.1f, 10f, "scale x%.2f")) { _puppetScale[idx] = scl; ApplyScaleTo(chara, scl, commit: false); }
            if (ImGui.IsItemDeactivatedAfterEdit()) ApplyScaleTo(chara, scl, commit: true);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Resize this puppet (0.1x–10x). Non-monster puppets resize live;\n" +
                                 "a monster's size bakes on the next Apply (redraw).");
            ImGui.SameLine();
            if (ImGui.SmallButton("1.0x##pupscale")) { _puppetScale[idx] = 1f; ApplyScaleTo(chara, 1f, commit: true); }

            // ── This puppet's OWN timeline id + Play/Loop/Stop (independent of the "…all" broadcast id) ──
            var tid = _puppetTid.TryGetValue(idx, out var t) ? t : _puppetTimelineId;
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Timeline:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(70);
            if (ImGui.InputInt("##tid", ref tid, 0)) _puppetTid[idx] = tid;
            ImGui.SameLine();
            using (new Disabled(tid is <= 0 or > ushort.MaxValue))
            {
                if (ImGui.SmallButton("Play")) { _anim.PlayOnce(chara, (ushort)tid); ReportOneShot(chara, (ushort)tid); }
                ImGui.SameLine();
                if (ImGui.SmallButton("Loop")) { _anim.Loop(chara, (ushort)tid); ReportHeldLoop(chara, (ushort)tid); }
            }
            ImGui.SameLine();
            PushRed();
            var stop = ImGui.SmallButton("Stop");
            PopColors();
            if (stop) StopAnim(chara);
            if (_anim.IsPlaying(idx)) { ImGui.SameLine(); ImGui.TextDisabled("[animating]"); }

            // ── Set-piece stances for RP events. A puppet has NO native input loop (guide Principle 1), so a
            // "standing with weapon drawn" or "dead on the ground" pose must be DRIVEN and held — the game won't
            // settle it there on its own. Both route through the SAME funnels the Timeline row above uses (Rule 1):
            //   • Draw weapon — hold the battle idle clip (BtlIdle renders the weapon out; it's the exact stance a
            //     possessed armed puppet now settles into, so the standalone button and possession agree).
            //   • Die — stitch the death FALL then hold the lying dead pose via PlaySequence (the "Die" compound
            //     gesture). Ids come from the shipped timeline index by Codebook key, NOT hard-coded literals, so a
            //     per-patch regen can't silently drift them. ResolveCommonId (not ResolvePlayable) because the caps
            //     gate profiles only monster/demihuman resident .paps and returns 0 for a humanoid — and these
            //     puppets are humanoid NPCs. Both poses end via the Stop button above (Sanitize clears the hold).
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Set piece:");
            ImGui.SameLine();
            // Draw weapon is a TOGGLE (DM ask): press once to hold the weapon-drawn battle idle, press again to
            // sheathe. "Drawn" is read live from the actor's held base loop (IsHoldingLoop), so the label always
            // mirrors the real stance with no shadow flag; sheathing routes through the same StopAnim funnel as
            // the Stop button (blends back to idle + clears the peer loop state).
            var wpnDrawn = _anim.IsHoldingLoop(chara, LocomotionData.BtlIdle);
            if (ImGui.SmallButton((wpnDrawn ? "Sheathe weapon" : "Draw weapon") + "##drawwpn"))
            {
                if (wpnDrawn) StopAnim(chara);
                else { _anim.Loop(chara, LocomotionData.BtlIdle); ReportHeldLoop(chara, LocomotionData.BtlIdle); }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Toggle a weapon-drawn battle idle stance — press again to sheathe and return to\n" +
                                 "the neutral idle. (Stop clears it too.)");
            ImGui.SameLine();
            ushort dieIntro = (ushort)_timeline.ResolveCommonId("battle/dead");
            ushort dieHold  = (ushort)_timeline.ResolveCommonId("battle/dead_pose");
            using (new Disabled(dieIntro == 0 || dieHold == 0))
            {
                if (ImGui.SmallButton("Die")) { _anim.PlaySequence(chara, dieIntro, dieHold); ReportHeldLoop(chara, dieHold); }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Play the death fall, then hold the lying dead pose. Press Stop to clear.");

            // Cycle pose — the DM's "/cpose button", within the limit of what a PUPPET can be driven to hold.
            // Steps through the standing idle-pose ActionTimeline variants (normal/idle -> idle_inactive1/2/3) and
            // HOLDS the next one via the same Loop/BaseOverride funnel "Draw weapon"/"Die" use. It clears any held
            // base lane first, so it also drops a held "Draw weapon" stance back to idle. Honesty note: this is NOT
            // the true /cpose axis — real /cpose is applied by an unmapped game function (EmoteController.SetPose)
            // that no-ops when written to a puppet, so we cycle the real idle CLIPS instead (drivable, held, testable).
            ImGui.SameLine();
            if (ImGui.SmallButton("Cycle pose")) _anim.CyclePose(chara);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Step this puppet through the standing idle-pose variants (normal idle + the three\n" +
                                 "idle_inactive clips) and hold it. Clears a held stance first, so a drawn weapon\n" +
                                 "returns to the neutral idle.");

            // Weapon VISIBILITY (the /displayarms axis), read LIVE from the actor so the box always mirrors
            // reality — distinct from the "Draw weapon" STANCE beside it. Puppets equip their OWN mob weapon
            // (Issue 1) shown by default (SpawnService clears the clone's inherited hide bit); this hides/shows it.
            ImGui.SameLine();
            var wpnShown = _guise.GetWeaponDrawn(idx);
            if (ImGui.Checkbox("Show weapon", ref wpnShown))
            {
                // A HUMAN guise is MANAGED by Glamourer, whose StateListener re-asserts a fixed WeaponState every
                // redraw and slams a native hide straight back on (that reassertion is what froze this toggle ON for
                // the DM). So for a human guise drive VISIBILITY through Glamourer's own Weapon meta — cooperate with
                // the lock instead of losing to it. Monster/Demihuman guises aren't Glamourer-managed: native path.
                if (_humanGuise.IsGuised(idx)) _humanGuise.SetWeaponShown(idx, wpnShown);
                else _guise.SetWeaponDrawn(idx, wpnShown);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Show or hide this puppet's weapon (sheathed-on-body visibility, the /displayarms\n" +
                                 "axis). On by default. Distinct from \"Draw weapon\", which holds a weapon-drawn stance.");
        }
        else
        {
            ImGui.TextDisabled("(no character handle this frame, try again next frame)");
        }

        ImGui.Separator();

        if (despawn)
        {
            _spawn.Despawn(idx);
            _puppetGuise.Remove(idx); _puppetTid.Remove(idx); _puppetSpeed.Remove(idx); _puppetScale.Remove(idx);
            if (_selectedPuppet == idx) _selectedPuppet = ushort.MaxValue; // re-homes to the first live puppet next draw
        }

        ImGui.PopID();
    }

    /// <summary>Drop per-puppet UI state (worn label, per-row timeline id, speed, dialed scale) for indexes
    /// that are no longer live puppets — e.g. a zone change cleared the spawn set — so the dicts can't
    /// accumulate stale keys that a recycled index would inherit.</summary>
    private void PrunePuppetLabels()
    {
        if (_puppetGuise.Count == 0 && _puppetTid.Count == 0 && _puppetSpeed.Count == 0 && _puppetScale.Count == 0) return;
        var live = _spawn.Spawned;
        foreach (var key in _puppetGuise.Keys.ToList()) if (!live.Contains(key)) _puppetGuise.Remove(key);
        foreach (var key in _puppetTid.Keys.ToList())   if (!live.Contains(key)) _puppetTid.Remove(key);
        foreach (var key in _puppetSpeed.Keys.ToList()) if (!live.Contains(key)) _puppetSpeed.Remove(key);
        foreach (var key in _puppetScale.Keys.ToList()) if (!live.Contains(key)) _puppetScale.Remove(key);
    }

    /// <summary>Spawn tab first-run empty state (mockup 4b): a centered ghost glyph + "No puppets yet" heading, a
    /// one-line description, the three numbered steps to a staged scene, and a diamond note. Shown in place of the
    /// per-puppet rows until the DM spawns their first puppet; a zone change returns the tab to this state. Pure
    /// guidance — no state, no side effects.</summary>
    private void DrawSpawnEmptyState()
    {
        var accent = _accent.Primary;
        var dim = ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled];

        // Centre a single line of text within the current content width.
        void Center(string text, Vector4? color)
        {
            float w = ImGui.CalcTextSize(text).X;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (ImGui.GetContentRegionAvail().X - w) * 0.5f));
            if (color is { } c) ImGui.TextColored(c, text); else ImGui.TextUnformatted(text);
        }

        ImGui.Dummy(new Vector2(0f, 6f));

        // Centred ghost glyph in the accent (Dalamud's icon font — always present, so it can't fail like a
        // game-icon lookup).
        ImGui.PushFont(UiBuilder.IconFont);
        var glyph = FontAwesomeIcon.Ghost.ToIconString();
        float gw = ImGui.CalcTextSize(glyph).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (ImGui.GetContentRegionAvail().X - gw) * 0.5f));
        ImGui.TextColored(accent, glyph);
        ImGui.PopFont();

        ImGui.Spacing();
        Center("No puppets yet", null);
        Center("Spawn a disguised set-piece and it gets its own control row here.", dim);

        ImGui.Dummy(new Vector2(0f, 10f));

        // The three numbered steps, left-aligned at a gentle centring indent so the 1·2·3 flow reads as a guide.
        float indent = MathF.Max(0f, (ImGui.GetContentRegionAvail().X - 460f) * 0.5f);
        if (indent > 0f) ImGui.Indent(indent);
        SpawnStep(accent, dim, "1", "Search a mob above", "Type a name or family in the search box, then press Spawn.");
        SpawnStep(accent, dim, "2", "Or drop a blank dummy", "'Spawn blank dummy' places a plain, non-targetable puppet a step ahead of you.");
        SpawnStep(accent, dim, "3", "Disguise, pose & animate", "Pick the puppet in the roster below to move, rotate, resize, freeze and animate it.");
        if (indent > 0f) ImGui.Unindent(indent);

        ImGui.Dummy(new Vector2(0f, 6f));
        Center("◆  Puppets are non-targetable set-pieces — a zone change clears them all.", dim);
    }

    /// <summary>One numbered step for <see cref="DrawSpawnEmptyState"/>: an accent "N." + bold title, then the
    /// dimmed, wrapped body indented under it.</summary>
    private void SpawnStep(Vector4 accent, Vector4 dim, string n, string title, string body)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, accent);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(n + ".");
        ImGui.PopStyleColor();
        ImGui.SameLine(0f, 8f);
        ImGui.TextUnformatted(title);
        ImGui.Indent(20f);
        ImGui.PushStyleColor(ImGuiCol.Text, dim);
        ImGui.TextWrapped(body);
        ImGui.PopStyleColor();
        ImGui.Unindent(20f);
        ImGui.Spacing();
    }

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
                              || (_locSearch.TryGetValue(r.BaseId, out var loc) && loc.Contains(fl, StringComparison.Ordinal)));
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
        foreach (var r in _index.Rows)
            if (_harvest.TryGetName(r.BaseId, out var n) && r.LiveName != n)
                r.LiveName = n;
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
        ImGui.PushID((int)r.BaseId);

        ImGui.TableNextColumn();
        var fav = _config.Favorites.Contains(r.BaseId);
        // Bare star toggle (no button frame — the mockup's clean gold ★): a filled accent star when
        // favourited, a dim hollow star otherwise. Transparent Button bg turns SmallButton into a frameless
        // glyph; the accent tint ties the "on" state to the suite palette (gold by default).
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 1f, 1f, 0.08f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 1f, 1f, 0.12f));
        ImGui.PushStyleColor(ImGuiCol.Text, fav ? _accent.Primary : new Vector4(0.45f, 0.47f, 0.52f, 1f));
        var favToggled = ImGui.SmallButton(fav ? "★" : "☆");
        ImGui.PopStyleColor(4);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(fav ? "Unfavourite" : "Add to Favourites");
        if (favToggled)
        {
            if (fav) _config.Favorites.Remove(r.BaseId);
            else _config.Favorites.Add(r.BaseId);
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
        var clicked = ImGui.Selectable(r.DisplayName, _selected?.BaseId == r.BaseId,
                                       ImGuiSelectableFlags.SpanAllColumns);
        if (r.NameIsHeuristic)
        {
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Name inferred from the instanced encounter roster (BossMod); this base is\n" +
                                 "unnamed in the crowdsourced catalog; its home instance names its boss.");
        }
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
    private void DrawRowMarker(MobRow r)
    {
        if (r.Source == NpcSource.Event)
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
        // The humanoid-NPC set is now many Race→Clan nodes (not one flat node); gather them and order the
        // leaves alphabetically by "Race — Clan" label so the section reads A→Z.
        _treeEventNodes = nodes.Values.Where(n => n.Category == "EventNpc")
            .OrderBy(n => n.ZoneLabel, StringComparer.OrdinalIgnoreCase).ToList();
        _treeUnknown = nodes.Values.Where(n => !n.Located && n.Key != MinionNodeKey && n.Category != "EventNpc")
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
        if (minionNode != null)
        {
            if (ExpansionDivider("Minions & summons", rowRightEdge, MinionSectionKey, autoExpand, $"{minionCount} minions"))
                DrawZoneNode(minionNode, autoExpand, rowRightEdge);
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

    private bool TryLocate(MobRow r, out uint territoryId)
    {
        if (_manual.TryGetPrimary(r.BaseId, out territoryId)) return true;    // Tier M: hand-curated tag (cutscene-script props) — overrides every estimate/inference
        if (r.Name.Length > 0 && _stems.TryMatch(r.Name, out territoryId)) return true; // Tier N: curated dungeon name-stem (director-spawned trash "Babil slasher" -> Tower of Babil) — asserts authority over the scattered crowdsource telemetry below it
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
            _categories[r.BaseId] = ComputeCategories(r);
            var loc = ComputeLocSearch(r);
            if (loc.Length > 0) _locSearch[r.BaseId] = loc;

            // Zones-pill source: add every distinct, nameable home territory this row locates to. Skip
            // Event NPCs + minions (they're fenced into their own sections with no home zone, exactly as
            // DrawLocationTree buckets them), and skip territories the content sheet can't name (engine
            // limbo — the same TryGet guard DrawLocationTree uses). One-time at load; free at draw.
            if (r.Source != NpcSource.Event && !_companion.IsMinion(r.BaseId)
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
    private HashSet<string> CategoriesOf(MobRow r) => _categories.TryGetValue(r.BaseId, out var s) ? s : ComputeCategories(r);

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
            if (r.Source == NpcSource.Event) continue;
            total++;
            var named = !r.IsUnnamed;
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

    private void DrawAnimTab()
    {
        // The animation SUBJECT is normally YOU, but when a puppet is spawned the DM can retarget the whole
        // playback surface onto "the focused puppet" (the Spawn roster's selection) via the "Drive" selector
        // below — the same target-generic funnels the Spawn per-puppet surface uses, so one code path drives
        // self OR a puppet (single-control-mechanism rule). This is what surfaces a spawned humanoid puppet's
        // emote list here: the specials/emotes/common lists scope to the SUBJECT's worn guise (subjectGuise),
        // not just _wornGuise. The header (identity + Revert) stays SELF-bound — RevertGuise is a local-player
        // hard revert by construction (it nulls _wornGuise + your Moniker), never safe to point at a puppet.
        var self = Self();
        var focusedPuppet = FocusedPuppet();
        var canPuppet = focusedPuppet != null;
        if (!canPuppet) _animApplyToPuppet = false;
        var toPuppet = _animApplyToPuppet && canPuppet;
        var target = toPuppet ? focusedPuppet : self;              // subject the playback panel + lists drive
        var subjectGuise = target == null ? null
                         : toPuppet ? _puppetGuise.GetValueOrDefault(target.ObjectIndex) : _wornGuise;

        var selfLabel = self?.Name.TextValue ?? "(no local player)";
        var playing = self != null && _anim.IsPlaying(self.ObjectIndex);

        var acc = _accent.Primary;
        // Lit (accented) while guised — the same "you're disguised; click to drop it" cue the Catalog Revert uses.
        var guised = self != null && (_guise.IsGuised(self.ObjectIndex) || _humanGuise.IsGuised(self.ObjectIndex));

        // ── Header: "● You · <name>" (green presence dot) on the left; self-actions (Revert / Dump state)
        // right-aligned on the same line. (2a mockup.) Revert and Dump stay ENABLED with no target below.
        {
            var dotCol = self != null ? new Vector4(0.36f, 0.80f, 0.45f, 1f) : new Vector4(0.45f, 0.47f, 0.52f, 1f);
            var hdl = ImGui.GetWindowDrawList();
            var dpos = ImGui.GetCursorScreenPos();
            float lh = ImGui.GetTextLineHeight();
            hdl.AddCircleFilled(new Vector2(dpos.X + 4f, dpos.Y + lh / 2f + 2f), 4f, ImGui.GetColorU32(dotCol), 12);
            ImGui.Dummy(new Vector2(13f, lh));
            ImGui.SameLine(0f, 0f);
            ImGui.TextUnformatted($"You · {selfLabel}");
            if (playing) { ImGui.SameLine(0f, 6f); ImGui.TextDisabled("[animating]"); }

            // Right-align Revert + Dump state on the header line (measure their button widths + push a spacer).
            ImGui.SameLine();
            const float gap = 6f;
            var fpx = ImGui.GetStyle().FramePadding.X;
            float revertW = ImGui.CalcTextSize("Revert").X + fpx * 2f;
            float dumpW   = ImGui.CalcTextSize("Dump state").X + fpx * 2f;
            float need = revertW + gap + dumpW;
            float remaining = ImGui.GetContentRegionAvail().X;
            if (remaining > need) { ImGui.Dummy(new Vector2(remaining - need, 0f)); ImGui.SameLine(0f, 0f); }

            // Revert — same RevertGuise the Catalog chip calls (sanitises animation, drops BOTH guise families,
            // clears the active-disguise identity, mirrors to HMS, clears any Moniker nameplate). Distinct from
            // "Reset to Normal" below: unstick clears a STUCK ANIMATION but KEEPS the disguise; Revert drops it.
            if (HmUi.AccentButton("Revert", "revert_anim", guised, acc) && self != null) RevertGuise(self);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Restore your real model + size and un-stick any animation, the same Revert as the\n" +
                                 "Catalog tab. (\"Reset to Normal\" below only clears a stuck animation; Revert\n" +
                                 "also drops the disguise.)");
            ImGui.SameLine(0f, gap);
            // Dump state — walk-regression diagnostic; log the anim + movement-intent snapshot. Read-only.
            if (HmUi.AccentButton("Dump state", "dumpstate", false, acc) && target != null)
                _anim.DumpTimelineState(target, _selected is { } s ? $"{s.SkeletonCode} mc{s.ModelCharaId} {s.DisplayName}" : "no-guise");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Log a one-shot snapshot of the animation + movement-intent state to /xllog\n" +
                                 "(BaseOverride, active timeline slots, MoveController forward-speed/heading).\n" +
                                 "Fire it while a guise is sliding or stuck-walking, then read the 'Anim[…]' lines.");
        }

        ImGui.Spacing();

        // ── Drive selector: play animations on YOU, or on the Spawn tab's focused puppet. Only shown once a
        // puppet is live so the common self-only flow stays uncluttered (mirrors the Catalog's "Disguise focused
        // puppet" gate). Retargets `target` — and with it the whole PLAYBACK panel + every timeline list below,
        // since they already route through the target-generic funnels — so a spawned humanoid puppet's emotes
        // become searchable and playable here (the DM's "I can't see a puppet's emotes" ask). ──
        if (canPuppet)
        {
            const float driveGap = 6f;
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled("Drive");
            ImGui.SameLine();
            float driveW = MathF.Min(150f, (ImGui.GetContentRegionAvail().X - HmUi.PanelPad - driveGap) / 2f);
            if (HmUi.AccentButton("You", "anim_to_self", !_animApplyToPuppet, acc, driveW))
                _animApplyToPuppet = false;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Play animations on yourself.");
            ImGui.SameLine(0f, driveGap);
            if (HmUi.AccentButton("Focused puppet", "anim_to_pup", _animApplyToPuppet, acc, driveW))
                _animApplyToPuppet = true;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Play animations on the puppet selected in the Spawn tab's roster.\n" +
                                 "Its specials/emotes and the Common list below follow the puppet's own guise.");
            ImGui.Spacing();
        }

        // ── Red "Reset to Normal" card — the prominent unstick. Sanitize() forces the actor back to a normal,
        // unlocked idle (clears BaseOverride/OverallSpeed, SetMode Normal); we also clear any held loop for peers.
        if (HmUi.DangerCard("Reset to Normal", "Unstick: clears a frozen or stuck timeline", "unstick") && target != null)
            { _anim.Sanitize(target); ReportHeldLoop(target, 0); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Force the character back to a normal, unlocked idle.\n" +
                             "Use this if an animation left it stuck ('operating a siege machine').\n" +
                             "Safe to press anytime.");

        ImGui.Spacing();

        using var _ = new Disabled(target == null);

        // ── PLAYBACK panel: Speed + Elevation as sliders with right-aligned mono readouts and a right-side
        // Reset button each, then Freeze, then the raw-timeline-id Advanced expander. (2a mockup.)
        using (HmUi.Panel("PLAYBACK"))
        {
            // Speed. Freeze (below) pins OverallSpeed 0 every frame (AnimationService re-asserts it) so a
            // bobbing idle holds dead still — a one-shot press wouldn't hold (the native animator resets speed
            // toward 1 each frame). The live value shows in the readout box; the slider carries no inline text.
            ImGui.TextUnformatted("Speed");
            HmUi.Readout($"{_speed:0.00}×");
            // Slider then a right-side Reset button. HmUi.Panel insets content on the LEFT only, so a
            // -1f/-PanelPad slider would run under the panel border and clip whatever follows; give the
            // slider an explicit width (avail − Reset − spacing − PanelPad) and place Reset after it.
            // (batch-2: fixes the slider overflow AND turns the old bare "1×" into a labelled Reset.)
            var pbStyle = ImGui.GetStyle();
            float pbResetW = ImGui.CalcTextSize("Reset").X + pbStyle.FramePadding.X * 2f;
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - pbResetW - pbStyle.ItemSpacing.X - HmUi.PanelPad);
            if (ImGui.SliderFloat("##animspeed", ref _speed, 0f, 3f, "") && target != null)
                _anim.SetSpeed(target, _speed);
            ImGui.SameLine();
            if (ImGui.Button("Reset##spdreset") && target != null) { _speed = 1f; _anim.SetSpeed(target, 1f); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Reset playback speed to 1.00×.");

            // Elevation: drop a floor-clipping flyer / lift a floor-spawned mob WITHOUT moving the real
            // character (GuiseService writes + re-asserts the draw offset per frame). Read back each frame so
            // the slider stays put between drags. Pairs with Freeze for a "still, on the ground" ambush.
            var voff = target != null ? _guise.GetVerticalOffset(target.ObjectIndex) : 0f;
            ImGui.TextUnformatted("Elevation");
            HmUi.Readout($"{voff:+0.00;-0.00;+0.00}");
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - pbResetW - pbStyle.ItemSpacing.X - HmUi.PanelPad);
            if (ImGui.SliderFloat("##voffset", ref voff, -20f, 20f, "") && target != null)
                ApplyElevationTo(target, voff, commit: false);
            if (ImGui.IsItemDeactivatedAfterEdit() && target != null)
                ApplyElevationTo(target, voff, commit: true); // sync on release
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Raise/lower where the model is DRAWN; your character doesn't move.\n" +
                                 "Negative = down (sink a hovering gunship/boss to the ground);\n" +
                                 "positive = up (lift a mob that spawned in the floor, or stage a flyer).\n" +
                                 "Range ±20. Ctrl+click a slider to type an exact value.");
            ImGui.SameLine();
            if (ImGui.Button("Reset##voffreset") && target != null) ApplyElevationTo(target, 0f, commit: true);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Reset elevation to 0 (draw at the character's real height).");

            ImGui.Spacing();

            // Freeze toggle. Pins OverallSpeed 0 every frame (AnimationService re-asserts it) so a bobbing
            // idle holds dead still; toggle off to resume at 1.00×. (batch-2: the "Jump anim on Space" opt-in
            // that used to sit beside this was removed — it never reliably fired the jump .pap.)
            var frozen = target != null && _anim.IsFrozen(target.ObjectIndex);
            if (ImGui.Checkbox("Freeze", ref frozen) && target != null)
                ApplyFreeze(target, frozen); // single self-Freeze funnel (target-generic: self or the driven puppet)
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Hold the animation on a still frame (pins speed 0 every frame).\n" +
                                 "Use it to make a boss 'stand perfectly still'. Toggle off to resume.");

            // Advanced (raw timeline id) — default CLOSED now that the red "Reset to Normal" card above is the
            // primary unstick. Its red Stop is still the way to END a raw loop.
            if (HmUi.GroupHeader("Advanced · raw timeline id", "", ref _advancedOpen, "adv"))
            {
                ImGui.SetNextItemWidth(80);
                ImGui.InputInt("##tid", ref _timelineId, 0);
                ImGui.SameLine();
                if (ImGui.Button("Play once##raw") && target != null && _timelineId is > 0 and <= ushort.MaxValue)
                    { _anim.PlayOnce(target, (ushort)_timelineId); ReportOneShot(target, (ushort)_timelineId); }
                ImGui.SameLine();
                if (ImGui.Button("Loop##raw") && target != null && _timelineId is > 0 and <= ushort.MaxValue)
                    { _anim.Loop(target, (ushort)_timelineId); ReportHeldLoop(target, (ushort)_timelineId); }
                ImGui.SameLine();
                PushRed();
                var stopRaw = ImGui.Button("Stop");
                PopColors();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Stop any looping/one-shot animation and blend back to a normal idle.");
                if (stopRaw && target != null)
                    StopAnim(target);
            }
        }

        // ── Search + the single global Loop toggle — HOISTED to the TOP (0.9.1) so it visibly HEADS every
        // animation list below (This-mob specials, the Human Emotes set, and Common) instead of hiding between
        // them. It filters ALL of them by name or /command through the one _animFilter DrawTimelineButtons reads
        // (single-control-mechanism rule): emote rows carry the slash command as their Key, so "/wave" matches.
        // Before 0.9.1 this box sat BELOW the 220px Emotes scroll-box, so a DM filtering emotes never saw it act
        // ("emote search doesn't appear to be live"). One box, every list. The field fills the row up to the Loop
        // toggle — measured from the style, so it stays correct on high-DPI (no hardcoded width). ──
        var animStyle  = ImGui.GetStyle();
        float animLoopW = ImGui.GetFrameHeight() + animStyle.ItemInnerSpacing.X + ImGui.CalcTextSize("Loop").X;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - animLoopW - animStyle.ItemSpacing.X);
        ImGui.InputTextWithHint("##animsearch", "filter animations / emotes by name or /command…", ref _animFilter, 48);
        ImGui.SameLine();
        ImGui.Checkbox("Loop", ref _loopMode);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Checked: clicking a timeline HOLDS it as a looping base pose (until you press Stop).\n" +
                             "Unchecked: clicking plays it once, then the game blends back to idle.");

        ImGui.Spacing();

        // ── The subject's specials (or a hint to pick one), now BELOW the search that filters it. Scopes to the
        // SUBJECT's WORN model (subjectGuise = your _wornGuise, or the focused puppet's guise), not the catalog
        // browse-cursor: browsing while disguised no longer drifts this list off what the subject wears, and
        // shedding/changing the disguise updates it (batch-3 item #6).
        // A blank (untracked) puppet is a clone of the local player — a human body that plays emotes even
        // though it carries no mob identity, so treat it as human here. This is the DM's exact case: "spawned
        // a humanoid dummy (elezen), I cannot see emotes." Every DISGUISE path records _puppetGuise (spawn-as,
        // per-row apply, fav, re-guise), so a null guise on a puppet means genuinely blank, never a dressed one
        // we failed to track. Its Common list falls to the human "playables" view below because its empty
        // SkeletonCode has no caps profile. (If the DM is themselves monster-disguised when they drop a blank
        // dummy the clone inherits that body and these emotes won't visibly fire — a rare corner; disguising
        // the puppet through its row records the real guise and swaps this to the correct specials.)
        var subjectIsBlankPuppet = toPuppet && subjectGuise is null;
        var subjectIsHuman = subjectGuise is { McType: 1 } || subjectIsBlankPuppet;

        if (subjectIsHuman)
        {
            // Human body (a cNNNN NPC guise OR a blank DM-clone puppet): no monster specials, but it DOES play
            // emotes — /point, /wave, /dance &c. Surface the full emote set here; each is a TimelineRow that
            // rides the SAME TriggerTimeline funnel (Rule 1) so the global Loop toggle and the search box below
            // govern it with no bespoke path. Scroll-boxed like Common so ~100 rows don't shove the rest of the
            // tab off-screen.
            var emotes = _timeline.Emotes;
            if (HmUi.GroupHeader("Emotes", $"human · {emotes.Count}", ref _emotesOpen, "emotehdr"))
            {
                if (emotes.Count == 0)
                    ImGui.TextDisabled("No emotes loaded.");
                else
                {
                    ImGui.BeginChild("##emotelist", new Vector2(0f, 220f), true, ImGuiWindowFlags.None);
                    DrawTimelineButtons(emotes, target);
                    ImGui.EndChild();
                }
            }
        }
        else if (subjectGuise is { } sel)
        {
            // Monster/demihuman guise: list the skeleton's catalogued specials, then any glued combos.
            var skel = sel.SkeletonCode;
            var specials = _timeline.ForSkeleton(skel);
            if (HmUi.GroupHeader($"This mob · {sel.DisplayName}", skel, ref _specialsOpen, "skelhdr"))
            {
                if (specials.Count == 0)
                    ImGui.TextDisabled($"No catalogued specials for {skel}. Try the Common set below.");
                else
                    DrawTimelineButtons(specials, target);
            }

            // Combos: one-click compound gestures that glue an intro clip to its terminal pose — "Die" =
            // play the death FALL once, then HOLD the lying dead_pose (the pair a DM would otherwise fire by
            // hand, and "never uses separately"). Shown ONLY when BOTH constituents resolve as caps-valid for
            // this skeleton (ResolveCombos), so it never offers a Die the model can't actually perform. Each
            // rides AnimationService.PlaySequence (the single compound-gesture funnel, Rule 1); the terminal
            // HOLD is mirrored to peers via ReportHeldLoop so a synced viewer sees the pose held.
            var combos = ResolveCombos(skel);
            if (combos.Count > 0 && HmUi.GroupHeader("Combos", $"{combos.Count}", ref _combosOpen, "combohdr"))
            {
                foreach (var (label, introId, holdId) in combos)
                {
                    if (HmUi.AccentButton(label, $"combo_{label}", false, acc) && target != null)
                        { _anim.PlaySequence(target, introId, holdId); ReportHeldLoop(target, holdId); }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"Play the '{label}' sequence: the intro animation once, then hold its final pose.\n" +
                                         "Press \"Reset to Normal\" above to release the hold.");
                }
            }
        }
        else
        {
            // Only reached for an undisguised SELF — a blank puppet is handled as human above. Prompt to apply
            // a disguise so the mob's animations list here.
            ImGui.BeginChild("##nospecials", new Vector2(0f, ImGui.GetFrameHeightWithSpacing() + 4f), true, ImGuiWindowFlags.None);
            ImGui.TextDisabled("Disguise yourself from the Catalog to list the mob's animations here.");
            ImGui.EndChild();
        }

        ImGui.Spacing();

        // Shared common timelines. For a monster/demihuman guise we now KNOW which of these the skeleton
        // can actually play: skel-anim-caps.csv carries every resident .pap's internal animation table, so
        // TimelineIndex.ValidCommonFor trims the ~440-row pile down to the base-lane moves the model
        // genuinely defines (idle/attack/dead/turn) and drops the ~400 player-body dummies that would fire
        // nothing. That "works vs dummy" fact was long assumed un-carryable offline — it is exactly what the
        // caps extraction now carries. The trimmed view needs no chips (it's a handful of rows); the
        // "Show all timelines" escape hatch restores the full, chip-navigable pile for experimentation and
        // is the ONLY view for a human (cNNNN) guise, which has no caps profile and really does walk/emote.
        // Two independent default-trims, both released by "Show all timelines":
        //  - a MONSTER/DEMIHUMAN guise (has a caps profile) trims to the base-lane rows its .pap set actually
        //    defines (ValidCommonFor) — a handful of rows, no chips needed;
        //  - a HUMAN (cNNNN) guise has no caps, so instead we hide the pure locomotion/reaction junk
        //    (IsPlayable) and let the provenance chips navigate the ~353 playable rows that remain.
        var skelSel = subjectGuise?.SkeletonCode ?? "";
        var hasCaps = _timeline.HasCaps(skelSel);
        var capsTrim = hasCaps && !_showAllTimelines;
        var playTrim = !hasCaps && !_showAllTimelines;
        _animPlayableCount ??= _timeline.Common.Count(t => IsPlayable(t.Key));

        string commonTitle, commonMeta;
        if (capsTrim)      { commonTitle = $"Common · playable by {skelSel}"; commonMeta = $"{_timeline.ValidCommonFor(skelSel).Count} of {_timeline.Common.Count}"; }
        else if (playTrim) { commonTitle = "Common · playables";              commonMeta = $"{_animPlayableCount} of {_timeline.Common.Count}"; }
        else               { commonTitle = "Common";                          commonMeta = $"{_timeline.Common.Count}"; }
        if (HmUi.GroupHeader(commonTitle, commonMeta, ref _commonOpen, "commonhdr"))
        {
            ImGui.Checkbox("Show all timelines", ref _showAllTimelines);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(hasCaps
                    ? "Off: show only the base-lane timelines THIS skeleton's animation files\n" +
                      "actually define (idle/attack/dead/turn); the rest are player-body\n" +
                      "dummies that do nothing on a monster.\n" +
                      "On: show every Common timeline (grouped by the chips) to experiment."
                    : "Off: hide pure locomotion & hit-reaction rows (walk/run/swim/knockback)\n" +
                      "that no skill triggers and your guise already animates itself.\n" +
                      "On: show every Common timeline, including those, grouped by the chips.");

            IReadOnlyList<TimelineRow> commonRows;
            if (capsTrim)
            {
                // Already trimmed to what the skeleton can play — the provenance chips add nothing, list it.
                commonRows = _timeline.ValidCommonFor(skelSel);
            }
            else
            {
                if (WrappedChips(AnimGroupChips, _animGroup, "animgrp") is { } gk) _animGroup = gk;
                IEnumerable<TimelineRow> q = _timeline.Common;
                if (playTrim) q = q.Where(t => IsPlayable(t.Key));                         // human: drop the junk buckets
                if (_animGroup != "All") q = q.Where(t => ProvenanceCategory(t.Key) == _animGroup);
                commonRows = q as IReadOnlyList<TimelineRow> ?? q.ToList();
            }

            // Common is the LAST section now (Advanced moved above), so fill ALL remaining window
            // height; floor at 140 so it stays usable on a short window or when the specials list is long.
            var listH = MathF.Max(140f, ImGui.GetContentRegionAvail().Y);
            ImGui.BeginChild("##commonlist", new Vector2(0, listH), true, ImGuiWindowFlags.None);
            DrawTimelineButtons(commonRows, target);
            ImGui.EndChild();
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

    /// <summary>The "playables" default filter for a HUMAN (cNNNN) guise, which has no caps profile and so
    /// otherwise shows the full ~420-row pile. Hides the two junk provenance buckets — Move (walk/run/swim/
    /// jump: ~92 rows) and React (hit/knockback/guard: ~16) — that no skill triggers and the guise's own
    /// locomotion already covers. Leaves the 353 playable rows. The "Show all timelines" checkbox flips this
    /// off, exactly mirroring the monster caps escape-hatch.</summary>
    private static bool IsPlayable(string key)
        => ProvenanceCategory(key) is not ("Move" or "React");

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
        var id = (ushort)t.Id;
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
        ("NPCs", "EventNpc"), ("Minion", "Minion"), ("Unknown", "Unknown"),
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

    public void Dispose() { }

    /// <summary>Tiny RAII helper for ImGui.BeginDisabled/EndDisabled.</summary>
    private readonly struct Disabled : IDisposable
    {
        public Disabled(bool disabled) => ImGui.BeginDisabled(disabled);
        public void Dispose() => ImGui.EndDisabled();
    }
}
