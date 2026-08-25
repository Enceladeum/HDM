using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using CSCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using CSSceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;
using CSCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;

namespace HDM;

/// <summary>
/// Task #78 — POSSESS model. A DM "possesses" a spawned puppet: the DM's own body FREEZES in place, WASD/mouse
/// now drive the PUPPET through the world (roaming free of the DM), and the camera orbits the puppet. True
/// remote-control — the opposite of the earlier mirror build (which co-located the puppet on the DM and copied
/// the DM's absolute movement). Chosen by the DM after testing mirror mode ("DM still is not frozen in place …
/// we should intercept wasd as if the DM were wearing the disguise himself").
///
/// HOW IT WORKS — snap-back freeze over the PROVEN measure→classify→drive pipeline (docs/HMS-to-HDM-Locomotion-
/// Guide.md). The animation signal only exists while the DM's OWN body moves natively (the game writes the
/// resolved locomotion clip to <c>TimelineIds[0]</c> and integrates a real position delta only for an actor it
/// pilots — Principle 1). So we do NOT stop the DM from moving; we let the game move the DM one frame's worth,
/// MEASURE that native delta + the clip it chose, REPLICATE both onto the puppet (accumulating the puppet's own
/// world position from its spawn anchor), and THEN snap the DM's position back to the anchor it started at. Net
/// effect: the DM never accumulates displacement (frozen in place, save sub-frame micro-jitter others may see —
/// accepted for a client-side/self-scoped plugin), while the puppet roams by exactly the deltas the DM's input
/// produced, with the run/walk/strafe/turn/jump animation the game already picked. Rotation is left FREE on the
/// DM (needed both to steer and to generate the turn signal); only position is pinned.
///
/// CAMERA — the stock third-person camera only follows the local player for free. To make it follow the puppet we
/// hook the active camera's vfunc 18 (<c>GetCameraTargetObject</c>) and return the puppet while possessing (the
/// Ktisis approach; a plain field write can't move the orbit pivot — it's delivered by that vfunc). Mouse-drag
/// yaw/pitch keep working because the game treats the returned object as the pivot. Released → the detour falls
/// through, the hook is disabled, the camera returns to the DM. Graceful-degrades (freeze + drive still work) if
/// the camera can't be resolved.
///
/// SELF-SCOPE, HARD RULE: possession ONLY ever drives a puppet THIS plugin spawned — every entry point gates on
/// <see cref="SpawnService.IsSpawned"/>. The pilot we MEASURE is always the local player (the DM). HDM is
/// self-apply only.
///
/// ANY SKELETON. The <see cref="LocomotionData"/> values are skeleton-RELATIVE movement-timeline indices
/// (idle=3, runF=22, ...), not human clip ids, so <c>PlayTimeline(index)</c> resolves against the actor's OWN
/// animation set — a monster/demi-human puppet plays ITS run at index 22, not a human clip pasted on. The drive
/// loop copies the DM's resolved tl0 INDEX (semantic), so motion transfers across skeletons. Sparse skeletons
/// (missing strafe/turn/jump slots) simply fall back to what they have; those residuals are diagnosed via
/// <see cref="AnimationService.DumpTimelineState"/>, never gated out.
///
/// Plus a BLUE-DOT OVERLAY: an opt-in foreground overlay paints a dot over each puppet's head; clicking a dot
/// possesses it. The possessed puppet's own dot is hidden (task #72), so release goes through the UI button or
/// ESC. Drawn on the foreground draw list via UiBuilder.Draw, so it works even with the HDM window shut.
///
/// Release is idempotent and wired into EVERY teardown path — the puppet despawning (PuppetRemovedEvent), a zone
/// change, logout, and Dispose — so it can never strand mid-stride, and the camera hook + DM freeze are always
/// undone. CRITICAL (research): <c>_puppetAddress</c>/<c>_isPossessing</c> are cleared BEFORE a puppet can be
/// freed, so the camera detour never returns a dangling pointer. Everything runs on the framework thread.
/// </summary>
public sealed unsafe class PossessionService : IDisposable
{
    private readonly IFramework _framework;
    private readonly IObjectTable _objects;
    private readonly IClientState _clientState;
    private readonly IKeyState _keys;
    private readonly IGameGui _gameGui;
    private readonly IDalamudPluginInterface _pi;
    private readonly IGameInteropProvider _interop; // camera vfunc-18 retarget hook
    private readonly SpawnService _spawn;
    private readonly AnimationService _anim; // DriveLocomotion == the §4b one-writer timeline block
    private readonly IPluginLog _log;

    // Peer-sync hook (Phase A): fired once per driven frame so HMS's OnLocalPuppetMoved reads the puppet's live
    // timeline and broadcasts transform+anim to session peers. Bound at composition to HdmIpc.ReportPuppetMoved;
    // null in any build that doesn't wire sync (possession still drives locally). HDM computes the puppet state,
    // HMS only transports it — we send transform only, HMS reads tl0 itself off the live actor.
    private readonly Action<int, Vector3, float>? _reportPuppetMoved;

    // Peer-sync hook for the DM's OWN-body hide: fired on the _pilotHidden edge so HMS suppresses the DM's
    // own-body mirror on session peers while we're driving (the local Alpha=0 below only hides the DM on their
    // OWN screen — peers render a separate HMS-driven mirror this client can't touch). Bound at composition to
    // HdmIpc.ReportOwnBodyHidden; null in a build without sync (local hide still works).
    private readonly Action<bool>? _reportOwnBodyHidden;

