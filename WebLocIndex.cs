using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace HDM;

/// <summary>
/// Tier C location source: a point-in-time harvest of GamerEscape mob pages, resolved to a
/// <c>TerritoryType</c> through the game's own <c>PlaceName</c> sheet at load time.
///
/// The offline scraper writes <c>Data/mob-webloc-index.csv</c> as <c>name,locations</c> where
/// <c>locations</c> is a ';'-joined list of the raw <c>Location</c> strings harvested from each
/// page's <c>{{ARR Mob Row}}</c> templates — NOT resolved to ids. Resolution happens HERE, against
/// live sheets, so a patch that renames a zone is picked up without re-scraping and the shipped CSV
/// stays simple and human-auditable.
///
/// Three resolution stages, precision-first (the product decision): a raw string resolves to a
/// TerritoryType via (1) the DUTY stage — an exact match against the game's own
/// <c>ContentFinderCondition.Name</c>, which encodes the instance VARIANT ("Haukke Manor (Hard)",
/// "the Aurum Vale") that the shared PlaceName drops; this is what keeps a normal dungeon and its
/// Hard/Savage/Advanced twin apart, since BOTH carry the same PlaceName and the zone stage alone
/// collapses them onto the lower TerritoryType id (usually the wrong one — normal Haukke Manor is
/// TerritoryType 1040, Hard is 350, so a bare "Haukke Manor" zone lookup mis-picks 350). Then (2) the
/// ZONE stage — an explicit "(Zone)" marker, a parenthetical instance qualifier, or an exact zone-name
/// match, catching instanced content GamerEscape files under a bare zone-level name; then, only if both
/// fail, (3) the SUB-AREA stage — the game's own MapMarker sub-area labels (<c>PlaceNameSubtext</c>)
/// joined through the owning <c>Map</c> (<c>MapMarkerRange</c> → <c>TerritoryType</c>) back to a
/// territory. Stage 3 is the "place them better" bridge: GamerEscape commonly records the finer
/// sub-region a mob roams ("Sanguine Perch" in Central Shroud) rather than the zone, and that string —
/// which the zone stage alone rejects — is exactly the table the game uses to paint the on-screen
/// region label, so it maps cleanly to the parent territory. (Supersedes the earlier design that
/// discarded sub-area strings on the assumption those mobs were already placed by Tier A/B — measured
/// false for ~2,450 renderable overworld bases whose ONLY location signal is the sub-area name.)
///
/// Web is the least-authoritative source (community wiki, keyed only by display name, and one name
/// can back several BNpcBase variants), so it sits LAST in the TryLocate chain and only ever fills
/// the Unknown tail — it never overrides an observed or sheet-derived zone. A sub-area label can recur
/// across zones ("Central Compound" in several castrums), so the sub-area stage disambiguates by the
/// caller's estimated expansion (falling back to the lowest TerritoryType id for determinism).
/// Duplicated here rather than shared with HMS, per the project's duplication-over-coupling directive.
/// </summary>
public sealed class WebLocIndex
{
    // lowercased mob name -> raw GamerEscape location strings (page order preserved).
    private readonly Dictionary<string, string[]> _byName;
    // lowercased zone place-name -> TerritoryType id (World-preferred, lowest-id tiebreak).
    private readonly Dictionary<string, uint> _zoneByName;
    // lowercased DUTY name (ContentFinderCondition.Name) -> TerritoryType id (lowest-id tiebreak). The
    // variant-precise layer over _zoneByName: SE encodes the instance VARIANT in the duty name ("Haukke
    // Manor (Hard)", "the Aurum Vale") that the shared PlaceName drops, so this map is what separates a
    // normal dungeon from its Hard/Savage/Advanced twin (both carry the SAME PlaceName, so the zone stage
    // alone collapses them onto the lowest id — usually the wrong one). GamerEscape records exactly these
    // duty strings, so matching them is high-confidence; verified to share no name with any World zone.
    private readonly Dictionary<string, uint> _dutyByName;
    // lowercased sub-area place-name -> candidate (territory, expansion) pairs. Built from the game's
    // MapMarker sub-area labels (PlaceNameSubtext) joined to the Map that owns each marker group
    // (Map.MapMarkerRange) -> Map.TerritoryType. The "place them better" bridge: turns the finer
    // sub-area strings GamerEscape records into their parent TerritoryType. Names that are ALSO a zone
    // name are omitted (the zone stage already owns them), keeping this stage strictly additive; names
    // that recur across zones keep ALL candidates for the expansion-hint tiebreak at resolve time.
    private readonly Dictionary<string, (uint terr, byte ex)[]> _subByName;

