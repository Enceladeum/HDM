using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace HDM;

/// <summary>
/// The "HM-Sync treatment" UI kit — the shared boxed-panel / mono-label / meta-pill idiom lifted from
/// HM-Sync's <c>HMSyncUI</c> so HDM's browser reads in the same visual language as the rest of the HM tool
/// suite. Deliberately accent-INDEPENDENT structure (borders, section labels, pill outlines); the accent lives
/// in the chips/buttons the caller draws (which route through the plugin's <see cref="AccentPalette"/>). Keeping
/// these primitives in their own file lets MainWindow call-sites stay thin and re-homeable — collision-safe
/// against the concurrent MainWindow redesign, exactly like the accent work.
///
/// Panels box their content with an auto-fitting drawn border (no <c>BeginChild</c>, so no fixed height): the
/// cursor is captured on open and an <c>AddRect</c> is drawn on close. Use with <c>using</c>:
/// <code>using (HmUi.Panel("FAMILY")) { ...chips... }</code>
/// The scope carries its own state (start/width), so panels are nest-safe and side-by-side-safe (unlike
/// HMSyncUI's shared fields).
/// </summary>
internal static class HmUi
{
    /// <summary>Inner margin between a panel's border and its content — matches HMSyncUI's <c>PanelPad</c> so a
    /// side-by-side HDM/HM-Sync reads as one suite.</summary>
    public const float PanelPad = 8f;

    // Tones matched to HMSyncUI (panel border) and the mockup (meta-pill outline + two-tone text) so the two
    // plugins are visually indistinguishable side by side.
    private static readonly Vector4 PanelBorderCol = new(0.32f, 0.34f, 0.40f, 0.85f);
    private static readonly Vector4 PillBorderCol  = new(0.30f, 0.33f, 0.39f, 0.80f);
    private static readonly Vector4 PillNumberCol  = new(0.90f, 0.92f, 0.96f, 1f);
    private static readonly Vector4 PillLabelCol   = new(0.55f, 0.58f, 0.64f, 1f);

    // Neutral (off) chip/button tones — identical to MainWindow.Chip so DISGUISE·YOU buttons match the
    // FAMILY/CATEGORY chips exactly. Accent (on) tones are derived from the passed accent via AccentPalette.
    private static readonly Vector4 OffBtn   = new(0.15f, 0.16f, 0.19f, 1f);
    private static readonly Vector4 OffHover = new(0.24f, 0.25f, 0.29f, 1f);
    private static readonly Vector4 OffPress = new(0.30f, 0.34f, 0.40f, 1f);
    private static readonly Vector4 OffText  = new(0.70f, 0.73f, 0.78f, 1f);

    // ── Section label ────────────────────────────────────────────────────────────────────────────────────
    /// <summary>The mono-ish uppercase section caption above a panel. Neither plugin ships a monospace font, so
    /// the "mono" look is approximated the same way HMSyncUI does it — an UPPERCASE label in the dimmed
    /// (TextDisabled) tone. Pass already-uppercased text ("FAMILY", "DISGUISE · YOU").</summary>
    public static void SectionLabel(string label) => ImGui.TextDisabled(label);

    // ── Boxed panel ──────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Open a boxed panel with a section label. Dispose (end of the <c>using</c>) draws the border.
    /// Optional <paramref name="tooltip"/> is attached to the section label (hover the caption to read it).</summary>
    public static PanelScope Panel(string label, string? tooltip = null) => new(label, tooltip);