    // "Is a guise redraw in flight for this puppet?" — bound to GuiseService.IsRedrawing at composition. While
    // true we SKIP the per-frame timeline drive on the puppet (see OnUpdate): an explicit re-guise redraws the
    // puppet (DisableDraw→EnableDraw), and our per-frame PlayTimeline fights that rebuild so the new model never
    // lands on the DM's own screen (the "self keeps the old guise, peer sees the new" desync). The symmetric
    // counterpart to GuiseService.SuppressReassert, which stops the self-HEAL redraw from nulling the same
    // Timeline. Null in a build without it wired → drive is never paused (pre-fix behaviour).
    private readonly Func<int, bool>? _isRedrawing;

    // ── Live possession state ──────────────────────────────────────────────────────────────────────────
    private bool _isPossessing;
    private ushort _possessedIndex = ushort.MaxValue;

    // ── Possess anchors (the snap-back freeze) ───────────────────────────────────────────────────────────
    // _dmAnchor: where the DM's body freezes — each frame we snap the DM back here after measuring its native
    // step, so the DM never accumulates displacement. _puppetPos: the puppet's live world position, seeded at
    // its spawn spot (task #80 — it holds position on possession) and advanced by the DM's per-frame native
    // delta so the puppet roams. _puppetAddress: the possessed puppet's native GameObject, returned by the
    // camera detour as the orbit pivot; cleared BEFORE any despawn so the camera never derefs a freed pointer.
    private Vector3 _dmAnchor;
    private Vector3 _puppetPos;
    private float _puppetGroundY; // the puppet's fixed spawn elevation; jump arcs are added ABOVE this, never accumulated into it
    private nint _puppetAddress;

    // ── Mirror state (measured off the pilot each frame; the puppet is a pure function of it) ───────────
    // prevPilotPos/Rot: last frame's DM transform, for the position-delta speed/direction classify and the
    // mouse-pivot rotation-delta turn detection. lastMoveDir: the previous direction bin, fed back into
    // ComputeDirection for its forward/strafe hysteresis dead-band. facingOffset: the eased diagonal lean.
    private Vector3 _prevPilotPos;
    private float _prevPilotRot;
    private byte _lastMoveDir;      // LocomotionData.DirForward (0) at rest
    private float _facingOffset;    // eased lean toward travel on forward diagonals

    // Low-passed render followers (jitter smoothing): what we actually WRITE to the puppet each frame. They chase
    // the exact accumulated _puppetPos / finalRot so the model glides instead of vibrating on the snap-back's
    // per-frame micro-noise. Seeded from the puppet's spawn transform on Possess; horizontal + facing only (Y
    // tracks the jump arc verbatim). Peers are unaffected — they still interpolate the raw _puppetPos we report.
    private Vector3 _puppetPosRender;
    private float _puppetRotRender;

    // ── Camera retarget hook (vfunc 18 GetCameraTargetObject) ────────────────────────────────────────────
    // Created lazily on the first Possess (the camera must exist), enabled only while possessing. The detour
    // returns the possessed puppet so the stock third-person camera orbits it; mouse yaw/pitch keep working.
    private delegate nint CameraTargetDelegate(nint cameraSelf);
    private Hook<CameraTargetDelegate>? _camHook;
    private bool _camHookTried; // don't re-attempt vtable resolution every possess if it once failed

    // ── Change-gated diagnostic (guide §5b / skill Principle 2) ─────────────────────────────────────────
    // Log ONE line per state transition (tl0 or resolved target changed), not a per-frame firehose, so the
    // pilot→tuple→target chain is legible while validating. Demote to Debug once the pipeline is proven.
    private ushort _dbgLastTl0;
    private int _dbgLastTarget = -1; // -1 forces the first frame to log

    // ── Peer-sync report throttle ────────────────────────────────────────────────────────────────────────
    // Fire ReportPuppetMoved change-gated (skip while the puppet is stationary, so an idle-but-possessed puppet
    // doesn't spam the relay) and rate-capped (a moving puppet reports at ~30 Hz, not the full frame rate). The
    // interval is a play-test knob: peer mirrors interpolate, so it can drop further if the relay flags PuppetMove volume.
    private long _lastMoveReportMs;
    private Vector3 _lastReportedPos;
    private float _lastReportedRot;
    private int _reportsSent; // diagnostic: peer-sync reports fired this possession (surfaced on the change-gated line)
    private const long MoveReportIntervalMs = 33;      // ~30 Hz cap
    private const float MoveReportPosEpsSq = 0.0001f;  // (0.01 yalm)^2 — squared so we skip a per-frame sqrt
    private const float MoveReportRotEps = 0.01f;      // radians

    /// <summary>Opt-in blue-dot overlay toggle (session state, driven by the Spawn tab checkbox). When on, a
    /// clickable dot is painted over each live puppet's head regardless of whether the HDM window is open.</summary>
    public bool OverlayEnabled { get; set; }

    /// <summary>Opt-in (default OFF, seeded from <see cref="Configuration.AllowPossessOthersPuppets"/>): let THIS
    /// client possess puppets it did NOT originate — the mirrors of a peer's spawns. Off means control of a spawn
    /// is exclusive to its originator (on every other client it's a mirror, which <see cref="CanPossess"/> refuses).
    /// A possessor-side switch: a client can only gate its OWN behaviour, so the exclusivity holds because each
    /// peer's HDM declines mirrors by default. Flip for cooperative NPC-support (a helper driving the DM's NPC).</summary>
    public bool AllowPossessOthers { get; set; }

    /// <summary>The single ownership gate every possession entry point (the Possess call, the Wear button, the
    /// blue-dot overlay) consults: obj#idx is possessable iff it's one of our live puppets AND either WE
    /// originated it or <see cref="AllowPossessOthers"/> is on. Keeps "only the originator drives a spawn" in one
    /// place rather than re-deriving it at each call site.</summary>
    public bool CanPossess(ushort idx) => _spawn.IsSpawned(idx) && (AllowPossessOthers || _spawn.IsOriginated(idx));

