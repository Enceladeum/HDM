using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using CSCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace HDM;

/// <summary>
/// Action-timeline triggers for a guised actor, with a hard safety rule: we
/// never leave the character in a locked mode.
///
/// The v0.1 build looped animations by entering <c>CharacterModes.AnimLock</c>
/// (Brio's poser pattern). That is the classic footgun — AnimLock blocks logout
/// and movement, and the game surfaces it as "you cannot do that while operating
/// a siege machine." If tracking was lost (e.g. a zone change cleared it) the
/// actor could never be un-locked from the UI. This rewrite removes AnimLock
/// from the normal flow entirely:
///
///  - One-shot: <c>TimelineSequencer.PlayTimeline(id)</c>. Blends once over the
///    current base; the game blends back on its own. Never touches Mode.
///  - Loop: set <c>Timeline.BaseOverride = id</c> and blend the same id for a
///    gap-free start. BaseOverride "forces base animation when the character is
///    in a Normal OR AnimLock state" (FFXIVClientStructs) — so we get the loop
///    while STAYING in Normal. The actor can still move and log out. Trade-off:
///    the override may drop if the actor starts moving; that self-healing miss
///    is vastly preferable to a locked character.
///  - Speed: a DUAL hook, Brio's proven pattern (single-hooking OverallSpeed was the v1/v2 bug —
///    Galatea Magna's idle kept bobbing because the pinned OverallSpeed never reached the running
///    slots):
///      * <c>CalculateAndApplyOverallSpeed</c> — re-writes <c>Timeline.OverallSpeed</c> AFTER
///        <c>Original()</c> and returns true, so the container-level speed (particles, and the
///        value the animator re-derives from) holds at our pinned freeze (0) / slow-mo.
///      * <c>ActionTimelineSequencer.SetSlotSpeed</c> — the function that actually writes each
///        animation slot's playback speed. We intercept it and substitute our pinned value for our
///        own actor, so every game-initiated slot (re)start (a looping idle re-arms its base slot
///        continually) is forced to the freeze instead of snapping back to 1.
///    <see cref="SetSpeed"/> also pushes the pinned speed onto the currently-active slots the
///    instant it's set, so the freeze bites immediately rather than waiting for the next slot
///    re-arm. Setting speed back to 1 clears the pin and restores the slots to normal.
///
/// <see cref="Sanitize"/> is the universal recovery: it clears BaseOverride,
/// resets speed, forces <c>CharacterModes.Normal</c> via <c>SetMode</c> (the same
/// transition the user triggered manually by mounting then dismounting), and
/// blends timeline 3 (Brio's reset) back to idle. It works whether or not we
/// were tracking the actor, so it also recovers state left behind by an earlier
/// build or by the game itself. Every stop / revert / dispose / zone-change
/// routes through it.
///
/// Same envelope as GuiseService: framework thread only, clear-not-write across
/// territory/logout (object table is rebuilt), revert on Dispose.
/// </summary>
public sealed unsafe class AnimationService : IDisposable
{
    private readonly IFramework _framework;
    private readonly IObjectTable _objects;
    private readonly IClientState _clientState;
    private readonly IPluginLog _log;

    // Hook on the container-level speed-apply call. Re-asserts a pinned freeze/slow-mo on
    // OverallSpeed each frame (see class remarks and <see cref="CalcOverallSpeedDetour"/>).
    private delegate bool CalcOverallSpeedDelegate(TimelineContainer* timeline);
    private readonly Hook<CalcOverallSpeedDelegate>? _calcSpeedHook;

    // Hook on the per-slot speed writer — the value the animator ACTUALLY plays each slot at. This
    // is the piece a single OverallSpeed hook missed: forcing this holds the freeze on a looping
    // idle that keeps re-arming its base slot (see <see cref="SetSlotSpeedDetour"/>).
    private delegate void SetSlotSpeedDelegate(ActionTimelineSequencer* seq, uint slot, float speed);
    private readonly Hook<SetSlotSpeedDelegate>? _setSlotSpeedHook;

    // Change-gated diagnostics (Principle 2: confirm the hook fires and matches the right actor
    // before trusting the freeze). Each holds the ObjectIndex we last logged a force for; -1 = none.
    // Reset when a pin is (re)set or cleared, so each freeze re-logs exactly once per detour. Demote
    // the two Information lines to Debug once the freeze is confirmed holding in the field.
    private int _calcGate = -1;
    private int _slotGate = -1;

