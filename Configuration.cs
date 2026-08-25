using System.Collections.Generic;
using Dalamud.Configuration;

namespace HDM;

/// <summary>
/// Persisted settings. Load via <c>PluginInterface.GetPluginConfig()</c>, save
/// with <c>PluginInterface.SavePluginConfig(this)</c> on change.
/// </summary>
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 4;

    /// <summary>Favorited BNpcBase row ids (starred in the catalog).</summary>
    public HashSet<uint> Favorites { get; set; } = [];

    /// <summary>
    /// Per-favourite absolute scale override, used by the Favourites tab's focused-library controls.
    /// Keyed by BNpcBase id; a base absent here falls back to the mob's authored native scale
    /// (<see cref="MobRow.Scale"/>). Purely additive — empty by default, absent in older configs
    /// deserializes to an empty dict, so no migration is needed. Only Monster/Demihuman guises size
    /// through GuiseService; a Human (Glamourer) favourite ignores this (it has no HDM-owned scale).
    /// </summary>
    public Dictionary<uint, float> FavScales { get; set; } = [];

    /// <summary>
    /// Per-favourite draw-elevation override (a vertical draw offset in the same units as the Wisp's
    /// +2.80 lift), used by the Favourites tab's focused-library controls. Keyed by BNpcBase id; a base
    /// absent here (or within ~0 of the floor) applies no lift — the model sits at native ground height.
    /// Family-agnostic: it's a GuiseService draw offset, so it works on Monster/Demihuman AND Human
    /// (Glamourer) favourites alike. Purely additive — empty by default, absent in older configs
    /// deserializes to an empty dict, so no migration is needed.
    /// </summary>
    public Dictionary<uint, float> FavElevations { get; set; } = [];

    /// <summary>
    /// LEGACY / vestigial. Once chose a catalog model-family filter (0 = Monster only, 1 = All,
    /// 2 = Human, 3 = Demihuman). All three families render (Monster/Demihuman via ModelChara swap
    /// + equipment in GuiseService; Human via Glamourer in HumanGuise), so the filter only ever hid
    /// rows — search + the Skel-prefix legend cover it — and the "Show:" chip row was removed. Nothing
    /// reads this field anymore; it's kept purely so old configs still deserialize cleanly (same as
    /// <see cref="TargetMode"/>). The v2→v3 migration that flipped the old Monster-only default is now
    /// moot but harmless.
    /// </summary>
    public int TypeFilter { get; set; } = 1;

    /// <summary>
    /// LEGACY / vestigial. Once chose the apply subject (0 = self, 1 = current target). HDM is now
    /// self-apply ONLY — a disguise is worn by you and propagates to others through HMS sync, never
    /// enforced unilaterally on another actor (the Glamourer model). The Self/Target UI is gone and
    /// nothing reads this field anymore; it's kept purely so old configs still deserialize cleanly.
    /// </summary>
    public int TargetMode { get; set; } = 0;

    /// <summary>
    /// Catalog grouping: 0 = No groups (flat list), 1 = Location (the HMS-style tree —
    /// expansion dividers → collapsible zone nodes → indented mobs, via TerritoryIndex +
    /// ManualLocationIndex + ContentIndex), 2 = Family (Monster/Demihuman/Human), 3 = Named
    /// vs Unnamed. Location is the headline view and now the DEFAULT; its unplaceable tail
    /// (still the majority until the harvester fills in) is fenced into a collapsed "Unknown
    /// location" section, so the default view stays readable. Absent in pre-0.6 configs this
    /// deserializes to 0 (flat) — that's fine, it's a view preference, not migrated.
    /// </summary>
    public int GroupBy { get; set; } = 1;

    /// <summary>
    /// Catalog category filter (the Duty-Finder-style chips): "All" (no filter) or one of the
    /// ContentIndex category names — World, City, Inn, Housing, Dungeon, Trial, Raid, Deep Dungeon,
    /// Variant &amp; Criterion, Solo, PvP, Gold Saucer — plus the synthetic "Unknown" (rows with no
    /// resolved home territory). Single-select; narrows the catalog to one content family the way
    /// the in-game Duty Finder tabs do. New key — absent in older configs deserializes to the
    /// initializer default "All", so no migration is needed.
    /// </summary>
    public string CategoryFilter { get; set; } = "All";

    /// <summary>
    /// Catalog model-family filter: "All" (no filter) or "Monster" / "Demihuman" / "Human" — keyed
    /// off each row's <see cref="MobRow.Kind"/> (McType 3 / 2 / 1). Restored by request to isolate
    /// the player-similar families: "Human" rows ARE the player c-skeleton, so native player emotes
    /// (/sit, /doze) work on those guises; "Demihuman" is the semi-humanoid middle; monster/demihuman
    /// skeletons carry none of the player emote set (the caps oracle confirms it). Single-select chip
    /// row beside Category. New key — absent in older configs deserializes to "All", so no migration
    /// is needed. (Supersedes the vestigial <see cref="TypeFilter"/>, whose awkward int encoding —
    /// 0=Monster/1=All/2=Human/3=Demihuman — it deliberately does NOT reuse.)
    /// </summary>
    public string FamilyFilter { get; set; } = "All";

    /// <summary>
    /// Legacy (v1) flag: apply the mob's native scale. Kept only so old configs
    /// migrate into <see cref="ScaleMode"/>. Prefer ScaleMode going forward.
    /// </summary>
    public bool ApplyScale { get; set; } = true;

    /// <summary>
    /// How to size the disguise:
    ///   0 = Off    — leave the actor's current scale untouched (LEGACY: no longer selectable in
    ///                the UI as of the modeless scale rework; the v3→v4 migration flips saved 0→1 so
    ///                nobody is stuck on an unreachable mode. The value is still honoured by the apply
    ///                path for back-compat, but the UI only ever writes 1 or 2 now),
    ///   1 = Native — apply the mob's authored scale (Galatea Magna = 2.0),
    ///   2 = Custom — apply <see cref="ScaleCustom"/> as an absolute multiplier.
    /// Bosses are often authored non-1.0, so Native keeps lore proportions; Custom
    /// lets a DM shrink a behemoth to lalafell height or scale anything to taste.
    /// </summary>
    public int ScaleMode { get; set; } = 1;

    /// <summary>Absolute scale used when <see cref="ScaleMode"/> == 2 (1.0 = model default).</summary>
    public float ScaleCustom { get; set; } = 1.0f;

    /// <summary>
    /// Opt-in (default OFF): when applying a disguise to YOURSELF, also set your nameplate to the
    /// disguise's name via Moniker (HMoniker v2.1+), which syncs it to peers through HMS; reverting
    /// clears it. Lifted from HOutfits' NPC "Apply name" toggle. Self-only — a puppet's nameplate is
    /// never touched (Moniker's SetLocalName only renames the local player). Inert when Moniker isn't
    /// installed (the toggle greys out). New key — absent in older configs deserializes to false, so
    /// no migration is needed.
    /// </summary>
    public bool ApplyName { get; set; } = false;

    /// <summary>
    /// Accent colour (RGBA, 0..1) — HDM's own theme accent, mirroring HM-Sync's Config→Accents. Drives active
    /// chips, the tab highlight, checkmarks / slider grabs, and the selection wash (see <see cref="AccentPalette"/>).
    /// Gold by default — the SAME default HM-Sync ships — so an un-themed HDM already matches an un-themed
    /// HM-Sync. Neutral (grey) and Danger (warm-red) are FIXED in the UI; hover and text-on-accent are DERIVED,
    /// so any accent stays legible. Stored as float[4] to match HM-Sync's wire shape exactly. New key — absent
    /// in older configs deserializes to this gold default, so no migration is needed.
    /// </summary>
    public float[] AccentColor { get; set; } = { 0.83f, 0.62f, 0.20f, 1f };

    /// <summary>
    /// Opt-in (default ON): when HM-Sync is installed AND exposes its accent over IPC, follow HM-Sync's accent
    /// instead of <see cref="AccentColor"/>, so a DM's whole HM tool-suite shares one hue. Falls back to
    /// <see cref="AccentColor"/> whenever HM-Sync is absent or its accent IPC is unavailable (e.g. an older
    /// HM-Sync without the provider). The sync is READ-ONLY and pull-based — HDM consumes HMSync.GetAccentColor;
    /// see <see cref="HmsIpc"/> + docs/hms-accent-ipc-ask.md. New key — absent in older configs deserializes to
    /// true, so no migration is needed.
    /// </summary>
    public bool SyncAccentWithHms { get; set; } = true;

    /// <summary>
    /// Default ON: paint the clickable blue possess-dot over each live puppet's head (the Spawn tab's "Show
    /// possess dots" toggle). Seeded into <see cref="PossessionService.OverlayEnabled"/> at startup and written
    /// back when the checkbox flips, so the overlay preference persists across sessions. Absent in older configs
    /// this deserializes to true (the initializer stands — Newtonsoft leaves un-present keys untouched), so the
    /// overlay defaults on for everyone; no migration is needed.
    /// </summary>
    public bool ShowPossessionDots { get; set; } = true;

    /// <summary>
    /// Opt-in (default OFF): allow THIS client to possess puppets it did NOT originate — i.e. the mirrors of
    /// another player's spawns that HMS reproduces in your world. Off by default so control of a spawned puppet
    /// is exclusive to its originator: on every OTHER client the puppet is a mirror, and a mirror can't be
    /// possessed, so only the DM who spawned it can drive it. Turn this on when helping run someone else's event
    /// (a helper puppeteering one of the DM's NPCs). This is inherently a POSSESSOR-side switch: a client can
    /// only ever gate its OWN behaviour (FFXIV plugins can't enforce anything on a remote client), so "exclusive
    /// to the originator" holds precisely because every peer's HDM refuses mirrors by default. Seeded into
    /// <see cref="PossessionService.AllowPossessOthers"/> at startup and written back when the checkbox flips.
    /// New key — absent in older configs deserializes to false, so no migration is needed.
    /// </summary>
    public bool AllowPossessOthersPuppets { get; set; } = false;

    /// <summary>
    /// Opt-in (default OFF): let the Tier A3 runtime harvester passively sample live BattleNpc spawns while
    /// you're in a duty and record their <c>BNpcBase → territory</c> + localized name to a user-local CSV
    /// (see <see cref="MobHarvester"/>), which then feeds the catalog's location tree and live nameplates.
    /// OFF by default so a fresh install collects nothing until asked — the shipped CSVs already place the
    /// overworld; this only fills the instanced tail (the YoRHa androids &amp;co.) for a DM who opts in.
    /// Gates NEW collection only: rows already harvested to disk keep answering location lookups even when
    /// this is off. Seeded into <see cref="MobHarvester.Enabled"/> at startup and written back when the
    /// Config toggle flips. New key — absent in older configs deserializes to false, so no migration is needed.
    /// </summary>
    public bool HarvestMobNames { get; set; } = false;
}
