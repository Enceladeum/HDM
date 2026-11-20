using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace HDM;

/// <summary>Where a catalog row came from. <see cref="Battle"/> is the offline BNpcBase catalog
/// (Data/mob-model-index.csv) that makes up the bulk of the index; <see cref="Event"/> is a humanoid
/// ENpcBase Event NPC built at runtime from the game sheets (the same set Glamourer's NPC tab shows).
/// The two id spaces never collide — BNpcBase ids are &lt;20,000, ENpcBase ids are 1,000,000+ — so both
/// live in one <see cref="MobIndex.Rows"/> keyed by their raw id; this flag is the only thing that tells
/// them apart, and it drives (a) which sheet the customize/equip readers pull from and (b) the UI's
/// own "Event NPCs" catalog category + tree section.
/// <see cref="Mount"/> (b16) is a Mount-sheet creature, keyed 3,000,000 + Mount RowId: no BNpcBase/ENpcBase
/// behind it, so the NPC readers find nothing (no equipment/weapon/customize) and it renders as a bare
/// ModelChara swap - monster-model mounts only.</summary>
public enum NpcSource { Battle, Event, Mount }

/// <summary>One row of the mob catalog (one BNpcBase with a model).</summary>
public sealed record MobRow(
    uint BaseId,
    uint NameId,
    string Name,
    int ModelCharaId,
    int McType,      // 0=none 1=Human 2=DemiHuman 3=Monster
    int McModel,
    int McBase,
    int McVariant,
    float Scale,
    string RosterName = "")
{
    /// <summary>Provenance of this row (BNpcBase catalog vs. runtime ENpcBase Event NPC). Init-only so a
    /// row's origin is fixed at construction; the CSV loader never sets it, so every catalog row defaults
    /// to <see cref="NpcSource.Battle"/> and only <c>EventNpcIndex</c> stamps <see cref="NpcSource.Event"/>.
    /// Event rows are always McType 1 (Human): they render solely through the Glamourer path, are never
    /// scaled, and file into their own catalog category/tree section instead of the location tiers.</summary>
    public NpcSource Source { get; init; } = NpcSource.Battle;

    /// <summary>Catalog ROW identity (distinct from <see cref="BaseId"/>, the BODY identity). A BNpcBase is a
    /// shared body the server pairs with many BNpcNames (base 12 = northern vulture, arbor buzzard, chasm
    /// buzzard, ...), so the catalog carries one row per (base, name) pair. The primary row's key IS its
    /// BaseId - so every favourite/selection saved before pairs existed still resolves - and each alias row
    /// gets a stable synthetic key, <c>0x8000_0000 | NameId &lt;&lt; 15 | BaseId</c>, that survives a data
    /// regen and never collides with a BNpcBase (&lt;32,768) or an Event NPC (1,000,000+). Row-level state
    /// (favourites, selection, category/search caches) keys on this; anything about the body (spawn, guise,
    /// scale, minion, location evidence) keeps using BaseId.</summary>
    public uint RowKey { get => _rowKey != 0 ? _rowKey : BaseId; init => _rowKey = value; }
    private readonly uint _rowKey;

    /// <summary>An extra name for a body whose primary row is another row (see <see cref="RowKey"/>).</summary>
    public bool IsAlias { get; init; }

    /// <summary>The body carries more than one name (this row and at least one other share the BaseId). Its
    /// BaseId-keyed location evidence is then ambiguous - it can't say WHICH named mob was where - so the
    /// locator prefers this row's own name evidence first.</summary>
    public bool SharedBase { get; init; }

    /// <summary>Synthetic alias row key; 0 when the ids don't fit (caller then skips the alias).</summary>
    public static uint AliasKey(uint baseId, uint nameId)
        => baseId < (1u << 15) && nameId < (1u << 16) ? 0x8000_0000u | (nameId << 15) | baseId : 0;

    /// <summary>Playable-human customize triplet — populated ONLY on <see cref="NpcSource.Event"/> rows
    /// (0 on every Battle/minion row, which never render through the human path). <see cref="Race"/> 1-8
    /// (Hyur..Viera), <see cref="Clan"/> (Tribe) 1-16, <see cref="Gender"/> 0=male/1=female — the raw
    /// ENpcBase values <c>EventNpcIndex</c> already reads at its ValidHuman gate. Kept as bytes so the UI
    /// can GROUP the NPC catalog by Race→Clan and mark each row's gender cheaply, with no per-row sheet
    /// read (the readable names are denormalized alongside — see <see cref="RaceName"/>).</summary>
    public byte Race { get; init; }
    public byte Clan { get; init; }
    public byte Gender { get; init; }

    /// <summary>Localized Race / Clan display names for Event rows (e.g. "Hyur" / "Midlander"), interned
    /// once at index build from the Race/Tribe sheets so the tree can label a Race→Clan node without
    /// holding an IDataManager. Empty on non-Event rows.</summary>
    public string RaceName { get; init; } = "";
    public string ClanName { get; init; } = "";

    /// <summary>Skeleton code, e.g. "m0333" for Galatea Magna. Determines which
    /// animation set (chara/&lt;kind&gt;/XNNNN/animation/...) the model can play.</summary>
    public string SkeletonCode => McType switch
    {
        1 => $"c{McModel:D4}",
        2 => $"d{McModel:D4}",
        3 => $"m{McModel:D4}",
        _ => "",
    };

    /// <summary>Live localized nameplate captured by the runtime harvester (Tier A3) the moment the DM
    /// walks the content the base spawns in — mutable because it arrives AFTER load and is pushed in by
    /// MainWindow's live-name sync. It is the single most AUTHORITATIVE label there is (a first-hand
    /// in-game reading), so it outranks even the crowdsourced catalog <see cref="Name"/>: it both fills
    /// a catalog-blank base (the YoRHa androids) AND CORRECTS a mis-paired one (base 19218 reads its true
    /// "lone swordsman" instead of the mis-joined "North Shroud lemur"). Empty until first sighted.</summary>
    public string LiveName { get; set; } = "";

    /// <summary>Row label. Priority: the first-hand <see cref="LiveName"/> (harvested nameplate — most
    /// authoritative); else the crowdsourced catalog <see cref="Name"/>; else the heuristic
    /// <see cref="RosterName"/> backfilled from the instanced encounter roster (the BossMod name-feed —
    /// e.g. base 19519 shows "Chort" though the catalog left it blank); else skeleton + the unique
    /// BNpcBase id ("(unnamed) m0018 #31"), so every entry is still a distinct, searchable, clickable
    /// label instead of a wall of identical "(unnamed) m0018".</summary>
    public string DisplayName =>
        LiveName.Length > 0 ? LiveName
        : Name.Length > 0 ? Name
        : RosterName.Length > 0 ? RosterName
        : FamilyName.Length > 0 ? FamilyName + "*"
        : $"(unnamed) {SkeletonCode} #{BaseId}";

    /// <summary>b16: a name APPROXIMATED from the model, for a base with no name of its own (no live, catalog
    /// or roster name): the most common name among the other rows drawn with the same ModelChara (named
    /// mobs first, then event NPCs / mounts) — the "label by what else uses this model" idea from Limber's
    /// creature list. Shown with a trailing asterisk so it never passes for a real name; <see cref="FamilyHint"/>
    /// lists the model's other users for the tooltip. Set once at index build.</summary>
    public string FamilyName { get; set; } = "";
    public string FamilyHint { get; set; } = "";

    /// <summary>The shown name is the model-approximated <see cref="FamilyName"/> (asterisked).</summary>
    public bool NameIsInferred => LiveName.Length == 0 && Name.Length == 0 && RosterName.Length == 0 && FamilyName.Length > 0;

    /// <summary>Capitalise the first letter for display ("restless raptor" -> "Restless raptor"); BNpcName and
    /// most sheet names are stored lower-case. Applied once at load, never per frame.</summary>
    public static string Cap(string s) => s.Length > 0 && char.IsLower(s[0]) ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    /// <summary>True when the shown name is the heuristic roster backfill rather than a first-hand or
    /// catalog name — no live sighting, no crowdsourced <see cref="Name"/>, but an instanced encounter
    /// names it. Provenance flag: lets the UI mark inferred names and keeps the "unnamed" filters honest.
    /// A <see cref="LiveName"/> is a real observation, so it is NOT heuristic (it clears this flag).</summary>
    public bool NameIsHeuristic => LiveName.Length == 0 && Name.Length == 0 && RosterName.Length > 0;

    /// <summary>True when the row has NO usable name at all — no harvested <see cref="LiveName"/>, no
    /// crowdsourced catalog <see cref="Name"/>, and no roster-inferred <see cref="RosterName"/> — so
    /// <see cref="DisplayName"/> is the "(unnamed) &lt;skel&gt; #&lt;id&gt;" fallback. This is exactly what
    /// the catalog's "Hide unnamed" declutter toggle removes; a live- or heuristic-named row (e.g. Chort)
    /// is NOT unnamed by this test, since it shows a real name.</summary>
    public bool IsUnnamed => LiveName.Length == 0 && Name.Length == 0 && RosterName.Length == 0 && FamilyName.Length == 0;

    /// <summary>Model family from McType. Only Monster renders reliably from a bare
    /// ModelChara swap; Human/Demihuman need equipment + customize HDM won't apply.</summary>
    public string Kind => McType switch
    {
        1 => "Human",
        2 => "Demihuman",
        3 => "Monster",
        _ => "Other",
    };
}