    /// <summary>Hide the DM's own body while driving a puppet (default on). The frozen DM still plays a run-in-place
    /// locomotion clip — that clip IS the signal the drive loop harvests, so we can't stop it without losing the
    /// drive, and we must NOT tear the DM's draw object down (GuiseService.SetHidden's DisableDraw path) because
    /// that nulls the very Timeline we read. Instead we fade the DM to <c>Alpha=0</c> — the draw object stays live
    /// (signal intact) but the model is invisible on the DM's OWN screen (client-side only, matching the HMS-vs-
    /// local split: what peers see is the sync path's problem, not this one). Re-asserted per frame while driving
    /// (the game restores Alpha on its own draw passes) and restored to 1 on Release.</summary>
    public bool HidePilotWhileDriving { get; set; } = true;
    private bool _pilotHidden; // true once we've forced the pilot's Alpha to 0, so Release restores it exactly once

    // ── Classifier speed thresholds (yalms/sec) — HMS StateCaptureService, verbatim ─────────────────────
    // The hybrid classify reads MODE/jump/turn/sprint-flag/armed from the pilot's TimelineIds[0] (lag-free,
    // the game's own answer) but derives the speed MAGNITUDE from the pilot's position delta per second,
    // thresholded into walk/run/sprint. tl0 alone doesn't cleanly separate walk vs run; the position delta
    // does. Sprint is confirmed by tl0 (IsSprint) OR a delta past the sprint threshold.
    private const float WalkSpeed = 0.5f;
    private const float RunSpeed = 3.5f;
    private const float SprintSpeed = 7.0f;

    // ── Movement-INTENT idle threshold (yalms/sec, MoveController forward axis) ──────────────────────────
    // fwdSpeed (MoveController+0x1C8) zeroes the INSTANT WASD releases — no glide-tail — so anything under this
    // means "not trying to move" and the puppet drops to Idle NOW, killing the position-delta walk that stayed
    // latched after release (the "forward walk won't stop unless you tap S" bug). Well below a walk (~2 y/s), and
    // fwdSpeed has no decay ramp so the margin is ample. Only the IDLE GATE reads intent; the walk/run/sprint
    // MAGNITUDE still comes from the position delta (robust to any offset/scale drift in the intent read).
    private const float IntentMoveEps = 0.1f;

    // ── Render smoothing (jitter) ────────────────────────────────────────────────────────────────────────
    // The puppet's world position is ACCUMULATED exactly (_puppetPos) from the DM's per-frame native step, but the
    // snap-back makes each measured step micro-jitter frame to frame, so writing it raw makes the driven model
    // vibrate. We instead RENDER from a low-passed follower that converges to _puppetPos, so the DM's orbit view is
    // as clean as a network-interpolated player. Exponential factors (1 = no smoothing); horizontal + facing only.
    private const float PosSmooth = 0.35f;
    private const float RotSmooth = 0.40f;

    // ── Glitch-delta clamp ────────────────────────────────────────────────────────────────────────────────
    // The puppet advances by the DM's per-frame native position delta. A teleport / zone-edge / knockback can
    // produce a huge one-frame delta that would fling the puppet across the map; ignore any frame whose delta
    // exceeds this (far beyond a sprint's ~0.15 yalm/frame at 60fps).
    private const float MaxFrameDelta = 5f;

    // ── Facing lean (guide §4c) ─────────────────────────────────────────────────────────────────────────
    // A strafing/forward-diagonal body should lean toward its travel direction rather than stay square-on.
    // We lean on FORWARD-diagonals only (the common W+D run) — clamped to the forward/strafe quadrant and
    // eased exponentially so it ramps in/out instead of snapping. Base rotation stays the pilot's yaw, so
    // ordinary turning keeps zero lag. (Backward-lean is deferred; the guide notes strafe→no lean anyway.)
    private const float LeanClamp = MathF.PI / 3f; // 60°, matches the forward/strafe quadrant boundary
    private const float FacingEase = 0.25f;

    // ── Overlay look ───────────────────────────────────────────────────────────────────────────────────
    // The dot floats above the puppet's CROWN, not its foot-anchored Position: approximate the model height
    // from its LIVE draw-object scale times a nominal humanoid height, plus a small clearance.
    private const float NominalHeadHeight = 1.9f; // ~a player's crown height (yalms) at model scale 1.0
    private const float HeadClearance = 0.55f;    // extra lift so the dot floats clear of the crown
    private const float DotRadius = 7f;
    private const float DotHitPad = 6f;         // click tolerance beyond the visible dot (the hit-window half-size)
    private static readonly Vector4 DotFill = new(0.20f, 0.50f, 1.00f, 0.85f); // blue — a controllable puppet
    private static readonly Vector4 DotRing = new(0.85f, 0.92f, 1.00f, 0.95f); // bright rim for legibility

    // Borderless, backgroundless per-dot hit windows (see DrawOverlay): each hosts the InvisibleButton that
    // does the click test in ImGui's OWN coordinate space, so only the tiny head region captures the mouse.
    private const ImGuiWindowFlags DotWinFlags =
        ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav |
        ImGuiWindowFlags.NoScrollWithMouse;

    public PossessionService(IFramework framework, IObjectTable objects, IClientState clientState,
                             IKeyState keys, IGameGui gameGui, IDalamudPluginInterface pi,
                             IGameInteropProvider interop, SpawnService spawn,
                             AnimationService anim, IPluginLog log,
                             Action<int, Vector3, float>? reportPuppetMoved = null,
                             Action<bool>? reportOwnBodyHidden = null,
                             Func<int, bool>? isRedrawing = null)
    {
        _framework = framework;
        _objects = objects;
        _clientState = clientState;
        _keys = keys;
        _gameGui = gameGui;
        _pi = pi;
        _interop = interop;
        _spawn = spawn;
        _anim = anim;
        _log = log;
        _reportPuppetMoved = reportPuppetMoved;
        _reportOwnBodyHidden = reportOwnBodyHidden;
        _isRedrawing = isRedrawing;

        _framework.Update += OnUpdate;
        _pi.UiBuilder.Draw += DrawOverlay;
        _clientState.TerritoryChanged += OnTerritoryChanged;
        _clientState.Logout += OnLogout;
        // The worn puppet vanishing (explicit despawn, zone, logout, dispose) MUST release — otherwise we'd
        // keep driving a freed index next frame.
        _spawn.PuppetRemovedEvent += OnPuppetRemoved;
    }

