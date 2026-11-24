using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;

namespace HDM;

/// <summary>
/// b33 (experimental): play every NPC event pose on every adult human body.
///
/// The game ships many event poses only for some bodies (adjust cap: male Midlander + male Elezen; ground rest 3: female
/// Miqo'te only), and its own fallback to another body is inconsistent (lying suffering falls back for female Au Ra,
/// crying on the ground does not), so on many bodies a pose silently does nothing. Human skeletons share bone names, so
/// another body's file plays fine; animation mods have copied paps across races this way for years.
///
/// Why Penumbra (b31/b32 tried without it): timeline paps are NOT loaded through CharacterBase.ResolvePapPath. They go
/// through six "motion pack" loaders (LoadAlwaysResidentMotionPacks, LoadMotionPacks, ...) with GetResourceAsync
/// INLINED - no call to hook. Penumbra redirects them by patching those functions' machine code (its PapRewriter).
/// Doing the same ourselves means patching the very instructions Penumbra patches on every client: a real crash, and
/// it can't coexist with Penumbra. So HDM hands Penumbra game-path SWAPS (missing body path -> donor body path) as one
/// temporary mod applied to all collections. No files are written; it lives in memory and is removed on unload. Every
/// client running HDM registers the same swaps, so peers (HMS mirrors, puppets) see ported poses too.
///
/// HDM's second Penumbra use (the first: RedrawObject on a Human-guise revert, see PenumbraIpc), deliberate, as the
/// only route that reaches these loads.
/// </summary>
public sealed class UniversalPoses : IDisposable
{
    private const string Tag = "HDM.UniversalPoses";
    private const int Priority = -100;   // below any temporary mod a user or sync tool might add for the same path
    private static readonly int[] Bodies =
        [101, 201, 301, 401, 501, 601, 701, 801, 901, 1001, 1101, 1201, 1301, 1401, 1501, 1601, 1701, 1801];

    private readonly IFramework _framework;
    private readonly IDataManager _data;
    private readonly IPluginLog _log;
    private readonly Configuration _config;
    private readonly PoseLibrary _poses;
    private readonly AddTemporaryModAll _add;
    private readonly RemoveTemporaryModAll _remove;
    private readonly EventSubscriber _penumbraReady;
    private readonly Dictionary<int, int[]> _donorOrder = new();
    private readonly Dictionary<(int Body, string Key), int> _donorOf = new();

    private Dictionary<string, string>? _swaps;           // built once, in the background
    private bool _registered;

    public UniversalPoses(IDalamudPluginInterface pi, IFramework framework, IDataManager data, IPluginLog log,
                          Configuration config, PoseLibrary poses)
    {
        _framework = framework; _data = data; _log = log; _config = config; _poses = poses;
        _add = new AddTemporaryModAll(pi);
        _remove = new RemoveTemporaryModAll(pi);
        // Penumbra (re)loaded after us: its temporary mods start empty, so register again.
        _penumbraReady = Initialized.Subscriber(pi, () => _framework.RunOnFrameworkThread(() => { _registered = false; Apply(); }));
        foreach (var b in Bodies) _donorOrder[b] = BuildOrder(b);
        _poses.Universal = config.UniversalPoses;
        Task.Run(Build);
    }

    /// <summary>True while the swaps are registered with Penumbra.</summary>
    public bool Active => _registered;

    /// <summary>How many (body, pose) pairs are borrowed.</summary>
    public int Rewrites => _swaps?.Count ?? 0;

    /// <summary>Why it isn't active, for the Config line.</summary>
    public string Status { get; private set; } = "building the list";

    public void SetEnabled(bool on)
    {
        _config.UniversalPoses = on;
        Apply();
        _poses.Universal = on;
    }