    // Timeline 3 = normal/idle. Brio's "reset blend" target.
    private const ushort ResetBlend = 3;

    // Object indices we've driven a timeline on. Used to auto-sanitise on
    // teardown / zone-change and to surface "playing" state in the UI. It is a
    // best-effort set — Sanitize does NOT depend on membership.
    private readonly HashSet<int> _touched = [];

    // objectIndex -> playback speed we must RE-ASSERT inside the game's CalculateAndApplyOverallSpeed
    // call. The native animator resets Timeline.OverallSpeed toward 1 continuously, so a one-shot
    // SetSpeed drifts back to normal within a frame; this pins a freeze (0) or slow-mo until the
    // speed is set back to 1 (which removes the entry). Self-apply subject, same as guise.
    private readonly Dictionary<int, float> _speedPin = [];

    // objectIndex -> a FULL-timeline replay loop. The plain Loop (BaseOverride) forces the base lane,
    // which for a full gesture like Galatea Magna's open-arms plays only its LOOP portion — the
    // "awkward truncated loop" the DM flagged, skipping the intro/wind-up entirely. This instead
    // re-issues the WHOLE timeline each time it finishes (watched via the sequencer's slot ids on the
    // framework tick), so start→end plays in full on repeat. Reserved for one-shot-style gestures;
    // resident-special HOLD poses (mon_sp_X_loop, which only render while held) still use BaseOverride,
    // because replaying one would flicker it a frame at a time. Ended by Stop/Sanitize like everything.
    private sealed class ReplayLoop { public ushort Id; public bool WaitingForStart; public int Ticks; }
    private readonly Dictionary<int, ReplayLoop> _replays = [];
    // If the sequencer hasn't picked the timeline into a slot within this many ticks of us issuing it,
    // re-issue (defensive against a dropped PlayTimeline); once seen, we wait for the slot to clear.
    private const int ReplayStartTimeout = 30;

    // objectIndex -> a compound "intro then hold" gesture (the Combos buttons — Die = play the death FALL
    // once, then HOLD the lying dead_pose). This glues together a pair the DM would otherwise fire by hand
    // in sequence, which "you'd never use separately." Same slot-watch machinery as _replays: wait for the
    // intro to occupy a slot, then for it to CLEAR (finished), then clamp BaseOverride to the terminal pose
    // and re-assert it each tick so the native animator can't drift off it (dead_pose is a base-type held
    // pose, exactly like a resident-special loop — it only renders while held). Mutually exclusive with a
    // replay on one actor (PlaySequence clears any replay). Ended by Stop/Sanitize like everything.
    private sealed class PoseSequence { public ushort IntroId; public ushort HoldId; public bool WaitingForStart; public bool Holding; public int Ticks; }
    private readonly Dictionary<int, PoseSequence> _sequences = [];

    // objectIndex -> the locomotion timeline we last applied via DriveLocomotion. This is the
    // "LastAppliedAnim" of the §4b one-writer block: the single source of truth for whether the driven
    // clip CHANGED this frame (EDGE: fire once + pin) or is the same looping clip (SUSTAIN: re-assert
    // BaseOverride so the native animator can't drift it back). 0 = cleared to natural idle. Possession
    // drives this every frame while a DM wears a human puppet; cleared alongside the other per-actor
    // state on Forget/Sanitize/territory/logout so a recycled index can't inherit a stale pin.
    private readonly Dictionary<int, ushort> _locoLastApplied = [];

