using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace HDM;

/// <summary>
/// b44: thin consumer of HOutfits' "dressing target" IPC, so the Stage's "Dress…" button opens HOutfits pointed at the
/// selected puppet (or at you): its sets, pieces, NPC outfits, faces and weapons then apply to that character. Contract
/// (HOutfits provides, HDM consumes):
///
///   HOutfits.Target.ApiVersion -> (uint major, uint minor)   gate on major == 1
///   HOutfits.Target.Set        (int objectIndex, string label) -> bool   retarget + open the window; false = not a human actor
///   HOutfits.Target.Clear      ()                                        back to dressing yourself
///   HOutfits.Target.Get        () -> int                                 current target, -1 = yourself
///
/// Inert until HOutfits ships it: a missing provider makes ApiVersion throw, read as "not available" (the button greys
/// out with a hint) - the same pattern as MonikerIpc.
/// </summary>
public sealed class HOutfitsIpc
{
    private readonly ICallGateSubscriber<(uint, uint)> _apiVersion;
    private readonly ICallGateSubscriber<int, string, bool> _set;
    private readonly ICallGateSubscriber<object> _clear;
    private readonly ICallGateSubscriber<int> _get;

    public HOutfitsIpc(IDalamudPluginInterface pi)
    {
        _apiVersion = pi.GetIpcSubscriber<(uint, uint)>("HOutfits.Target.ApiVersion");
        _set        = pi.GetIpcSubscriber<int, string, bool>("HOutfits.Target.Set");
        _clear      = pi.GetIpcSubscriber<object>("HOutfits.Target.Clear");
        _get        = pi.GetIpcSubscriber<int>("HOutfits.Target.Get");
    }

    /// <summary>True when HOutfits is installed and offers the dressing target. Polled by the Stage every frame, so the
    /// answer is cached for a few seconds (a missing provider throws, and exceptions per frame are wasteful).</summary>
    public bool Available
    {
        get
        {
            var now = System.Environment.TickCount64;
            if (now < _nextCheck) return _available;
            _nextCheck = now + 3000;
            try { _available = _apiVersion.InvokeFunc().Item1 == 1; }
            catch { _available = false; }
            return _available;
        }
    }
    private bool _available;
    private long _nextCheck;

    /// <summary>Point HOutfits at this actor and open it. False if HOutfits declined (not a human) or isn't there.</summary>
    public bool DressTarget(int objectIndex, string label)
    {
        try { return _set.InvokeFunc(objectIndex, label); }
        catch { return false; }
    }

    /// <summary>Point HOutfits back at the local player.</summary>
    public void DressSelf()
    {
        try { _clear.InvokeAction(); }
        catch { /* not available */ }
    }

    /// <summary>HOutfits' current target object index (-1 = yourself, or unavailable).</summary>
    public int CurrentTarget
    {
        get
        {
            try { return _get.InvokeFunc(); }
            catch { return -1; }
        }
    }
}
