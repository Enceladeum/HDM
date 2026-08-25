using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace HDM;

/// <summary>
/// Builds the humanoid Event NPC (ENpcBase) catalog rows at runtime from the game sheets — the same set
/// Glamourer's "NPCs" tab shows. HDM already carries the BNpc humanoids (in the offline CSV); this
/// fills the gap with the named Event NPCs (Mother Miounne, the Scions, quest characters, generic
/// townsfolk…) so they can be applied to self AND spawned as clone-puppets, all from one catalog.
///
/// Why runtime, not an offline CSV: the set derives purely from client sheets (ENpcBase + ENpcResident +
/// ModelChara), so building it live means it always matches the installed game version with no per-patch
/// data step — and every kept row is a Human (McType 1) that renders through the existing Glamourer path,
/// so no new render code is needed.
///
/// Filter + dedup mirror Glamourer's NpcCustomizeSet.CreateEnpcData verbatim (the reference the user
/// pointed at): keep a row iff (a) ModelChara.Type == 1, (b) its ENpcResident name is non-empty, and
/// (c) its customize is a valid playable human (race/clan/gender in range); then deduplicate by
/// name + appearance so the many identical "delivery moogle"/"attendant" rows collapse to one while
/// genuinely different looks that share a name are all kept.
///
/// Ids: an ENpcBase RowId is 1,000,000+, which never collides with a BNpcBase id (&lt;20,000), so these
/// rows drop straight into <see cref="MobIndex.Rows"/> keyed by their raw id (via MobIndex's extraRows).
/// Each row carries <see cref="NpcSource.Event"/> so the readers pull ENpcBase columns and the UI files
/// them under their own "Event NPCs" catalog category + tree section.
/// </summary>
public sealed class EventNpcIndex
{
    /// <summary>The built Event NPC rows, ready to fold into the catalog. Empty if the sheets fail to load.</summary>
    public IReadOnlyList<MobRow> Rows { get; }

    public EventNpcIndex(IDataManager data, NpcData npc, IPluginLog log)
    {
        var rows = new List<MobRow>(4096);
        try
        {
            var enpc     = data.GetExcelSheet<ENpcBase>();
            var resident = data.GetExcelSheet<ENpcResident>();
            // Race/Clan display-name lookups, built once (8 races, 16 clans) and denormalized onto every
            // kept row so the catalog tree can group + label by Race→Clan with no live sheet handle. Column
            // 0 (Masculine) is the group label: for English it equals Feminine, and the node names a
            // CATEGORY ("Hyur — Midlander"), not a single NPC, so the masculine form reads correctly.
            var raceName = new Dictionary<uint, string>();
            foreach (var rr in data.GetExcelSheet<Race>()) raceName[rr.RowId] = rr.Masculine.ExtractText();
            var clanName = new Dictionary<uint, string>();
            foreach (var tr in data.GetExcelSheet<Tribe>()) clanName[tr.RowId] = tr.Masculine.ExtractText();
            // "name|appearance" signatures already emitted, so exact duplicates collapse (Glamourer dedup).
            var seen = new HashSet<string>();
            var kept = 0;
            var dupes = 0;

            foreach (var e in enpc)
            {
                // (a) must be a human model (the only kind the Glamourer path can paint).
                if (e.ModelChara.ValueNullable is not { } mc || mc.Type != 1)
                    continue;

                // (b) must have a non-blank resident name (row id == ENpcBase id).
                var name = (resident.GetRowOrDefault(e.RowId)?.Singular.ToString() ?? "").Trim();
                if (name.Length == 0)
                    continue;

                // (c) must be a valid playable human — race/clan/gender in range. Approximates Glamourer's
                // CustomizeManager.Races/Clans/Genders membership test without needing those exact sets.
                // Capture the triplet here (the ONLY place ENpcBase exposes it) so it rides onto the row
                // for the catalog's Race→Clan grouping + per-row gender marker, instead of being discarded.
                var raceId   = (uint)e.Race.RowId;
                var tribeId  = (uint)e.Tribe.RowId;
                var genderId = (uint)e.Gender;
                if (!ValidHuman(raceId, genderId, tribeId))
                    continue;

                // Dedup by name + appearance. Reuse the Event readers so the signature is EXACTLY what the
                // guise will paint — no drift between the dedup key and the applied look.
                if (!seen.Add(AppearanceSignature(name, (int)mc.RowId, npc, e.RowId)))
                {
                    dupes++;
                    continue;
                }

                rows.Add(new MobRow(
                    BaseId:       e.RowId,
                    NameId:       0,
                    Name:         name,
                    ModelCharaId: (int)mc.RowId,
                    McType:       (int)mc.Type,
                    McModel:      (int)mc.Model,
                    McBase:       (int)mc.Base,
                    McVariant:    (int)mc.Variant,
                    Scale:        1.0f)
                {
                    Source   = NpcSource.Event,
                    Race     = (byte)raceId,
                    Clan     = (byte)tribeId,
                    Gender   = (byte)genderId,
                    RaceName = raceName.GetValueOrDefault(raceId, ""),
                    ClanName = clanName.GetValueOrDefault(tribeId, ""),
                });
                kept++;
            }

            log.Information($"EventNpcIndex: {kept} humanoid Event NPCs built ({dupes} appearance-duplicates collapsed).");
        }
        catch (Exception ex)
        {
            log.Error(ex, "EventNpcIndex: failed to build Event NPC rows");
        }
        Rows = rows;
    }

    // Race 1-8 (Hyur..Viera), Gender 0/1, Clan/Tribe 1-16 — the playable-human ranges Glamourer's
    // CustomizeManager sets enumerate. A row outside these is a non-human placeholder we skip.
    private static bool ValidHuman(uint race, uint gender, uint tribe)
        => race is >= 1 and <= 8 && gender <= 1 && tribe is >= 1 and <= 16;

    // name + model + the 26 customize bytes + 10 equip slots, read exactly as the guise will read them
    // (so two rows collapse only when they truly paint the same body). Startup-only, so the extra sheet
    // reads are immaterial.
    private static string AppearanceSignature(string name, int modelCharaId, NpcData npc, uint id)
    {
        var sb = new StringBuilder(name).Append('|').Append(modelCharaId);
        if (npc.TryGetCustomize(id, NpcSource.Event) is { } c)
        {
            sb.Append('|');
            foreach (var b in c) sb.Append(b).Append('.');
        }
        if (npc.TryGetEquipment(id, NpcSource.Event) is { } eq)
        {
            sb.Append('|');
            foreach (var s in eq)
                sb.Append(s.Id).Append(',').Append(s.Variant).Append(',').Append(s.Stain0).Append(',').Append(s.Stain1).Append(';');
        }
        return sb.ToString();
    }
}