    public AnimationService(IGameInteropProvider interop, IFramework framework, IObjectTable objects, IClientState clientState, IPluginLog log)
    {
        _framework = framework;
        _objects = objects;
        _clientState = clientState;
        _log = log;

        // Freeze/slow-mo needs BOTH hooks (Brio's pattern). A per-tick field write loses the race to
        // the native animator; the container hook holds OverallSpeed and the slot hook holds the
        // value each slot actually plays at. Log the resolved addresses so a stale CS signature (a
        // silent 0/failed resolve) is obvious in the log rather than presenting as "freeze does
        // nothing" (Principle 2: a lever that silently no-ops usually means the target was wrong).
        try
        {
            var calcAddr = TimelineContainer.Addresses.CalculateAndApplyOverallSpeed.Value;
            var slotAddr = ActionTimelineSequencer.Addresses.SetSlotSpeed.Value;
            _log.Information($"Anim: hooking CalcOverallSpeed@0x{calcAddr:X} SetSlotSpeed@0x{slotAddr:X}");

            _calcSpeedHook = interop.HookFromAddress<CalcOverallSpeedDelegate>(calcAddr, CalcOverallSpeedDetour);
            _calcSpeedHook.Enable();

            _setSlotSpeedHook = interop.HookFromAddress<SetSlotSpeedDelegate>(slotAddr, SetSlotSpeedDetour);
            _setSlotSpeedHook.Enable();
        }
        catch (Exception e)
        {
            _log.Error(e, "Anim: failed to hook speed functions; freeze/slow-mo will not hold");
        }

        _framework.Update += OnUpdate;
        _clientState.TerritoryChanged += OnTerritoryChanged;
        _clientState.Logout += OnLogout;
    }

    /// <summary>True if we've driven a timeline on this actor since the last sanitise.</summary>
    public bool IsPlaying(int objectIndex) => _touched.Contains(objectIndex);

    /// <summary>
    /// Play a timeline once, blended over the current base. Does not change Mode.
    /// Clears any held pose first: a one-shot (e.g. Battle Idle) must SUPERSEDE a held Special,
    /// not blend under it. Without clearing <c>BaseOverride</c> the held Special keeps re-asserting
    /// itself every frame and stomps the one-shot ("stuck in the special anim"); we also drop any
    /// replay/sequence tracking so <see cref="OnUpdate"/> doesn't re-issue the old loop underneath.
    /// </summary>
    public void PlayOnce(ICharacter chara, ushort timelineId)
    {
        var n = (CSCharacter*)chara.Address;
        if (n == null) return;
        var idx = chara.ObjectIndex;
        n->Timeline.BaseOverride = 0;
        _replays.Remove(idx);
        _sequences.Remove(idx);
        n->Timeline.TimelineSequencer.PlayTimeline(timelineId);
        _touched.Add(idx);
    }

    /// <summary>
    /// Loop a timeline as the forced base animation, WITHOUT locking the actor.
    /// Stays in Normal mode (see class remarks); the character keeps full agency.
    /// </summary>
    public void Loop(ICharacter chara, ushort timelineId)
    {
        var n = (CSCharacter*)chara.Address;
        if (n == null) return;
        n->Timeline.BaseOverride = timelineId;
        n->Timeline.TimelineSequencer.PlayTimeline(timelineId); // gap-free start
        _touched.Add(chara.ObjectIndex);
    }

    /// <summary>
    /// Loop a timeline by REPLAYING it in full each time it ends, rather than forcing its base lane.
    /// This is the fix for full gestures (Galatea's open-arms &co.) that <see cref="Loop"/> truncates:
    /// <see cref="Loop"/>'s <c>BaseOverride</c> plays only the looping tail, so the intro/wind-up never
    /// shows and it reads as an awkward partial loop; here we let the WHOLE timeline play, watch the
    /// sequencer for it to finish, then re-issue it (<see cref="OnUpdate"/>). BaseOverride is cleared so
    /// it can't force the tail underneath. Stays in Normal mode (no lock), same as the rest.
    /// </summary>
    public void LoopReplay(ICharacter chara, ushort timelineId)
    {
        var n = (CSCharacter*)chara.Address;
        if (n == null) return;
        var idx = chara.ObjectIndex;
        n->Timeline.BaseOverride = 0;                              // replay drives the full timeline; no base-lane force
        n->Timeline.TimelineSequencer.PlayTimeline(timelineId);
        _replays[idx] = new ReplayLoop { Id = timelineId, WaitingForStart = true, Ticks = 0 };
        _touched.Add(idx);
        _log.Information($"Anim: obj#{idx} replay-loop timeline {timelineId} (full-timeline re-trigger)");
    }