    // "Floor 77 (Heaven-on-High)" / "Halatali (Hard) (Zone)" — capture the trailing (...) group.
    private static readonly Regex ParenTail = new(@"^(.*?)\s*\(([^)]+)\)\s*$", RegexOptions.Compiled);

    /// <summary>Distinct mob names carrying at least one location string (startup log).</summary>
    public int NameCount => _byName.Count;
    /// <summary>Zone names known for resolution (startup log).</summary>
    public int ZoneCount => _zoneByName.Count;
    /// <summary>Duty names (ContentFinderCondition.Name) known for resolution — the variant-precise
    /// layer over the zone map that separates a dungeon from its Hard/Savage twin (startup log).</summary>
    public int DutyCount => _dutyByName.Count;
    /// <summary>Sub-area labels known for resolution — the MapMarker bridge (startup log).</summary>
    public int SubAreaCount => _subByName.Count;

    /// <summary>Resolve a catalog row's name to a TerritoryType via the harvested web locations.
    /// Tries each raw string in page order; the first that resolves (zone stage, then sub-area stage)
    /// wins. <paramref name="expansionHint"/> (an estimated ExVersion, 255 = none) disambiguates a
    /// sub-area label shared by several zones. False when the name isn't in the harvest or none of its
    /// locations resolve.</summary>
    public bool TryGetTerritory(string name, byte expansionHint, out uint territoryId)
        => TryGetTerritory(name, expansionHint, out territoryId, out _);

    /// <summary>As <see cref="TryGetTerritory(string,byte,out uint)"/>, additionally reporting whether the
    /// winning resolution came from the sub-area (MapMarker) stage — so the coverage diagnostic can
    /// isolate that stage's lift over the zone stage.</summary>
    public bool TryGetTerritory(string name, byte expansionHint, out uint territoryId, out bool viaSubArea)
    {
        territoryId = 0;
        viaSubArea = false;
        if (string.IsNullOrEmpty(name)) return false;
        if (!_byName.TryGetValue(name.Trim().ToLowerInvariant(), out var locs)) return false;
        foreach (var raw in locs)
            if (TryResolve(raw, expansionHint, out territoryId, out viaSubArea)) return true;
        return false;
    }