    /// <summary>True while a puppet is being worn/driven.</summary>
    public bool IsPossessing => _isPossessing;

    /// <summary>The GLOBAL object index of the worn puppet, or -1 if none. The Spawn tab shows it.</summary>
    public int PossessedIndex => _isPossessing ? _possessedIndex : -1;

    /// <summary>
    /// Begin wearing a puppet: from now on it mirrors the DM's own body every frame. Self-scoped — refuses
    /// anything that isn't one of our live puppets. Any skeleton is drivable (locomotion indices resolve
    /// per-skeleton). Switching from another puppet cleanly releases it first.
    /// </summary>
    public void Possess(ushort globalIndex)
    {
        if (!_spawn.IsSpawned(globalIndex))
        {
            _log.Warning($"Wear: refused obj#{globalIndex} — not an HDM puppet.");
            return;
        }
        // Ownership gate: control of a puppet is exclusive to the client that originated it. A mirror of a peer's
        // spawn is refused here unless the DM opted into AllowPossessOthers (cooperative NPC-support). This is the
        // authoritative check — it covers BOTH possession entry points (this call and the blue-dot overlay click),
        // so no path can drive a foreign puppet by default even though _spawned lists mirrors too.
        if (!AllowPossessOthers && !_spawn.IsOriginated(globalIndex))
        {
            _log.Information($"Wear: refused obj#{globalIndex} — a mirror of another player's puppet; only its " +
                            "originator can possess it (enable \"Allow possessing others' puppets\" to override).");
            return;
        }
        var obj = _objects[globalIndex];
        if (obj is not ICharacter c || c.Address == nint.Zero)
        {
            _log.Warning($"Wear: obj#{globalIndex} did not resolve to a live character.");
            return;
        }
        if (_isPossessing && _possessedIndex == globalIndex) return; // already possessing this one

        // Need a live pilot to MEASURE and a real anchor to freeze at; refuse if the DM isn't resolvable (a null
        // LocalPlayer would leave _dmAnchor at (0,0,0) and the snap-back would yank the DM to the world origin).
        if (_objects.LocalPlayer is not { } me || me.Address == nint.Zero)
        {
            _log.Warning($"Possess: refused obj#{globalIndex} — no local player to drive from.");
            return;
        }

        if (_isPossessing) Release(); // switching: release the current first

        _possessedIndex = globalIndex;
        _isPossessing = true;
        _puppetAddress = c.Address; // the camera detour's orbit pivot

        // Freeze the DM where they stand now; the puppet HOLDS its current spawn spot (task #80) and advances
        // from there. Seed prev-pilot from the DM's CURRENT transform so the first frame's delta is ~0 (no
        // spurious opening-frame speed spike).
        _dmAnchor = me.Position;
        _prevPilotPos = me.Position;
        _prevPilotRot = me.Rotation;
        _puppetPos = c.Position;
        _puppetGroundY = c.Position.Y; // fixed ground plane; the jump arc is mirrored ABOVE this each frame
        _puppetPosRender = c.Position; // seed the smoothing followers on the puppet's spot so frame 1 doesn't lerp from origin
        _puppetRotRender = c.Rotation;
        _lastMoveDir = LocomotionData.DirForward;
        _facingOffset = 0f;
        _dbgLastTl0 = 0;
        _dbgLastTarget = -1;

        // Seed the sync throttle from the puppet's current transform so the first real movement reports promptly.
        _lastReportedPos = c.Position;
        _lastReportedRot = c.Rotation;
        _lastMoveReportMs = 0;
        _reportsSent = 0;

        // Retarget the camera onto the puppet (Ktisis-style vfunc-18 hook). Graceful-degrades — freeze + drive
        // still work if the camera can't be resolved.
        EnableCameraFollow();

        _log.Information($"Possess: DM now driving puppet obj#{globalIndex} @0x{c.Address:X} " +
                         $"(freeze DM @ {_dmAnchor.X:0.0},{_dmAnchor.Z:0.0}; puppet holds {_puppetPos.X:0.0},{_puppetPos.Z:0.0}; camera→puppet).");
    }

    /// <summary>
    /// Stop wearing. Idempotent (safe when not wearing, and from any teardown path). Returns the puppet to its
    /// natural idle so it doesn't freeze mid-stride, and leaves it as a static set-piece where it last mirrored.
    /// No restore math for the DM — in mirror mode the DM always kept native control of their own body.
    /// </summary>
    public void Release()
    {
        if (!_isPossessing) return;
        var idx = _possessedIndex;

        // Clear the possess flag + orbit-pivot pointer BEFORE anything else (research gotcha): once _isPossessing
        // is false the camera detour falls through to the real target, so it can never deref a puppet that's
        // about to be freed. Then disable the hook so the stock camera returns to the DM.
        _isPossessing = false;
        _possessedIndex = ushort.MaxValue;
        _puppetAddress = nint.Zero;
        DisableCameraFollow();

        // Return the puppet to natural idle (clear BaseOverride) so it doesn't hold the last locomotion clip.
        // Safe no-op if it's already despawned. The DM needs no position restore — the snap-back kept it at its
        // anchor the whole time, so releasing just stops pinning it and native control resumes seamlessly.
        if (_spawn.IsSpawned(idx) && _objects[idx] is ICharacter c && c.Address != nint.Zero)
        {
            _anim.DriveLocomotion(c, 0);
            // One final report so HMS broadcasts the idle tl0 — otherwise peers keep the last locomotion clip and
            // the mirror is stuck running in place after the DM lets go.
            if (_reportPuppetMoved is { } report)
            {
                var slot = _spawn.SlotOf(idx);
                if (slot >= 0) report(slot, c.Position, c.Rotation);
            }
        }

        // Fade the DM back in if we'd hidden them (Alpha=0) while driving — restore to fully opaque so we never
        // leave the player invisible after letting go. Cheap in-bounds field write on the live local player.
        if (_pilotHidden && _objects.LocalPlayer is { } dm && dm.Address != nint.Zero)
            ((CSCharacter*)dm.Address)->Alpha = 1f;
        SetPilotHidden(false);

        _facingOffset = 0f;
        _log.Information($"Possess: released puppet obj#{idx}; DM unfrozen, camera restored.");
    }