    /// <summary>
    /// Play a compound "intro then hold" gesture — the Combos buttons (Die = play the death FALL once,
    /// then HOLD the lying dead_pose). Fires <paramref name="introId"/> as a one-shot (BaseOverride
    /// cleared, exactly like <see cref="LoopReplay"/> so the intro plays in full), then <see cref="OnUpdate"/>
    /// watches its slot; when the intro finishes and the slot clears, it clamps <c>BaseOverride</c> to
    /// <paramref name="holdId"/> and re-asserts it each tick so the native animator can't drift off the
    /// terminal pose (dead_pose is a base-type held pose, like a resident-special loop). This glues a pair
    /// the DM would otherwise fire by hand — "you'd never use separately." Clears any replay on this actor
    /// (mutually exclusive); ended by <see cref="Stop"/>/<see cref="Sanitize"/> like everything. Normal mode
    /// throughout — no lock.
    /// </summary>
    public void PlaySequence(ICharacter chara, ushort introId, ushort holdId)
    {
        var n = (CSCharacter*)chara.Address;
        if (n == null) return;
        var idx = chara.ObjectIndex;
        n->Timeline.BaseOverride = 0;                              // intro plays as a full one-shot; no base-lane force yet
        n->Timeline.TimelineSequencer.PlayTimeline(introId);
        _sequences[idx] = new PoseSequence { IntroId = introId, HoldId = holdId, WaitingForStart = true, Ticks = 0 };
        _replays.Remove(idx);                                      // a replay and a sequence can't co-drive one actor
        _touched.Add(idx);
        _log.Information($"Anim: obj#{idx} sequence intro {introId} -> hold {holdId}");
    }

    /// <summary>
    /// Drive ONE frame of locomotion on a piloted puppet: apply the resolved <paramref name="target"/>
    /// timeline via the §4b one-writer block — the single place that writes TimelineIds[0]/BaseOverride
    /// per frame for a driven actor. "Last writer by line-order wins" is a whole bug class, so
    /// possession funnels its per-frame animation through here and NOWHERE else.
    ///
    /// Two disciplines are baked in, both HMS scar tissue:
    ///  - LOOPING clips (walk/run/idle) are re-pinned via <c>BaseOverride</c> EVERY frame or the native
    ///    animator drifts them back — <c>PlayTimeline</c> fires the clip, <c>BaseOverride</c> holds it.
    ///  - TERMINAL one-shots (jump/dismount landing — <see cref="LocomotionData.IsLandingClip"/>) must
    ///    NOT be sustained: pinning a land clip past its ~9-frame natural length re-shows its final
    ///    knee-bend squat (the "double-squat" glitch). Fire it ONCE, release <c>BaseOverride</c>, let it
    ///    hand off to idle naturally.
    /// <paramref name="target"/>==0 means "natural idle": clear <c>BaseOverride</c> once and let the
    /// game drive. Framework thread; mirrors HMS's ResolvePuppetAnimation write block verbatim.
    /// </summary>
    public void DriveLocomotion(ICharacter chara, ushort target)
    {
        var n = (CSCharacter*)chara.Address;
        if (n == null) return;
        var idx = chara.ObjectIndex;
        var last = _locoLastApplied.TryGetValue(idx, out var l) ? l : (ushort)0;
        var targetIsLandingOneShot = target != 0 && LocomotionData.IsLandingClip(target);

        if (target != 0)
        {
            if (last != target)                                     // EDGE: the clip changed this frame
            {
                n->Timeline.BaseOverride = targetIsLandingOneShot ? (ushort)0 : target;
                n->Timeline.TimelineSequencer.PlayTimeline(target); // fire it once
                _locoLastApplied[idx] = target;
                _touched.Add(idx);
            }
            else if (!targetIsLandingOneShot)                       // SUSTAIN: re-assert the loop pin
            {
                n->Timeline.BaseOverride = target;
            }
        }
        else if (last != 0)                                         // target 0 => clear to natural idle
        {
            n->Timeline.BaseOverride = 0;
            _locoLastApplied[idx] = 0;
        }
    }

    // Framework tick: advance the two slot-watched per-actor loops. Both guards are cheap no-ops when
    // nothing is active (the common case), so this stays free until a replay or a sequence is running.
    private void OnUpdate(IFramework _)
    {
        if (_replays.Count != 0) TickReplays();
        if (_sequences.Count != 0) TickSequences();
    }

