using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Glamourer.Api.Enums;
using Newtonsoft.Json.Linq;
using EquipmentModelId = FFXIVClientStructs.FFXIV.Client.Game.Character.EquipmentModelId;

namespace HDM;

/// <summary>
/// The Human (ModelChara.Type==1, c-skeleton) guise path — the third leg of the
/// "one-stop shop" alongside Monster/Demihuman in <see cref="GuiseService"/>.
///
/// Unlike Monster/Demihuman, this path does NOT swap ModelCharaId. A human NPC IS
/// a c-skeleton body; the swap target would be the player's own skeleton anyway.
/// What makes it *look* like the NPC is customize (face/body/colours) + gear, and
/// that surface belongs to Glamourer — a direct customize write on a player actor
/// is scrubbed by the game's FilterCustomizeData every redraw. So we paint the NPC
/// through Glamourer's ApplyState: clone the actor's current Glamourer state (a
/// JObject Glamourer definitely accepts), overwrite the Customize and Equipment
/// blocks with the NPC's (the FULL customize incl. the Race byte, so the disguise
/// takes the NPC's whole skeleton), then clear the DM's advanced overrides so no
/// "trace of the original actor" survives at render. Clearing takes TWO moves, not one
/// (see TryApplyOnce): (1) STRIP the Parameters/Materials blocks from the outgoing
/// JObject — GetState emits them with Apply=true under ApplicationRules.All, and the
/// DM's SkinDiffuse / lip / face-paint would otherwise re-paint over the NPC's basic
/// customize bytes; and (2) actively REVERT the live actor to game base
/// (RevertToGameBase => StateManager.ResetState) right before ApplyState, because a
/// PRIOR apply may have already pinned the DM's SkinDiffuse into Glamourer's persistent
/// state and an absent Parameters block only means "leave the actor's parameters
/// unchanged" — it can't clear an existing pin. Strip-only was 0.8.49 and failed
/// in-game for exactly that reason (skin byte stored yet skin rendered pale); the
/// revert wipes the pin (ModelData<=BaseData, every parameter source => Game, Materials
/// cleared), then the stripped ApplyState repaints the NPC onto the clean actor,
/// mirroring Glamourer's own "Apply NPC to Yourself" (NpcFromModifiers: parameters=0,
/// materials=false). Move (2) is LOCAL-PLAYER ONLY: a spawned puppet is a fresh Glamourer
/// identity that can't carry a pin, and on a cold first-spawn the revert's extra redraw
/// races the apply and leaves the puppet a bare DM clone — so puppets skip it (the 0.8.51
/// gate). The snapshot is cloned before the strip, so /hdm revert still restores the
/// player's true skin. See WriteCustomize for the customize overwrite.
///
/// Self-contained on purpose — the CustomItemId / CustomizeMap encoding is copied
/// from HOutfits' NpcStateBuilder rather than depended on (project directive:
/// duplication is preferred over coupling HDM to HOutfits).
///
/// Graceful degradation: if Glamourer isn't installed, Apply logs and no-ops (the
/// catalog still lists human rows; they just won't paint). Revert tracking is by
/// object index and cleared on territory change / logout, same as GuiseService.
/// </summary>
public sealed class HumanGuise : IDisposable
{
    private readonly GlamourerIpc _glam;
    // Penumbra redraw, used on a SINGLE path: self-revert of a human guise. A DM's Penumbra-modded
    // privacy glam only re-renders correctly after Penumbra's OWN redraw (re-resolves the mod file
    // paths); HDM's native GuiseService.Redraw can't (0.8.62 proved it). Falls back to the native
    // redraw when Penumbra is absent. See PenumbraIpc for the "why not native" write-up.
    private readonly PenumbraIpc _penumbra;
    // Dalamud command dispatch — used to run the LITERAL "/penumbra redraw self" on a self-revert (the exact
    // command the DM confirmed restores their look), routed straight into Penumbra's own command handler.
    private readonly ICommandManager _commandManager;
    private readonly NpcData _npc;
    private readonly GuiseService _guise;
    private readonly IClientState _clientState;
    private readonly IObjectTable _objects;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;

    // Object indices this plugin has human-guised (so Revert only reverts ours).
    private readonly HashSet<int> _guised = [];

    // Pre-guise Glamourer state per object index, captured ONCE before the first paint. Revert restores
    // this — the player's own glamour (their real outfit) — instead of Glamourer's blanket revert-to-base,
    // which strips the user's gear and leaves the "weird clothes" the bare NPC customize implies.
    private readonly Dictionary<int, JObject> _snapshots = new();

    // Human-guise applies whose Glamourer GetState came back null and are awaiting a retry. This is the fix
    // for "the FIRST spawned NPC puppet paints as a bare DM-clone, later ones are correct": a just-spawned,
    // freshly-named puppet isn't resolvable by Glamourer for a frame or two after its draw object goes
    // visible — GetState/ApplyState route through Glamourer's FindState → objects.Objects[idx] +
    // stateManager.GetOrCreate, which reads Glamourer's OWN per-frame actor/state cache; that cache is cold
    // on the session's first spawn and returns null until Glamourer's next tick registers the new actor. A
    // one-shot apply that lands in that gap bails and leaves the clone unpainted; by the second puppet the
    // cache is warm, so it "works from then on". Rather than guess a fixed extra delay, we re-attempt on the
    // framework thread until GetState succeeds or the budget runs out. Keyed by object index (one pending
    // apply per actor; a fresh Apply or a Revert supersedes an older pending one).
    private readonly Dictionary<int, PendingApply> _pending = new();

    private sealed class PendingApply
    {
        public uint      BaseId;
        public string    DisplayName = "";
        public NpcSource Source;
        public int       Attempts;   // StateNull attempts spent while waiting for Glamourer to resolve the actor
    }

    // ~2s at 60fps. The gap is normally 1–2 frames on a WARM client (the DM's own machine, where a prior
    // self-guise already primed Glamourer's c-skeleton pipeline). But a peer driving a MIRROR puppet
    // (HdmIpc.SpawnPuppet) may have a stone-cold Glamourer that has never registered a puppet this session,
    // so first registration can take materially longer than the old 0.5s bound — which timed out and left
    // the mirror a bare DM-clone (the 0.8.55 Human-peer-sync gap). Raised from 30 to give a cold peer firm
    // headroom; the retry is one cheap GetState per frame, and OnUpdate logs the ACTUAL frames-to-resolve so
    // we can see whether even this is tight on a real peer. Still bounded so a genuinely unregisterable actor
    // can't retry forever (the enriched timeout log then dumps why — see OnUpdate).
    private const int MaxApplyAttempts = 120;

    public HumanGuise(GlamourerIpc glam, PenumbraIpc penumbra, ICommandManager commandManager, NpcData npc, GuiseService guise, IClientState clientState, IObjectTable objects, IFramework framework, IPluginLog log)
    {
        _glam = glam;
        _penumbra = penumbra;
        _commandManager = commandManager;
        _npc = npc;
        _guise = guise;
        _clientState = clientState;
        _objects = objects;
        _framework = framework;
        _log = log;

        _clientState.TerritoryChanged += OnTerritoryChanged;
        _clientState.Logout += OnLogout;
        _framework.Update += OnUpdate;
    }

