using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace HDM;

/// <summary>
/// Tier D location source: the game's own <c>Level</c> sheet, filtered to the STATIC battle-NPC
/// placements (<c>Level.Type == 9</c>), which bind a <c>BNpcBase</c> directly to the
/// <c>TerritoryType</c> it is placed in. Pure client-file, pure id-join — <c>Level.Object</c> is the
/// BNpcBase row id and <c>Level.Territory</c> is its home zone — so it needs no name string, no
/// crowdsource, and regenerates itself every patch (built at load from live sheets, nothing shipped).
///
/// WHY IT EXISTS / WHAT IT COMPLEMENTS. Overworld combat mobs are SERVER-spawned, so no static table
/// places the wandering ones — that is the gap <see cref="TerritoryIndex"/> (crowdsource) and the
/// name tiers fill. But a large population of battle NPCs ARE statically placed by the client:
/// quest targets, event/FATE fixtures, striking dummies, ambient/scripted spawns. Those live in
/// <c>Level</c> with <c>Type == 9</c> and are exactly the rows a name-keyed or sighting-keyed source
/// tends to miss. This tier is keyed on the BNpcBase id, so it is fully ORTHOGONAL to every
/// name-based tier and to the sighting crowdsource — it can place a base with a blank/duplicate name.
///
/// EMPIRICALLY CONFIRMED, not doc-trusted (Principle 2). Over the 2026-08 sheets, <c>Type == 9</c>
/// carries 6,534 rows whose <c>Object</c> lands in the small BNpcBase id space ([1..20058]); 4,476 of
/// them join a catalog base, and EVERY one has <c>Territory &gt; 0</c>. Spot-checks resolve correctly
/// (base 2 "restless raptor" → La Noscea / Shroud field zones). 1,530 distinct catalog bases covered,
/// only ~4% placed in more than one zone.
///
/// DISAMBIGUATION (the multi-zone ~4%). A statically-recurring mob (a common early beast, a striking
/// dummy) is placed in several zones. We keep every candidate and, at query time, prefer a territory
/// <see cref="ContentIndex"/> can categorize (drops engine limbo territories like id 1), then one
/// whose expansion matches the caller's hint, then the lowest id for determinism — mirroring the
/// MapMarker sub-area bridge in <see cref="WebLocIndex"/>. A returned-but-uncategorizable territory
/// still counts as "located" (the row leaves the Unknown tail) exactly as the other tiers behave.
///
/// PRIORITY. Inserted BELOW the crowdsource/lore/instanced tiers and ABOVE the web scrape: it never
/// overrides an existing placement (zero regression risk), but its deterministic client-file answer
/// is preferred over the community-wiki scrape. The startup coverage log measures how much it
/// corroborates the earlier tiers vs. rescues from the Unknown tail, so promoting it to authoritative
/// later is a decision backed by a number, not a guess.
/// </summary>
public sealed class LevelPlacementIndex
{
    // BNpcBase id -> distinct candidate home territories, each tagged with its expansion (255 = a
    // territory ContentIndex can't categorize) for the query-time tiebreak.
    private readonly Dictionary<uint, (uint terr, byte ex)[]> _byBase;

    /// <summary>Distinct BNpcBases carrying at least one static placement (startup log).</summary>
    public int Count => _byBase.Count;

    /// <summary>Resolve a BNpcBase to its static home <c>TerritoryType</c> from the <c>Level</c> sheet.
    /// When the base is placed in several zones, prefer a categorizable territory, then one matching
    /// <paramref name="expansionHint"/> (255 = none), then the lowest id. False for any base with no
    /// <c>Type == 9</c> placement (the server-spawned overworld tail, and instanced-only bases).</summary>
    public bool TryGetPrimary(uint baseId, byte expansionHint, out uint territoryId)
    {
        territoryId = 0;
        if (!_byBase.TryGetValue(baseId, out var cands) || cands.Length == 0) return false;

        uint bestAny = 0; var haveAny = false;   // lowest id overall (last resort)
        uint bestKnown = 0; var haveKnown = false; // lowest id among ContentIndex-categorizable
        uint bestHint = 0; var haveHint = false;   // lowest id among those matching the expansion hint
        foreach (var (terr, ex) in cands)
        {
            if (!haveAny || terr < bestAny) { bestAny = terr; haveAny = true; }
            if (ex == 255) continue; // uncategorizable territory — only a last-resort candidate
            if (!haveKnown || terr < bestKnown) { bestKnown = terr; haveKnown = true; }
            if (expansionHint != 255 && ex == expansionHint && (!haveHint || terr < bestHint))
            { bestHint = terr; haveHint = true; }
        }
        territoryId = haveHint ? bestHint : haveKnown ? bestKnown : bestAny;
        return haveAny;
    }

    /// <summary>Every distinct static home <c>TerritoryType</c> this base is placed in across the
    /// <c>Level</c> sheet — the multi-location tree lists the base under each (a striking dummy or a
    /// common early beast statically placed in several zones shows in all of them). Order is placement
    /// order; the caller categorizes and drops any engine-limbo territory <see cref="ContentIndex"/>
    /// can't name. False for any base with no <c>Type == 9</c> placement.</summary>
    public bool TryGetAll(uint baseId, out IReadOnlyList<uint> territories)
    {
        if (_byBase.TryGetValue(baseId, out var cands) && cands.Length > 0)
        {
            var arr = new uint[cands.Length];
            for (int i = 0; i < cands.Length; i++) arr[i] = cands[i].terr;
            territories = arr;
            return true;
        }
        territories = Array.Empty<uint>();
        return false;
    }

    public LevelPlacementIndex(IDataManager data, ContentIndex content, IPluginLog log)
    {
        var acc = new Dictionary<uint, List<(uint terr, byte ex)>>(2048);
        var rows = 0;
        try
        {
            foreach (var lvl in data.GetExcelSheet<Level>())
            {
                if (lvl.Type != 9) continue;                 // static BattleNpc placements only
                var baseId = lvl.Object.RowId;               // Object IS the BNpcBase id when Type == 9 (polymorphic RowRef -> raw row id)
                var terr = lvl.Territory.RowId;
                if (baseId == 0 || terr == 0) continue;
                rows++;
                var ex = content.TryGet(terr, out var ci) ? ci.Expansion : (byte)255;
                if (!acc.TryGetValue(baseId, out var list)) acc[baseId] = list = new List<(uint terr, byte ex)>(1);
                var dup = false;
                foreach (var e in list) if (e.terr == terr) { dup = true; break; }
                if (!dup) list.Add((terr, ex));
            }
        }
        catch (Exception e)
        {
            log.Error(e, "LevelPlacementIndex: failed to read Level sheet; Tier D inactive.");
        }

        var outp = new Dictionary<uint, (uint terr, byte ex)[]>(acc.Count);
        var multi = 0;
        foreach (var kv in acc)
        {
            outp[kv.Key] = kv.Value.ToArray();
            if (kv.Value.Count > 1) multi++;
        }
        _byBase = outp;
        log.Information($"LevelPlacementIndex: {outp.Count} bases with a static home from {rows} Level Type-9 placements ({multi} multi-zone, disambiguated at query time).");
    }
}