    // Drive the full-timeline replay loops. For each, watch whether the requested timeline is still
    // occupying a sequencer slot: once it clears (the gesture finished and the game blended back), we
    // re-issue it so start→end plays again. A freshly-issued replay waits for the slot to pick it up
    // first (so we don't mistake "not started yet" for "already finished"), with a timeout re-issue as
    // a safety net.
    private void TickReplays()
    {
        foreach (var idx in new List<int>(_replays.Keys))
        {
            var go = _objects[idx];
            if (go is not ICharacter c || c.Address == nint.Zero) { _replays.Remove(idx); continue; }
            var n = (CSCharacter*)c.Address;
            var st = _replays[idx];
            st.Ticks++;
            var active = SlotHasTimeline(n, st.Id);
            if (st.WaitingForStart)
            {
                if (active) { st.WaitingForStart = false; st.Ticks = 0; }
                else if (st.Ticks >= ReplayStartTimeout)
                { n->Timeline.TimelineSequencer.PlayTimeline(st.Id); st.Ticks = 0; }
            }
            else if (!active)
            {
                n->Timeline.TimelineSequencer.PlayTimeline(st.Id); // finished — play the whole thing again
                st.WaitingForStart = true;
                st.Ticks = 0;
            }
        }
    }

    // Drive the compound "intro then hold" gestures (Combos). Three phases per actor, all watched off the
    // same sequencer-slot signal as the replays: (1) WaitingForStart — wait for the intro (the fall) to
    // occupy a slot, re-issuing on timeout so a dropped PlayTimeline can't strand it; (2) playing — wait
    // for the intro's slot to CLEAR (the fall finished); when it does, clamp BaseOverride to the terminal
    // hold pose and blend it in; (3) Holding — re-assert BaseOverride each tick so the native animator
    // can't drift off the lying dead_pose (a base-type held pose, exactly like a resident-special loop).
    private void TickSequences()
    {
        foreach (var idx in new List<int>(_sequences.Keys))
        {
            var go = _objects[idx];
            if (go is not ICharacter c || c.Address == nint.Zero) { _sequences.Remove(idx); continue; }
            var n = (CSCharacter*)c.Address;
            var s = _sequences[idx];

            if (s.Holding)
            {
                n->Timeline.BaseOverride = s.HoldId;               // hold the terminal pose against native drift
                continue;
            }

            s.Ticks++;
            var introActive = SlotHasTimeline(n, s.IntroId);
            if (s.WaitingForStart)
            {
                if (introActive) { s.WaitingForStart = false; s.Ticks = 0; }
                else if (s.Ticks >= ReplayStartTimeout)
                { n->Timeline.TimelineSequencer.PlayTimeline(s.IntroId); s.Ticks = 0; }
            }
            else if (!introActive)
            {
                // The fall finished — clamp to the lying pose and blend it in (gap-free handoff to the hold).
                n->Timeline.BaseOverride = s.HoldId;
                n->Timeline.TimelineSequencer.PlayTimeline(s.HoldId);
                s.Holding = true;
            }
        }
    }

    /// <summary>True if the given timeline id currently occupies any sequencer slot (i.e. it's still playing).</summary>
    private static bool SlotHasTimeline(CSCharacter* n, ushort id)
    {
        var ids = n->Timeline.TimelineSequencer.TimelineIds;
        for (var i = 0; i < ids.Length; i++)
            if (ids[i] == id) return true;
        return false;
    }

    /// <summary>
    /// Set playback speed (1.0 = normal, 0 = freeze-frame, &gt;1 = fast). A non-1 speed is
    /// PINNED and re-asserted inside the game's speed-apply call (<see cref="CalcOverallSpeedDetour"/>)
    /// because the native animator resets OverallSpeed toward 1 each frame; 1.0 clears the pin and
    /// lets the game drive normally again. The immediate write here gives instant feedback; the hook
    /// keeps it from drifting back.
    /// </summary>
    public void SetSpeed(ICharacter chara, float speed)
    {
        var n = (CSCharacter*)chara.Address;
        if (n == null) return;
        n->Timeline.OverallSpeed = speed;
        var idx = chara.ObjectIndex;
        if (speed == 1f)
        {
            _speedPin.Remove(idx);
            ForceActiveSlots(n, 1f); // restore normal playback on the slots immediately
        }
        else
        {
            _speedPin[idx] = speed;
            _touched.Add(idx);
            ForceActiveSlots(n, speed); // bite the freeze/slow-mo this frame, not on next slot re-arm
        }
        _calcGate = _slotGate = -1; // let each detour re-log one confirmation line for this change
    }