/// <summary>
/// Loads Data/mob-model-index.csv (generated offline by
/// <c>xivtool bnpc index --names bnpc-pairs.json</c>).
/// 16,243 rows, 15,526 named. All data is client-side sheet content; only the
/// Base↔Name label pairing came from the crowdsourced (Teamcraft gubal) archive.
/// </summary>
public sealed class MobIndex
{
    public IReadOnlyList<MobRow> Rows { get; }
    private readonly Dictionary<uint, MobRow> _byBase;   // BaseId -> PRIMARY row
    private readonly Dictionary<uint, MobRow> _byKey;    // RowKey -> row (primary + aliases + Event NPCs)

    /// <summary>Look up a catalog row by its <see cref="MobRow.RowKey"/> (favourites store this).</summary>
    public bool TryGetByKey(uint rowKey, out MobRow row) => _byKey.TryGetValue(rowKey, out row!);

    /// <summary>The row for an exact (base, name) pair - what a live actor's nameplate is. Falls back to the
    /// body's primary row when the pair isn't catalogued (newer content, or a name the crowdsource never saw).</summary>
    public bool TryGetByPair(uint baseId, uint nameId, out MobRow row)
    {
        if (nameId != 0 && MobRow.AliasKey(baseId, nameId) is var k and not 0 && _byKey.TryGetValue(k, out row!))
            return true;
        if (_byBase.TryGetValue(baseId, out row!))
            return true;
        return false;
    }

