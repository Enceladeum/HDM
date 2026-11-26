using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Glamourer.Api.Helpers;

namespace HDM;

/// <summary>b45: a compact, sync-sized form of a puppet's Glamourer look (gear, dyes, facewear and face/body customize).
/// HMS passes HDM's atom JSON through but caps it at 2,048 characters, and a full Glamourer state string runs 1.5-2.6k,
/// so only the sections a dressed puppet needs are kept, then deflated and base64-encoded.</summary>
public static class LookCodec
{
    private static readonly string[] Keep = ["FileVersion", "Equipment", "Customize", "Bonus"];

    public static string Encode(JObject state)
    {
        var slim = new JObject();
        foreach (var k in Keep)
            if (state[k] is { } v) slim[k] = v;
        var bytes = Encoding.UTF8.GetBytes(slim.ToString(Formatting.None));
        using var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(bytes, 0, bytes.Length);
        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>b49: deflate + base64 any JSON text (the mod list uses it too).</summary>
    public static string Pack(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        using var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(bytes, 0, bytes.Length);
        return Convert.ToBase64String(ms.ToArray());
    }

    public static string? Unpack(string? packed)
    {
        if (string.IsNullOrEmpty(packed)) return null;
        try
        {
            using var z = new DeflateStream(new MemoryStream(Convert.FromBase64String(packed)), CompressionMode.Decompress);
            using var r = new StreamReader(z, Encoding.UTF8);
            return r.ReadToEnd();
        }
        catch { return null; }
    }

    public static JObject? Decode(string? look)
    {
        if (string.IsNullOrEmpty(look)) return null;
        try
        {
            using var z = new DeflateStream(new MemoryStream(Convert.FromBase64String(look)), CompressionMode.Decompress);
            using var r = new StreamReader(z, Encoding.UTF8);
            return JObject.Parse(r.ReadToEnd());
        }
        catch { return null; }
    }
}

/// <summary>
/// b45: peers see a puppet the DM dressed in HOutfits. When Glamourer reports a state change on one of the DM's OWN
/// puppets while HOutfits is dressing that puppet (HOutfits.Target.Get == its index), wait ~0.5 s for the dressing to
/// settle, read the puppet's Glamourer state, encode it (<see cref="LookCodec"/>) and report it on the puppet's sync atom
/// (<see cref="HdmIpc.ReportLook"/>). The HOutfits gate keeps HDM's own disguise applies, which also fire StateChanged,
/// from being echoed as "looks". The DM's own body is never captured (Mare-style sync covers real players).
/// </summary>
public sealed class PuppetLookSync : IDisposable
{
    private readonly IFramework _framework;
    private readonly IObjectTable _objects;
    private readonly GlamourerIpc _glam;
    private readonly HdmIpc _ipc;
    private readonly HOutfitsIpc _outfits;
    private readonly IPluginLog _log;
    private readonly EventSubscriber<nint> _changed;
    private readonly Dictionary<int, long> _due = new();   // object index -> when to capture (ms tick)

    public PuppetLookSync(IDalamudPluginInterface pi, IFramework framework, IObjectTable objects, GlamourerIpc glam,
                          HdmIpc ipc, HOutfitsIpc outfits, IPluginLog log)
    {
        _framework = framework; _objects = objects; _glam = glam; _ipc = ipc; _outfits = outfits; _log = log;
        _changed = Glamourer.Api.IpcSubscribers.StateChanged.Subscriber(pi, OnStateChanged);
        _framework.Update += Tick;
    }

    private void OnStateChanged(nint address)
    {
        if (address == nint.Zero) return;
        var target = _outfits.CurrentTarget;
        if (target < 0) return;                                        // HOutfits dressing the DM, or not installed
        if (_objects[target] is not { } obj || obj.Address != address) return;
        if (_ipc.PuppetSlotFor(target) is null) return;                // only the DM's own puppets
        _due[target] = Environment.TickCount64 + 500;                  // debounce: a set fires one change per slot
    }

    private void Tick(IFramework _)
    {
        if (_due.Count == 0) return;
        var now = Environment.TickCount64;
        foreach (var (idx, at) in new List<KeyValuePair<int, long>>(_due))
        {
            if (now < at) continue;
            _due.Remove(idx);
            if (_ipc.PuppetSlotFor(idx) is not { } slot) continue;
            if (_glam.GetState(idx) is not { } state) continue;
            var look = LookCodec.Encode(state);
            _ipc.ReportLook(slot, look);
            _log.Information($"Sync: puppet obj#{idx} dressed; look {look.Length} chars sent with its atom");
        }
    }

    public void Dispose()
    {
        _framework.Update -= Tick;
        _changed.Dispose();
    }
}