    /// <summary>Raw GamerEscape location string -> TerritoryType. Precision-ordered: DUTY stage first
    /// (variant-precise), then ZONE, then the additive SUB-AREA stage; each candidate form (whole, bare,
    /// parenthetical inner/outer) is tried duty-then-zone before falling through to sub-area, so the most
    /// specific match always wins where several could match.</summary>
    private bool TryResolve(string raw, byte expansionHint, out uint territoryId, out bool viaSubArea)
    {
        territoryId = 0;
        viaSubArea = false;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw.Trim();

        // Strip an explicit trailing GE "(Zone)" marker, then resolve the REMAINDER by the same logic as
        // any other string — do NOT return here. This is what lets "Copperbell Mines (Hard) (Zone)" reduce
        // to "Copperbell Mines (Hard)" and then match through the paren-outer path below: Hard/Extreme/
        // Savage variants share their base zone's PlaceName, so the fully-qualified string never matches a
        // zone directly. (Measured: this early-return-turned-fall-through recovers ~99 renderable rows —
        // the entire (Hard)(Zone) dungeon tail — with no hard-coded difficulty list.)
        if (s.EndsWith("(Zone)", StringComparison.OrdinalIgnoreCase))
            s = s.Substring(0, s.Length - "(Zone)".Length).Trim();

        // DUTY stage (highest precision), whole string FIRST — BEFORE the paren split below can strip a
        // "(Hard)"/"(Savage)"/"(Advanced)" qualifier and collapse the string onto the base zone. The duty
        // name is the only signal that separates same-PlaceName instance variants: "Haukke Manor (Hard)"
        // -> the Hard territory (350) instead of being reduced to "Haukke Manor" -> the normal one (1040);
        // "the Aurum Vale" -> its dungeon, which a bare zone lookup misses (the leading "the" the duty name
        // carries isn't in the zone PlaceName). Measured against the datamining sheets before shipping.
        if (TryDuty(s, out territoryId)) return true;

        // Parenthetical qualifier: try the inner group first, then the outer — DUTY then zone on each, then
        // sub-area on both. Inner-first serves the instance form ("Floor 77 (Heaven-on-High)" -> the
        // Heaven-on-High zone; "Ground Floor (Haukke Manor)" -> the Haukke Manor DUTY, i.e. normal 1040);
        // the outer match then catches the difficulty form ("Copperbell Mines (Hard)" -> base zone
        // "Copperbell Mines", same duty family / expansion / category — good enough to group).
        var m = ParenTail.Match(s);
        if (m.Success)
        {
            if (TryDuty(m.Groups[2].Value, out territoryId)) return true;
            if (TryZone(m.Groups[2].Value, out territoryId)) return true;
            if (TryDuty(m.Groups[1].Value, out territoryId)) return true;
            if (TryZone(m.Groups[1].Value, out territoryId)) return true;
            if (TrySubArea(m.Groups[2].Value, expansionHint, out territoryId, out viaSubArea)) return true;
            if (TrySubArea(m.Groups[1].Value, expansionHint, out territoryId, out viaSubArea)) return true;
        }

        // Bare string: exact zone-name match (catches trial/dungeon zone names like "The Whorleater"; the
        // duty name was already tried above), then the sub-area label match ("Sanguine Perch" -> Central Shroud).
        if (TryZone(s, out territoryId)) return true;
        return TrySubArea(s, expansionHint, out territoryId, out viaSubArea);
    }

    /// <summary>Sub-area label -> TerritoryType (the MapMarker bridge). When a label recurs across
    /// zones, prefer the candidate whose expansion matches <paramref name="expansionHint"/> (255 =
    /// none); if that doesn't single one out, fall back to the lowest TerritoryType id for determinism.
    /// Sets <paramref name="viaSubArea"/> on any hit.</summary>
    private bool TrySubArea(string name, byte expansionHint, out uint territoryId, out bool viaSubArea)
    {
        territoryId = 0;
        viaSubArea = false;
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (!_subByName.TryGetValue(name.Trim().ToLowerInvariant(), out var cands) || cands.Length == 0)
            return false;
        viaSubArea = true;
        if (cands.Length == 1) { territoryId = cands[0].terr; return true; }
        if (expansionHint != 255)
        {
            uint match = 0;
            var matches = 0;
            foreach (var c in cands)
                if (c.ex == expansionHint) { match = c.terr; matches++; }
            if (matches == 1) { territoryId = match; return true; }
        }
        var best = cands[0].terr;
        foreach (var c in cands)
            if (c.terr < best) best = c.terr;
        territoryId = best;
        return true;
    }

    private bool TryZone(string name, out uint territoryId)
    {
        territoryId = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;
        return _zoneByName.TryGetValue(name.Trim().ToLowerInvariant(), out territoryId);
    }

    /// <summary>Duty-name (ContentFinderCondition.Name) -> TerritoryType — the variant-precise stage.
    /// Distinct from <see cref="TryZone"/>: the duty name carries the "(Hard)"/"(Savage)"/"the …" the
    /// zone PlaceName drops, so it routes a same-PlaceName instance to its OWN territory.</summary>
    private bool TryDuty(string name, out uint territoryId)
    {
        territoryId = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;
        return _dutyByName.TryGetValue(name.Trim().ToLowerInvariant(), out territoryId);
    }

