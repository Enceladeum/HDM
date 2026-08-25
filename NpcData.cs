using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using EquipmentModelId = FFXIVClientStructs.FFXIV.Client.Game.Character.EquipmentModelId;

namespace HDM;

/// <summary>
/// Live Lumina reads for NPC appearance data the catalog CSV doesn't carry,
/// keyed by BNpcBase row id (the catalog's <c>BaseId</c>).
///
/// Why this exists: a Demihuman (McType 2, d-skeleton) renders <b>blank</b> from a
/// bare ModelChara swap — the skeleton alone has no visible mesh; the body + armor
/// come from the NPC's equipment set. The game populates a demihuman NPC's
/// draw-data equipment slots from its <c>BNpcBase.NpcEquip</c> reference, so to
/// reproduce the NPC we read that same set and write it into
/// <c>DrawData.EquipmentModelIds</c> alongside the model swap (see GuiseService).
///
/// The slot order and the model/variant/dye decode are taken verbatim from
/// HOutfits' verified <c>NpcService.ReadEquipFromNpcEquip</c> (packed model value:
/// low 16 = model id, next 8 = variant; dyes are separate Stain rows). Order
/// matches <c>EquipmentSlot</c> / <c>DrawData.EquipmentModelIds</c>:
/// Head, Body, Hands, Legs, Feet, Ears, Neck, Wrists, RFinger, LFinger.
/// </summary>
public sealed class NpcData
{
    private readonly IDataManager _data;
    private readonly IPluginLog _log;

    public NpcData(IDataManager data, IPluginLog log)
    {
        _data = data;
        _log = log;
    }

    /// <summary>
    /// The 10 equipment model ids for a catalog row's NPC gear, or null if the row
    /// (or its equipment source) can't be resolved — in which case a demihuman body
    /// may be invisible and the caller should fall back to a bare swap. Weapons are
    /// NOT here (they use a 3-part model this 2-part struct can't hold, and are a
    /// separate draw object); the Human path reads them via <see cref="TryGetWeapons(uint, NpcSource)"/>.
    ///
    /// Source-less overload = <see cref="NpcSource.Battle"/>: keeps the existing call
    /// sites (GuiseService's demihuman path) reading BNpcBase.NpcEquip unchanged.
    /// </summary>
    public EquipmentModelId[]? TryGetEquipment(uint baseId) => TryGetEquipment(baseId, NpcSource.Battle);

    /// <summary>
    /// Source-aware equipment read. <see cref="NpcSource.Battle"/> reads BNpcBase.NpcEquip;
    /// <see cref="NpcSource.Event"/> reads an ENpcBase — preferring its linked NpcEquip set
    /// only when the row's own body+legs columns are both empty, else the inline columns
    /// (Glamourer's NpcCustomizeSet precedence). Slot order and decode are identical for
    /// all three, so the same <see cref="Slot"/> helper serves every path.
    /// </summary>
    public EquipmentModelId[]? TryGetEquipment(uint baseId, NpcSource source)
    {
        if (source == NpcSource.Event)
            return TryGetEventEquipment(baseId);

        var sheet = _data.GetExcelSheet<BNpcBase>();
        if (sheet.GetRowOrDefault(baseId) is not { } bnpc)
        {
            _log.Warning($"NpcData: BNpcBase {baseId} not found.");
            return null;
        }
        if (bnpc.NpcEquip.ValueNullable is not { } e)
            return null; // no NpcEquip reference on this base
        return FromNpcEquip(e);
    }

    /// <summary>Event NPC (ENpcBase) equipment. Precedence verbatim from Glamourer's
    /// NpcCustomizeSet.CreateEnpcData: an ENpcBase carries its appearance BOTH inline and
    /// (optionally) via an NpcEquip reference; prefer the reference ONLY when it is set and
    /// the row's OWN body+legs columns are both empty, otherwise the inline columns win.</summary>
    private EquipmentModelId[]? TryGetEventEquipment(uint baseId)
    {
        var sheet = _data.GetExcelSheet<ENpcBase>();
        if (sheet.GetRowOrDefault(baseId) is not { } e)
        {
            _log.Warning($"NpcData: ENpcBase {baseId} not found.");
            return null;
        }
        if (e.NpcEquip.RowId != 0 && e.NpcEquip.ValueNullable is { } ne && e.ModelBody == 0 && e.ModelLegs == 0)
            return FromNpcEquip(ne);
        return FromEnpcInline(e);
    }

