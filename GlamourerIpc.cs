using Dalamud.Plugin;
using Glamourer.Api.Enums;
using Glamourer.Api.IpcSubscribers;
using Newtonsoft.Json.Linq;

namespace HDM;

/// <summary>
/// Thin wrapper over the Glamourer IPC — the Human (c-skeleton) guise path only.
///
/// Why the Human path needs Glamourer at all (and Monster/Demihuman don't): a
/// ModelChara.Type==1 model is a bare human body skeleton. A raw ModelCharaId swap
/// leaves it T-posed (no customize, no gear), and writing the NPC's CustomizeData
/// directly into the character doesn't stick in live play — the game's
/// FilterCustomizeData scrubs NPC-only customize off a player actor every redraw.
/// Glamourer owns that surface: its ApplyState paints both the customize block and
/// NPC-model gear (encoded as CustomItemIds) and makes it persist. This wrapper is
/// a self-contained copy of HOutfits' GlamourerIpc (duplication is intentional —
/// no cross-plugin dependency), trimmed to the three calls the human path uses.
///
/// All Invoke() calls marshal onto the framework thread inside Glamourer; call
/// from the framework thread (UI Draw is fine).
/// </summary>
public sealed class GlamourerIpc
{
    private readonly ApiVersion _apiVersion;
    private readonly RevertState _revert;
    private readonly GetState _getState;
    private readonly ApplyState _applyState;
    private readonly SetMetaState _setMetaState;

    public GlamourerIpc(IDalamudPluginInterface pi)
    {
        _apiVersion   = new ApiVersion(pi);
        _revert       = new RevertState(pi);
        _getState     = new GetState(pi);
        _applyState   = new ApplyState(pi);
        _setMetaState = new SetMetaState(pi);
    }

    /// <summary>
    /// True if Glamourer is loaded and answering. We don't gate on a specific
    /// major: the ApplyState / RevertState / GetState labels this plugin binds are
    /// stable across current Glamourer (2.x). If the version call throws, the
    /// subscriber isn't registered => Glamourer isn't loaded, and the human guise
    /// path graceful-degrades (the caller logs and no-ops).
    /// </summary>
    public bool Available
    {
        get
        {
            try
            {
                return _apiVersion.Invoke().Major > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Read an actor's full Glamourer state as a JObject (customize + equipment +
    /// meta). Returns null if the call didn't succeed. The human-guise builder
    /// clones this known-good template and overwrites only the fields it changes,
    /// so it never has to reconstruct Glamourer's full schema blind.
    /// </summary>
    public JObject? GetState(int objectIndex)
    {
        try
        {
            var (ec, json) = _getState.Invoke(objectIndex);
            return ec == GlamourerApiEc.Success ? json : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Apply a full Glamourer state JObject to an actor. This is the only path that
    /// can paint customize (face/body) — the API has no per-customize setter. Flags
    /// select which regions apply (Equipment, Customization, or both).
    /// </summary>
    public GlamourerApiEc ApplyState(JObject state, int objectIndex, ApplyFlag flags)
        => _applyState.Invoke(state, objectIndex, key: 0, flags: flags);

    /// <summary>
    /// Set ONE meta-visibility flag (weapon / hat / visor / …) on an actor, surgically — the correct
    /// tool for toggling a human guise's drawn-weapon, borrowed from HMS's GlamourerIpc.SetMeta.
    ///
    /// Why not ApplyState (the b6 attempt): a hand-built JObject carrying only Equipment.Weapon.Show
    /// is a partial design with no Customize block, which Glamourer rejects with "the loaded design
    /// does not contain any customization data, reset to default" — it wiped the guise instead of
    /// toggling the weapon. SetMetaState pokes the single meta bit through Glamourer's own setter with
    /// no design load, so it never disturbs customize/equipment. It also OVERWRITES a Fixed WeaponState
    /// (which the StateListener re-asserts every redraw); an omitted/Apply=false block cannot clear a
    /// Fixed meta, which is why the b5 "unmanage by omission" path was inert.
    ///
    /// Flag sense is VISIBILITY (true = shown), matching Glamourer's own checkbox. Returns true on
    /// Success; false (caught) when Glamourer is absent so the caller can log and no-op.
    /// </summary>
    public bool SetMeta(int objectIndex, MetaFlag flag, bool visible)
    {
        try
        {
            var ec = _setMetaState.Invoke(objectIndex, flag, visible, key: 0, flags: ApplyFlag.Equipment);
            return ec == GlamourerApiEc.Success;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Revert this actor fully back to game/automation state — equipment AND
    /// customization, matching "/glamour revert &lt;actor&gt;". Used to undo a human
    /// guise. Note: like the in-game revert, this also washes out any unrelated
    /// Glamourer state the user had on the actor (acceptable for a DM tool).
    /// </summary>
    public GlamourerApiEc Revert(int objectIndex)
        => _revert.Invoke(objectIndex, key: 0, flags: ApplyFlagEx.RevertDefault);

    /// <summary>
    /// Reset this actor's Glamourer state to the GAME BASE — a FULL wipe that clears
    /// customize, equipment, AND the advanced overrides: every CustomizeParameter
    /// (SkinDiffuse / LipDiffuse / FeatureColor / FacePaint colours) back to game-derived,
    /// plus the material colour-tables. Routes to Glamourer's StateManager.ResetState,
    /// which is reached ONLY by the combined Equipment|Customization case of its private
    /// Revert switch (either flag alone hits ResetEquip / ResetCustomize, and neither of
    /// those touches parameters or materials). We pass the two flags EXPLICITLY rather
    /// than ApplyFlagEx.RevertDefault so the full-wipe routing is guaranteed and does not
    /// depend on that constant resolving to the same combination.
    ///
    /// This is the human-guise path's lever to erase a DM SkinDiffuse that an earlier
    /// apply pinned into Glamourer's PERSISTENT actor state. Stripping the Parameters
    /// block from an outgoing ApplyState JObject can't do it — Glamourer reads an absent
    /// Parameters block as "leave the actor's current parameters unchanged", so the pin
    /// survives. A revert is the only IPC call that actively clears it. Safe on a clean
    /// actor: it just resets to game base a frame before we repaint the NPC.
    /// </summary>
    public GlamourerApiEc RevertToGameBase(int objectIndex)
        => _revert.Invoke(objectIndex, key: 0, flags: ApplyFlag.Equipment | ApplyFlag.Customization);
}