    /// <summary>True if Glamourer is present so the human path can actually paint.</summary>
    public bool Available => _glam.Available;

    public bool IsGuised(int objectIndex) => _guised.Contains(objectIndex);

    /// <summary>
    /// Paint a human NPC onto the actor at <paramref name="objectIndex"/> via Glamourer. Call from the
    /// framework thread (UI Draw is fine). No-op with a warning if Glamourer is absent or the actor's
    /// state can't be read. <paramref name="source"/> selects which sheet the customize+gear come from:
    /// <see cref="NpcSource.Battle"/> reads a BNpcBase (default, unchanged behaviour);
    /// <see cref="NpcSource.Event"/> reads the ENpcBase inline appearance (the humanoid Event NPC set).
    /// </summary>
    public void Apply(int objectIndex, uint baseId, string displayName, NpcSource source = NpcSource.Battle)
    {
        if (!_glam.Available)
        {
            _log.Warning($"Guise: Glamourer not installed — human guise '{displayName}' (base {baseId}) cannot render. Install Glamourer to use Human-type guises.");
            return;
        }

        // A fresh apply supersedes any pending retry for this actor.
        _pending.Remove(objectIndex);

        // Paint now, then decide what follow-up the actor needs:
        //   • StateNull        — Glamourer can't resolve the actor yet (cold first-spawn): queue a per-frame
        //                        retry until it resolves (bounded), instead of bailing to a permanent DM-clone.
        //   • Applied          — the paint STORED into Glamourer, but the draw object may render the equipment
        //                        swap in place yet NOT the customize/Race change (the skeleton never rebuilds).
        //                        On a PUPPET that's the cold first-spawn gap (the first puppet keeps the DM's
        //                        race until a later spawn warms the pipeline — the "first spawn caches the last
        //                        disguise" report). On SELF it's the self-only RevertToGameBase wipe
        //                        (TryApplyOnce move 2) redrawing the local player to game-base in the SAME tick
        //                        as the NPC ApplyState: on a cold apply the wipe's redraw wins, so the first
        //                        apply shows the DM's TRUE race + basic gear and the NPC lands only on a second
        //                        apply (the "first click reverts to the original race, second click applies the
        //                        disguise" report). Either way, force a draw-object rebuild + re-assert so
        //                        Glamourer re-applies the stored NPC customize/Race onto the fresh, warm
        //                        skeleton — the automatic equivalent of that working "second click".
        var outcome = TryApplyOnce(objectIndex, baseId, displayName, source);
        if (outcome == ApplyOutcome.StateNull)
        {
            _pending[objectIndex] = new PendingApply { BaseId = baseId, DisplayName = displayName, Source = source, Attempts = 1 };
            // A PUPPET (peer mirror or spawned set-piece) hitting a cold Glamourer is the case we're chasing —
            // surface its retry at Information so it shows in /xllog without a Debug filter; self stays quiet.
            if (IsLocalPlayer(objectIndex))
                _log.Debug($"Guise: Glamourer GetState(obj#{objectIndex}) not ready for human guise '{displayName}' — retrying (cold race).");
            else
                _log.Information($"Guise: Glamourer GetState(puppet obj#{objectIndex}) not ready for human guise '{displayName}' — retrying up to {MaxApplyAttempts} frames (cold-spawn race).");
        }
        else if (outcome == ApplyOutcome.Applied && !IsLocalPlayer(objectIndex))
        {
            // PUPPET-ONLY. This native draw-object rebuild fixes the cold FIRST-SPAWN render gap (the first
            // puppet keeps the DM's race until a later spawn warms the pipeline). SELF does NOT reach here: a
            // self first-apply now returns ApplyOutcome.Deferred from TryApplyOnce — it DEFERS the NPC paint
            // until the RevertToGameBase redraw settles (the proper "defer the re-assert" self fix; see
            // DeferSelfApply), and a self re-apply lands inline uncontested, so neither self case is Applied
            // here. History: 0.8.66 wrongly extended this rebuild to SELF and REGRESSED apply to "never lands"
            // (the onSettled re-assert hit StateNull on the half-rebuilt local draw object); DeferSelfApply
            // avoids that by waiting a fixed delay for Glamourer's commit instead of chaining off the rebuild.
            RedrawGuise(objectIndex, baseId, displayName, source);
        }
    }

    // A puppet is any guised actor that isn't the local player. Only the local player can hold a legacy
    // SkinDiffuse pin (so only self gets RevertToGameBase), and only puppets get the cold-draw rebuild.
    private bool IsLocalPlayer(int objectIndex) => objectIndex == (_objects.LocalPlayer?.ObjectIndex ?? -1);

    // NOTE (0.8.68 → 0.9.1): callers gate this to PUPPETS only — self was reverted out after 0.8.66's self
    //   rebuild regressed apply to "never lands" (see the Apply call site). The proper self fix HAS now
    //   landed (0.9.1: DeferSelfApply defers the NPC paint past the RevertToGameBase redraw), so self never
    //   reaches RedrawGuise anymore; the self/RevertToGameBase notes below are retained as history only.
    // A just-painted guise (self OR puppet) whose customize/Race may not have RENDERED even though ApplyState
    // STORED it: the draw object swaps equipment in place but a Race change needs a skeleton rebuild the paint
    // path didn't force. On a PUPPET that's the cold first-spawn gap — the first puppet keeps the DM's race
    // while every later one is right (the "first spawn caches the last disguise" report — race half; 0.8.53's
    // spaced re-paints fixed the equipment half, but a re-ApplyState of the SAME stored state can't force the
    // skeleton rebuild a Race change needs). On SELF it's the self-only RevertToGameBase wipe (TryApplyOnce
    // move 2) racing the NPC ApplyState in the same tick, so a cold apply renders the DM's true race + basic
    // gear and the NPC lands only on a second apply (the "first click reverts to the original race, second
    // click applies the disguise" report). Force a draw-object rebuild through GuiseService's redraw machine: on
    // the rebuild Glamourer re-applies its stored NPC customize+Race onto the fresh skeleton — the same reason
    // the Monster path (which always redraws via Apply) is right on its first apply, and what Penumbra's redraw
    // does for Glamourer minus the Penumbra dependency. Native is enough HERE (unlike the REVERT path's
    // DeferSelfRedraw, which restores the DM's Penumbra-MODDED privacy glam and so needs Penumbra's own redraw
    // to re-resolve those paths): the apply paints a STOCK NPC with no modded paths to re-resolve. The onSettled
    // re-assert re-pushes the NPC state onto the rebuilt, warm draw object — the automatic equivalent of the
    // user's working "second click" — so the guise lands even if Glamourer doesn't auto-re-apply on a native
    // redraw; it's gated on the actor still being guised so a Revert during the sub-second rebuild window can't
    // be undone by a late re-paint.
    private void RedrawGuise(int objectIndex, uint baseId, string displayName, NpcSource source)
    {
        _guise.Redraw(objectIndex, onSettled: () =>
        {
            if (_guised.Contains(objectIndex))
                TryApplyOnce(objectIndex, baseId, displayName, source, verbose: false);
        });
        _log.Debug($"Guise: obj#{objectIndex} '{displayName}' painted — forcing a draw-object rebuild + re-assert so the NPC customize/Race renders (self: RevertToGameBase-vs-ApplyState race; puppet: cold-spawn render gap).");
    }