    public WebLocIndex(IDalamudPluginInterface pi, IDataManager data, ContentIndex content, IPluginLog log)
    {
        _byName = LoadCsv(pi, log);
        _zoneByName = BuildZoneMap(data, content, log);
        _dutyByName = BuildDutyMap(data, log);
        _subByName = BuildSubAreaMap(data, content, log); // after _zoneByName: the sub-area map reads it to stay additive
        log.Information($"WebLocIndex: {_byName.Count} names harvested, {_zoneByName.Count} zone names + {_dutyByName.Count} duty names + {_subByName.Count} sub-area labels for resolution.");
    }

    private static Dictionary<string, string[]> LoadCsv(IDalamudPluginInterface pi, IPluginLog log)
    {
        var map = new Dictionary<string, string[]>(4096, StringComparer.Ordinal);
        var path = Path.Combine(pi.AssemblyLocation.DirectoryName!, "Data", "mob-webloc-index.csv");
        if (!File.Exists(path))
        {
            // Absent until the offline scrape ships its CSV — Tier C simply stays dark, exactly
            // as the Location grouping behaved before this index existed.
            log.Information($"WebLocIndex: no web supplement at {path} (Tier C inactive).");
            return map;
        }
        try
        {
            using var r = new StreamReader(path, Encoding.UTF8);
            r.ReadLine(); // header
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                var f = SplitCsv(line);
                if (f.Count < 2) continue;
                var name = f[0].Trim().ToLowerInvariant();
                if (name.Length == 0) continue;
                var locs = f[1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (locs.Length == 0) continue;
                map[name] = locs;
            }
            log.Information($"WebLocIndex: {map.Count} rows loaded from {path}");
        }
        catch (Exception e)
        {
            log.Error(e, $"WebLocIndex: failed to load {path}; Tier C inactive.");
        }
        return map;
    }

    private static Dictionary<string, uint> BuildZoneMap(IDataManager data, ContentIndex content, IPluginLog log)
    {
        // zone place-name -> TerritoryType, mirroring MobLoreIndex's reverse map but keyed by the
        // NAME string (GamerEscape gives us a name, not a PlaceName id). A name can back several
        // territories (open-world zone + instanced re-uses), so prefer the open-world one and
        // tie-break on lowest row id for determinism.
        var map = new Dictionary<string, uint>(2048, StringComparer.Ordinal);
        try
        {
            foreach (var t in data.GetExcelSheet<TerritoryType>())
            {
                if (t.PlaceName.ValueNullable is not { } pn) continue;
                var name = pn.Name.ToString();
                if (string.IsNullOrEmpty(name)) continue;
                var key = name.ToLowerInvariant();
                var isWorld = content.TryGet(t.RowId, out var ci) && ci.Category == "World";
                if (!map.TryGetValue(key, out var cur))
                {
                    map[key] = t.RowId;
                }
                else
                {
                    var curIsWorld = content.TryGet(cur, out var cci) && cci.Category == "World";
                    if (isWorld && !curIsWorld) map[key] = t.RowId;
                    else if (isWorld == curIsWorld && t.RowId < cur) map[key] = t.RowId;
                }
            }
        }
        catch (Exception e)
        {
            log.Error(e, "WebLocIndex: failed to build zone map; Tier C inactive.");
        }
        return map;
    }

    /// <summary>Build the duty-name -> TerritoryType map (the variant-precise stage). Keyed by
    /// <c>ContentFinderCondition.Name</c> — which encodes the instance VARIANT ("Haukke Manor (Hard)",
    /// "the Aurum Vale") that the shared PlaceName drops — so a normal dungeon and its Hard/Savage/Advanced
    /// twin resolve to DIFFERENT territories instead of the zone stage collapsing both onto the lowest
    /// shared-PlaceName id (which for Haukke Manor is the Hard territory 350, not the normal 1040). A duty
    /// name is effectively unique and shares no name with any World zone (verified against the sheets), so
    /// this can lead the zone stage without hijacking overworld mobs; tie-break on lowest row id for
    /// determinism if a name ever recurs.</summary>
    private static Dictionary<string, uint> BuildDutyMap(IDataManager data, IPluginLog log)
    {
        var map = new Dictionary<string, uint>(1024, StringComparer.Ordinal);
        try
        {
            foreach (var t in data.GetExcelSheet<TerritoryType>())
            {
                if (t.ContentFinderCondition.ValueNullable is not { RowId: not 0 } cfc) continue;
                var name = cfc.Name.ToString();
                if (string.IsNullOrEmpty(name)) continue;
                var key = name.ToLowerInvariant();
                if (!map.TryGetValue(key, out var cur) || t.RowId < cur) map[key] = t.RowId;
            }
        }
        catch (Exception e)
        {
            log.Error(e, "WebLocIndex: failed to build duty map; duty stage inactive.");
        }
        return map;
    }