    // Set the pilot-hidden flag and, on a CHANGE, mirror it to peers via HMS. The Alpha=0 write that actually
    // hides the DM locally is re-asserted every frame (see the drive loop), but peers only need the edge, so the
    // report is deduped here — a per-frame SendMessage would spam HMS with identical booleans. Reporting false on
    // release/toggle-off restores the DM's own-body mirror on peers.
    private void SetPilotHidden(bool hidden)
    {
        if (_pilotHidden == hidden) return;
        _pilotHidden = hidden;
        _reportOwnBodyHidden?.Invoke(hidden);
    }

    // Per-frame mirror. Cheap no-op unless actively wearing. MEASURE the pilot → CLASSIFY into the portable
    // tuple → RESOLVE to a timeline → DRIVE the puppet (position, rotation, animation). Guide §7.
    private void OnUpdate(IFramework _)
    {
        if (!_isPossessing) return;
        var idx = _possessedIndex;

        // Re-resolve the puppet each tick (staleness-proof). Gone -> release cleanly.
        if (!_spawn.IsSpawned(idx)) { Release(); return; }
        var pupObj = _objects[idx];
        if (pupObj is not ICharacter puppet || puppet.Address == nint.Zero) { Release(); return; }

        // Esc releases (a backup to the UI Release button; it may also open the game menu).
        if (_keys[VirtualKey.ESCAPE]) { Release(); return; }

        // ── MEASURE the pilot (the DM's own natively-simulated body) — guide §2 ──────────────────────────
        if (_objects.LocalPlayer is not { } dm || dm.Address == nint.Zero) return; // no pilot this frame
        var pilot = (CSCharacter*)dm.Address;
        ushort tl0 = pilot->Timeline.TimelineSequencer.TimelineIds[0]; // the game's resolved locomotion clip
        bool armed = pilot->Timeline.IsWeaponDrawn;
        // Read the DM's lag-free movement INTENT (MoveController fwdSpeed/heading) next to the measured position
        // delta. Non-invasive: the snap-back still drives THIS build; logging both (in the diagnostic below)
        // confirms intent tracks WASD and that fwdSpeed matches the delta-derived speed — the CLASSIFY half of
        // the clean DM-static decouple (docs/HDM-possession-clean-decouple-brief.md §1-2). The SUPPRESS half —
        // whether intent survives a genuinely pinned body so the snap-back + Alpha=0 can be removed — is a
        // separate behaviour-change experiment.
        bool haveIntent = AnimationService.TryReadMoveIntent(pilot, out float intentFwd, out float intentHeading);
        Vector3 pos = dm.Position;  // collision + gravity resolved — mirror verbatim
        float rot = dm.Rotation;    // input-resolved yaw

        float dt = (float)_framework.UpdateDelta.TotalSeconds;
        if (dt <= 0f || dt > 0.25f) dt = 1f / 60f; // guard a paused / stalled frame

        // ── CLASSIFY into the portable tuple (mode, speed, direction, isTurning, jumpPhase, armed) — §3 ──
        byte mode = LocomotionData.DetectModeFromTimeline(tl0);
        byte jumpPhase = LocomotionData.DetectJumpPhase(tl0);
        bool isSprintTl = LocomotionData.IsSprint(tl0);

        float dx = pos.X - _prevPilotPos.X;
        float dz = pos.Z - _prevPilotPos.Z;
        float horizDist = MathF.Sqrt(dx * dx + dz * dz);
        float speed = horizDist / dt; // yalms/sec

        // IDLE GATE from movement INTENT (instant on press AND release), speed MAGNITUDE from the position delta.
        // The snap-back leaves `speed` latched non-zero after the key releases — it only cleared when a counter-key
        // was tapped (the "forward walk won't stop unless you press S" bug). fwdSpeed zeroes the instant the key
        // releases, so we force Idle on it regardless of the stale delta. Symmetrically, on PRESS fwdSpeed ramps
        // before the position delta does, so intent-moving-but-delta-cold gets at least a walk (kills the start
        // slide). Falls back to pure position bins when the intent read is unavailable. Strafe caveat: fwdSpeed is
        // the forward/back axis, so a pure lateral strafe (rare under default controls, which turn not strafe)
        // reads ~0 and idles — if a strafing puppet under-animates, this gate is why.
        bool intentMoving = haveIntent && MathF.Abs(intentFwd) > IntentMoveEps;
        bool intentIdle = haveIntent && !intentMoving;

        byte moveState;
        if (intentIdle) moveState = LocomotionData.SpeedIdle;                        // key released -> stop NOW
        else if (isSprintTl && speed > WalkSpeed) moveState = LocomotionData.SpeedSprint;
        else if (speed > SprintSpeed) moveState = LocomotionData.SpeedSprint;
        else if (speed > RunSpeed) moveState = LocomotionData.SpeedRun;
        else if (speed > WalkSpeed) moveState = LocomotionData.SpeedWalk;
        else if (intentMoving) moveState = LocomotionData.SpeedWalk;                 // intent leads the delta on start -> at least walk
        else moveState = LocomotionData.SpeedIdle;

        byte direction = LocomotionData.ComputeDirection(dx, dz, rot, _lastMoveDir);
        _lastMoveDir = direction;

        float rotDelta = LocomotionData.WrapAngle(rot - _prevPilotRot);
        // Keyboard turn-in-place plays a dedicated turn clip (caught by tl0); mouse pivot rotates SILENTLY
        // (no clip), so fall back to the rotation delta to know a turn is happening and which way.
        bool isTurning = LocomotionData.IsTurnTimeline(tl0) || MathF.Abs(rotDelta) > LocomotionData.MinTurnDelta;

        // ── RESOLVE the tuple to a single winning timeline (priority: jump > move > turn > idle) — §4a ───
        ushort target;
        if (jumpPhase != LocomotionData.JumpNone)
        {
            target = LocomotionData.GetJumpTimeline(mode, armed, jumpPhase);
            if (target == 0) target = LocomotionData.GetTimeline(mode, armed, moveState, direction); // mode w/o jump
        }
        else if (moveState != LocomotionData.SpeedIdle)
        {
            target = LocomotionData.GetTimeline(mode, armed, moveState, direction);
        }
        else if (isTurning && MathF.Abs(rotDelta) > LocomotionData.MinTurnDelta)
        {
            target = LocomotionData.GetTurnTimeline(mode, armed, rotDelta > 0f); // rotΔ>0 == turning left
        }
        else if (armed)
        {
            // Armed idle: a puppet has NO native input loop (guide Principle 1) to settle into a weapon-drawn
            // battle idle the way the DM's own body does. Clearing BaseOverride to the "natural idle" (target 0)
            // left the last battle-locomotion clip (BtlWalk) looping in place — the "waddle / twist dance" the
            // DM saw on a standing armed puppet. Drive the battle idle stance (BtlIdle) explicitly through the
            // SAME one-writer that already renders battle walk/run correctly, so a standing armed puppet holds a
            // clean combat idle. This is Principle 1's "write the state the native reader drives from, once, then
            // let it hold" — DriveLocomotion SUSTAIN re-pins 34 each tick.
            target = LocomotionData.GetTimeline(mode, armed, LocomotionData.SpeedIdle, direction);
        }
        else
        {
            target = 0; // unarmed idle -> DriveLocomotion clears BaseOverride to the natural idle
        }

        // ── FACING LEAN toward travel on forward diagonals (eased); base stays pilot yaw — §4c ───────────
        float targetLean = 0f;
        if (moveState != LocomotionData.SpeedIdle && direction == LocomotionData.DirForward && horizDist > 1e-4f)
        {
            float rel = LocomotionData.WrapAngle(MathF.Atan2(dx, dz) - rot); // travel angle relative to facing
            targetLean = Math.Clamp(rel, -LeanClamp, LeanClamp);
        }
        _facingOffset += (targetLean - _facingOffset) * FacingEase;
        float finalRot = LocomotionData.WrapAngle(rot + _facingOffset);

        // ── DRIVE — advance the puppet by the DM's native per-frame step, then FREEZE the DM (snap-back) ──
        // Horizontal (X/Z) and vertical (Y) are driven DIFFERENTLY because the snap-back gives them different
        // meanings. Since we pin the DM back to _dmAnchor every frame (below) and set _prevPilotPos = _dmAnchor
        // (frame end), each frame's measured `pos` is the native single-step FROM the anchor:
        //   • X/Z — one incremental walk/run step. ACCUMULATE it so the puppet roams from its spawn spot.
        //   • Y   — NOT an increment: pos.Y - _dmAnchor.Y is the ABSOLUTE arc height at this instant of the jump
        //           (the native jump controller re-derives height from a time curve off the pinned ground base, so
        //           dmDrift reads the true apex ~0.64y, not a per-frame delta). Accumulating it over-integrated and
        //           flung the puppet skyward; zeroing it killed the arc (only the clip's baked mesh lift showed — the
        //           "very shallow hop"). ASSIGN it: puppet Y = fixed ground + the DM's measured height, so the puppet
        //           traces the DM's real jump arc 1:1 and returns to ground on landing (pos.Y==anchor.Y ⇒ Δ0).
        // The guard tests only the HORIZONTAL magnitude so a jumping-run's vertical component can't trip MaxFrameDelta
        // and drop the legit horizontal step. Terrain-following / real gravity is the deferred HMS collision work.
        var frameDelta = pos - _prevPilotPos;
        var horizStep = new Vector3(frameDelta.X, 0f, frameDelta.Z);
        if (horizStep.Length() <= MaxFrameDelta)
            _puppetPos += horizStep;
        _puppetPos.Y = _puppetGroundY + (pos.Y - _dmAnchor.Y); // mirror the DM's absolute jump arc above the ground plane

        // Jitter smoothing: WRITE a low-passed follower that converges to the exact _puppetPos / finalRot, so the
        // model glides instead of vibrating on the snap-back's per-frame micro-noise. Horizontal + facing only —
        // Y tracks the jump arc verbatim (smoothing it would lag/damp the hop). Peers unaffected: ReportDriveIfDue
        // below still sends the raw _puppetPos, and their mirrors interpolate it.
        _puppetPosRender.X += (_puppetPos.X - _puppetPosRender.X) * PosSmooth;
        _puppetPosRender.Z += (_puppetPos.Z - _puppetPosRender.Z) * PosSmooth;
        _puppetPosRender.Y = _puppetPos.Y;
        _puppetRotRender = LocomotionData.WrapAngle(_puppetRotRender + LocomotionData.WrapAngle(finalRot - _puppetRotRender) * RotSmooth);

        _spawn.SetPosition(idx, _puppetPosRender);
        _spawn.SetRotation(idx, _puppetRotRender);
        // PAUSE the timeline drive while an explicit re-guise is redrawing THIS puppet: the redraw tears the draw
        // object down and rebuilds it with the new model, and a concurrent per-frame PlayTimeline/BaseOverride
        // write fights that rebuild — which left the DM's OWN view stuck on the old guise while an un-possessed
        // peer mirror (no drive fighting it) rebuilt cleanly. Position/rotation above are harmless to the rebuild
        // and keep tracking so the puppet doesn't stall in place; only the conflicting Timeline write is skipped.
        // The drive resumes the frame the redraw finishes (SUSTAIN re-pins BaseOverride, which also re-kicks a
        // demihuman's re-rig on the freshly-rebuilt skeleton). Never paused in a build without the predicate wired.
        if (_isRedrawing?.Invoke(idx) != true)
            _anim.DriveLocomotion(puppet, target); // §4b one-writer: EDGE fire+pin / SUSTAIN re-pin / landing release

        // Peer sync (Phase A): report the driven transform so HMS broadcasts it to session peers. Placed AFTER
        // DriveLocomotion so the puppet's tl0 is current when HMS samples it. Throttled — see ReportDriveIfDue.
        ReportDriveIfDue(idx, finalRot);

        // Snap the DM's body back to its anchor — THIS is the freeze. We already measured the native step above
        // (the animation/position signal the pipeline needs), so undoing the displacement now costs nothing but
        // keeps the DM planted. Rotation is left free (the DM must be able to turn to steer + make the turn clip).
        // Writing GameObject.Position on the live, this-frame-resolved local player is an in-bounds field write —
        // no sig-scanned pointer (unlike the 0.8.79 CTD freeze), so it needs no page-validation.
        pilot->GameObject.Position = _dmAnchor;

        // Suppress the DM's visible run-in-place: fade to Alpha=0 while driving (draw object stays live, so the
        // Timeline signal we harvest above is untouched). Re-asserted each frame because the game restores Alpha
        // on its own draw passes. Honours the live toggle — flip it off mid-drive and the DM fades back in.
        if (HidePilotWhileDriving) { pilot->Alpha = 0f; SetPilotHidden(true); }
        else if (_pilotHidden)     { pilot->Alpha = 1f; SetPilotHidden(false); }

        // Change-gated diagnostic: one line per pilot-clip or resolved-target transition. dmDrift reveals whether
        // the snap-back is holding (should stay ~one frame's step); if it grows unbounded the write isn't sticking.
        if (tl0 != _dbgLastTl0 || target != _dbgLastTarget)
        {
            _dbgLastTl0 = tl0;
            _dbgLastTarget = target;
            var dmDrift = (pos - _dmAnchor).Length();
            // intentFwd should track the delta-derived speed; relHead (heading - facing) should track the
            // direction bin — the two readings the clean decouple would source INSTEAD of the position delta.
            var intentStr = haveIntent
                ? $"intent[fwd={intentFwd:0.0} relHead={LocomotionData.WrapAngle(intentHeading - rot):0.00}]"
                : "intent[unverified]";
            _log.Information(
                $"Possess obj#{idx}: pilot tl0={tl0} armed={armed} -> mode={mode} speed={moveState}({speed:0.0}y/s) " +
                $"dir={direction} jump={jumpPhase} turn={isTurning} => target={target}; dmDrift={dmDrift:0.00} {intentStr} sync={_reportsSent}");
        }

        // Snap-back model: next frame the DM integrates from the anchor again, so prev = anchor (NOT pos). Each
        // frame's measured delta then = the native step from the anchor. (If dmDrift grows in the log the Position
        // write isn't sticking on this build — flip this to `pos` and drop the snap-back write above.)
        _prevPilotPos = _dmAnchor;
        _prevPilotRot = rot;
    }

