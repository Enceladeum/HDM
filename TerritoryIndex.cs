using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace HDM;

/// <summary>
/// Loads Data/mob-territory-index.csv — the <c>BaseId → TerritoryType</c> roster join
/// that powers the catalog's Location grouping (and, later, a "resident of" line in the
/// target inspector). Schema: <c>BaseId,TerritoryTypeId,Observations</c>, long form (one
/// row per observed (mob, territory) pair).
///
/// COVERAGE CAVEAT: this file is currently <b>overworld-only</b> (~1,629 of the ~15,211
/// renderable catalog bases; 45 territories; zero instanced content). That is the ceiling
/// of the offline Teamcraft crowd data — combat mobs are server-spawned, so dungeon/trial/
/// raid rosters (Chort &amp; co.) simply aren't in any static source. The instanced tier
/// comes from the runtime harvester (see <c>docs/runtime-mob-territory-harvester-spec.md</c>);
/// when that lands it merges into this same schema and the Location groups fill in for free.
/// </summary>
public sealed class TerritoryIndex
{
    // BaseId -> the TerritoryType it was observed in most (highest Observations wins), so a
    // mob that strays across several zones still groups under its main home rather than a
    // one-off stray sighting.
    private readonly Dictionary<uint, uint> _primary;

    // BaseId -> EVERY territory it was observed in, most-observed FIRST. The long-form CSV already
    // records one row per (mob, territory) pair, so a mob that roams/recurs across zones carries
    // several entries here; the single-home _primary above is just this list's head. Powers the
    // multi-location tree (a mob shown under every zone it lives in), where _primary drives the single
    // "home" used by the category chips and the target-identity line.
    private readonly Dictionary<uint, uint[]> _all;

    /// <summary>How many catalog bases carry a territory (the "located" count).</summary>
    public int Count => _primary.Count;

    /// <summary>Resolve a BNpcBase id to the TerritoryType it most-belongs-to. False when the
    /// mob has no roster entry (the ~89% instanced/uncovered tail, until the harvester runs).</summary>
    public bool TryGetPrimary(uint baseId, out uint territoryId) => _primary.TryGetValue(baseId, out territoryId);

    /// <summary>Every TerritoryType this base was observed in, most-observed first (so element 0 equals
    /// <see cref="TryGetPrimary"/>). The multi-location tree lists the base under each. False for the
    /// uncovered tail.</summary>
    public bool TryGetAll(uint baseId, out IReadOnlyList<uint> territories)
    {
        if (_all.TryGetValue(baseId, out var arr)) { territories = arr; return true; }
        territories = Array.Empty<uint>();
        return false;
    }

    public TerritoryIndex(IDalamudPluginInterface pi, IPluginLog log)
    {
        // Per base, accumulate observations for EVERY territory it was seen in, merging the
        // long-form CSV's one-row-per-(mob,territory) rows (so a mob that recurs in a zone across
        // several rows sums its weight there). Both outputs are then derived from this same
        // accumulator: _primary is element 0 after ranking by observations, _all is the whole
        // ranked list — so the single-home head can never disagree with the multi-location list.
        var acc = new Dictionary<uint, Dictionary<uint, int>>();
        var path = Path.Combine(pi.AssemblyLocation.DirectoryName!, "Data", "mob-territory-index.csv");
        try
        {
            using var r = new StreamReader(path, Encoding.UTF8);
            r.ReadLine(); // header
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                // Plain numeric CSV — no quoted fields — so a bare split is safe.
                var f = line.Split(',');
                if (f.Length < 2) continue;
                if (!uint.TryParse(f[0], out var baseId)) continue;
                if (!uint.TryParse(f[1], out var terr)) continue;
                var obs = f.Length > 2 && int.TryParse(f[2], out var o) ? o : 1;
                if (!acc.TryGetValue(baseId, out var byTerr))
                    acc[baseId] = byTerr = new Dictionary<uint, int>();
                byTerr[terr] = byTerr.TryGetValue(terr, out var prev) ? prev + obs : obs;
            }
            log.Information($"TerritoryIndex: {acc.Count} bases with a territory loaded from {path}");
        }
        catch (Exception e)
        {
            log.Error(e, $"TerritoryIndex: failed to load {path}");
        }

        var primary = new Dictionary<uint, uint>(acc.Count);
        var all = new Dictionary<uint, uint[]>(acc.Count);
        foreach (var kv in acc)
        {
            // Rank this base's territories by observation count, descending: element 0 is the
            // single "home" (== _primary), the rest follow in confidence order for the tree.
            var ranked = new List<KeyValuePair<uint, int>>(kv.Value);
            ranked.Sort((a, b) => b.Value.CompareTo(a.Value));
            var terrs = new uint[ranked.Count];
            for (int i = 0; i < ranked.Count; i++) terrs[i] = ranked[i].Key;
            primary[kv.Key] = terrs[0];
            all[kv.Key] = terrs;
        }
        _primary = primary;
        _all = all;
    }
}