    /// <summary>Scope token for <see cref="Panel"/>. Non-boxing when used with <c>using</c> (the compiler calls
    /// <see cref="Dispose"/> via a constrained call on the struct).</summary>
    public readonly struct PanelScope : IDisposable
    {
        private readonly Vector2 _start;
        private readonly float _width;

        internal PanelScope(string label, string? tooltip)
        {
            SectionLabel(label);
            if (tooltip is not null && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
            _start = ImGui.GetCursorScreenPos();
            _width = ImGui.GetContentRegionAvail().X;
            // Top inner padding. The bottom edge accrues an extra ItemSpacing.Y before Dispose captures its
            // cursor, so the top adds the same to stay visually equidistant (matches HMSyncUI's note).
            ImGui.Dummy(new Vector2(0f, 3f + ImGui.GetStyle().ItemSpacing.Y));
            ImGui.Indent(PanelPad); // left inner margin (full-width content in a panel uses -PanelPad for the right)
        }

        public void Dispose()
        {
            ImGui.Unindent(PanelPad);
            ImGui.Dummy(new Vector2(0f, 3f)); // bottom inner padding
            var end = ImGui.GetCursorScreenPos();
            ImGui.GetWindowDrawList().AddRect(_start, new Vector2(_start.X + _width, end.Y),
                ImGui.GetColorU32(PanelBorderCol), 6f);
            ImGui.Spacing();
        }
    }

    // ── Accent pill button ───────────────────────────────────────────────────────────────────────────────
    /// <summary>A pill button identical in look to <c>MainWindow.Chip</c>, but width-capable and with the accent
    /// passed in (this kit is accent-agnostic). Active = filled accent + auto-contrast ink; inactive = dark
    /// neutral + muted ink. <paramref name="width"/> 0 = size to content; use a positive width for a filled grid
    /// cell. Returns true on click.</summary>
    public static bool AccentButton(string label, string id, bool active, Vector4 accent, float width = 0f)
    {
        ImGui.PushStyleColor(ImGuiCol.Button,        active ? accent                       : OffBtn);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, active ? AccentPalette.Lighten(accent, 1.15f) : OffHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  OffPress);
        ImGui.PushStyleColor(ImGuiCol.Text,          active ? AccentPalette.TextOn(accent) : OffText);
        var clicked = ImGui.Button($"{label}##{id}", new Vector2(width, 0f));
        ImGui.PopStyleColor(4);
        return clicked;
    }