    // The 10 gear slots from a NpcEquip row (BNpc path, and the ENpc-with-reference path).
    private static EquipmentModelId[] FromNpcEquip(NpcEquip e) =>
    [
        Slot(e.ModelHead,      e.DyeHead.RowId,      e.Dye2Head.RowId),
        Slot(e.ModelBody,      e.DyeBody.RowId,      e.Dye2Body.RowId),
        Slot(e.ModelHands,     e.DyeHands.RowId,     e.Dye2Hands.RowId),
        Slot(e.ModelLegs,      e.DyeLegs.RowId,      e.Dye2Legs.RowId),
        Slot(e.ModelFeet,      e.DyeFeet.RowId,      e.Dye2Feet.RowId),
        Slot(e.ModelEars,      e.DyeEars.RowId,      e.Dye2Ears.RowId),
        Slot(e.ModelNeck,      e.DyeNeck.RowId,      e.Dye2Neck.RowId),
        Slot(e.ModelWrists,    e.DyeWrists.RowId,    e.Dye2Wrists.RowId),
        Slot(e.ModelRightRing, e.DyeRightRing.RowId, e.Dye2RightRing.RowId),
        Slot(e.ModelLeftRing,  e.DyeLeftRing.RowId,  e.Dye2LeftRing.RowId),
    ];

    // The 10 gear slots inline on an ENpcBase row (identical column names + packed encoding
    // to NpcEquip, so the same Slot decode applies).
    private static EquipmentModelId[] FromEnpcInline(ENpcBase e) =>
    [
        Slot(e.ModelHead,      e.DyeHead.RowId,      e.Dye2Head.RowId),
        Slot(e.ModelBody,      e.DyeBody.RowId,      e.Dye2Body.RowId),
        Slot(e.ModelHands,     e.DyeHands.RowId,     e.Dye2Hands.RowId),
        Slot(e.ModelLegs,      e.DyeLegs.RowId,      e.Dye2Legs.RowId),
        Slot(e.ModelFeet,      e.DyeFeet.RowId,      e.Dye2Feet.RowId),
        Slot(e.ModelEars,      e.DyeEars.RowId,      e.Dye2Ears.RowId),
        Slot(e.ModelNeck,      e.DyeNeck.RowId,      e.Dye2Neck.RowId),
        Slot(e.ModelWrists,    e.DyeWrists.RowId,    e.Dye2Wrists.RowId),
        Slot(e.ModelRightRing, e.DyeRightRing.RowId, e.Dye2RightRing.RowId),
        Slot(e.ModelLeftRing,  e.DyeLeftRing.RowId,  e.Dye2LeftRing.RowId),
    ];

    private static EquipmentModelId Slot(uint modelValue, uint dye, uint dye2) => new()
    {
        Id      = (ushort)(modelValue & 0xFFFF),
        Variant = (byte)((modelValue >> 16) & 0xFF),
        Stain0  = (byte)dye,
        Stain1  = (byte)dye2,
    };

    // --- Weapons (#79) ---------------------------------------------------------

    /// <summary>
    /// One NPC weapon's three-part model (Set / Type / Variant) plus its two dyes. Unlike armor
    /// (a 2-part model+variant packed in 32 bits), a weapon packs a THIRD component — the secondary
    /// "Type"/"Base" model — into a 64-bit column, so it can't reuse <see cref="EquipmentModelId"/>
    /// (which has no secondary field). The Human guise writes these into Glamourer's MainHand/OffHand
    /// slots (see HumanGuise.WriteWeapon).
    /// </summary>
    public readonly record struct NpcWeapon(ushort Set, ushort Type, byte Variant, byte Dye, byte Dye2);

    /// <summary>
    /// The NPC's main-hand and off-hand weapon models (either may be null when the slot is empty).
    /// Every NPC carries a weapon — the game forbids weaponless characters — so a Human guise that
    /// stops at armor leaves the puppet holding the DM's own weapon, breaking the disguise. Weapons
    /// come from the SAME source (NpcEquip row for Battle, ENpcBase inline/reference for Event) the
    /// equipment does, chosen by the identical precedence, so the weapon always matches the body.
    ///
    /// Source-less overload = <see cref="NpcSource.Battle"/>, matching TryGetEquipment.
    /// </summary>
    public (NpcWeapon? Main, NpcWeapon? Off) TryGetWeapons(uint baseId) => TryGetWeapons(baseId, NpcSource.Battle);

    /// <summary>Source-aware weapon read; see <see cref="TryGetWeapons(uint)"/>.</summary>
    public (NpcWeapon? Main, NpcWeapon? Off) TryGetWeapons(uint baseId, NpcSource source)
    {
        if (source == NpcSource.Event)
            return TryGetEventWeapons(baseId);

        var sheet = _data.GetExcelSheet<BNpcBase>();
        if (sheet.GetRowOrDefault(baseId) is not { } bnpc)
        {
            _log.Warning($"NpcData: BNpcBase {baseId} not found (weapons).");
            return (null, null);
        }
        if (bnpc.NpcEquip.ValueNullable is not { } e)
            return (null, null); // no NpcEquip reference on this base
        return WeaponsFromNpcEquip(e);
    }

