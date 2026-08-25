using System;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace HDM;

/// <summary>
/// HDM — client-side mob disguises with animation triggers.
///
/// Architecture (deliberately mirrors HOutfits):
///  - MobIndex: the offline-generated catalog (Data/mob-model-index.csv).
///  - GuiseService: ModelCharaId + scale write + redraw, with revert tracking.
///  - AnimationService: action-timeline blend/loop/stop, with revert tracking.
///  - MainWindow: catalog UI.
///
/// Separate plugin from HOutfits on purpose: the timeline/redraw code on
/// non-human CharacterBases is the CTD-prone surface, and this boundary maps
/// 1:1 onto the future DMS module's IPC needs ("apply guise X to actor Y",
/// "play timeline Z on actor Y").
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable Objects { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static ITargetManager Targets { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider Interop { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    // Possession (Task #40): IGameGui for the blue-dot overlay's WorldToScreen (consumed only by
    // PossessionService). The mirror model needs no signature scan — it measures the pilot via CS structs.
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static ITextureProvider Textures { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

#if HDM_TESTING
    // Testing build (HDM_TESTING / Debug): the command surface is /hdmt ONLY. It deliberately never
    // registers /hdm (nor the legacy /hguise alias) — a testing build answers to /hdmt and nothing else,
    // so you always know which build you're driving and it cannot shadow the public /hdm. InternalName and
    // config stay "HDM" (see the header note), so the testing build still reads your real HDM.json
    // favourites. Pairs with the HDMT window header — see MainWindow.cs.
    // internal (not private) so the MainWindow usage/help strings can render the *active* command
    // name — a testing build's on-screen help then reads "/hdmt apply …", never a "/hdm" it won't answer.
    internal const string Command = "/hdmt";
#else
    internal const string Command = "/hdm";
    // Legacy alias: the plugin shipped as HGuise through 0.8.54, so keep /hguise working for saved
    // macros / muscle memory. Hidden from the command list (ShowInHelp=false); delete for a clean break.
    private const string CommandAlias = "/hguise";
#endif

    // ImGui window-id suffix that keeps a testing build's SHARED-CODE windows from conjoining with a
    // co-loaded prod HDM. ImGui shares one global context across every loaded plugin, so two windows that
    // Begin() with the same id string merge into one. The main window diverges its ###id inline (see
    // MainWindow.cs); windows drawn from shared code (the possession dots) append this instead. Empty in
    // prod so those ids are byte-unchanged; "Testing" in the testing build. Never promoted (Debug-only).
#if HDM_TESTING
    internal const string BuildIdSuffix = "Testing";
#else
    internal const string BuildIdSuffix = "";
#endif

    private readonly WindowSystem _windows = new("HDM");
    private readonly MainWindow _main;
    private readonly GuiseService _guise;
    private readonly HumanGuise _humanGuise;
    private readonly AnimationService _anim;
    private readonly SpawnService _spawn;
    private readonly PossessionService _possession;
    private readonly HdmIpc _ipc;
    private readonly MobHarvester _harvest;

    public Plugin()
    {
        var config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var migrated = false;
        // v1 -> v2: migrate the old ApplyScale bool into the ScaleMode enum.
        if (config.Version < 2)
        {
            config.ScaleMode = config.ApplyScale ? 1 : 0;
            config.Version = 2;
            migrated = true;
        }
        // v2 -> v3: the Type filter used to default to Monster-only because c/d
        // couldn't render; now they can (Demihuman via equipment write, Human via
        // Glamourer), so the filter is just a convenience. Flip anyone still on the
        // old Monster-only default to All so c/d are visible without hunting for it.
        if (config.Version < 3)
        {
            if (config.TypeFilter == 0)
                config.TypeFilter = 1; // All
            config.Version = 3;
            migrated = true;
        }
        // v3 -> v4: the Scale UI dropped the explicit "Off" mode (ScaleMode 0). Scale is now a live
        // Native/custom multiplier with no "leave size untouched" chip, so a saved 0 would light no
        // scale chip and disguises would apply unscaled with nothing to explain it. Flip Off -> Native.
        if (config.Version < 4)
        {
            if (config.ScaleMode == 0)
                config.ScaleMode = 1; // Native
            config.Version = 4;
            migrated = true;
        }
        if (migrated)
            PluginInterface.SavePluginConfig(config);

        var timeline  = new TimelineIndex(PluginInterface, DataManager, Log);
        var content   = new ContentIndex(DataManager, Log);
        var territory = new TerritoryIndex(PluginInterface, Log);
        var instanced = new InstanceRosterIndex(PluginInterface, Log);
        // Tier M: hand-curated BNpcBase->TerritoryType tags for cutscene-script props that NO
        // automated tier can reach (the white YoRHa androids &co. — ModelChara=0 event-spawn props).
        // Highest priority in TryLocate: a verified tag overrides every estimate/inference.
        var manual    = new ManualLocationIndex(PluginInterface, Log);
        // Tier N: curated dungeon-NAME stems (Data/mob-dungeon-stems.csv). Places director-spawned
        // dungeon trash — which no flat sheet, roster, or crowdsource table can reach — by the dungeon
        // word in its name ("Babil slasher" -> Tower of Babil). Sits just below Tier M and above
        // crowdsource: one stem rule places a whole roster, and asserts authority over the scattered
        // telemetry that would otherwise misfile a model-reused mob (see DungeonStemIndex).
        var stems     = new DungeonStemIndex(PluginInterface, Log);
        var npc       = new NpcData(DataManager, Log);
        // Humanoid Event NPC set (ENpcBase — the same catalog Glamourer's "NPCs" tab shows), built live
        // from the game sheets and folded into MobIndex below. Their 1,000,000+ ids never collide with a
        // BNpcBase id (<20,000), so they share one Rows/_byBase with the offline catalog; every kept row
        // is McType 1 (Human) and renders through the existing Glamourer path — no new render code. Reuses
        // npc's Event readers to dedup by true appearance, so it's built after npc and before MobIndex.
        var eventNpcs = new EventNpcIndex(DataManager, npc, Log);
        // Heuristic name backfill: an unnamed catalog base that an instanced encounter roster
        // names (its headline boss) shows that name — e.g. base 19519 → "Chort". Resolver chains
        // roster base→NameId (Tier A2) then NameId→BNpcName; fires only for catalog-blank rows so
        // a crowdsourced name always wins. Built before MobIndex because it feeds the load.
        var index     = new MobIndex(PluginInterface, Log,
            baseId => instanced.TryGetName(baseId, out var nid) ? npc.ResolveBNpcName(nid) : null,
            eventNpcs.Rows);
        var lore      = new MobLoreIndex(DataManager, content, Log);
        // Tier D: deterministic BNpcBase->TerritoryType from the client's own Level sheet (Type 9 static
        // placements). Pure game-file id-join, orthogonal to the name/sighting tiers; regenerates per patch.
        var level     = new LevelPlacementIndex(DataManager, content, Log);
        var webloc    = new WebLocIndex(PluginInterface, DataManager, content, Log);
        // Minion/summon class: offline BaseId->minion-name tags for catalog rows that ARE summonable
        // minions. Orthogonal to the location tiers — a minion's home is a reward source, not a
        // TerritoryType, so it can never be "located"; this pulls the ~513 of them out of the Unknown
        // tail into their own catalog category + tree section (see CompanionIndex).
        var companion = new CompanionIndex(PluginInterface, DataManager, Log);
        // Home-zone label for each humanoid Event NPC (where they live), the Catalog's orientation aid
        // next to the NPC name. Same Level-sheet id-join as LevelPlacementIndex but on Type 8 (ENpcBase),
        // resolved to zone names via ContentIndex; render-time lookup keyed by the row's ENpcBase id.
        var enpcLoc   = new EnpcLocationIndex(DataManager, content, Log);
        // Tier A3: the DM's own runtime sightings — passively records BaseId->TerritoryType (+ live
        // names) while playing instanced content, the only source that reaches the dungeon/trial/raid
        // roster the offline tables can't (YoRHa androids &co.). Read-only, framework-thread, duty-only.
        _harvest      = new MobHarvester(Framework, Objects, ClientState, content, PluginInterface, Log);
        var glamour   = new GlamourerIpc(PluginInterface);
        // Penumbra consumer for the ONE call HDM makes to Penumbra: RedrawObject(self) on a Human-guise
        // revert. A DM's Penumbra-modded privacy glam only re-renders after Penumbra's own redraw (it
        // re-resolves the mod file paths); HDM's native GuiseService.Redraw can't (0.8.62). Inert when
        // Penumbra is absent (Redraw returns false → HumanGuise falls back to the native rebuild).
        var penumbra  = new PenumbraIpc(PluginInterface);
        // Thin consumer of HMoniker's "Moniker.*" IPC (lifted from HOutfits): drives the optional
        // "rename my nameplate to the disguise" toggle. HMoniker owns the local name + syncs it to peers
        // through HMS, so HDM only calls SetLocalName/ClearLocalName — no nameplate sync of its own. Inert
        // (toggle greys out) when HMoniker isn't installed; independently loadable, bound by string label.
        var moniker   = new MonikerIpc(PluginInterface);
        // Theme accent (0.8.70). AccentPalette is the shared accent engine (Config tab picker + derived tones);
        // HmsIpc is HDM's FIRST consumer of an HMS→modules provider — it reads HM-Sync's user-set accent over
        // IPC so the HM tool-suite shares one hue. Inert (HDM uses its own gold) until HM-Sync ships the accent
        // provider; see HmsIpc + docs/hms-accent-ipc-ask.md for the articulated ask. Independently loadable,
        // bound by string label — neither plugin hard-references the other.
        var hms       = new HmsIpc(PluginInterface);
        var accent    = new AccentPalette(config, hms);
        _guise        = new GuiseService(Framework, Objects, ClientState, npc, Log);
        // HumanGuise borrows GuiseService's native redraw machine to rebuild a cold first-spawn puppet's draw
        // object after a Glamourer paint (so its customize/Race renders — the equipment-lands-but-race-doesn't
        // fix); built after _guise so it can take the reference.
        _humanGuise   = new HumanGuise(glamour, penumbra, CommandManager, npc, _guise, ClientState, Objects, Framework, Log);
        _anim         = new AnimationService(Interop, Framework, Objects, ClientState, Log);
        // Actor spawn: bring blank BattleNpc puppets into the world for the caller to disguise through
        // the SAME objectIndex-keyed render services (Principle 1 — a spawned actor is a driven puppet).
        // Depends on _guise/_humanGuise/_anim (it Forgets all three's per-index tracking on despawn, so a
        // recycled puppet index can't inherit a stale model/offset/speed-pin/replay), so it's built after
        // them and disposed BEFORE them (delete puppets while those services are still live).
        _spawn        = new SpawnService(Framework, Objects, ClientState, _guise, _humanGuise, _anim, Log);
        // Cross-plugin IPC provider: lets HMS OBSERVE this DM's disguise/puppet state (to sync it to peers)
        // and DRIVE this client's actors to mirror a remote DM. Built after every render service it wraps
        // (it forwards to _guise/_humanGuise/_anim/_spawn) and after _spawn (it subscribes to puppet
        // lifecycle events); disposed FIRST in teardown so HMS stops calling receiver methods before the
        // services it drives are torn down. Independently loadable — HMS binds by string, gates on
        // HDM.ApiVersion; neither plugin hard-references the other.
        _ipc          = new HdmIpc(PluginInterface, index, _guise, _humanGuise, _anim, _spawn, Objects, Log);
        // Possession (Task #40): a DM "wears" a spawned puppet and drives it with FULL locomotion via the POSSESS
        // model — each frame the DM moves natively one step (generating the animation/position signal), we MEASURE
        // that native delta and accumulate it onto the puppet from its own spawn anchor, then SNAP the DM back to
        // its anchor (a frozen DM + a roaming puppet), and retarget the camera's orbit pivot onto the puppet via a
        // vfunc-18 (GetCameraTargetObject) hook — hence it needs the hook provider (Interop). Plus a click-to-wear
        // blue-dot overlay. Takes _guise (the human-skeleton gate) and _anim (the §4b one-writer timeline block).
        // Self-scoped to _spawn's puppets (never the player is driven; the player is only the pilot we measure), so
        // it's built after _spawn/_guise/_anim and disposed BEFORE _spawn (release + unsubscribe, and drop the
        // camera hook, while the puppets still exist).
        // The last arg wires possession's per-frame peer sync: each driven frame fires HdmIpc.ReportPuppetMoved,
        // which HMS's OnLocalPuppetMoved already consumes (reads the puppet's live tl0, broadcasts transform+anim).
        // _ipc is built above, so it's live here. HDM drives; HMS only transports.
        // ReportOwnBodyHidden mirrors possession's local Alpha=0 hide to peers so HMS suppresses the DM's own-body
        // mirror while driving (otherwise a peer sees the frozen DM standing next to the moving puppet).
        // Last arg (isRedrawing) lets possession PAUSE its per-frame timeline drive on a puppet while an explicit
        // re-guise is redrawing it: otherwise the drive's PlayTimeline fights the DisableDraw→EnableDraw rebuild
        // and the DM's own view stays on the OLD model while the un-possessed peer mirror updates (the focused-
        // puppet re-guise desync). Symmetric to SuppressReassert below, which stops the self-HEAL redraw from
        // nulling the same driven Timeline.
        _possession   = new PossessionService(Framework, Objects, ClientState, KeyState, GameGui, PluginInterface, Interop, _spawn, _anim, Log, _ipc.ReportPuppetMoved, _ipc.ReportOwnBodyHidden, _guise.IsRedrawing);
        // Don't let the guise self-heal re-assert (which redraws) fire on the puppet possession is piloting —
        // that redraw would null the Timeline its driven animation reads. The DM's own body isn't driven, so it
        // still self-heals normally; only the actively possessed index is suppressed.
        _guise.SuppressReassert = idx => _possession.PossessedIndex == idx;
        _possession.OverlayEnabled = config.ShowPossessionDots; // seed the possess-dot overlay from the persisted preference (default on)
        _possession.AllowPossessOthers = config.AllowPossessOthersPuppets; // seed the ownership gate (default off — only a puppet's originator drives it)
        _harvest.Enabled = config.HarvestMobNames; // seed the runtime name/territory harvester (default off — opt in via Config)
        _main         = new MainWindow(index, timeline, content, territory, manual, stems, instanced, lore, level, webloc, companion, enpcLoc, _harvest, _guise, _humanGuise, _anim, _spawn, _possession, _ipc, moniker, config, PluginInterface, Objects, Targets, ClientState, Textures, Log, accent);

        _windows.AddWindow(_main);

        PluginInterface.UiBuilder.Draw         += _windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi   += OpenMain;
        PluginInterface.UiBuilder.OpenConfigUi += OpenMain;

        CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the catalog (no arg). Subcommands: apply <name|BaseId> · spawn <name|BaseId> · despawn · revert · hide · wisp · help.",
        });
#if !HDM_TESTING
        CommandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand) { ShowInHelp = false });
#endif
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(Command);
#if !HDM_TESTING
        CommandManager.RemoveHandler(CommandAlias);
#endif
        PluginInterface.UiBuilder.Draw         -= _windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi   -= OpenMain;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenMain;
        _windows.RemoveAllWindows();

        // IPC first: broadcast Disposing + unregister the gates and unsubscribe puppet-lifecycle events
        // while _spawn (and the render services it drives) are all still live, so no in-flight HMS receiver
        // call lands on a half-torn-down service.
        _ipc.Dispose();
        // Possession before _spawn: release the freeze counter + camera retarget and unsubscribe the puppet
        // lifecycle event while the puppets it drives still exist (Release must run before they're deleted).
        _possession.Dispose();
        // Order matters: stop animation overrides before reverting models —
        // both revert game state, and both must run before teardown completes.
        _harvest.Dispose();
        _spawn.Dispose();    // delete every live puppet FIRST — it Forgets guise/anim tracking, so those services must still be live
        _anim.Dispose();
        _guise.Dispose();
        _humanGuise.Dispose();
        _main.Dispose();
    }

    private void OpenMain() => _main.IsOpen = true;

    /// <summary>
    /// Command surface. Bare <c>/hdm</c> toggles the window (unchanged). With an argument it drives the
    /// same self-actions the buttons do — so a DM can bind a macro ("/hdm wisp", "/hdm apply Ifrit",
    /// "/hdm revert") without opening the UI. The action methods live on MainWindow (they own the
    /// catalog + services) and hand back a short status line we echo to chat; the whole thing runs on the
    /// framework thread (command dispatch), same as the UI click handlers, so game access is safe.
    /// A bare unknown token is treated as an apply-by-name so "/hdm Ifrit" just works.
    /// </summary>
    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        if (trimmed.Length == 0) { _main.Toggle(); return; }

        var space = trimmed.IndexOf(' ');
        var verb = (space < 0 ? trimmed : trimmed[..space]).ToLowerInvariant();
        var rest = space < 0 ? "" : trimmed[(space + 1)..].Trim();

        var msg = verb switch
        {
            "revert" or "off" or "reset" => _main.CommandRevert(),
            "hide" or "unhide" or "show" => _main.CommandHide(),
            "wisp"                       => _main.CommandWisp(),
            "apply" or "become"          => _main.CommandApply(rest),
            "spawn"                      => _main.CommandSpawn(rest),
            "despawn" or "despawnall"    => _main.CommandDespawnAll(),
            "diag" or "dump"             => _main.CommandDiag(),
            // Dev-only: exercise the HMS IPC receiver path (apply/revert/play/spawn/snapshot) on a local
            // actor without HMS loaded — see MainWindow.CommandIpc. Not advertised in HelpText.
            "ipc"                        => _main.CommandIpc(rest),
            "open" or "menu" or "config" => OpenAndSilence(),
            "help" or "?"                => HelpText,
            _                            => _main.CommandApply(trimmed), // "/hdm Ifrit" => apply by name
        };
        if (!string.IsNullOrEmpty(msg))
            Chat.Print($"[HDM] {msg}");
    }

    private const string HelpText =
        "commands: apply <name|BaseId> · spawn <name|BaseId> · despawn · revert · hide · wisp · open · (no argument opens the window).";

    private string OpenAndSilence() { _main.IsOpen = true; return ""; }
}