    // Fire the throttled per-frame peer-sync report. Change-gated (skip while stationary) + rate-capped (~30 Hz).
    // Sends the SLOT (the cross-client identity), not the global index — HMS namespaces the puppet by slot.
    private void ReportDriveIfDue(ushort globalIndex, float puppetRot)
    {
        if (_reportPuppetMoved is null) return;
        var now = Environment.TickCount64;
        if (now - _lastMoveReportMs < MoveReportIntervalMs) return;
        var moved = (_puppetPos - _lastReportedPos).LengthSquared() > MoveReportPosEpsSq
                    || MathF.Abs(LocomotionData.WrapAngle(puppetRot - _lastReportedRot)) > MoveReportRotEps;
        if (!moved) return;
        var slot = _spawn.SlotOf(globalIndex);
        if (slot < 0) return; // not a tracked own-puppet — nothing to sync
        _reportPuppetMoved(slot, _puppetPos, puppetRot);
        _lastMoveReportMs = now;
        _lastReportedPos = _puppetPos;
        _lastReportedRot = puppetRot;
        _reportsSent++;
    }

    // ── Blue-dot overlay ───────────────────────────────────────────────────────────────────────────────
    // Foreground overlay: a blue dot floating over each puppet's head; click to start wearing it. Independent
    // of the HDM window (UiBuilder.Draw), so the DM can wear a puppet without opening the catalog. The dot
    // floats above the CROWN (HeadLift, scale-aware), DISAPPEARS on the puppet you're wearing, and the click is
    // handled by a real ImGui InvisibleButton in a tiny per-dot window (the only reliable hit-test path). The
    // dot VISUAL rides the foreground list so it draws on top of everything.
    private void DrawOverlay()
    {
        if (!OverlayEnabled) return;
        var spawned = _spawn.Spawned;
        if (spawned.Count == 0) return;

        var dl = ImGui.GetForegroundDrawList();
        var fill = ImGui.GetColorU32(DotFill);
        var ring = ImGui.GetColorU32(DotRing);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        for (int i = 0; i < spawned.Count; i++)
        {
            var idx = spawned[i];
            if (_isPossessing && idx == _possessedIndex) continue; // hide the dot on the puppet being worn
            if (!CanPossess(idx)) continue; // no possess-dot on a puppet this client may not drive (a peer's mirror)
            var obj = _objects[idx];
            if (obj is not ICharacter c || c.Address == nint.Zero) continue;
            if (!_spawn.TryGetTransform(idx, out var pos, out _)) continue;
            var head = new Vector3(pos.X, pos.Y + HeadLift(c), pos.Z);
            if (!_gameGui.WorldToScreen(head, out var screen)) continue; // off-screen / behind the camera

            dl.AddCircleFilled(screen, DotRadius, fill, 16);
            dl.AddCircle(screen, DotRadius, ring, 16, 1.5f);

            // A tiny borderless window + InvisibleButton at the same spot: ImGui does the hit-test in its own
            // space. Only this small rect captures the mouse; the rest of the screen still reaches the game.
            float r = DotRadius + DotHitPad;
            ImGui.SetNextWindowPos(new Vector2(screen.X - r, screen.Y - r));
            ImGui.SetNextWindowSize(new Vector2(r * 2f, r * 2f));
            // BuildIdSuffix keeps a testing build's dots from conjoining with a co-loaded prod HDM's:
            // same id string => ImGui merges the windows. Empty in prod (id byte-unchanged), "Testing"
            // in the testing build. The hit-button id is scoped inside this now-distinct window, so it
            // needs no suffix of its own.
            if (ImGui.Begin($"##hdmdot{idx}{Plugin.BuildIdSuffix}", DotWinFlags))
            {
                ImGui.InvisibleButton($"##hit{idx}", new Vector2(r * 2f, r * 2f));
                if (ImGui.IsItemHovered())
                {
                    dl.AddCircle(screen, DotRadius + 2.5f, ring, 16, 2f); // hover feedback
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                }
                if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                    Possess(idx);
            }
            ImGui.End();
        }
        ImGui.PopStyleVar(2);
    }