    /// <summary>Push a playback speed onto every currently-active animation slot right now, so a
    /// freeze/slow-mo (or the return to 1.0) takes visible effect the instant it's dialed in instead
    /// of waiting for the game to next re-arm a slot. The <see cref="SetSlotSpeedDetour"/> keeps it
    /// pinned afterward.</summary>
    private static void ForceActiveSlots(CSCharacter* n, float speed)
    {
        var seq = &n->Timeline.TimelineSequencer;
        var ids = seq->TimelineIds;
        for (uint slot = 0; slot < ids.Length; slot++)
            if (ids[(int)slot] != 0)
                seq->SetSlotSpeed(slot, speed);
    }

    /// <summary>True if this actor's animation is pinned frozen (speed 0) — the UI freeze toggle reads this.</summary>
    public bool IsFrozen(int objectIndex) => _speedPin.TryGetValue(objectIndex, out var s) && s == 0f;

    // Re-assert pinned playback speeds at the exact point the game applies them. The native
    // animator recomputes OverallSpeed (normally toward 1) and applies it to every slot in this
    // call each frame; re-writing our pinned value AFTER Original() is what makes a freeze (0) or
    // slow-mo actually hold — a per-framework-tick write executes at the wrong point and is
    // overwritten before the animation advances. Keyed by ObjectIndex (the handle SetSpeed pins
    // under, read here off the container's OwnerObject). Returns true when we changed the speed so
    // the game re-propagates it to the slots. Cheap: the pin dictionary is empty in the common case.
    private bool CalcOverallSpeedDetour(TimelineContainer* timeline)
    {
        var result = _calcSpeedHook!.Original(timeline);
        if (_speedPin.Count != 0)
        {
            var owner = timeline->OwnerObject;
            if (owner != null && _speedPin.TryGetValue(owner->GameObject.ObjectIndex, out var pinned))
            {
                timeline->OverallSpeed = pinned;
                int idx = owner->GameObject.ObjectIndex;
                if (idx != _calcGate) { _calcGate = idx; _log.Information($"Anim: CalcOverallSpeed forcing obj#{idx} -> {pinned}"); }
                return true;
            }
        }
        return result;
    }

    // Substitute our pinned freeze/slow-mo for the speed the game is about to write to a slot, for
    // OUR actor only. This is the hook that actually holds a freeze: a looping idle continually
    // re-arms its base slot at speed 1, and without this the animation snaps straight back to full
    // speed a frame after we pin OverallSpeed (the "Galatea Magna still bobs" bug). Cheap: the pin
    // dictionary is empty in the common case, so we pass through untouched.
    private void SetSlotSpeedDetour(ActionTimelineSequencer* seq, uint slot, float speed)
    {
        var final = speed;
        if (_speedPin.Count != 0 && seq != null)
        {
            var parent = seq->Parent;
            if (parent != null && _speedPin.TryGetValue(parent->GameObject.ObjectIndex, out var pinned))
            {
                final = pinned;
                int idx = parent->GameObject.ObjectIndex;
                if (idx != _slotGate) { _slotGate = idx; _log.Information($"Anim: SetSlotSpeed forcing obj#{idx} -> {pinned}"); }
            }
        }
        _setSlotSpeedHook!.Original(seq, slot, final);
    }

    /// <summary>
    /// Force the actor all the way back to a clean, unlocked idle — the "unstick"
    /// hammer and the post-animation sanitiser. Independent of tracking, so it
    /// recovers a character stuck by a previous build. Also aliased as <see cref="Stop"/>.
    /// </summary>
    public void Sanitize(ICharacter chara)
    {
        var n = (CSCharacter*)chara.Address;
        if (n == null) return;

        n->Timeline.BaseOverride = 0;           // stop forcing any base animation
        n->Timeline.OverallSpeed = 1f;          // undo freeze/slow-mo
        n->SetMode(CharacterModes.Normal, 0);   // exit AnimLock / any special mode
        n->Timeline.TimelineSequencer.PlayTimeline(ResetBlend); // blend to idle

        _speedPin.Remove(chara.ObjectIndex);    // stop the freeze pin re-asserting after an unstick
        _replays.Remove(chara.ObjectIndex);     // stop any full-timeline replay loop
        _sequences.Remove(chara.ObjectIndex);   // stop any compound intro->hold gesture
        _locoLastApplied.Remove(chara.ObjectIndex); // drop the §4b one-writer state (BaseOverride cleared above)
        _touched.Remove(chara.ObjectIndex);
        _calcGate = _slotGate = -1;             // a later freeze re-logs its confirmation lines
        _log.Information($"Anim: obj#{chara.ObjectIndex} sanitised to Normal");
    }