    private enum ApplyOutcome { Applied, StateNull, NothingToPaint, Deferred }

    /// <summary>One paint attempt. Returns <see cref="ApplyOutcome.StateNull"/> when Glamourer can't yet
    /// resolve the actor (the caller queues a retry), <see cref="ApplyOutcome.NothingToPaint"/> when the NPC
    /// has neither customize nor equip (terminal), or <see cref="ApplyOutcome.Applied"/> on success. Assumes
    /// Glamourer is available — both callers (<see cref="Apply"/> and <see cref="OnUpdate"/>) check first.</summary>
    private ApplyOutcome TryApplyOnce(int objectIndex, uint baseId, string displayName, NpcSource source, bool verbose = true)
    {
        var state = _glam.GetState(objectIndex);
        if (state is null)
            return ApplyOutcome.StateNull;

        // Snapshot the TRUE pre-guise state, once. Re-applying a guise (swapping NPC faces without an
        // intervening Revert) must not overwrite this with an already-guised state, or Revert would
        // restore a disguise instead of the player's own look.
        if (!_snapshots.ContainsKey(objectIndex))
            _snapshots[objectIndex] = (JObject)state.DeepClone();

        var customize = _npc.TryGetCustomize(baseId, source);
        var equip     = _npc.TryGetEquipment(baseId, source);

        if (customize == null && equip == null)
        {
            _log.Warning($"Guise: base {baseId} ({source}) has neither customize nor equipment — nothing to paint for human guise '{displayName}'.");
            return ApplyOutcome.NothingToPaint;
        }

        // Captured for the post-apply readback diagnostic below (did Glamourer actually STORE the
        // NPC head/body we wrote, or reset the slot?). 0 = we didn't write that slot this call.
        ulong headWritten = 0, bodyWritten = 0;
        string headBefore = "-", bodyBefore = "-";
        // Customize before-values (the DM's OWN skin/race/clan/face, read from the pre-write state) — the
        // instrument for the "skin colour is inherited from the DM, not the NPC" report. Paired with the
        // authored bytes (customize[]) and the post-apply stored values, this gives a before->written->stored
        // triplet: stored==written => the guise DID paint the NPC skin (any residual DM-skin look is the
        // unpainted first-spawn clone, or a model/material feature customize can't reproduce); stored==before
        // => Glamourer rejected/reset the customize (a real apply bug). Captured BEFORE WriteCustomize mutates
        // custObj in place, so these hold the player's own values, not what we're about to write.
        string skinBefore = "-", raceBefore = "-", clanBefore = "-", faceBefore = "-";

        if (equip != null && state["Equipment"] is JObject equipObj)
        {
            // The disguise's HEAD is AUTHORITATIVE — no dependency on the player's helmet state, real
            // or glamoured. WriteEquipment writes the NPC head item when the NPC has one (ModelHead !=
            // 0) and CLEARS the slot to "Nothing" when it doesn't, so the player's own headgear can
            // never bleed onto the disguise. SetHeadgearShown then forces the "Hide Head Gear" metadata
            // to match: show the NPC helm/hood/mask past the player's hide-headgear preference; hide
            // when the NPC is bare so the cleared slot stays empty and the NPC's own face shows.
            var npcHeadModel = equip.Length > 0 ? equip[0].Id : (ushort)0;
            headBefore = SlotItemId(equipObj, "Head");
            bodyBefore = SlotItemId(equipObj, "Body");
            if (equip.Length > 0 && equip[0].Id != 0) headWritten = CustomItemId(equip[0].Id, equip[0].Variant, 1);
            if (equip.Length > 1 && equip[1].Id != 0) bodyWritten = CustomItemId(equip[1].Id, equip[1].Variant, 2);

            WriteEquipment(equipObj, equip);
            SetHeadgearShown(equipObj, npcHeadModel != 0);
        }

        // #79: adopt the NPC's native weapon(s). Every NPC carries a weapon (the game forbids
        // weaponless characters); a Human guise that stops at armor leaves the puppet holding the
        // DM's OWN weapon, breaking the disguise. Weapons live in the SAME Glamourer Equipment
        // object under "MainHand"/"OffHand" with the identical per-slot schema as armor — the only
        // differences are that the CustomItemId carries a NON-ZERO secondary (the weapon's Type/Base
        // model, whereas armor's is 0) and a weapon FullEquipType sentinel (UnknownMainhand/
        // UnknownOffhand: the NPC sheet doesn't carry the specific weapon category, but the sentinels
        // route the model to the correct hand and the mesh loads from Set/Type/Variant — no ClassJob
        // is involved, mirroring Glamourer's own NPC-tab weapon application). Encoding verified
        // verbatim against HOutfits' NpcStateBuilder. Read independently of the armor block so a
        // weapon still lands even if the armor read came back null. Applied under the existing
        // ApplyFlag.Equipment (weapons share the Equipment object — no separate flag).
        if (state["Equipment"] is JObject weaponObj)
        {
            var (mainHand, offHand) = _npc.TryGetWeapons(baseId, source);
            if (mainHand is { } mh) WriteWeapon(weaponObj, "MainHand", mh, EquipTypeMainHand);
            if (offHand  is { } oh) WriteWeapon(weaponObj, "OffHand",  oh, EquipTypeOffHand);
            if (verbose && (mainHand != null || offHand != null))
                _log.Information(
                    $"Guise[weapon] obj#{objectIndex} '{displayName}' base {baseId} ({source}): " +
                    $"MainHand={(mainHand is { } m ? $"{m.Set}-{m.Type}-{m.Variant} dye {m.Dye}/{m.Dye2}" : "none")} " +
                    $"OffHand={(offHand is { } o ? $"{o.Set}-{o.Type}-{o.Variant} dye {o.Dye}/{o.Dye2}" : "none")}.");
        }

        if (customize != null && state["Customize"] is JObject custObj)
        {
            // Diagnostic for the "skin colour / cosmetics inherit from the DM, not the NPC" report (and the
            // earlier 2B "keeps the player's hair colour even though face/hairstyle DO apply" report). Skin
            // (byte 8), Face (byte 5), Hair (byte 10) all go through the same WriteCustomize path — full 0xFF
            // mask, no shift, Apply=true — so a miss on skin/colour alone is either (a) the authored value
            // isn't what the eye expects (2B's white hair lives in her model/material, not the customize
            // index, so a paint-on-player body can't reproduce it — the same could be true of some special
            // NPC skins), or (b) Glamourer rejects/resets the customize on apply. Capture the DM's OWN values
            // NOW (before WriteCustomize overwrites custObj) so the readback below can show before->written->
            // stored and tell (a) from (b) — instrument, don't guess.
            // (Raised to Information while actively chasing the skin report; demote to Debug once settled.)
            skinBefore = CustValue(state, "SkinColor");
            raceBefore = CustValue(state, "Race");
            clanBefore = CustValue(state, "Clan");
            faceBefore = CustValue(state, "Face");
            if (verbose)
                _log.Information(
                    $"Guise[human-diag] base {baseId} '{displayName}' authored BNpcCustomize: " +
                    $"Race={customize[0]} Gender={customize[1]} Clan={customize[4]} Face={customize[5]} " +
                    $"Hairstyle={customize[6]} SkinColor={customize[8]} HairColor={customize[10]} " +
                    $"Highlights={((customize[7] & 0x80) != 0 ? 1 : 0)} HighlightsColor={customize[11]} " +
                    $"EyeColorR={customize[9]} EyeColorL={customize[15]}; " +
                    $"Glamourer state carries SkinColor key={custObj["SkinColor"] is JObject} HairColor key={custObj["HairColor"] is JObject}");
            WriteCustomize(custObj, customize);
        }

        // Clear the DM's advanced overrides so NO trace of the original actor survives at render. This needs
        // TWO moves, because they cover different halves of the problem and neither alone is sufficient:
        //
        //  (1) STRIP the outgoing JObject's Parameters + Materials blocks. GetState builds the JObject with
        //      ApplicationRules.All, so it emits BOTH with Apply=true: "Parameters" carries the
        //      CustomizeParameters — SkinDiffuse ("Skin Color" as float RGB), LipDiffuse, FeatureColor and the
        //      FacePaint colours — and "Materials" carries the skin colour-tables. Left in, they re-paint the
        //      DM's skin over the NPC's basic SkinColor byte at render. Removing them stops THIS apply writing
        //      them. (ApplyFlag can't suppress them: DesignConverter.FromJObject only ever RemoveCustomize/
        //      RemoveEquip, never parameters/materials — verified in Glamourer source.)
        //
        //  (2) REVERT the LIVE actor to game base first. Strip-only was 0.8.49 and it FAILED in-game — the
        //      NPC's SkinColor byte stored correctly yet the skin still rendered the DM's pale shade. Cause: a
        //      PRIOR apply had already pinned the DM's SkinDiffuse into Glamourer's persistent actor state, and
        //      an absent Parameters block means "leave the actor's parameters unchanged" (Glamourer
        //      LoadParameters(null) => Application.Parameters = 0). So the strip PRESERVES an existing pin
        //      instead of clearing it. RevertToGameBase routes to StateManager.ResetState, which copies
        //      BaseData over ModelData, sets every CustomizeParameter source back to Game and clears Materials
        //      (verified in Glamourer source) — i.e. it wipes the pinned SkinDiffuse. We revert FIRST, then the
        //      stripped ApplyState repaints the NPC customize+equip onto the now-clean actor; with Parameters
        //      gone nothing re-pins, so the NPC's basic SkinColor byte drives the skin, exactly as Glamourer's
        //      own "Apply NPC to Yourself" (NpcFromModifiers: parameters=0, materials=false).
        //
        //      Move (2) is gated to the LOCAL PLAYER. Only the local player can hold such a pin — it's left in
        //      Glamourer's PERSISTENT per-actor state by a prior buggy build that painted the DM. A spawned
        //      puppet is a brand-new Glamourer identity (unique name, NameId 0) no build has ever painted, so
        //      its parameters are already Game-sourced and Materials empty: the revert would clear nothing. And
        //      on a COLD first-spawn actor the revert's redraw-to-clone-base races the ApplyState redraw and the
        //      puppet sticks as a bare DM clone ("the first spawn copies the DM, the second is correct" — the
        //      0.8.50 regression). Puppets therefore skip the revert and go straight to the stripped ApplyState,
        //      which is already the NpcFromModifiers shape and paints NPC skin correctly on a fresh actor.
        //
        // The snapshot cloned above kept both blocks (it's taken before this strip), so /hdm revert still
        // restores the player's true skin. The revert (self only) runs after GetState succeeded, so the
        // cold-spawn retry loop (which returns StateNull before reaching here) never triggers a revert storm.
        var hadParams    = state["Parameters"] is JObject pj && pj.Count > 0;
        var hadMaterials = state["Materials"] is JObject mj && mj.Count > 0;
        state.Remove("Parameters");
        state.Remove("Materials");

        // Self, FIRST apply of a guise-session only: wipe any legacy SkinDiffuse pin (see move (2) above).
        // Gated to the transition FROM the DM's true self (isLocalPlayer && NOT already _guised) because
        // RevertToGameBase schedules a redraw-to-game-base that competes with THIS tick's NPC ApplyState — and
        // on a RE-apply (swapping NPC faces while already disguised, or re-clicking the same row) that
        // competition is deterministic and the revert WINS, so the DM saw their TRUE race + basic gear on every
        // second click ("go down the list and every other disguise reverts to the original" — the 0.8.68
        // alternation; the "basic gear" is the game-base smallclothes RevertToGameBase resets to). The pin only
        // needs clearing ONCE (a legacy artifact of an old build that painted the DM; current applies strip
        // Parameters/Materials so none is ever re-created), so skipping it on re-applies is safe AND kills the
        // alternation: after the first apply no revert runs, so ApplyState lands uncontested every time. The gate
        // RE-ARMS after a /hdm revert (which drops the index from _guised and restores the DM to true self, where
        // the wipe is a harmless base->base no-op again). A puppet is a fresh identity with no pin (and the
        // revert's redraw would race its cold first-spawn), so it also skips — isLocalPlayer already excluded it.
        var isLocalPlayer = IsLocalPlayer(objectIndex);
        if (isLocalPlayer && !_guised.Contains(objectIndex))
        {
            // Self, FIRST apply of this guise-session — cold from the DM's true self, OR (the case the old
            // gate comment missed) the first Human apply after a Monster/Demi detour: a monster guise never
            // enters _guised, so a Monster→Human SWITCH lands here too, not just a cold start. The revert IS
            // correct here — it wipes the DM's LIVE skin params (a legacy SkinDiffuse pin, and/or the DM's own
            // real skin, which the Monster→Human path re-established via HumanGuise.Revert's snapshot restore)
            // so the absent Parameters block below can't PRESERVE them onto the NPC. But RevertToGameBase
            // schedules a redraw-to-game-base that, fired in THIS tick, DETERMINISTICALLY WINS the race against
            // the ApplyState paint — so the DM saw their TRUE race + basic (smallclothes) gear on the first
            // click and the NPC only landed on a SECOND ("flipping between different disguises requires a
            // second application"; "every other disguise reverts to the original"). Fix: don't paint inline.
            // Latch _guised, run the revert, and DEFER the NPC paint a few ticks until the revert redraw has
            // SETTLED — the automatic "second click" the 0.8.68 note prescribed ("defer the re-assert"). The
            // deferred re-run sees _guised.Contains == true, so it skips the revert and ApplyStates uncontested
            // (and falls to the _pending per-frame retry if Glamourer can't resolve the freshly-redrawn draw
            // object yet). The snapshot was captured ABOVE (before the strip/revert), so /hdm revert still
            // restores the DM's true look regardless of the deferral.
            var rev = _glam.RevertToGameBase(objectIndex);
            _guised.Add(objectIndex);
            if (verbose)
                _log.Information(
                    $"Guise[human-diag] obj#{objectIndex} '{displayName}': cleared DM advanced overrides before apply — " +
                    $"stripped JObject (Parameters present={hadParams}, Materials present={hadMaterials}) + reverted live " +
                    $"actor to game base ({rev}) to wipe pinned/live SkinDiffuse (first apply of this guise-session); " +
                    $"DEFERRING the NPC paint {SelfRedrawDelayTicks} ticks so it lands after the revert redraw settles " +
                    $"(the automatic 'second click').");
            DeferSelfApply(objectIndex, baseId, displayName, source);
            return ApplyOutcome.Deferred;
        }

        if (verbose)
            _log.Information(
                $"Guise[human-diag] obj#{objectIndex} '{displayName}': cleared DM advanced overrides before apply — " +
                $"stripped JObject (Parameters present={hadParams}, Materials present={hadMaterials}) + " +
                $"{(isLocalPlayer ? "skipped revert (already guised — re-apply lands uncontested)" : "skipped revert (fresh puppet — no pin to clear; the revert's redraw would race the cold first-spawn)")} " +
                $"so the NPC's basic customize renders.");

        var ec = _glam.ApplyState(state, objectIndex, ApplyFlag.Equipment | ApplyFlag.Customization);
        _guised.Add(objectIndex);

        // Principle-2 readback: re-read the state Glamourer actually stored and compare it to what we
        // wrote. This is the instrument that distinguishes the two live hypotheses for the "Ascian has
        // a mask but no hood, and the player's hair shows through" report:
        //   (a) our head/body CustomItemId is REJECTED/reset by Glamourer -> stored != written -> the
        //       slot fell back to the player's item (or Nothing); the write is the bug.
        //   (b) it stores exactly as authored -> stored == written -> the hood lives in a slot we
        //       don't drive (it's body geometry that renders as-authored) — a model/data question, not
        //       an apply bug. Hair values are dumped too so "player's hair pokes through" traces to
        //       whether Customize actually took. Demote to Debug once the mechanism is nailed down.
        // Skin/Race/Clan/Face before->written->stored is the definitive read for the "inherits the DM's skin"
        // report: if stored==written the NPC customize DID land (the residual DM look is the unpainted
        // first-spawn clone or a model/material feature), if stored==before Glamourer rejected it. "written"
        // is blank when this apply had no customize (equip-only). Raised to Information while chasing; demote
        // once settled. Skipped on the quiet re-paints (verbose:false) so the spaced render-gap re-pushes don't
        // spam the log — and it also saves the extra GetState round-trip on each of them.
        if (verbose)
        {
            var applied = _glam.GetState(objectIndex);
            var wroteCust = customize != null;
            _log.Information(
                $"Guise[readback] obj#{objectIndex} '{displayName}' base {baseId}: " +
                $"Head before={headBefore} written={headWritten} stored={SlotItemId(applied?["Equipment"] as JObject, "Head")}; " +
                $"Body before={bodyBefore} written={bodyWritten} stored={SlotItemId(applied?["Equipment"] as JObject, "Body")}; " +
                $"Hat stored Show={HatShow(applied)}; " +
                $"SkinColor before={skinBefore} written={(wroteCust ? customize![8].ToString() : "-")} stored={CustValue(applied, "SkinColor")}; " +
                $"Race before={raceBefore} written={(wroteCust ? customize![0].ToString() : "-")} stored={CustValue(applied, "Race")}; " +
                $"Clan before={clanBefore} written={(wroteCust ? customize![4].ToString() : "-")} stored={CustValue(applied, "Clan")}; " +
                $"Face before={faceBefore} written={(wroteCust ? customize![5].ToString() : "-")} stored={CustValue(applied, "Face")}; " +
                $"Hairstyle stored={CustValue(applied, "Hairstyle")} HairColor stored={CustValue(applied, "HairColor")}.");
        }

        _log.Information($"Guise: obj#{objectIndex} -> human NPC '{displayName}' (base {baseId}) via Glamourer ApplyState ({ec}).");
        return ApplyOutcome.Applied;
    }

    // Drain pending human-guise retries on the framework thread (see _pending). Cheap when idle. Each pending
    // apply re-attempts until Glamourer resolves the actor (Applied) or the frame budget is spent; a terminal
    // "nothing to paint" also clears it. If Glamourer vanished mid-retry nothing will ever resolve, so drop.
    private void OnUpdate(IFramework _)
    {
        if (_pending.Count == 0) return;
        foreach (var objectIndex in new List<int>(_pending.Keys))
        {
            if (!_pending.TryGetValue(objectIndex, out var p)) continue;
            if (!_glam.Available) { _pending.Remove(objectIndex); continue; }

            // Retry every frame until Glamourer resolves the actor (Applied / NothingToPaint) or the attempt
            // budget runs out — the cold-spawn StateNull retry.
            var outcome = TryApplyOnce(objectIndex, p.BaseId, p.DisplayName, p.Source);
            if (outcome == ApplyOutcome.StateNull)
            {
                if (++p.Attempts > MaxApplyAttempts)
                {
                    _pending.Remove(objectIndex);
                    // Enriched timeout: dump the actor's LIVE draw state so a genuine failure localizes in
                    // one test instead of the next speculative build. This is the discriminating read for the
                    // Human-peer-sync gap — if the puppet is drawn+visible with a valid player identity yet
                    // Glamourer still won't return state, the identifier is being rejected (pursue the
                    // name-keyed apply); if the draw object is null/hidden, the spawn settle is the culprit;
                    // if Glamourer isn't registered at all, the peer simply lacks it.
                    _log.Warning(
                        $"Guise: Glamourer never returned state for obj#{objectIndex} after {MaxApplyAttempts} frames — " +
                        $"human guise '{p.DisplayName}' (base {p.BaseId}) not applied. " +
                        $"Glamourer={( _glam.Available ? "registered" : "ABSENT")}; actor: {_guise.DescribeActor(objectIndex)}.");
                }
                continue; // keep waiting for registration
            }

            // Resolved: drop the retry. Log the ACTUAL frames it took Glamourer to register this actor —
            // reaching OnUpdate at all means the synchronous first paint hit StateNull (a cold client), so
            // this only fires for the cold case and directly measures whether the budget is right for a peer.
            _pending.Remove(objectIndex);
            _log.Information($"Guise: human guise obj#{objectIndex} '{p.DisplayName}' resolved after {p.Attempts} cold-spawn retry frame(s) ({outcome}).");
            // Then force a draw-object rebuild so the customize/Race renders (the cold-draw render gap the
            // readback can't see). PUPPET-ONLY (the !IsLocalPlayer guard, 0.8.68): 0.8.66's self rebuild
            // regressed self-apply to "never lands". As of 0.9.1 the self RevertToGameBase-vs-ApplyState
            // race is handled elsewhere — a self FIRST apply returns Deferred from TryApplyOnce (revert +
            // DeferSelfApply after the redraw settles) and never queues here; if a DeferSelfApply requeue
            // does land a self index here (cold Glamourer during the deferred paint), _guised is already set
            // so the re-run skips the revert and resolves uncontested — still no wiping rebuild for self.
            // Puppets get the rebuild for the cold-spawn render gap; nothing-to-paint is terminal.
            if (outcome == ApplyOutcome.Applied && !IsLocalPlayer(objectIndex))
                RedrawGuise(objectIndex, p.BaseId, p.DisplayName, p.Source);
        }
    }

    /// <summary>Revert a human guise this plugin applied by restoring the player's own pre-guise Glamourer
    /// state (their real outfit), NOT Glamourer's blanket revert-to-base — the latter drops the user's
    /// glamour and leaves them in the "weird clothes" the bare NPC customize implies. Falls back to a plain
    /// revert only if we somehow hold no snapshot. No-op if we didn't guise it.</summary>
    public void Revert(int objectIndex)
    {
        // Cancel any in-flight retry (a guise queued but not yet painted — see _pending) so it can't land
        // AFTER the revert. Do this before the _guised guard: a still-pending guise isn't in _guised yet.
        _pending.Remove(objectIndex);
        if (!_guised.Remove(objectIndex)) return;
        var hadSnapshot = _snapshots.Remove(objectIndex, out var snapshot);
        if (!_glam.Available) return;
        if (hadSnapshot && snapshot is not null)
        {
            // Restore the DM's pre-guise Glamourer state (customize + equipment + Parameters/Materials skin)
            // so exit state == entry state — their own glamour, NOT vanilla. ApplyState alone fixes the
            // LOGICAL state, which is all Mare needs: the PEER's copy is rebuilt from that logical state and
            // renders the restored glam correctly (0.8.59 proved this — peer was right, only self was stale).
            //
            // 0.8.60 tried to ALSO cure the self-side stale gear by wiping to game base BEFORE ApplyState
            // (RevertToGameBase). That REGRESSED the peer to the DM's TRUE body: the wipe fired a game-base
            // Glamourer state event Mare latched onto, and the follow-up ApplyState didn't reliably
            // re-propagate. So do NOT wipe — ApplyState the snapshot straight, keeping the peer correct.
            var isLocalPlayer = IsLocalPlayer(objectIndex);
            var ec = _glam.ApplyState(snapshot, objectIndex, ApplyFlag.Equipment | ApplyFlag.Customization);
            // The self-side stale race/gear after a revert is a DRAW-OBJECT TIMING problem: ApplyState above
            // only QUEUES Glamourer's restore of the DM's real customize (race) + equipment, which Glamourer
            // commits to the LOCAL draw object a frame or two later. So the Penumbra redraw that re-resolves
            // the DM's modded gear/skin/race paths must run AFTER that commit — deferred, not inline. 0.8.63's
            // inline redraw rebuilt from the pre-commit, still-disguise draw state, which is exactly why it did
            // NOT match a MANUAL "/penumbra redraw self" the DM types a moment later (by then Glamourer has
            // committed, so the manual redraw rebuilds from the RIGHT state). See DeferSelfRedraw for the full
            // why (incl. why Penumbra's redraw and not HDM's native one). Self only: the peer already renders
            // correctly from the restored logical state (Mare); a puppet is throwaway mid-teardown.
            if (isLocalPlayer)
                DeferSelfRedraw(objectIndex, snapshot);
            _log.Information($"Guise: obj#{objectIndex} human guise reverted by restoring pre-guise state ({ec}).");
        }
        else
        {
            var ec = _glam.Revert(objectIndex);
            _log.Information($"Guise: obj#{objectIndex} human guise reverted via Glamourer fallback ({ec}).");
        }
    }

    // How long to wait after the revert's ApplyState before redrawing self. The stale-race/gear symptom is
    // pure TIMING: ApplyState only queues Glamourer's customize/equipment restore, which Glamourer commits to
    // the local draw object a frame or two later. 0.8.63 fired Penumbra's RedrawObject in the SAME tick, so it
    // rebuilt from the still-disguise draw state and re-resolved the WRONG race — which is why it did not match
    // a MANUAL "/penumbra redraw self" the DM types a moment later (by then Glamourer has committed, so the
    // manual redraw rebuilds correctly). ~10 ticks (~0.17s at 60fps) clears Glamourer's commit with margin yet
    // still reads as instant on a revert. If this proves too tight on a busy frame, the deterministic upgrade
    // is to redraw off Glamourer's StateFinalized event for this object instead of a fixed delay.
    private const int SelfRedrawDelayTicks = 10;

    // Redraw the LOCAL player a few ticks after a Human-guise revert's ApplyState, so Penumbra re-resolves the
    // DM's real (modded) gear/skin/race from the now-committed logical state — by running the ACTUAL manual
    // "/penumbra redraw self" the DM confirmed is the one thing that restores their look. Penumbra's redraw
    // (not HDM's native GuiseService.Redraw) is required for a MODDED actor: a native DisableDraw→EnableDraw
    // rebuild REUSES Penumbra's already-resolved file paths, whereas Penumbra's redraw re-walks its collection
    // system and re-resolves them (0.8.62 proved the native rebuild leaves modded gear stale). Falls back to
    // the native rebuild only when Penumbra is absent (a user with no modded paths to re-resolve anyway).
    private void DeferSelfRedraw(int objectIndex, JObject snapshot)
    {
        _ = _framework.RunOnTick(() =>
        {
            if (_guised.Contains(objectIndex)) return; // re-guised during the delay — don't fight the new guise
            // PRIMARY: run the LITERAL "/penumbra redraw self" the DM confirmed works by hand — dispatched
            // through Dalamud straight into Penumbra's own command handler, the identical code path to typing
            // it (Brio drives Glamourer's /glamourer the same way). ProcessCommand returns true if a handler
            // took it (i.e. Penumbra is present). We only reach here for self, so "self" is the right target.
            if (_commandManager.ProcessCommand("/penumbra redraw self"))
            {
                _log.Information($"Guise: obj#{objectIndex} self-redrawn via /penumbra redraw self after revert.");
                return;
            }
            // Penumbra's command isn't registered → try the typed IPC, then a native rebuild (Penumbra absent).
            if (_penumbra.Redraw(objectIndex))
            {
                _log.Information($"Guise: obj#{objectIndex} self-redrawn via Penumbra IPC after revert (command dispatch missed).");
                return;
            }
            _guise.Redraw(objectIndex, onSettled: () =>
            {
                if (!_guised.Contains(objectIndex))
                    _glam.ApplyState(snapshot, objectIndex, ApplyFlag.Equipment | ApplyFlag.Customization);
            });
            _log.Information($"Guise: obj#{objectIndex} self-redrawn natively after revert (Penumbra absent).");
        }, delayTicks: SelfRedrawDelayTicks);
    }

    // Paint the human guise onto SELF a few ticks after RevertToGameBase, so the NPC ApplyState lands AFTER
    // the revert's redraw-to-game-base has settled instead of racing it in the same tick (which the revert
    // deterministically wins — the "first click shows the DM's true race + basic gear, second click lands the
    // NPC" alternation, worst on a cross-family switch like Monster→Human where every first Human apply
    // re-reverts). This is the automatic "second click" the 0.8.68 note prescribed ("defer the re-assert"),
    // reusing the proven revert-path delay (SelfRedrawDelayTicks). The caller already added objectIndex to
    // _guised, so this re-run's doRevert gate (isLocalPlayer && !_guised.Contains) is FALSE — it skips the
    // revert and ApplyStates uncontested. Gated on the actor still being guised so a /hdm revert during the
    // delay cancels the paint; falls to the _pending per-frame retry if Glamourer still can't resolve the
    // freshly-redrawn draw object. 0.8.66's self rebuild failed here because it re-asserted on the still-
    // rebuilding draw object (StateNull, dropped); the fixed delay clears Glamourer's commit first.
    private void DeferSelfApply(int objectIndex, uint baseId, string displayName, NpcSource source)
    {
        _ = _framework.RunOnTick(() =>
        {
            if (!_guised.Contains(objectIndex)) return;   // reverted during the delay — don't paint a cancelled guise
            if (!_glam.Available) return;
            var outcome = TryApplyOnce(objectIndex, baseId, displayName, source, verbose: false);
            if (outcome == ApplyOutcome.StateNull)
            {
                _pending[objectIndex] = new PendingApply { BaseId = baseId, DisplayName = displayName, Source = source, Attempts = 1 };
                _log.Debug($"Guise: obj#{objectIndex} deferred self-apply of '{displayName}' hit a cold Glamourer state — queued the per-frame retry.");
            }
            else
                _log.Information($"Guise: obj#{objectIndex} human guise '{displayName}' landed on the deferred self-apply (auto 'second click' after the revert redraw settled; {outcome}).");
        }, delayTicks: SelfRedrawDelayTicks);
    }

    /// <summary>Drop tracking for an object index WITHOUT calling Glamourer — for a puppet being despawned,
    /// where the object is about to be deleted so a paint/revert is pointless and stale tracking on a
    /// recycled index would be wrong. Mirrors <see cref="GuiseService.Forget"/> for the Human path.</summary>
    public void Forget(int objectIndex)
    {
        _guised.Remove(objectIndex);
        _snapshots.Remove(objectIndex);
        _pending.Remove(objectIndex); // a despawn cancels any in-flight retry on that (soon-recycled) index
    }

    /// <summary>Revert every human guise this plugin still tracks (used on dispose).</summary>
    public void RevertAll()
    {
        foreach (var idx in new List<int>(_guised))
            Revert(idx);
    }

    // --- Glamourer state building (ported from HOutfits' NpcStateBuilder) ------

    // Equipment slot order matches NpcData.TryGetEquipment's output (Head..LFinger)
    // and the Glamourer state's Equipment object keys. Each entry carries the
    // FullEquipType byte Glamourer's CustomItemId needs (Head=1..Finger=9), verified
    // verbatim against Glamourer's Penumbra.GameData FullEquipType enum. Weapons are
    // NOT in this array — they use a 3-part (Set/Type/Variant) model and a weapon
    // FullEquipType sentinel, so they're written separately by WriteWeapon (#79).
    private static readonly (string Key, ulong EquipType)[] Slots =
    [
        ("Head",    1),
        ("Body",    2),
        ("Hands",   3),
        ("Legs",    4),
        ("Feet",    5),
        ("Ears",    6),
        ("Neck",    7),
        ("Wrists",  8),
        ("RFinger", 9),
        ("LFinger", 9),
    ];

    private const ulong CustomFlag = 1ul << 48;

    // Build the CustomItemId Glamourer stores for NPC (non-item) gear, verbatim from
    // Glamourer's Penumbra.GameData CustomItemId(model, secondary, variant, type):
    //   model | (secondary<<16) | (variant<<32) | (type<<40) | CustomFlag.
    // Armor has no secondary model id (secondary = 0).
    private static ulong CustomItemId(ushort model, byte variant, ulong equipType)
        => model
         | (0ul << 16)
         | ((ulong)variant << 32)
         | (equipType << 40)
         | CustomFlag;

    // FullEquipType sentinels for weapons (Penumbra.GameData, verbatim from HOutfits' EquipTypeFor):
    // the NPC sheet doesn't carry the specific weapon category (Sword=12, Bow=14, …), which is normally
    // derived from an item's equip-category and NPC gear has no item. Penumbra ships UnknownMainhand=66 /
    // UnknownOffhand=67 for exactly this case: ToSlot() maps them to MainHand/OffHand and the model loads
    // from Set/Type/Variant (bits 0-39) — the mesh does NOT depend on the category byte. Stamping 0/Unknown
    // instead would make ToSlot() return Unknown and the weapon would route nowhere.
    private const ulong EquipTypeMainHand = 66; // FullEquipType.UnknownMainhand
    private const ulong EquipTypeOffHand  = 67; // FullEquipType.UnknownOffhand

    // Weapon variant of CustomItemId: unlike armor, a weapon carries a NON-ZERO secondary (its Type/Base
    // model id), threaded through bits 16-31. Same shape as HOutfits' NpcStateBuilder.CustomItemId(NpcPiece):
    //   set | (type<<16) | (variant<<32) | (equipType<<40) | CustomFlag.
    private static ulong WeaponCustomItemId(ushort set, ushort type, byte variant, ulong equipType)
        => set
         | ((ulong)type << 16)
         | ((ulong)variant << 32)
         | (equipType << 40)
         | CustomFlag;

    // Overwrite one weapon slot ("MainHand"/"OffHand") in the Glamourer Equipment object with the NPC's
    // weapon — same schema as armor's slot write (ItemId is the weapon CustomItemId, stains carry the NPC
    // dyes). No-ops if the slot key is absent (a non-weapon-capable state), which the [weapon] diagnostic
    // surfaces.
    private static void WriteWeapon(JObject equip, string key, NpcData.NpcWeapon w, ulong equipType)
    {
        if (equip[key] is not JObject slot)
            return;
        slot["ItemId"]     = WeaponCustomItemId(w.Set, w.Type, w.Variant, equipType);
        slot["Apply"]      = true;
        slot["Stain"]      = w.Dye;
        slot["Stain2"]     = w.Dye2;
        slot["ApplyStain"] = true;
    }

    private static void WriteEquipment(JObject equip, EquipmentModelId[] pieces)
    {
        for (var i = 0; i < Slots.Length && i < pieces.Length; i++)
        {
            var (key, equipType) = Slots[i];
            if (equip[key] is not JObject slot)
                continue;
            var m = pieces[i];
            if (m.Id == 0)
            {
                // The disguise fully OWNS every slot: an NPC with nothing in a slot CLEARS it to "Nothing"
                // (ItemId 0) rather than leaving the DM's own piece to bleed through. Earlier builds cleared
                // ONLY Head (i == 0), so the DM's accessories — necklace, earrings, bracelets, rings — and any
                // unspecified armour rode along on the disguise as "extra gear" the NPC never wears. Clearing
                // EVERY empty slot matches Glamourer's own "Apply NPC to Yourself" (which applies the NPC's
                // WHOLE equipment set, empty slots included) and the "no trace of the original actor" principle
                // (the same one behind the Parameters/Materials strip). Head stays authoritative for the same
                // reason, with its show/hide metadata driven separately by SetHeadgearShown. An NPC that
                // genuinely has an empty armour slot renders as the game shows it (bare / smallclothes) — that
                // IS the NPC's real look, and it's still preferable to wearing the DM's identity.
                slot["ItemId"]     = 0;
                slot["Apply"]      = true;
                slot["Stain"]      = 0;
                slot["Stain2"]     = 0;
                slot["ApplyStain"] = true;
                continue;
            }
            slot["ItemId"]     = CustomItemId(m.Id, m.Variant, equipType);
            slot["Apply"]      = true;
            slot["Stain"]      = m.Stain0;
            slot["Stain2"]     = m.Stain1;
            slot["ApplyStain"] = true;
        }
    }

    // --- Readback diagnostics (Principle 2) ------------------------------------
    // Small string extractors used by the post-apply readback in Apply. They read the exact JSON
    // schema Glamourer round-trips, so a mismatch (key absent, value reset) shows plainly in the log.
    private static string SlotItemId(JObject? equip, string key)
        => equip?[key] is JObject slot ? slot["ItemId"]?.ToString() ?? "?" : "-";

    private static string HatShow(JObject? state)
        => state?["Equipment"] is JObject eq && eq["Hat"] is JObject hat ? hat["Show"]?.ToString() ?? "?" : "-";

    private static string CustValue(JObject? state, string key)
        => state?["Customize"] is JObject c && c[key] is JObject f ? f["Value"]?.ToString() ?? "?" : "-";

    // Drive head-gear visibility in a Glamourer state. Glamourer keeps the "Hide Head Gear"
    // preference as a metadata sibling of the head item ("Hat", with a Show flag) SEPARATE from the
    // item itself, so we can set it independent of what's in Equipment.Head:
    //  * show=true  — the NPC wears headgear we wrote into Head; force it visible past the player's
    //                 own hide-headgear preference (the tempered-imperial headless fix).
    //  * show=false — the NPC is bare-headed. WriteEquipment has already cleared Head to "Nothing"
    //                 (ItemId 0); this also flips the hide-headgear metadata off so the player's own
    //                 "show helmet" preference can't re-reveal a phantom slot on the disguise (the
    //                 masked-Ascian "keeps the officer cap" fix) and the NPC's own face shows.
    // Safely no-ops if the key is absent, in which case the [head] diagnostic reveals the real schema.
    private static void SetHeadgearShown(JObject equip, bool show)
    {
        if (equip["Hat"] is JObject hat)
        {
            hat["Show"]  = show;
            hat["Apply"] = true;
        }
    }

    // Glamourer Customize JObject key -> (byte index in the 26-byte customize array,
    // bit mask within that byte). Keyed per CustomizeIndex (36 options), NOT per byte
    // (26): five bytes pack several options behind masks. Values are masked but NOT
    // shifted, matching Penumbra's CustomizeArray.Get (`Data[offset] & mask`). Table
    // copied verbatim from Penumbra.GameData CustomizeIndex.ToByteAndMask.
    private static readonly (string Key, int Byte, byte Mask)[] CustomizeMap =
    [
        ("Race",              0,  0xFF),
        ("Gender",            1,  0xFF),
        ("BodyType",          2,  0xFF),
        ("Height",            3,  0xFF),
        ("Clan",              4,  0xFF),
        ("Face",              5,  0xFF),
        ("Hairstyle",         6,  0xFF),
        ("Highlights",        7,  0x80),
        ("SkinColor",         8,  0xFF),
        ("EyeColorRight",     9,  0xFF),
        ("HairColor",         10, 0xFF),
        ("HighlightsColor",   11, 0xFF),
        ("FacialFeature1",    12, 0x01),
        ("FacialFeature2",    12, 0x02),
        ("FacialFeature3",    12, 0x04),
        ("FacialFeature4",    12, 0x08),
        ("FacialFeature5",    12, 0x10),
        ("FacialFeature6",    12, 0x20),
        ("FacialFeature7",    12, 0x40),
        ("LegacyTattoo",      12, 0x80),
        ("TattooColor",       13, 0xFF),
        ("Eyebrows",          14, 0xFF),
        ("EyeColorLeft",      15, 0xFF),
        ("EyeShape",          16, 0x7F),
        ("SmallIris",         16, 0x80),
        ("Nose",              17, 0xFF),
        ("Jaw",               18, 0xFF),
        ("Mouth",             19, 0x7F),
        ("Lipstick",          19, 0x80),
        ("LipColor",          20, 0xFF),
        ("MuscleMass",        21, 0xFF),
        ("TailShape",         22, 0xFF),
        ("BustSize",          23, 0xFF),
        ("FacePaint",         24, 0x7F),
        ("FacePaintReversed", 24, 0x80),
        ("FacePaintColor",    25, 0xFF),
    ];

    private static void WriteCustomize(JObject cust, byte[] c)
    {
        // Writes the FULL basic customize including the top-level Race byte (index 0) — the disguise takes
        // the NPC's whole skeleton (a Miqo'te NPC gives the viewer cat ears), per the "complete disguise,
        // no trace of the original actor" requirement. (A brief 0.8.48 experiment skipped Race to mirror
        // Glamourer's NPC-tab AllRelevant == All & ~Race; that was FALSIFIED in-game — skin stayed pale with
        // Race dropped, proving Race was never the skin culprit. The real culprit is the advanced Parameters/
        // Materials trace, stripped in TryApplyOnce; see the class note.)
        foreach (var (key, byteIdx, mask) in CustomizeMap)
        {
            if (byteIdx >= c.Length)
                continue;
            if (cust[key] is not JObject field)
                continue; // key absent from this state (e.g. actor is non-human): leave it
            field["Value"] = (byte)(c[byteIdx] & mask); // masked, not shifted
            field["Apply"] = true;
        }
    }

    // Object table is rebuilt across these; tracked indices are stale. Clear, don't
    // revert (the actors are gone / re-materialised by the server anyway).
    private void OnTerritoryChanged(uint _) { _guised.Clear(); _snapshots.Clear(); _pending.Clear(); }
    private void OnLogout(int type, int code) { _guised.Clear(); _snapshots.Clear(); _pending.Clear(); }

    public void Dispose()
    {
        try { RevertAll(); }
        catch (Exception e) { _log.Error(e, "Guise: human RevertAll on dispose failed"); }
        _framework.Update -= OnUpdate;
        _clientState.TerritoryChanged -= OnTerritoryChanged;
        _clientState.Logout -= OnLogout;
    }
}
