using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace HDM;

/// <summary>
/// Tier B location source: a fully client-side, patch-proof <c>BNpcName/BNpcBase →
/// TerritoryType</c> map recovered from the game's own gameplay sheets. It fills the
/// Location grouping's "Unknown" tail beyond the Tier A crowdsource roster
/// (<see cref="TerritoryIndex"/>) without any external data — the game literally tells us
/// where a bounded set of mobs live, in two sheets:
///
///   • Hunting Log — <c>MonsterNoteTarget</c> rows carry <c>BNpcName → PlaceNameZone</c>
///     (a PlaceName id). We reverse <c>TerritoryType.PlaceName</c> to turn that zone name
///     back into an overworld TerritoryType. (~400 base names.)
///
///   • Hunt marks — <c>TerritoryType.NotoriousMonsterTerritory</c> points at a
///     <c>NotoriousMonsterTerritory</c> row whose <c>NotoriousMonsters[]</c> list resolves
///     (via <c>NotoriousMonster</c>) to <c>BNpcName</c> + <c>BNpcBase</c>. Here the
///     TerritoryType is the OWNER of the mark list, so the territory is exact — no
///     PlaceName round-trip. (~200 names, and these come with a real BNpcBase too.)
///
/// Both resolve to a TerritoryType id that <see cref="ContentIndex"/> already classifies,
/// so the recovered rows slot straight into the same Location sections as crowdsource.
///
/// Rank/elemental variants ("tempered sylvan sough") get their OWN BNpcName id, distinct
/// from the base ("sylvan sough"), so an exact id match misses them. A leading-word
/// name-stem fallback (<see cref="TryGetByNameStem"/>) drops up to two prefix words and
/// re-probes the located-name set, inheriting the base mob's zone. This is a best-effort
/// display convenience — it only affects which section a row is filed under, never the
/// model that gets applied — so the occasional generous match is acceptable.
///
/// Coverage ceiling is the union of those two sheets (~1,000 base names, ~7% of the
/// renderable catalog); the instanced/uncatalogued remainder still awaits the runtime
/// harvester. Duplicated here rather than shared with HMS, per the project's
/// duplication-over-coupling directive.
/// </summary>
public sealed class MobLoreIndex
{
    // BNpcName id -> TerritoryType. The primary exact lookup (Hunting Log + hunt marks).
    private readonly Dictionary<uint, uint> _byName;
    // BNpcBase id -> TerritoryType. Exact-base hits, available only from the hunt-mark
    // sheet (which names a BNpcBase directly); lets a rank variant that shares a base id
    // resolve even when its NAME id differs.
    private readonly Dictionary<uint, uint> _byBase;
    // lowercased BNpcName singular -> TerritoryType, over the same located set, for the
    // stem fallback. Keyed by string because the variant we're rescuing has a different id.
    private readonly Dictionary<string, uint> _byNameString;

    /// <summary>Located BNpcName ids (exact-name coverage), for the startup log line.</summary>
    public int NameCount => _byName.Count;
    /// <summary>Located BNpcBase ids (exact-base coverage, hunt marks), for the startup log line.</summary>
    public int BaseCount => _byBase.Count;

    /// <summary>Exact BNpcName → territory. The catalog row's <c>NameId</c> is a BNpcName id,
    /// so this is a direct join for any mob the Hunting Log or a hunt mark names.</summary>
    public bool TryGetByName(uint nameId, out uint territoryId)
    {
        if (nameId != 0) return _byName.TryGetValue(nameId, out territoryId);
        territoryId = 0;
        return false;
    }

    /// <summary>Exact BNpcBase → territory (hunt marks only). The catalog's <c>BaseId</c> is a
    /// BNpcBase id, so this catches mark variants whose base is known even if the name id isn't.</summary>
    public bool TryGetByBase(uint baseId, out uint territoryId)
    {
        if (baseId != 0) return _byBase.TryGetValue(baseId, out territoryId);
        territoryId = 0;
        return false;
    }

    /// <summary>Stem fallback: try the whole name, then drop one and two leading words, probing
    /// the located-name set each time. Recovers rank/elemental variants ("tempered sylvan sough"
    /// → "sylvan sough") that carry a distinct BNpcName id. First hit wins.</summary>
    public bool TryGetByNameStem(string name, out uint territoryId)
    {
        territoryId = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;
        var words = name.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;
        var maxDrop = Math.Min(2, words.Length - 1);
        for (var drop = 0; drop <= maxDrop; drop++)
        {
            var stem = string.Join(' ', words, drop, words.Length - drop);
            if (_byNameString.TryGetValue(stem, out territoryId)) return true;
        }
        return false;
    }