    // Event NPC weapons, mirroring TryGetEventEquipment's precedence EXACTLY so the weapon source
    // matches the body source: prefer the NpcEquip reference only when it is set and the row's own
    // body+legs are both empty, otherwise the ENpcBase inline columns.
    private (NpcWeapon? Main, NpcWeapon? Off) TryGetEventWeapons(uint baseId)
    {
        var sheet = _data.GetExcelSheet<ENpcBase>();
        if (sheet.GetRowOrDefault(baseId) is not { } e)
        {
            _log.Warning($"NpcData: ENpcBase {baseId} not found (weapons).");
            return (null, null);
        }
        if (e.NpcEquip.RowId != 0 && e.NpcEquip.ValueNullable is { } ne && e.ModelBody == 0 && e.ModelLegs == 0)
            return WeaponsFromNpcEquip(ne);
        return (Weapon(e.ModelMainHand, e.DyeMainHand.RowId, e.Dye2MainHand.RowId),
                Weapon(e.ModelOffHand,  e.DyeOffHand.RowId,  e.Dye2OffHand.RowId));
    }

    private static (NpcWeapon? Main, NpcWeapon? Off) WeaponsFromNpcEquip(NpcEquip e) =>
        (Weapon(e.ModelMainHand, e.DyeMainHand.RowId, e.Dye2MainHand.RowId),
         Weapon(e.ModelOffHand,  e.DyeOffHand.RowId,  e.Dye2OffHand.RowId));

    // Decode a packed 64-bit weapon model column into Set/Type/Variant. Layout (verbatim from
    // HOutfits' NpcService.AddWeapon, which matches Brio's ModelMainHand -> WeaponModelId copy and
    // Glamourer's CharacterWeapon(ModelMainHand | dye<<48 | dye2<<56)): Set = bits 0-15 (primary
    // model), Type = bits 16-31 (secondary model), Variant = bits 32-39. The model column's own
    // stain bytes are ignored; dyes come from the separate Dye columns, exactly as armor is read.
    private static NpcWeapon? Weapon(ulong modelValue, uint dye, uint dye2)
    {
        if (modelValue == 0) return null; // no weapon in this slot
        var set = (ushort)(modelValue & 0xFFFF);
        if (set == 0) return null;
        var type    = (ushort)((modelValue >> 16) & 0xFFFF);
        var variant = (byte)((modelValue >> 32) & 0xFF);
        return new NpcWeapon(set, type, variant, (byte)dye, (byte)dye2);
    }

    /// <summary>
    /// The 26-byte customize array for a catalog row, or null if the row (or its
    /// customize source) can't be resolved. Used by the Human (McType 1) guise path:
    /// a human NPC's *look* is customize (face/body/colours) + gear, and Glamourer
    /// paints the customize block from this array (see HumanGuise). Indices/columns
    /// are verbatim from HOutfits' <c>ReadCustomizeFromBNpc</c>, which mirrors
    /// Glamourer's NpcCustomizeSet.
    ///
    /// Source-less overload = <see cref="NpcSource.Battle"/> (BNpcBase.BNpcCustomize),
    /// preserving the existing call sites unchanged.
    /// </summary>
    public byte[]? TryGetCustomize(uint baseId) => TryGetCustomize(baseId, NpcSource.Battle);

    /// <summary>
    /// Source-aware customize read. <see cref="NpcSource.Battle"/> reads BNpcBase's
    /// BNpcCustomize reference; <see cref="NpcSource.Event"/> reads an ENpcBase's inline
    /// customize columns (identical field names — the ENpcBase row is a self-contained
    /// superset that carries its own appearance, per Glamourer's FromEnpcBase).
    /// </summary>
    public byte[]? TryGetCustomize(uint baseId, NpcSource source)
    {
        if (source == NpcSource.Event)
            return TryGetEventCustomize(baseId);

        var sheet = _data.GetExcelSheet<BNpcBase>();
        if (sheet.GetRowOrDefault(baseId) is not { } bnpc)
        {
            _log.Warning($"NpcData: BNpcBase {baseId} not found.");
            return null;
        }
        if (bnpc.BNpcCustomize.ValueNullable is not { } r)
            return null; // no BNpcCustomize reference on this base
        return CustomizeFromBnpc(r);
    }

    private byte[]? TryGetEventCustomize(uint baseId)
    {
        var sheet = _data.GetExcelSheet<ENpcBase>();
        if (sheet.GetRowOrDefault(baseId) is not { } e)
        {
            _log.Warning($"NpcData: ENpcBase {baseId} not found.");
            return null;
        }
        return CustomizeFromEnpc(e);
    }

