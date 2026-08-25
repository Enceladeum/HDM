using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace HDM;

/// <summary>One territory resolved to its Duty-Finder classification + expansion.</summary>
public sealed record ContentInfo(
    uint TerritoryId,
    string Category,     // the chip: City, World, Dungeon, Trial, Raid, Deep Dungeon, …
    string PlaceName,    // "Lapis Manalis" (the map name)
    string Region,       // region place-name ("Coerthas", "Tural", …)
    string DutyName,     // ContentFinderCondition.Name; "" when open-world
    string ContentType,  // ContentType.Name ("Dungeons"); "" when open-world
    byte Expansion,      // ExVersion row id (0=ARR … 5=DT)
    int Level,           // ContentFinderCondition.ClassJobLevelRequired; 0 when not a duty
    int SortKey,         // CFC SortKey (Duty-Finder order) for stable per-expansion duty sorting
    bool IsDuty)         // true = instanced content (has a ContentFinderCondition)
{
    public string ExShort => Expansion < ContentIndex.ExShort.Length ? ContentIndex.ExShort[Expansion] : $"Ex{Expansion}";
    public string ExLong  => Expansion < ContentIndex.ExLong.Length  ? ContentIndex.ExLong[Expansion]  : $"Expansion {Expansion}";

    /// <summary>One-line human summary for the inspector, e.g.
    /// "[Dungeon · DT] Lapis Manalis (Lv90)" or "[World · HW] Coerthas Western Highlands".</summary>
    public string Summary
    {
        get
        {
            var name = DutyName.Length > 0 ? DutyName : PlaceName;
            var lvl = Level > 0 ? $" (Lv{Level})" : "";
            return $"[{Category} · {ExShort}] {name}{lvl}";
        }
    }
}

/// <summary>
/// In-plugin Lumina reader that classifies every TerritoryType into its Duty-Finder
/// category (World / City / Dungeon / Trial / Raid / …), expansion, and — for instanced
/// content — the duty name + level. This is the content-structure half of the catalog's
/// category navigation, and it is fully client-side: no crowdsource dependency.
///
/// Why it exists here (not shared with HMS): HDM deliberately duplicates rather than
/// couples (same directive as HumanGuise/NpcData). The classification table is ported
/// verbatim from HMS's proven <c>CategoryFor</c>/expansion mapping so HDM's chips match
/// exactly what a player sees in the in-game Duty Finder.
///
/// What it deliberately does NOT do: map a catalog mob to the duty it spawns in. Combat
/// mobs are server-spawned (there are zero BattleNpc placements in the static LGB data), so
/// the BNpcBase→TerritoryType roster can only come from crowdsource telemetry — that table
/// is a separate Fable deliverable (see shared-RnD/hdm-mob-content-map). Once it lands,
/// the category UI joins it against this index; until then this index still powers the live
/// "you are in [Dungeon] The Clyteum" context (the player's current territory is always known).
/// </summary>
public sealed class ContentIndex
{
    // Expansion index -> label, by ExVersion row id. Extend when a new expansion ships (an
    // unknown id degrades gracefully to "Ex{n}" rather than throwing).
    internal static readonly string[] ExShort = { "ARR", "HW", "SB", "ShB", "EW", "DT" };
    internal static readonly string[] ExLong =
        { "A Realm Reborn", "Heavensward", "Stormblood", "Shadowbringers", "Endwalker", "Dawntrail" };

    private readonly Dictionary<uint, ContentInfo> _byTerritory;

    /// <summary>Every classified territory, keyed by TerritoryType row id (the live
    /// <c>IClientState.TerritoryType</c> value). Used by the future category UI to enumerate
    /// duties per chip/expansion.</summary>
    public IReadOnlyDictionary<uint, ContentInfo> ByTerritory => _byTerritory;

    public ContentIndex(IDataManager data, IPluginLog log)
    {
        var map = new Dictionary<uint, ContentInfo>();
        try
        {
            var sheet = data.GetExcelSheet<TerritoryType>();
            foreach (var row in sheet)
            {
                if (row.RowId == 0) continue;
                var place  = row.PlaceName.ValueNullable?.Name.ToString() ?? "";
                var region = row.PlaceNameRegion.ValueNullable?.Name.ToString() ?? "";
                var use    = row.TerritoryIntendedUse.RowId;
                var ex     = (byte)row.ExVersion.RowId;

                string dutyName = "", ctype = "";
                int level = 0, sort = 0;
                var isDuty = false;
                var cfc = row.ContentFinderCondition.ValueNullable;
                if (cfc is { RowId: not 0 } c)
                {
                    isDuty   = true;
                    dutyName = c.Name.ToString();
                    ctype    = c.ContentType.ValueNullable?.Name.ToString() ?? "";
                    level    = c.ClassJobLevelRequired;
                    sort     = c.SortKey;
                }

                // No usable identity (unnamed placeholder/dev territory): skip.
                if (place.Length == 0 && dutyName.Length == 0) continue;

                map[row.RowId] = new ContentInfo(
                    row.RowId, CategoryFor(use, ctype), place, region,
                    dutyName, ctype, ex, level, sort, isDuty);
            }
            log.Information($"ContentIndex: {map.Count} territories classified.");
        }
        catch (Exception e)
        {
            log.Error(e, "ContentIndex: failed to build from TerritoryType sheet.");
        }
        _byTerritory = map;
    }

    /// <summary>Resolve a TerritoryType id (e.g. <c>IClientState.TerritoryType</c>) to its
    /// content classification. False for id 0 or an unclassified/unnamed territory.</summary>
    public bool TryGet(uint territoryId, out ContentInfo info) => _byTerritory.TryGetValue(territoryId, out info!);

    // Two-key classifier: a duty's ContentType (the Duty-Finder classifier) wins; otherwise
    // TerritoryIntendedUse. Ported verbatim from HMS's proven CategoryFor. English
    // ContentType.Name is plural ("Dungeons"/"Trials"/"Raids"); IntendedUse values are the
    // game's own territory-use enum (0=town … see cases). Anything unrecognised → "Other".
    private static string CategoryFor(uint use, string contentType) => contentType switch
    {
        "Dungeons" => "Dungeon",
        "Trials" => "Trial",
        "Raids" or "Ultimate Raids" or "Chaotic Alliance Raid" => "Raid",
        "Deep Dungeons" => "Deep Dungeon",
        "PvP" => "PvP",
        "V&C Dungeon Finder" => "Variant & Criterion",
        "Gold Saucer" => "Gold Saucer",
        _ => use switch
        {
            0 => "City",
            1 => "World",
            2 => "Inn",
            13 or 14 => "Housing",
            5 => "Housing",                 // Mordion Gaol
            15 or 54 => "Solo Instances",
            29 => "Solo Duty",
            3 => "Dungeon",
            4 or 57 or 58 => "Variant & Criterion",
            7 or 10 => "Trial",
            8 or 16 or 17 or 36 => "Raid",
            31 => "Deep Dungeon",
            _ => "Other",
        },
    };
}