    /// <summary>A prominent full-accent action button (HM-Sync's <c>PrimaryButton</c> look) — always filled with
    /// the accent, auto-contrast ink. When <paramref name="enabled"/> is false it dims to ~half alpha but stays
    /// clickable, so the caller's tooltip can still explain what's missing (matches HDM's existing "Spawn lights
    /// only when it can act, but stays clickable" behaviour). <paramref name="height"/> 0 = default frame height;
    /// pass a positive value for a chunkier, more prominent hero button. Returns true on any click (enabled or
    /// not — the caller no-ops with a log when it can't act).</summary>
    public static bool PrimaryButton(string label, string id, Vector4 accent, float width, bool enabled, float height = 0f)
    {
        if (!enabled) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * 0.55f);
        ImGui.PushStyleColor(ImGuiCol.Button,        accent);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccentPalette.Lighten(accent, 1.12f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  AccentPalette.Lighten(accent, 1.12f));
        ImGui.PushStyleColor(ImGuiCol.Text,          AccentPalette.TextOn(accent));
        var clicked = ImGui.Button($"{label}##{id}", new Vector2(width, height));
        ImGui.PopStyleColor(4);
        if (!enabled) ImGui.PopStyleVar();
        return clicked;
    }

    // ── Row name button ──────────────────────────────────────────────────────────────────────────────────
    // A prominent, LEFT-aligned, filled+rounded name button for list rows (Favourites, and any future
    // "apply this entry" list). Bigger and glossier than a bare Selectable: it always reads as a button
    // (persistent fill + border), fills accent when active (the entry you're wearing / selected), and CLIPS a
    // long label instead of overflowing the row. Optional textTint colours the inactive ink (e.g. HDM's
    // heuristic-name cue). Attach a tooltip with ImGui.IsItemHovered() on the line right after the call.
    private static readonly Vector4 RowBtnIdle  = new(0.15f, 0.16f, 0.19f, 1f);
    private static readonly Vector4 RowBtnHover = new(0.24f, 0.25f, 0.29f, 1f);
    private static readonly Vector4 RowBtnInk   = new(0.88f, 0.90f, 0.94f, 1f);

    /// <summary>A tall, left-aligned, filled+rounded list-row name button. <paramref name="width"/> 0 = fill
    /// the content region. Active = accent fill + auto-contrast ink; inactive = dark neutral + <paramref
    /// name="textTint"/> (or a bright default). Returns true on click; the caller decides plain-vs-modifier.</summary>
    public static bool RowNameButton(string label, bool active, Vector4 accent, float width, string id, Vector4? textTint = null)
    {
        float h = ImGui.GetFrameHeight() + 6f;                 // taller than a normal frame → distinct
        if (width <= 0f) width = ImGui.GetContentRegionAvail().X;
        var p = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton($"##rowname{id}", new Vector2(width, h));
        bool hover = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        var max = new Vector2(p.X + width, p.Y + h);
        Vector4 fill = active ? (hover ? AccentPalette.Lighten(accent, 1.12f) : accent)
                              : (hover ? RowBtnHover : RowBtnIdle);
        dl.AddRectFilled(p, max, ImGui.GetColorU32(fill), 6f);
        dl.AddRect(p, max, ImGui.GetColorU32(active ? AccentPalette.Lighten(accent, 1.25f) : PanelBorderCol), 6f);
        uint ink = active ? ImGui.GetColorU32(AccentPalette.TextOn(accent))
                          : ImGui.GetColorU32(textTint ?? RowBtnInk);
        var tSz = ImGui.CalcTextSize(label);
        dl.PushClipRect(new Vector2(p.X + 4f, p.Y), new Vector2(max.X - 6f, max.Y), true);
        dl.AddText(new Vector2(p.X + 10f, p.Y + (h - tSz.Y) * 0.5f), ink, label);
        dl.PopClipRect();
        return clicked;
    }

    // ── Meta pills ───────────────────────────────────────────────────────────────────────────────────────
    /// <summary>A compact outlined pill showing a bright number + a dimmed label ("<b>25,782</b> matches"). The
    /// endless count string becomes a row of these. Reflows: pills pack left-to-right and wrap when the next one
    /// would overflow the content region. Cheap (draw-list text + one InvisibleButton per pill for layout).</summary>
    public static void MetaPills(params (string number, string label)[] pills)
    {
        if (pills is null || pills.Length == 0) return;
        const float interGap = 6f;
        float rightEdge = ImGui.GetWindowPos().X + ImGui.GetContentRegionMax().X;
        for (int i = 0; i < pills.Length; i++)
        {
            MetaPill(i, pills[i].number, pills[i].label);
            if (i + 1 < pills.Length)
            {
                float nextW = PillWidth(pills[i + 1].number, pills[i + 1].label);
                if (ImGui.GetItemRectMax().X + interGap + nextW < rightEdge) ImGui.SameLine(0f, interGap);
            }
        }
    }

    private const float PillPadX = 7f;
    private const float PillPadY = 3f;
    private const float PillInnerGap = 4f;

    private static float PillWidth(string number, string label)
        => PillPadX * 2f + ImGui.CalcTextSize(number).X + PillInnerGap + ImGui.CalcTextSize(label).X;

    private static void MetaPill(int i, string number, string label)
    {
        var numSize = ImGui.CalcTextSize(number);
        var labSize = ImGui.CalcTextSize(label);
        float w = PillPadX * 2f + numSize.X + PillInnerGap + labSize.X;
        float h = PillPadY * 2f + MathF.Max(numSize.Y, labSize.Y);

        var p = ImGui.GetCursorScreenPos();
        // InvisibleButton reserves the exact box AND participates in normal layout (SameLine / wrap flow off
        // GetItemRectMax); we paint the visuals ourselves so the number and label can be two different tones.
        ImGui.InvisibleButton($"##pill{i}", new Vector2(w, h));

        var dl = ImGui.GetWindowDrawList();
        dl.AddRect(p, new Vector2(p.X + w, p.Y + h), ImGui.GetColorU32(PillBorderCol), 4f);
        float ty = p.Y + PillPadY;
        dl.AddText(new Vector2(p.X + PillPadX, ty), ImGui.GetColorU32(PillNumberCol), number);
        dl.AddText(new Vector2(p.X + PillPadX + numSize.X + PillInnerGap, ty), ImGui.GetColorU32(PillLabelCol), label);
    }

    // ── Danger card ──────────────────────────────────────────────────────────────────────────────────────
    // The Animations mockup's red "Reset to Normal (unstick)" box: a full-width clickable card, filled dark-red
    // with a ring glyph + title + dimmed subtitle, brightening on hover. Danger is a FIXED hue (not accent-
    // derived) — an unstick is destructive regardless of the suite's accent, exactly like MainWindow.PushRed.
    private static readonly Vector4 DangerFill      = new(0.20f, 0.10f, 0.10f, 1f);
    private static readonly Vector4 DangerFillHover = new(0.27f, 0.13f, 0.13f, 1f);
    private static readonly Vector4 DangerBorder    = new(0.55f, 0.26f, 0.26f, 0.85f);
    private static readonly Vector4 DangerTitle     = new(0.93f, 0.62f, 0.57f, 1f);
    private static readonly Vector4 DangerSub       = new(0.72f, 0.52f, 0.50f, 1f);

    /// <summary>A full-width clickable danger card (ring glyph + <paramref name="title"/> + dimmed
    /// <paramref name="subtitle"/>). Returns true on click; attach a tooltip with <c>ImGui.IsItemHovered()</c>
    /// on the line after the call (the InvisibleButton is the last-drawn item).</summary>
    public static bool DangerCard(string title, string subtitle, string id)
    {
        float w = ImGui.GetContentRegionAvail().X;
        var tSz = ImGui.CalcTextSize(title);
        var sSz = ImGui.CalcTextSize(subtitle);
        const float padX = 12f, padY = 8f, gap = 2f, glyphCol = 30f;
        float h = padY * 2f + tSz.Y + gap + sSz.Y;
        var p = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton($"##danger{id}", new Vector2(w, h));
        bool hover = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, new Vector2(p.X + w, p.Y + h), ImGui.GetColorU32(hover ? DangerFillHover : DangerFill), 6f);
        dl.AddRect(p, new Vector2(p.X + w, p.Y + h), ImGui.GetColorU32(DangerBorder), 6f);
        // Ring-with-tick — a proxy for the mockup's circular-arrow "reset" mark (the default font has no ↺ glyph).
        uint titleCol = ImGui.GetColorU32(DangerTitle);
        float cx = p.X + padX + 7f, cy = p.Y + h / 2f;
        dl.AddCircle(new Vector2(cx, cy), 7f, titleCol, 16, 2f);
        dl.AddLine(new Vector2(cx, cy - 7f), new Vector2(cx + 5f, cy - 4f), titleCol, 2f);
        float tx = p.X + padX + glyphCol;
        dl.AddText(new Vector2(tx, p.Y + padY), titleCol, title);
        dl.AddText(new Vector2(tx, p.Y + padY + tSz.Y + gap), ImGui.GetColorU32(DangerSub), subtitle);
        return clicked;
    }

    // ── Inline readout ───────────────────────────────────────────────────────────────────────────────────
    // The framed mono value pinned to the right of a Playback row ("1.00×", "+0.00"). Call it immediately after
    // the row's left label; it rejoins that line, right-aligns to the content edge, and reserves its own box.
    private static readonly Vector4 ReadoutBg     = new(0.10f, 0.10f, 0.12f, 1f);
    private static readonly Vector4 ReadoutBorder = new(0.30f, 0.33f, 0.39f, 0.80f);
    private static readonly Vector4 ReadoutText   = new(0.90f, 0.92f, 0.96f, 1f);

    /// <summary>Right-aligned framed value box on the current line. No ID (non-interactive, reserves via a
    /// Dummy), so it's safe to call several times per frame. Robust to panel indent (measures the remaining
    /// line width after rejoining the label's line rather than assuming full width). Keeps a <see cref="PanelPad"/>
    /// right margin so the box mirrors the panel's LEFT inset instead of brushing the panel border (and reads
    /// tidily even outside a panel).</summary>
    public static void Readout(string text)
    {
        ImGui.SameLine(); // rejoin the label's line (caller drew a normal, non-SameLine label just before)
        var sz = ImGui.CalcTextSize(text);
        const float padX = 8f, padY = 2f;
        float w = sz.X + padX * 2f, h = sz.Y + padY * 2f;
        float remaining = ImGui.GetContentRegionAvail().X - PanelPad; // right margin mirrors the panel's left inset
        if (remaining > w) { ImGui.Dummy(new Vector2(remaining - w, 0f)); ImGui.SameLine(0f, 0f); }
        var p = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, new Vector2(p.X + w, p.Y + h), ImGui.GetColorU32(ReadoutBg), 4f);
        dl.AddRect(p, new Vector2(p.X + w, p.Y + h), ImGui.GetColorU32(ReadoutBorder), 4f);
        dl.AddText(new Vector2(p.X + padX, p.Y + padY), ImGui.GetColorU32(ReadoutText), text);
        ImGui.Dummy(new Vector2(w, h));
    }

    // ── Group header ─────────────────────────────────────────────────────────────────────────────────────
    // The mockup's grouped-list headers ("▼ Emotes … human skeleton · 16", "▼ Common — playables … 28 of 28"):
    // a full-width expander with a custom triangle + left title + right-aligned dimmed meta. Replaces
    // ImGui.CollapsingHeader (which can't right-align secondary text); the caller holds the open-state bool.
    private static readonly Vector4 GroupHover = new(1f, 1f, 1f, 0.05f);
    private static readonly Vector4 GroupText  = new(0.86f, 0.88f, 0.92f, 1f);
    private static readonly Vector4 GroupMeta  = new(0.55f, 0.58f, 0.64f, 1f);

    /// <summary>Full-width expander header. Toggles <paramref name="open"/> on click and returns its new value,
    /// so callers can write <c>if (HmUi.GroupHeader(..., ref open, id))</c>. Pair with a following
    /// <c>BeginChild</c> for the sticky-header-over-scroll look.</summary>
    public static bool GroupHeader(string label, string meta, ref bool open, string id)
    {
        float w = ImGui.GetContentRegionAvail().X;
        float h = ImGui.GetFrameHeight();
        var p = ImGui.GetCursorScreenPos();
        if (ImGui.InvisibleButton($"##grp{id}", new Vector2(w, h))) open = !open;
        bool hover = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        if (hover) dl.AddRectFilled(p, new Vector2(p.X + w, p.Y + h), ImGui.GetColorU32(GroupHover), 4f);
        uint tcol = ImGui.GetColorU32(GroupText);
        float midY = p.Y + h / 2f;
        float triX = p.X + 4f;
        if (open)
            dl.AddTriangleFilled(new Vector2(triX, midY - 3f), new Vector2(triX + 8f, midY - 3f), new Vector2(triX + 4f, midY + 3f), tcol);
        else
            dl.AddTriangleFilled(new Vector2(triX + 1f, midY - 4f), new Vector2(triX + 1f, midY + 4f), new Vector2(triX + 6f, midY), tcol);
        var lSz = ImGui.CalcTextSize(label);
        dl.AddText(new Vector2(p.X + 18f, midY - lSz.Y / 2f), tcol, label);
        if (!string.IsNullOrEmpty(meta))
        {
            var mSz = ImGui.CalcTextSize(meta);
            dl.AddText(new Vector2(p.X + w - mSz.X - 4f, midY - mSz.Y / 2f), ImGui.GetColorU32(GroupMeta), meta);
        }
        return open;
    }
}