    // Where to float the dot above a puppet's foot-anchored Position: a nominal humanoid crown height scaled
    // by the puppet's LIVE draw-object scale (so an authored-large boss lifts its dot proportionally) plus a
    // small clearance. Reads Scale @ Graphics.Scene.Object +0x70 off the live draw object; falls back to 1.0
    // if the draw object isn't up yet. In-bounds reads on a live actor on the framework thread — crash-safe.
    private float HeadLift(ICharacter c)
    {
        float scale = 1f;
        var draw = ((CSCharacter*)c.Address)->GameObject.DrawObject;
        if (draw != null)
        {
            var s = ((CSSceneObject*)draw)->Scale;
            var m = MathF.Max(s.X, MathF.Max(s.Y, s.Z));
            if (m > 0f && !float.IsNaN(m) && !float.IsInfinity(m)) scale = m;
        }
        return NominalHeadHeight * scale + HeadClearance;
    }

    // ── Camera follow (vfunc-18 retarget) ────────────────────────────────────────────────────────────────
    // Enable: lazily hook the ACTIVE camera's GetCameraTargetObject (vtable slot 18) and turn it on. While
    // possessing, the detour returns the puppet, so the stock third-person camera orbits it (mouse yaw/pitch keep
    // working — the game treats the returned object as the pivot). A one-time resolution failure is remembered
    // (_camHookTried) so we don't rescan every possess; possession simply proceeds camera-less (graceful degrade).
    private void EnableCameraFollow()
    {
        if (_camHook == null && !_camHookTried)
        {
            _camHookTried = true;
            try
            {
                var cm = CSCameraManager.Instance();
                if (cm == null || cm->Camera == null)
                {
                    _log.Warning("Possess: camera unavailable — driving without camera follow.");
                    return;
                }
                nint camPtr = (nint)cm->Camera;
                nint vtbl = *(nint*)camPtr;           // vtable pointer at offset 0 of the camera object
                nint vf18 = *(nint*)(vtbl + 18 * 8);  // slot 18 = GetCameraTargetObject (8 bytes/entry, x64)
                _camHook = _interop.HookFromAddress<CameraTargetDelegate>(vf18, CameraTargetDetour);
                _log.Information($"Possess: camera-target hook resolved @0x{vf18:X}.");
            }
            catch (Exception e)
            {
                _log.Error(e, "Possess: failed to hook the camera target — driving without camera follow.");
                _camHook = null;
            }
        }
        _camHook?.Enable();
    }

