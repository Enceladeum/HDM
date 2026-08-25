using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace HDM;

/// <summary>
/// Thin consumer wrapper over Moniker's (HMoniker's) IPC for the nameplate half —
/// lifted verbatim from HOutfits so HDM's "rename nameplate to the disguise" toggle
/// drives the SAME cross-plugin surface (one Moniker owns the local name; HMS carries
/// it to peers). Contract locked with the Moniker instance (HMoniker v1.1.0+, ApiVersion 2.1):
///
///   Moniker.ApiVersion    -> (uint major, uint minor)   gate on major==2 &amp;&amp; minor>=1
///   Moniker.SetLocalName  -> (string) -> bool           sets the LOCAL player's nameplate name
///   Moniker.ClearLocalName-> ()                          restores the real name
///
/// Wire labels are "Moniker.*" (NOT "HMoniker.*") — the plugin renamed but kept the IPC
/// namespace for HMS compatibility. Moniker owns the First/Middle/Last split; we pass the
/// raw disguise name string. SetLocalName only ever touches the local player (HMoniker's
/// provider rejects an IPC name aimed at the local player from the courier path, but this
/// call is the user's own explicit action so it writes their own config); HMoniker then
/// syncs the change to peers through HMS — HDM does no nameplate sync itself.
///
/// The version gate is INSIDE the try/catch: a missing Moniker makes the ApiVersion
/// subscriber throw, which we treat as "not available" (grey out the toggle) rather than
/// letting it surface.
/// </summary>
public sealed class MonikerIpc
{
    private readonly ICallGateSubscriber<(uint, uint)> _apiVersion;
    private readonly ICallGateSubscriber<string, bool> _setLocalName;
    private readonly ICallGateSubscriber<object> _clearLocalName;

    public MonikerIpc(IDalamudPluginInterface pi)
    {
        _apiVersion     = pi.GetIpcSubscriber<(uint, uint)>("Moniker.ApiVersion");
        _setLocalName   = pi.GetIpcSubscriber<string, bool>("Moniker.SetLocalName");
        _clearLocalName = pi.GetIpcSubscriber<object>("Moniker.ClearLocalName");
    }

    /// <summary>
    /// True if Moniker is installed and new enough to have the local-name calls.
    /// Adding those methods was a MINOR bump (2.0 -> 2.1), so gate on minor too:
    /// major-only can't tell 2.0 (no SetLocalName) from 2.1 (has it).
    /// </summary>
    public bool Available
    {
        get
        {
            try
            {
                var (major, minor) = _apiVersion.InvokeFunc();
                return major == 2 && minor >= 1;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Set the local player's nameplate name (Moniker splits it). True on success.</summary>
    public bool SetLocalName(string name)
    {
        try
        {
            return _setLocalName.InvokeFunc(name);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Restore the real name. Call only when a name was actually applied this session.</summary>
    public void ClearLocalName()
    {
        try
        {
            _clearLocalName.InvokeAction();
        }
        catch
        {
            // Moniker not present / call unavailable — nothing to clear.
        }
    }
}