    /// <summary>Look up a catalog row by its BNpcBase id — the same value a live actor
    /// exposes as <c>IGameObject.BaseId</c>. Searches the FULL index, including rows the
    /// UI's renderable/family filter hides, so the target inspector can identify anything
    /// (a hit that the table doesn't list is a filtered family; a miss is an Event NPC or
    /// content newer than this data drop).</summary>
    public bool TryGetByBase(uint baseId, out MobRow row) => _byBase.TryGetValue(baseId, out row!);

    /// <param name="rosterName">Optional heuristic name backfill: given a BNpcBase id, returns a
    /// display name when the base is unnamed in the catalog but an instanced encounter roster names
    /// it (the BossMod name-feed → BNpcName), else null. Invoked only for rows with a blank catalog
    /// Name, so a real catalog name always wins and provenance stays intact (see <see cref="MobRow.NameIsHeuristic"/>).</param>
    /// <param name="extraRows">Optional runtime-built rows to fold into the catalog after the CSV load —
    /// the humanoid Event NPC set (ENpcBase). They key by their own 1,000,000+ id, which never collides
    /// with a BNpcBase id, so they share <see cref="Rows"/> and <c>_byBase</c> with the offline catalog and
    /// every existing dictionary/Favorites/PushID path works on them unchanged. Each row carries
    /// <see cref="NpcSource.Event"/> so the UI and readers can tell them apart.</param>
    public MobIndex(IDalamudPluginInterface pi, IPluginLog log, Func<uint, string?>? rosterName = null,
        IEnumerable<MobRow>? extraRows = null)
    {
        var rows = new List<MobRow>(20000);
        var dataDir = Path.Combine(pi.AssemblyLocation.DirectoryName!, "Data");
        var path = Path.Combine(dataDir, "mob-model-index.csv");
        var pairs = LoadPairs(Path.Combine(dataDir, "mob-name-pairs.csv"), log);
        int filled = 0, renamed = 0, aliases = 0;
        try
        {
            using var r = new StreamReader(path, Encoding.UTF8);
            r.ReadLine(); // header
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                var f = SplitCsv(line);
                if (f.Count < 9) continue;
                var baseId = uint.Parse(f[0]);
                var nameId = f[1].Length == 0 ? 0u : uint.Parse(f[1]);
                var name = MobRow.Cap(f[2]);

                // Name pairs (Data/mob-name-pairs.csv): the evidence-chosen primary name replaces the old
                // one-name-per-base pairing (which mis-joined ~1.2k bases), and every other name the body
                // ships under becomes its own alias row below.
                List<(uint NameId, string Name, bool Primary)>? named = null;
                if (pairs.TryGetValue(baseId, out named))
                {
                    foreach (var p in named)
                        if (p.Primary)
                        {
                            if (!p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) renamed++;
                            nameId = p.NameId; name = MobRow.Cap(p.Name);
                            break;
                        }
                }
                var shared = named is { Count: > 1 };

                // Heuristic backfill: only for still-blank names, so it never overrides a
                // crowdsourced name — it only shrinks the unnamed tail.
                var roster = "";
                if (name.Length == 0 && rosterName?.Invoke(baseId) is { Length: > 0 } rn)
                {
                    roster = MobRow.Cap(rn);
                    filled++;
                }
                var primary = new MobRow(
                    baseId, nameId, name,
                    int.Parse(f[3]),
                    int.Parse(f[4]), int.Parse(f[5]), int.Parse(f[6]), int.Parse(f[7]),
                    float.Parse(f[8], System.Globalization.CultureInfo.InvariantCulture),
                    roster) { SharedBase = shared };
                rows.Add(primary);

                if (!shared) continue;
                foreach (var p in named!)
                {
                    if (p.Primary) continue;
                    var key = MobRow.AliasKey(baseId, p.NameId);
                    if (key == 0) continue;
                    rows.Add(primary with { NameId = p.NameId, Name = MobRow.Cap(p.Name), RosterName = "", RowKey = key, IsAlias = true });
                    aliases++;
                }
            }
            log.Information($"MobIndex: {rows.Count} rows from {path} — {renamed} relabelled and {aliases} alias rows " +
                            $"from the name pairs, {filled} blank names backfilled from the instanced roster.");
        }
        catch (Exception e)
        {
            log.Error(e, $"MobIndex: failed to load {path}");
        }

