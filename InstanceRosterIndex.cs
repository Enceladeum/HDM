using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace HDM;

/// <summary>
/// Loads Data/mob-territory-instanced.csv — the <c>BaseId → TerritoryType</c> binding for
/// INSTANCED content (dungeon/trial/raid bosses and their adds), which the crowdsource roster
/// (<see cref="TerritoryIndex"/>) and the name-keyed sheet/web tiers structurally miss: combat
/// mobs are server-spawned, so no static world table places an instanced boss, and an unnamed
/// or proxy-model boss can't be reached by a name lookup either.
///
/// SOURCE: harvested offline from the community BossMod encounter modules — each module's
/// <c>OID</c> enum is the encounter's BNpcBase roster, and <c>ModuleInfo(GroupType=CFC, GroupID)</c>
/// joins to <c>ContentFinderCondition.TerritoryType</c>. 514 CFC encounters → ~1,300 base→territory
/// rows over 164 territories. This is why "Anima" (base 13309) resolves to TerritoryType 969
/// (Tower of Babil) with no runtime observation. Schema: <c>BaseId,TerritoryId,NameId,Label,SourceModule</c>.
///
/// AMBIGUITY RULE: a handful of BNpcBases are generic mechanic/helper actors reused across many
/// instances (tethers, falling rocks, flying carpets). Any base that appears under more than one
/// TerritoryType is dropped — placing it anywhere would be a guess, and these aren't mobs a user
/// would disguise as. Everything kept maps to exactly one home instance.
/// </summary>
public sealed class InstanceRosterIndex
{
    private readonly Dictionary<uint, uint> _territory; // base -> its single home TerritoryType
    private readonly Dictionary<uint, uint> _name;       // base -> primary-boss BNpcName (0 = none)

    /// <summary>How many instanced bases carry an unambiguous home territory.</summary>
    public int Count => _territory.Count;

    /// <summary>Resolve a BNpcBase to its instanced home TerritoryType. False for anything not in
    /// a harvested encounter roster (overworld mobs, and the runtime-only tail like base 13843).</summary>
    public bool TryGetPrimary(uint baseId, out uint territoryId) => _territory.TryGetValue(baseId, out territoryId);

    /// <summary>Resolve a BNpcBase to the primary-boss BNpcName the encounter binds, when present.
    /// Only the module's headline boss carries a name; adds/mechanics return false. (Wired for a
    /// future catalog name-feed; not consumed yet.)</summary>
    public bool TryGetName(uint baseId, out uint nameId)
    {
        if (_name.TryGetValue(baseId, out nameId) && nameId != 0) return true;
        nameId = 0;
        return false;
    }

    public InstanceRosterIndex(IDalamudPluginInterface pi, IPluginLog log)
    {
        // Pass 1: gather every (base -> territory) sighting and the best name per base.
        var seen = new Dictionary<uint, HashSet<uint>>();
        var names = new Dictionary<uint, uint>();
        var path = Path.Combine(pi.AssemblyLocation.DirectoryName!, "Data", "mob-territory-instanced.csv");
        try
        {
            using var r = new StreamReader(path, Encoding.UTF8);
            r.ReadLine(); // header
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                // Label and SourceModule carry no commas, so a bare split is safe.
                var f = line.Split(',');
                if (f.Length < 2) continue;
                if (!uint.TryParse(f[0], out var baseId)) continue;
                if (!uint.TryParse(f[1], out var terr)) continue;
                (seen.TryGetValue(baseId, out var set) ? set : seen[baseId] = new HashSet<uint>()).Add(terr);
                if (f.Length > 2 && uint.TryParse(f[2], out var nid) && nid != 0 && !names.ContainsKey(baseId))
                    names[baseId] = nid;
            }
        }
        catch (Exception e)
        {
            log.Error(e, $"InstanceRosterIndex: failed to load {path}");
        }

        // Pass 2: keep only unambiguous bases (exactly one home territory); drop reused mechanics.
        var territory = new Dictionary<uint, uint>(seen.Count);
        int dropped = 0;
        foreach (var kv in seen)
        {
            if (kv.Value.Count == 1)
            {
                foreach (var t in kv.Value) territory[kv.Key] = t;
            }
            else dropped++;
        }
        _territory = territory;
        _name = names;
        log.Information($"InstanceRosterIndex: {territory.Count} instanced bases loaded ({dropped} ambiguous dropped) from {path}");
    }
}
