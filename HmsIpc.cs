using System;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace HDM;

/// <summary>
/// Thin consumer wrapper over HM-Sync's IPC — currently just the ACCENT surface, and notably HDM's FIRST
/// read FROM HMS. Every other HDM↔HMS call runs the other way (HMS consumes HDM's provider, see HdmIpc); this
/// is the seed of the reverse direction — an HMS→modules provider surface that the world-editor / co-op-edit
/// work will grow. It lets HDM follow HM-Sync's user-set accent so the whole HM tool-suite shares one hue,
/// exactly as HDM already follows Moniker (see <see cref="MonikerIpc"/> — this is the same subscriber +
/// version-gate idiom, lifted deliberately).
///
/// Contract HDM binds against (HM-Sync must EXPOSE these as providers — see docs/hms-accent-ipc-ask.md for
/// the full articulated ask):
///   HMSync.ApiVersion     -> () -> (uint major, uint minor)   gate on major &gt;= 1 (accent surface = v1.0)
///   HMSync.GetAccentColor -> () -> float[]                     RGBA, 0..1, length 4; empty/short = unavailable
///
/// PoC status: at time of writing HM-Sync exposes NO IPC provider (it only consumes). This consumer is
/// scaffolded AHEAD of that provider — until HM-Sync ships it, <see cref="Available"/> is false and HDM
/// silently uses its own accent (which defaults to the SAME gold HM-Sync ships, so nothing looks wrong in
/// the meantime). The ask doc articulates the tiny provider HM-Sync adds to light this up.
///
/// Everything is inside try/catch: a missing or older HM-Sync makes the subscriber throw at invoke time,
/// which we treat as "not available" rather than letting it surface. Wire labels are "HMSync.*" (matching
/// HM-Sync's InternalName, the same way Moniker kept a stable IPC namespace across its rename) — HM-Sync must
/// register under the identical strings.
/// </summary>
public sealed class HmsIpc
{
    private const string HmsInternalName = "HMSync";
    private const string HmsDisplayName  = "HM-Sync";

    private readonly IDalamudPluginInterface _pi;
    private readonly ICallGateSubscriber<(uint, uint)> _apiVersion;
    private readonly ICallGateSubscriber<float[]> _getAccentColor;

    public HmsIpc(IDalamudPluginInterface pi)
    {
        _pi = pi;
        _apiVersion     = pi.GetIpcSubscriber<(uint, uint)>("HMSync.ApiVersion");
        _getAccentColor = pi.GetIpcSubscriber<float[]>("HMSync.GetAccentColor");
    }

    /// <summary>
    /// Is HM-Sync installed AND loaded? Distinct from <see cref="Available"/>: HMS can be present but too old
    /// to expose the accent IPC. Used only to word the Config-tab hint ("install HM-Sync" vs "update
    /// HM-Sync"); the accent path itself keys off <see cref="Available"/>. Matches InternalName OR display
    /// Name, case-insensitively (HMS ships as "HM-Sync", not "HMSync" — matching both avoids a silent miss).
    /// </summary>
    public bool Present
    {
        get
        {
            try
            {
                return _pi.InstalledPlugins.Any(p => p.IsLoaded &&
                    (string.Equals(p.InternalName, HmsInternalName, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(p.Name,         HmsDisplayName,  StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(p.Name,         HmsInternalName, StringComparison.OrdinalIgnoreCase)));
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// True if HM-Sync exposes the accent IPC (provider present, major &gt;= 1). Gated inside try/catch: a
    /// missing or older HM-Sync makes ApiVersion throw, which reads as "not available".
    /// </summary>
    public bool Available
    {
        get
        {
            try
            {
                var (major, _) = _apiVersion.InvokeFunc();
                return major >= 1;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Pull HM-Sync's current accent (RGBA, 0..1). Returns true + a valid float[4] on success; false when
    /// HM-Sync is absent/old or returns a malformed array — the caller then falls back to HDM's own accent.
    /// </summary>
    public bool TryGetAccent(out float[] rgba)
    {
        rgba = Array.Empty<float>();
        try
        {
            var c = _getAccentColor.InvokeFunc();
            if (c is { Length: >= 4 })
            {
                rgba = c;
                return true;
            }
        }
        catch
        {
            // HM-Sync not present / provider not registered — fall through to false.
        }
        return false;
    }
}