        // Fold in the runtime Event NPC rows (ENpcBase). Their 1,000,000+ ids never collide with a
        // BNpcBase id, so they share one Rows/_byBase with the offline catalog; DisplayName/search/tree
        // all then see a single unified list and the Source flag carries the provenance.
        if (extraRows != null)
        {
            var added = 0;
            foreach (var er in extraRows) { rows.Add(er); added++; }
            if (added > 0)
                log.Information($"MobIndex: +{added} runtime Event NPC rows appended (total {rows.Count}).");
        }
        LabelByModel(rows, log);
        Rows = rows;

        // BaseId -> the PRIMARY row (aliases share the BaseId, so first write — the primary, emitted before
        // its aliases — wins); RowKey -> every row.
        var byBase = new Dictionary<uint, MobRow>(rows.Count);
        var byKey = new Dictionary<uint, MobRow>(rows.Count);
        foreach (var r in rows) { byBase.TryAdd(r.BaseId, r); byKey.TryAdd(r.RowKey, r); }
        _byBase = byBase;
        _byKey = byKey;
    }

    /// <summary>b16: give every still-nameless battle row a model-approximated <see cref="MobRow.FamilyName"/>:
    /// the most common name among OTHER rows drawn with the same ModelChara — named battle rows first (a
    /// tonberry-model base becomes "Tonberry*"), then event NPCs / mounts on it. Ties break alphabetically so
    /// the label is stable across loads. The hint lists up to five of the model's other users.</summary>
    private static void LabelByModel(List<MobRow> rows, IPluginLog log)
    {
        var battle = new Dictionary<int, Dictionary<string, int>>();
        var other = new Dictionary<int, Dictionary<string, int>>();
        foreach (var r in rows)
        {
            if (r.Name.Length == 0 || r.ModelCharaId <= 0) continue;
            var map = r.Source == NpcSource.Battle ? battle : other;
            if (!map.TryGetValue(r.ModelCharaId, out var d)) map[r.ModelCharaId] = d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            d[r.Name] = d.GetValueOrDefault(r.Name) + 1;
        }
        var labelled = 0;
        foreach (var r in rows)
        {
            if (r.Source != NpcSource.Battle || !r.IsUnnamed || r.ModelCharaId <= 0) continue;
            if (!battle.TryGetValue(r.ModelCharaId, out var d) && !other.TryGetValue(r.ModelCharaId, out d)) continue;
            var ranked = d.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                          .Select(kv => kv.Key).ToList();
            r.FamilyName = ranked[0];
            r.FamilyHint = string.Join(", ", ranked.Take(5)) + (ranked.Count > 5 ? $" +{ranked.Count - 5} more" : "");
            labelled++;
        }
        log.Information($"MobIndex: {labelled} nameless rows labelled by model (asterisked).");
    }

    /// <summary>Loads Data/mob-name-pairs.csv (<c>BaseId,NameId,Name,Primary,Source</c>), generated by
    /// Data/gen-mob-name-pairs.py from the Penumbra.GameData pair table + the BossMod roster. Missing file =
    /// empty map = the old one-name-per-base behaviour.</summary>
    private static Dictionary<uint, List<(uint NameId, string Name, bool Primary)>> LoadPairs(string path, IPluginLog log)
    {
        var map = new Dictionary<uint, List<(uint, string, bool)>>(17000);
        try
        {
            using var r = new StreamReader(path, Encoding.UTF8);
            r.ReadLine();
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                var f = SplitCsv(line);
                if (f.Count < 4 || !uint.TryParse(f[0], out var b) || f[2].Length == 0) continue;
                uint.TryParse(f[1], out var n);
                if (!map.TryGetValue(b, out var list)) map[b] = list = new List<(uint, string, bool)>(2);
                list.Add((n, f[2], f[3] == "1"));
            }
            log.Information($"MobIndex: name pairs for {map.Count} bases from {path}");
        }
        catch (Exception e)
        {
            log.Warning($"MobIndex: no name pairs ({e.Message}); falling back to one name per base.");
        }
        return map;
    }

    /// <summary>Minimal quoted-field CSV split (quotes doubled inside quoted fields).</summary>
    private static List<string> SplitCsv(string line)
    {
        var outp = new List<string>(9);
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