    public MobLoreIndex(IDataManager data, ContentIndex content, IPluginLog log)
    {
        var byName       = new Dictionary<uint, uint>(1024);
        var byBase       = new Dictionary<uint, uint>(512);
        var byNameString = new Dictionary<string, uint>(1024, StringComparer.Ordinal);

        try
        {
            var territories = data.GetExcelSheet<TerritoryType>();

            // Reverse PlaceName → TerritoryType, so the Hunting Log's zone place-name can be
            // turned back into a live TerritoryType. A PlaceName can back several territories
            // (the overworld zone plus instanced re-uses), so prefer the open-world one
            // (ContentIndex Category "World"); tie-break on lowest row id for determinism.
            var placeToTerr = new Dictionary<uint, uint>();
            foreach (var t in territories)
            {
                var placeId = t.PlaceName.RowId;
                if (placeId == 0) continue;
                var isWorld = content.TryGet(t.RowId, out var ci) && ci.Category == "World";
                if (!placeToTerr.TryGetValue(placeId, out var cur))
                {
                    placeToTerr[placeId] = t.RowId;
                }
                else
                {
                    var curIsWorld = content.TryGet(cur, out var cci) && cci.Category == "World";
                    if (isWorld && !curIsWorld) placeToTerr[placeId] = t.RowId;
                    else if (isWorld == curIsWorld && t.RowId < cur) placeToTerr[placeId] = t.RowId;
                }
            }

            // Hunting Log: BNpcName → PlaceNameZone[i] → (reverse) → TerritoryType. The zone can
            // sit in ANY of the three PlaceNameZone slots (≈29% of rows leave [0] empty and carry
            // it in [1]/[2] — that's how "sylvan sough" hides in slot [1]), so take the first
            // non-zero slot rather than only [0].
            var huntingLogHits = 0;
            var monsterNotes = data.GetExcelSheet<MonsterNoteTarget>();
            foreach (var m in monsterNotes)
            {
                var nameId = m.BNpcName.RowId;
                if (nameId == 0) continue;
                uint placeId = 0;
                for (var i = 0; i < m.PlaceNameZone.Count; i++)
                {
                    var pid = m.PlaceNameZone[i].RowId;
                    if (pid != 0) { placeId = pid; break; }
                }
                if (placeId == 0 || !placeToTerr.TryGetValue(placeId, out var terr)) continue;
                if (byName.TryAdd(nameId, terr)) huntingLogHits++;
                var s = m.BNpcName.ValueNullable?.Singular.ToString();
                if (!string.IsNullOrEmpty(s)) byNameString.TryAdd(s.ToLowerInvariant(), terr);
            }

            // Hunt marks: TerritoryType owns a NotoriousMonsterTerritory whose list resolves to
            // BNpcName + BNpcBase. The owning territory IS the location — exact, no reverse map.
            var markNameHits = 0;
            foreach (var t in territories)
            {
                if (t.NotoriousMonsterTerritory.RowId == 0) continue;
                if (t.NotoriousMonsterTerritory.ValueNullable is not { } nmt) continue;
                foreach (var nmRef in nmt.NotoriousMonsters)
                {
                    if (nmRef.RowId == 0 || nmRef.ValueNullable is not { } nm) continue;
                    var baseId = nm.BNpcBase.RowId;
                    var nameId = nm.BNpcName.RowId;
                    if (baseId != 0) byBase.TryAdd(baseId, t.RowId);
                    if (nameId != 0 && byName.TryAdd(nameId, t.RowId)) markNameHits++;
                    if (nameId != 0)
                    {
                        var s = nm.BNpcName.ValueNullable?.Singular.ToString();
                        if (!string.IsNullOrEmpty(s)) byNameString.TryAdd(s.ToLowerInvariant(), t.RowId);
                    }
                }
            }

            log.Information(
                $"MobLoreIndex: {byName.Count} names + {byBase.Count} bases located from game sheets " +
                $"(Hunting Log {huntingLogHits}, hunt marks {markNameHits} names).");
        }
        catch (Exception e)
        {
            // Lumina/sheet failure must not take the plugin down — Location just falls back to
            // Tier A + Unknown, exactly as before this index existed.
            log.Error(e, "MobLoreIndex: failed to build from game sheets; location falls back to crowdsource only.");
        }

        _byName = byName;
        _byBase = byBase;
        _byNameString = byNameString;
    }
}