    private void DisableCameraFollow() => _camHook?.Disable();

    // The camera's orbit pivot. While possessing, hand it the puppet instead of the player so the camera follows
    // the puppet; otherwise fall through to the real target. _puppetAddress is cleared before any despawn, so a
    // non-zero value here is always a live actor.
    private nint CameraTargetDetour(nint cameraSelf)
    {
        if (_isPossessing && _puppetAddress != nint.Zero)
            return _puppetAddress;
        return _camHook!.Original(cameraSelf);
    }

    // ── Teardown wiring — Release from every path that could free the worn puppet ────────────────────────
    private void OnPuppetRemoved(ushort idx, int slot)
    {
        if (_isPossessing && idx == _possessedIndex) Release();
    }

    private void OnTerritoryChanged(uint _) => Release();
    private void OnLogout(int type, int code) => Release();

    public void Dispose()
    {
        try { Release(); }
        catch (Exception e) { _log.Error(e, "Possess: Release on dispose failed."); }

        _camHook?.Dispose();
        _camHook = null;

        _framework.Update -= OnUpdate;
        _pi.UiBuilder.Draw -= DrawOverlay;
        _clientState.TerritoryChanged -= OnTerritoryChanged;
        _clientState.Logout -= OnLogout;
        _spawn.PuppetRemovedEvent -= OnPuppetRemoved;
    }
}