    /// <summary>Build the sub-area label -> candidate (territory, expansion) map — the MapMarker bridge
    /// (see class docstring). FFXIV paints the on-screen sub-region label ("Sanguine Perch") from the
    /// MapMarker table: each <c>Map</c> owns a marker GROUP (<c>Map.MapMarkerRange</c>) and each marker
    /// carries a <c>PlaceNameSubtext</c> (the label) plus a <c>DataType</c>. We invert that join — label
    /// -> the <c>TerritoryType</c> of the Map that shows it — to place mobs GamerEscape only tagged with
    /// the finer sub-area name. Only <c>DataType==0</c> markers are pure region labels; nonzero DataType
    /// is a map-link / aetheryte / instance entrance — NOT a roam region, so including it would mis-place.
    /// Non-static: reads <see cref="_zoneByName"/> to stay strictly additive (drop any label that is
    /// already an exact zone name — the zone stage owns those). A label shared by several zones keeps
    /// EVERY candidate so the resolve-time expansion-hint tiebreak has something to choose from.</summary>
    private Dictionary<string, (uint terr, byte ex)[]> BuildSubAreaMap(IDataManager data, ContentIndex content, IPluginLog log)
    {
        var acc = new Dictionary<string, List<(uint terr, byte ex)>>(2048, StringComparer.Ordinal);
        try
        {
            var markers = data.GetSubrowExcelSheet<MapMarker>();
            foreach (var map in data.GetExcelSheet<Map>())
            {
                var terr = map.TerritoryType.RowId;
                if (terr == 0 || map.MapMarkerRange == 0) continue;
                if (!markers.TryGetRow(map.MapMarkerRange, out var group)) continue;
                var ex = content.TryGet(terr, out var ci) ? ci.Expansion : (byte)255;
                foreach (var mk in group)
                {
                    if (mk.DataType != 0) continue; // pure region labels only
                    if (mk.PlaceNameSubtext.ValueNullable is not { } pn) continue;
                    var name = pn.Name.ToString();
                    if (string.IsNullOrEmpty(name)) continue;
                    var key = name.ToLowerInvariant();
                    if (_zoneByName.ContainsKey(key)) continue; // additive only: zone stage owns exact zone names
                    if (!acc.TryGetValue(key, out var list)) acc[key] = list = new List<(uint terr, byte ex)>(2);
                    var dup = false;
                    foreach (var e in list) if (e.terr == terr) { dup = true; break; }
                    if (!dup) list.Add((terr, ex));
                }
            }
        }
        catch (Exception e)
        {
            log.Error(e, "WebLocIndex: failed to build sub-area map; sub-area stage inactive.");
        }
        var outp = new Dictionary<string, (uint terr, byte ex)[]>(acc.Count, StringComparer.Ordinal);
        foreach (var kv in acc) outp[kv.Key] = kv.Value.ToArray();
        return outp;
    }

    /// <summary>Minimal quoted-field CSV split (quotes doubled inside quoted fields) — mirrors MobIndex.</summary>
    private static List<string> SplitCsv(string line)
    {
        var outp = new List<string>(2);
        var sb = new StringBuilder();
        var inQ = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQ)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQ = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') inQ = true;
            else if (c == ',') { outp.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        outp.Add(sb.ToString());
        return outp;
    }
}
