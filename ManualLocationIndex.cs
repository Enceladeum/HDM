using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace HDM;

/// <summary>
/// Loads Data/mob-location-manual.csv — a small CURATED <c>BaseId → TerritoryType</c> table
/// for content that no automated location tier can reach. Schema:
/// <c>BaseId,TerritoryTypeId,Note</c> (Note is free text and may contain commas; <c>#</c>-lead
/// lines are comments).
///
/// Why it exists: the automated tiers (crowdsource, hunt-mark sheets, the instanced BossMod
/// roster, the runtime harvester, Level Type 9, the web scrape) all resolve mobs that either
/// spawn in the overworld or fight in a duty. Cutscene-script PROPS are neither — they have
/// <c>ModelChara=0</c> (identity is BNpcCustomize + NpcEquip on a default body) and are spawned
/// by an <c>ArrayEventHandler</c> event script rather than placed in any static or roster table,
/// so every tier structurally misses them. The white YoRHa androids the DM wants are the
/// canonical case. This index is <see cref="TryLocate"/>'s highest-priority tier ("Tier M"):
/// a hand-verified tag always wins over an estimated/inferred zone.
///
/// It also carries the human-readable <see cref="TryGetNote"/> string so the target inspector
/// can explain WHY a prop lives where it does ("YoRHa: Dark Apocalypse cutscene prop") — the
/// bit of provenance that a bare TerritoryType id can't convey.
/// </summary>
public sealed class ManualLocationIndex
{
    private readonly Dictionary<uint, uint> _primary;
    private readonly Dictionary<uint, string> _notes;

    /// <summary>How many bases carry a curated tag.</summary>
    public int Count => _primary.Count;

    /// <summary>Resolve a BNpcBase id to its curated home TerritoryType. False when untagged.</summary>
    public bool TryGetPrimary(uint baseId, out uint territoryId) => _primary.TryGetValue(baseId, out territoryId);

    /// <summary>The curated provenance note for a tagged base (empty string when none/untagged).</summary>
    public bool TryGetNote(uint baseId, out string note)
    {
        if (_notes.TryGetValue(baseId, out var n) && n.Length > 0) { note = n; return true; }
        note = "";
        return false;
    }

    public ManualLocationIndex(IDalamudPluginInterface pi, IPluginLog log)
    {
        var primary = new Dictionary<uint, uint>();
        var notes = new Dictionary<uint, string>();
        var path = Path.Combine(pi.AssemblyLocation.DirectoryName!, "Data", "mob-location-manual.csv");
        try
        {
            using var r = new StreamReader(path, Encoding.UTF8);
            r.ReadLine(); // header
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length == 0 || line[0] == '#') continue; // blank or comment
                // Split on the first two commas only, so a Note field may itself contain commas.
                var f = line.Split(',', 3);
                if (f.Length < 2) continue;
                if (!uint.TryParse(f[0].Trim(), out var baseId)) continue;
                if (!uint.TryParse(f[1].Trim(), out var terr)) continue;
                primary[baseId] = terr;
                if (f.Length > 2) notes[baseId] = f[2].Trim();
            }
            log.Information($"ManualLocationIndex: {primary.Count} curated location tags loaded from {path}");
        }
        catch (Exception e)
        {
            log.Error(e, $"ManualLocationIndex: failed to load {path}");
        }

        _primary = primary;
        _notes = notes;
    }
}