    /// <summary>UI-facing alias for <see cref="Sanitize"/>.</summary>
    public void Stop(ICharacter chara) => Sanitize(chara);

    /// <summary>Drop ALL tracking for an object index WITHOUT any native calls — the teardown twin of
    /// <see cref="Sanitize"/> for when the object is about to vanish (a puppet being despawned). Sanitize
    /// issues member calls (PlayTimeline/SetMode) on the actor; that's wrong to do on an object we're
    /// deleting this same tick, and pointless. This just forgets the actor from the per-frame loops
    /// (speed pin, replay loop) and the touched/playing set, so a RECYCLED index can't inherit a stale
    /// freeze or replay from <see cref="OnUpdate"/>. Mirrors GuiseService/HumanGuise.Forget; SpawnService
    /// calls all three before it frees a puppet's slot.</summary>
    public void Forget(int objectIndex)
    {
        _speedPin.Remove(objectIndex);
        _replays.Remove(objectIndex);
        _sequences.Remove(objectIndex);
        _locoLastApplied.Remove(objectIndex);
        _touched.Remove(objectIndex);
    }

    /// <summary>
    /// One-shot diagnostic: log a full snapshot of this actor's animation + movement-intent state at the
    /// current instant (Information level). Built for the "demihuman mobs slide, then stick in a walk loop"
    /// report — trigger it WHILE the loop is stuck (that state persists, so a one-shot catches it) to read,
    /// in two lines:
    ///  - whether HDM is forcing anything (BaseOverride / a speed pin / a replay loop) or it's purely
    ///    the game's native locomotion on the swapped skeleton — the DM already found "unstick" doesn't
    ///    clear it, which points at the game; this confirms BaseOverride==0 and no pin at the same instant;
    ///  - which timeline ids currently occupy the sequencer slots (is a walk/run timeline still resident?);
    ///  - the MoveController movement INTENT — forward speed + heading, the authoritative lag-free signal
    ///    (skill Movement §: read the controller, don't difference position). A stuck walk with fwdSpeed≈0
    ///    is the classic stop-late residual; a slide is position moving with NO locomotion timeline in a slot.
    /// Reads only; framework thread. The MoveController floats are functionally-mapped raw offsets, guarded
    /// by the CS-typed <c>OwnerObject</c> self-check (a wrong base prints "unverified", never garbage).
    /// <paramref name="tag"/> lets the caller stamp the guise identity (skeleton / ModelChara) it knows.
    /// </summary>
    public void DumpTimelineState(ICharacter chara, string tag = "diag")
    {
        var n = (CSCharacter*)chara.Address;
        if (n == null) { _log.Information($"Anim[{tag}]: null character — nothing to dump."); return; }

        var idx = chara.ObjectIndex;
        // Non-zero sequencer slots (slot 0 = active/loop, 1 = intro/start). Mirror SlotHasTimeline's access
        // path exactly so we read the same live buffer without copying the sequencer struct.
        var ids = n->Timeline.TimelineSequencer.TimelineIds;
        var slots = new StringBuilder();
        for (var i = 0; i < ids.Length; i++)
            if (ids[i] != 0) slots.Append($" [{i}]={ids[i]}");
        if (slots.Length == 0) slots.Append(" (all idle)");

        var pin = _speedPin.TryGetValue(idx, out var p) ? p.ToString("0.##") : "none";
        var replay = _replays.TryGetValue(idx, out var r) ? r.Id.ToString() : "none";
        var seq = _sequences.TryGetValue(idx, out var sq) ? $"{sq.IntroId}->{sq.HoldId}{(sq.Holding ? "(hold)" : "")}" : "none";

        _log.Information(
            $"Anim[{tag}] obj#{idx}: Mode={n->Mode}/{n->ModeParam} BaseOverride={n->Timeline.BaseOverride} " +
            $"OverallSpeed={n->Timeline.OverallSpeed:0.##} slots:{slots}");
        _log.Information(
            $"Anim[{tag}] obj#{idx}: HDM-driving[ speedPin={pin} replay={replay} seq={seq} touched={_touched.Contains(idx)} ]  {ReadMoveIntent(n)}");
    }

