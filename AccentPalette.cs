using System;
using System.Numerics;

namespace HDM;

/// <summary>
/// Central accent-colour engine, mirroring HM-Sync's Config→Accents palette so HDM speaks the same visual
/// language. <see cref="Primary"/> is the live accent (active chips, tab highlight, checkmarks, slider grabs,
/// selection wash); Neutral (grey) and Danger (warm-red) are FIXED, not derived. Hover and text-on-accent are
/// DERIVED (<see cref="Lighten"/> / <see cref="TextOn"/>) so any accent the user picks stays legible —
/// identical math to HMSyncUI, lifted deliberately (HDM duplicates rather than couples to HMS).
///
/// Sync: when <see cref="Configuration.SyncAccentWithHms"/> is on AND HM-Sync exposes its accent over IPC,
/// <see cref="Primary"/> follows HM-Sync's accent instead of HDM's own <see cref="Configuration.AccentColor"/>.
/// The pull is cached on a short cadence (<see cref="HmsPoll"/>) so a synced accent costs one IPC call every
/// couple of seconds, not one per frame. Falls back to the local accent whenever HM-Sync is absent or its IPC
/// is unavailable — and since both plugins default to the same gold, an un-themed HDM already matches an
/// un-themed HM-Sync, so the sync is invisible until someone actually re-themes HM-Sync.
/// </summary>
public sealed class AccentPalette
{
    private readonly Configuration _config;
    private readonly HmsIpc _hms;

    // The shared default both plugins ship with.
    private static readonly Vector4 Gold = new(0.83f, 0.62f, 0.20f, 1f);

    // HM-Sync accent cache — refreshed on a cadence, not per-frame (the IPC pull is cheap but not free, and
    // Primary is read many times each frame across the whole UI).
    private Vector4? _hmsAccent;
    private DateTime _hmsChecked = DateTime.MinValue;
    private static readonly TimeSpan HmsPoll = TimeSpan.FromSeconds(2);

    public AccentPalette(Configuration config, HmsIpc hms)
    {
        _config = config;
        _hms = hms;
    }

    /// <summary>The live accent: HM-Sync's when syncing and available, else HDM's own configured accent.</summary>
    public Vector4 Primary => SyncedFromHms ? _hmsAccent!.Value : Local;

    /// <summary>HDM's own configured accent (ignores sync) — what the Config picker edits and the sync fallback.</summary>
    public Vector4 Local
    {
        get
        {
            var a = _config.AccentColor;
            return (a is { Length: >= 4 }) ? new Vector4(a[0], a[1], a[2], a[3]) : Gold;
        }
    }

    /// <summary>True when the accent is currently pulled from HM-Sync (sync toggle on + HMS accent readable).</summary>
    public bool SyncedFromHms
    {
        get
        {
            if (!_config.SyncAccentWithHms) return false;
            RefreshHms();
            return _hmsAccent is not null;
        }
    }

    /// <summary>Is HM-Sync installed AND loaded? (Config-tab messaging only — pass-through to <see cref="HmsIpc"/>.)</summary>
    public bool HmsPresent => _hms.Present;

    /// <summary>Does HM-Sync expose the accent IPC? (Drives the "Synced" indicator — pass-through.)</summary>
    public bool HmsAccentAvailable => _hms.Available;

    // Pull + cache HM-Sync's accent on a cadence. Null when unavailable.
    private void RefreshHms()
    {
        if (DateTime.UtcNow - _hmsChecked < HmsPoll) return;
        _hmsChecked = DateTime.UtcNow;
        _hmsAccent = _hms.TryGetAccent(out var rgba)
            ? new Vector4(rgba[0], rgba[1], rgba[2], rgba[3])
            : null;
    }

    // ── Derived tones — identical math to HMSyncUI ──────────────────────────────────────────────────────
    public static Vector4 Lighten(Vector4 c, float f) => new(MathF.Min(c.X * f, 1f), MathF.Min(c.Y * f, 1f), MathF.Min(c.Z * f, 1f), c.W);
    public static Vector4 Darken(Vector4 c, float f) => new(c.X * f, c.Y * f, c.Z * f, c.W);

    /// <summary>Auto-contrast ink: dark on a light accent, light on a dark one (perceptual luminance).</summary>
    public static Vector4 TextOn(Vector4 bg)
    {
        float lum = 0.299f * bg.X + 0.587f * bg.Y + 0.114f * bg.Z;
        return lum > 0.55f ? new Vector4(0.10f, 0.09f, 0.04f, 1f) : new Vector4(0.97f, 0.97f, 0.99f, 1f);
    }

    /// <summary>A translucent tint of an accent — for wash fills (selection/header) where text sits on top and
    /// the dark window bg must show through to keep it legible.</summary>
    public static Vector4 Alpha(Vector4 c, float a) => new(c.X, c.Y, c.Z, a);

    /// <summary>Fixed neutral (unselected chip / off-toggle). NOT derived from the accent — mirrors HMS.</summary>
    public static readonly Vector4 Neutral = new(0.16f, 0.17f, 0.20f, 1f);

    /// <summary>Fixed danger (destructive action). NOT derived from the accent — mirrors HMS.</summary>
    public static readonly Vector4 Danger = new(0.42f, 0.16f, 0.16f, 1f);

    /// <summary>The default gold both plugins ship — exposed for the Config tab's "reset" affordance.</summary>
    public static Vector4 DefaultGold => Gold;
}