    private void Build()
    {
        try
        {
            var swaps = new Dictionary<string, string>(StringComparer.Ordinal);
            var donors = new Dictionary<(int, string), int>();
            foreach (var key in _poses.EventKeys)
            {
                var have = Bodies.Where(b => Has(b, key)).ToHashSet();
                if (have.Count is 0 or 18) continue;
                foreach (var b in Bodies)
                {
                    if (have.Contains(b)) continue;
                    var donor = _donorOrder[b].FirstOrDefault(have.Contains);
                    if (donor == 0) continue;
                    swaps[PapPath(b, key)] = PapPath(donor, key);
                    donors[(b, key)] = donor;
                }
            }
            _framework.RunOnFrameworkThread(() =>
            {
                _swaps = swaps;
                foreach (var kv in donors) _donorOf[kv.Key] = kv.Value;
                _log.Information($"UniversalPoses: {swaps.Count} borrowed animations across {_poses.EventKeys.Count} event poses (same-build donors only).");
                Apply();
                _poses.Universal = _config.UniversalPoses;   // refresh the per-body lists now that donors are known
            });
        }
        catch (Exception e) { _log.Error(e, "UniversalPoses: building the swap list failed."); Status = "failed to build"; }
    }

    private void Apply()
    {
        if (_swaps == null) return;
        try
        {
            if (_config.UniversalPoses && !_registered)
            {
                var ec = _add.Invoke(Tag, _swaps, string.Empty, Priority);
                _registered = ec.ToString() == "Success";
                Status = _registered ? "" : $"Penumbra said {ec}";
                _log.Information($"UniversalPoses: registered {_swaps.Count} swaps with Penumbra -> {ec}");
            }
            else if (!_config.UniversalPoses && _registered)
            {
                _remove.Invoke(Tag, Priority);
                _registered = false;
            }
            if (!_config.UniversalPoses) Status = "off";
        }
        catch (Exception e)
        {
            _registered = false;
            Status = "Penumbra not available";
            _log.Warning($"UniversalPoses: Penumbra unavailable ({e.GetType().Name}); poses stay on their own bodies.");
        }
    }

    private static string PapPath(int body, string key) => $"chara/human/c{body:D4}/animation/a0001/bt_common/{key}.pap";
    private bool Has(int body, string key) => _data.FileExists(PapPath(body, key));

    /// <summary>The body this body borrows <paramref name="key"/> from (0 = it has its own, or nobody has it).</summary>
    public int DonorFor(int body, string key) => _donorOf.GetValueOrDefault((body, key));

    // b42 (DM): paps carry bone TRANSLATIONS, not just rotations, so a donor of another build squashes or stretches the
    // body (a Lalafell pose crushed a Midlander; Elezen and Hrothgar poses stretched smaller bodies). Borrow only within
    // a build group; a pose no compatible body ships is simply not offered.
    //   0 Lalafell · 1 Elezen · 2 Roegadyn + Hrothgar · 3 everyone else (Midlander, Highlander, Miqo'te, Au Ra, Viera)
    public static int BuildGroup(int body) => (body / 100) switch { 11 or 12 => 0, 5 or 6 => 1, 9 or 10 or 15 or 16 => 2, _ => 3 };

    // Donor preference within the group: the body's own skeleton fallback chain, then the same sex, then Midlander.
    private int[] BuildOrder(int body)
    {
        var group = BuildGroup(body);
        var chain = _poses.BodyChain(body).Where(b => b != body && BuildGroup(b) == group).ToList();
        bool Male(int b) => b / 100 % 2 == 1;
        var rest = Bodies.Where(b => b != body && !chain.Contains(b) && BuildGroup(b) == group)
            .OrderBy(b => Male(b) == Male(body) ? 0 : 1)
            .ThenBy(b => b == 101 || b == 201 ? 0 : 1);
        return chain.Where(Bodies.Contains).Concat(rest).ToArray();
    }

    /// <summary>"Midlander ♂" for 101, "Au Ra ♀" for 1401.</summary>
    public static string BodyName(int body)
    {
        string[] races = ["Midlander", "Highlander", "Elezen", "Miqo'te", "Roegadyn", "Lalafell", "Au Ra", "Hrothgar", "Viera"];
        var h = body / 100;
        var r = (h - 1) / 2;
        return r is >= 0 and < 9 ? $"{races[r]} {(h % 2 == 1 ? "♂" : "♀")}" : $"c{body:D4}";
    }

    /// <summary>The adult bodies that ship their own animation for <paramref name="key"/>.</summary>
    public IReadOnlyList<int> NativeBodies(string key) => Bodies.Where(b => Has(b, key)).ToList();

    public void Dispose()
    {
        _penumbraReady.Dispose();
        try { if (_registered) _remove.Invoke(Tag, Priority); } catch { /* Penumbra gone first */ }
    }
}
