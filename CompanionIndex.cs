using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace HDM;

/// <summary>
/// Loads Data/mob-minion-index.csv — the set of catalog BNpcBase rows that ARE summonable
/// minions/companions, carrying the minion's name. Schema: <c>BaseId,MinionName</c> (one header
/// line; MinionName is free text and may contain commas, so only the FIRST comma splits;
/// <c>#</c>-lead lines are comments).
///
/// Why it exists: a large slice of the "Unknown location" tail isn't unplaceable overworld/duty
/// mobs — it's minions. A minion's "home" is a REWARD SOURCE (a duty drop, a Gold Saucer prize, a
/// MogStation purchase), never a TerritoryType, so no location tier can ever reach one; left alone
/// they sit in the Unknown pile forever (513 of them in the 2026-08 drop). This index pulls them
/// out into their own "Minions &amp; summons" class — a catalog category chip + a tree section — and
/// supplies the provenance note ("Minion: okuri chochin") the identity inspector shows. All are
/// McType 3 (Monster/m skeleton), so they render through the plain ModelCharaId swap like any mob.
///
/// How it's generated (offline, per patch — same pattern as the other Data catalogs): intersect the
/// catalog's ModelChara ids with the Companion sheet's Model column, then keep ONLY rows whose
/// BNpcName equals the Companion Singular. That name match is the safety gate: many real characters
/// reuse a minion's ModelChara (Wuk Lamat, Erenville, Ark Angel EV, giant beaver, Matanga prince…),
/// and only the true minion's base also carries the minion's own name — so requiring name==Singular
/// tags the minion and never the look-alike mob that merely shares its model.
/// </summary>
public sealed class CompanionIndex
{
    private readonly Dictionary<uint, string> _minions;
    // BNpcBase id -> Companion-sheet portrait icon, joined by name at load. Only real minions are keyed
    // (the join is gated by _minions membership), so a look-alike mob sharing a minion's ModelChara never
    // picks up an icon. Empty when the Companion sheet fails to load — TryGetIcon then just returns false.
    private readonly Dictionary<uint, uint> _iconByBase;

    /// <summary>How many catalog bases are tagged as summonable minions.</summary>
    public int Count => _minions.Count;

    /// <summary>True when this base is a summonable minion (fast membership test, no out param).</summary>
    public bool IsMinion(uint baseId) => _minions.ContainsKey(baseId);

    /// <summary>Resolve a base to its minion name. False (and empty name) when the base isn't a minion.</summary>
    public bool TryGetMinion(uint baseId, out string name)
    {
        if (_minions.TryGetValue(baseId, out var n) && n.Length > 0) { name = n; return true; }
        name = "";
        return false;
    }

    /// <summary>Resolve a minion base to its Companion-sheet portrait icon id (for a tiny row thumbnail).
    /// False (icon 0) when the base isn't a minion or the sheet didn't yield an icon for it.</summary>
    public bool TryGetIcon(uint baseId, out uint icon)
        => _iconByBase.TryGetValue(baseId, out icon) && icon > 0;

    public CompanionIndex(IDalamudPluginInterface pi, IDataManager data, IPluginLog log)
    {
        var minions = new Dictionary<uint, string>();
        var path = Path.Combine(pi.AssemblyLocation.DirectoryName!, "Data", "mob-minion-index.csv");
        try
        {
            using var r = new StreamReader(path, Encoding.UTF8);
            r.ReadLine(); // header
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length == 0 || line[0] == '#') continue; // blank or comment
                // Split on the first comma only, so a MinionName may itself contain commas.
                var f = line.Split(',', 2);
                if (f.Length < 2) continue;
                if (!uint.TryParse(f[0].Trim(), out var baseId)) continue;
                minions[baseId] = f[1].Trim();
            }
            log.Information($"CompanionIndex: {minions.Count} minion tags loaded from {path}");
        }
        catch (Exception e)
        {
            log.Error(e, $"CompanionIndex: failed to load {path}");
        }

        _minions = minions;

        // Join each tagged minion to its Companion-sheet portrait icon by NAME. The offline index kept only
        // bases whose BNpcName equals the Companion Singular (its safety gate), so MinionName == Singular
        // here and a name match is exact; keying the result by BNpcBase id (not by model) means the
        // look-alike mobs that merely reuse a minion's ModelChara never inherit the icon.
        var iconByBase = new Dictionary<uint, uint>(minions.Count);
        try
        {
            var iconBySingular = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in data.GetExcelSheet<Companion>())
            {
                var singular = c.Singular.ExtractText().Trim();
                if (singular.Length > 0) iconBySingular[singular] = (uint)c.Icon; // Singular is effectively unique; last wins
            }
            foreach (var (baseId, name) in minions)
                if (iconBySingular.TryGetValue(name, out var icon) && icon > 0)
                    iconByBase[baseId] = icon;
            log.Information($"CompanionIndex: {iconByBase.Count}/{minions.Count} minion icons resolved from the Companion sheet.");
        }
        catch (Exception e)
        {
            log.Error(e, "CompanionIndex: failed to resolve minion icons");
        }

        _iconByBase = iconByBase;
    }
}