    /// <summary>
    /// Read the MoveController's functionally-mapped movement INTENT — forward speed (MoveController+0x1C8,
    /// yalms/s; 0 at rest, ramps on key press) and active heading (+0x1D0, radians). Lag-free: written the
    /// instant input is applied, UPSTREAM of position integration (skill Movement §), so it is readable
    /// WITHOUT the body committing the displacement — the primitive the clean, DM-static possession drives
    /// from (docs/HDM-possession-clean-decouple-brief.md). Returns false with zeroed outs if the base can't be
    /// certified: the CS-typed OwnerObject (MoveController+0x3E0) must point back at this character, else the
    /// raw offsets are unreliable — one known-field check certifies the base rather than reading garbage
    /// (Principle 2). In-bounds reads inside a live embedded value-struct: crash-safe on the framework thread.
    /// </summary>
    public static bool TryReadMoveIntent(CSCharacter* n, out float fwdSpeed, out float heading)
    {
        fwdSpeed = 0f;
        heading = 0f;
        if (n == null) return false;
        var mc = &n->MoveController;
        if ((nint)mc->OwnerObject != (nint)n) return false; // base unverified — offsets unreliable
        fwdSpeed = *(float*)((byte*)mc + 0x1C8);
        heading  = *(float*)((byte*)mc + 0x1D0);
        return true;
    }

    // String form for the diagnostic dump: the same certified read, formatted (or the unverified reason).
    private static string ReadMoveIntent(CSCharacter* n)
    {
        if (TryReadMoveIntent(n, out var fwd, out var heading))
            return $"move: fwdSpeed={fwd:0.###} heading={heading:0.###}";
        var mc = &n->MoveController;
        return $"move: (base unverified — OwnerObject=0x{(nint)mc->OwnerObject:X} != char 0x{(nint)n:X})";
    }

    /// <summary>Sanitise every tracked actor whose object is still live.</summary>
    public void SanitizeAll()
    {
        foreach (var idx in new List<int>(_touched))
        {
            var go = _objects[idx];
            if (go is ICharacter c && c.Address != nint.Zero)
                Sanitize(c);
            else
                _touched.Remove(idx);
        }
    }

    // The object table is rebuilt across a territory change, but the LOCAL player
    // persists — and if it were left mid-override it would carry a forced
    // animation (or, from an old build, an AnimLock) into the new zone. Clean the
    // local actor with plain field/mode writes (safe mid-transition; we skip the
    // PlayTimeline member call here), then drop all tracking since puppet pointers
    // are now stale.
    private void OnTerritoryChanged(uint _) => SanitizeLocalThenClear();

    // On logout the character despawns and the server re-materialises true state
    // on next login — clear tracking, write nothing.
    private void OnLogout(int type, int code) { _touched.Clear(); _speedPin.Clear(); _replays.Clear(); _sequences.Clear(); _locoLastApplied.Clear(); _calcGate = _slotGate = -1; }

    private void SanitizeLocalThenClear()
    {
        try
        {
            var lp = _objects.LocalPlayer;
            if (lp != null && lp.Address != nint.Zero && _touched.Contains(lp.ObjectIndex))
            {
                var n = (CSCharacter*)lp.Address;
                n->Timeline.BaseOverride = 0;
                n->Timeline.OverallSpeed = 1f;
                n->Mode = CharacterModes.Normal; // raw field write — no member call mid-zone
                n->ModeParam = 0;
            }
        }
        catch (Exception e) { _log.Error(e, "Anim: territory sanitise failed"); }
        _touched.Clear();
        _speedPin.Clear();
        _replays.Clear();
        _sequences.Clear();
        _locoLastApplied.Clear();
        _calcGate = _slotGate = -1;
    }

    public void Dispose()
    {
        try { SanitizeAll(); }
        catch (Exception e) { _log.Error(e, "Anim: SanitizeAll on dispose failed"); }
        _framework.Update -= OnUpdate;
        _calcSpeedHook?.Disable();
        _calcSpeedHook?.Dispose();
        _setSlotSpeedHook?.Disable();
        _setSlotSpeedHook?.Dispose();
        _clientState.TerritoryChanged -= OnTerritoryChanged;
        _clientState.Logout -= OnLogout;
    }
}