    // Customize block from a BNpcCustomize row (Battle path).
    private static byte[] CustomizeFromBnpc(BNpcCustomize r)
    {
        var c = new byte[26];
        c[0]  = (byte)r.Race.RowId;
        c[1]  = (byte)r.Gender;
        c[2]  = (byte)r.BodyType;
        c[3]  = (byte)r.Height;
        c[4]  = (byte)r.Tribe.RowId;
        c[5]  = (byte)r.Face;
        c[6]  = (byte)r.HairStyle;
        c[7]  = (byte)r.HairHighlight;
        c[8]  = (byte)r.SkinColor;
        c[9]  = (byte)r.EyeHeterochromia;
        c[10] = (byte)r.HairColor;
        c[11] = (byte)r.HairHighlightColor;
        c[12] = (byte)r.FacialFeature;
        c[13] = (byte)r.FacialFeatureColor;
        c[14] = (byte)r.Eyebrows;
        c[15] = (byte)r.EyeColor;
        c[16] = (byte)r.EyeShape;
        c[17] = (byte)r.Nose;
        c[18] = (byte)r.Jaw;
        c[19] = (byte)r.Mouth;
        c[20] = (byte)r.LipColor;
        c[21] = (byte)r.BustOrTone1;
        c[22] = (byte)r.ExtraFeature1;
        c[23] = (byte)r.ExtraFeature2OrBust;
        c[24] = (byte)r.FacePaint;
        c[25] = (byte)r.FacePaintColor;
        return c;
    }

    // Customize block inline on an ENpcBase row (Event path). Field names are identical to
    // BNpcCustomize — the ENpcBase row carries its own human appearance (Glamourer FromEnpcBase).
    private static byte[] CustomizeFromEnpc(ENpcBase r)
    {
        var c = new byte[26];
        c[0]  = (byte)r.Race.RowId;
        c[1]  = (byte)r.Gender;
        c[2]  = (byte)r.BodyType;
        c[3]  = (byte)r.Height;
        c[4]  = (byte)r.Tribe.RowId;
        c[5]  = (byte)r.Face;
        c[6]  = (byte)r.HairStyle;
        c[7]  = (byte)r.HairHighlight;
        c[8]  = (byte)r.SkinColor;
        c[9]  = (byte)r.EyeHeterochromia;
        c[10] = (byte)r.HairColor;
        c[11] = (byte)r.HairHighlightColor;
        c[12] = (byte)r.FacialFeature;
        c[13] = (byte)r.FacialFeatureColor;
        c[14] = (byte)r.Eyebrows;
        c[15] = (byte)r.EyeColor;
        c[16] = (byte)r.EyeShape;
        c[17] = (byte)r.Nose;
        c[18] = (byte)r.Jaw;
        c[19] = (byte)r.Mouth;
        c[20] = (byte)r.LipColor;
        c[21] = (byte)r.BustOrTone1;
        c[22] = (byte)r.ExtraFeature1;
        c[23] = (byte)r.ExtraFeature2OrBust;
        c[24] = (byte)r.FacePaint;
        c[25] = (byte)r.FacePaintColor;
        return c;
    }

    /// <summary>
    /// Resolve a BNpcName row id to a Title-cased display name, or null if the row is missing or
    /// blank. The game stores battle-NPC singular names lowercase and capitalises them at display
    /// time via grammar rules; the HDM catalog is Title Case, so we Title-case here to match.
    /// Used to backfill the label of catalog bases the crowdsourced Name column left blank but an
    /// instanced encounter roster (BossMod) still names — e.g. base 19519 → name 14734 → "Chort".
    /// </summary>
    public string? ResolveBNpcName(uint nameId)
    {
        if (nameId == 0) return null;
        var sheet = _data.GetExcelSheet<BNpcName>();
        var raw = sheet.GetRowOrDefault(nameId)?.Singular.ToString();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return TitleCase(raw!);
    }

    // Small words kept lowercase mid-name when Title-casing a raw BNpcName singular (the game
    // stores them lowercase, e.g. "gladiator of sil'dih"); mirrors the offline scraper's rule.
    private static readonly HashSet<string> TitleSmallWords = new(StringComparer.OrdinalIgnoreCase)
        { "of", "the", "in", "on", "to", "and", "a", "an", "from", "with", "at", "by", "for", "de", "del", "des", "la", "le" };

    private static string TitleCase(string s)
    {
        var words = s.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (w.Length == 0) continue;
            words[i] = i > 0 && TitleSmallWords.Contains(w)
                ? w.ToLowerInvariant()
                : char.ToUpperInvariant(w[0]) + w.Substring(1);
        }
        return string.Join(' ', words);
    }
}
