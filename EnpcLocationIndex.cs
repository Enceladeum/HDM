using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace HDM;

/// <summary>
/// Home-zone labels for the humanoid Event NPCs (ENpcBase), built at runtime from the client's own
/// Level sheet — the "where they live" orientation aid the Catalog shows next to each NPC name. Mirrors
/// <see cref="LevelPlacementIndex"/>'s Type-9 (BNpcBase) id-join, but on <c>Level.Type == 8</c>, which
/// links an ENpcBase (verified against EXDSchema Level.yml: the Object switch, case 8 -> ENpcBase). For
/// each placed ENpcBase it collects every TerritoryType it appears in, resolves each to a readable zone
/// name through the same <see cref="ContentIndex"/> the location tiers use, and keeps a PRIMARY zone
/// (lowest TerritoryType id — the older base city/area an NPC usually calls home) plus a count of the
/// extra zones, so the label reads "New Gridania" or "New Gridania (+2)".
///
/// Why a side index, not a <see cref="MobRow"/> field: the Event rows are built (and appearance-deduped)
/// before this Level join runs, so denormalizing would couple construction order; a render-time lookup
/// keyed by the row's BaseId (== ENpcBase id, 1,000,000+) mirrors how the minion icon is fetched
/// (<c>CompanionIndex.TryGetIcon</c>) and keeps this fully self-contained.
///
/// Scope note that matches the DM's mental model: the Event catalog dedups by APPEARANCE, so an NPC that
/// looks identical across several zones collapses to ONE row (this shows that row's own placement), while
/// an NPC with a DISTINCT outfit per locale stays as SEPARATE rows — so each distinct look keeps its own
/// home zone, which is exactly the per-locale orientation the label is for.
/// </summary>
public sealed class EnpcLocationIndex
{
    private readonly Dictionary<uint, (string label, int extra)> _byBase;

    /// <summary>Number of Event NPCs given a home-zone label.</summary>
    public int Count => _byBase.Count;

    /// <summary>Home-zone label for an Event NPC by its ENpcBase id (the Event <see cref="MobRow.BaseId"/>).
    /// Returns the primary zone name and how many ADDITIONAL zones it is placed in (<paramref name="extra"/>
    /// == 0 means a single zone). False for a base with no readable Level Type-8 placement — leave the row
    /// unlabeled rather than guess.</summary>
    public bool TryGetLabel(uint baseId, out string label, out int extra)
    {
        if (_byBase.TryGetValue(baseId, out var v)) { label = v.label; extra = v.extra; return true; }
        label = ""; extra = 0; return false;
    }

    public EnpcLocationIndex(IDataManager data, ContentIndex content, IPluginLog log)
    {
        var byBase = new Dictionary<uint, (string label, int extra)>(4096);
        var multi = 0;
        try
        {
            // Pass 1: gather every distinct TerritoryType each ENpcBase is placed in (Level.Type == 8).
            var terrsByBase = new Dictionary<uint, List<uint>>(4096);
            foreach (var lvl in data.GetExcelSheet<Level>())
            {
                if (lvl.Type != 8) continue;                 // 8 = ENpc placement (EXDSchema: Object -> ENpcBase)
                var baseId = lvl.Object.RowId;               // the linked ENpcBase id (== Event MobRow.BaseId)
                var terr   = lvl.Territory.RowId;
                if (baseId == 0 || terr == 0) continue;
                if (!terrsByBase.TryGetValue(baseId, out var list))
                    terrsByBase[baseId] = list = new List<uint>(1);
                if (!list.Contains(terr)) list.Add(terr);
            }

            // Pass 2: pick a primary zone (lowest TerritoryType id among the placements that resolve to a
            // readable name) and count the rest, so the label reads "New Gridania" or "New Gridania (+2)".
            foreach (var (baseId, list) in terrsByBase)
            {
                var best = ""; uint bestId = 0; var named = 0;
                foreach (var t in list)
                {
                    if (!content.TryGet(t, out var ci) || ci.PlaceName.Length == 0) continue;
                    named++;
                    if (best.Length == 0 || t < bestId) { best = ci.PlaceName; bestId = t; }
                }
                if (best.Length == 0) continue;              // no placement resolved to a readable zone
                var extra = named - 1;
                byBase[baseId] = (best, extra);
                if (extra > 0) multi++;
            }
        }
        catch (Exception e)
        {
            log.Error(e, "EnpcLocationIndex: failed to read the Level sheet; NPC home-zone labels inactive.");
        }
        _byBase = byBase;
        log.Information($"EnpcLocationIndex: {_byBase.Count} Event NPCs given a home-zone label from Level Type-8 placements ({multi} placed in multiple zones).");
    }
}
