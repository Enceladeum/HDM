using Dalamud.Plugin;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace HDM;

/// <summary>
/// Minimal consumer wrapper over Penumbra's IPC. HDM makes exactly ONE Penumbra call:
/// <see cref="Redraw"/> on the local player — the programmatic form of the manual
/// "/penumbra redraw self" a DM confirmed is the ONLY thing that restores their real
/// (privacy-glam) appearance after a Human-guise revert.
///
/// Why Penumbra and not HDM's own native <see cref="GuiseService.Redraw"/> (which HDM uses
/// everywhere else, and which the standing "No penumbra workarounds" rule prefers): the two are
/// NOT equivalent for a MODDED actor. A raw draw-object rebuild (DisableDraw→EnableDraw) rebuilds
/// the skeleton but reuses Penumbra's already-resolved file redirections; when the reverted outfit
/// is Penumbra-modded GEAR, only PENUMBRA'S redraw re-resolves those paths through its collection
/// system and re-injects them. That is exactly why 0.8.62's native redraw left the DM in stale
/// disguise gear while a manual "/penumbra redraw self" fixed it. So the constraint is relaxed for
/// THIS ONE restore path only: a single RedrawObject on the local player, gated on Penumbra being
/// present (see <see cref="Redraw"/>'s bool return), used nowhere else in HDM.
///
/// Typed against Penumbra.Api 5.15.1 (same net10.0-windows TFM as HDM; pulled transitively by the
/// existing Glamourer.Api reference). Penumbra.Api.dll ships next to HDM.dll for the SAME ALC reason
/// Glamourer.Api.dll does — each plugin loads in its own AssemblyLoadContext and does not inherit
/// Penumbra's copy; the IPC crosses the boundary by string label ("Penumbra.RedrawObject.V5") with
/// marshalled args, so our own copy never clashes with Penumbra's. Inert when Penumbra is absent:
/// the subscriber constructs fine (GetIpcSubscriber never throws for a missing provider) and
/// <see cref="Redraw"/> swallows the invoke-time error and reports false so the caller can fall
/// back to the native redraw.
/// </summary>
public sealed class PenumbraIpc
{
    private readonly RedrawObject _redraw;

    public PenumbraIpc(IDalamudPluginInterface pi)
    {
        _redraw = new RedrawObject(pi);
    }

    /// <summary>
    /// Force Penumbra to redraw an actor by object-table index — the IPC equivalent of
    /// "/penumbra redraw self" (<see cref="RedrawType.Redraw"/> = a full destroy+rebuild that
    /// re-resolves the actor's mod files). Returns true if the call reached Penumbra; false if
    /// Penumbra isn't installed (the invoke throws IpcNotReady), so the caller can fall back to
    /// HDM's native <see cref="GuiseService.Redraw"/>.
    /// </summary>
    public bool Redraw(int objectIndex)
    {
        try
        {
            _redraw.Invoke(objectIndex, RedrawType.Redraw);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
