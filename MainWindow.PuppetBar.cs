using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;

namespace HDM;

// b55: the tear-off BAR (DM: "hms has a small tear off bar for carpet feature, maybe we should do the same for
// possession? freeze, resize, despawn, release?"). A small floating window, drawn from its own UiBuilder.Draw hook so
// it stays up with HDM's main window closed. Its subject: the possessed puppet while driving, else the Stage's subject
// (one puppet, or you). < > steps through You and every puppet. b56: it covers YOU too (DM: the bar didn't appear while
// disguised as a colossus with no puppets out): Revert / Freeze / Hide, size, height, Wisp.
// Pops out when a disguise or a possession starts (Config > Possession), from the driving banner's "Bar", the cast chip's
// right-click, or /hdm bar. Every control goes through the same funnels the Catalog and Stage use, so peers see the same.
public sealed partial class MainWindow
{
    private bool _showPuppetBar;
    private bool _barSawPossession;   // possession-start edge for the auto pop-out
    private bool _barSawGuise;        // disguise-start edge for the auto pop-out
    private bool _focusCatalogTab;

    /// <summary>/hdm bar.</summary>
    public string CommandBar()
    {
        _showPuppetBar = !_showPuppetBar;
        return _showPuppetBar ? "bar on." : "bar off.";
    }

    public void DrawPuppetBar()
    {
        var self = Self();
        var possessing = _possession.IsPossessing;
        var guised = self != null && (_guise.IsGuised(self.ObjectIndex) || _humanGuise.IsGuised(self.ObjectIndex));
        // Auto pop-out on the start edges (runs even with the main window closed).
        if (_config.PuppetBarOnPossess && ((possessing && !_barSawPossession) || (guised && !_barSawGuise))) _showPuppetBar = true;
        _barSawPossession = possessing;
        _barSawGuise = guised;

        if (!_showPuppetBar || self == null) return;

        // Subject: the possessed puppet, else the Stage's puppet subject, else you.
        ICharacter subject = self;
        if (possessing && _objects[_possession.PossessedIndex] is ICharacter driven) subject = driven;
        else if (_animApplyToPuppet && FocusedPuppet() is { } fp) subject = fp;
        var isSelf = subject.ObjectIndex == self.ObjectIndex;

        var acc = _accent.Primary;
        ImGui.PushStyleColor(ImGuiCol.TitleBg, new Vector4(acc.X * 0.32f, acc.Y * 0.32f, acc.Z * 0.32f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, new Vector4(acc.X * 0.55f, acc.Y * 0.55f, acc.Z * 0.55f, 1f));
        ImGui.SetNextWindowSize(new Vector2(320, 0), ImGuiCond.FirstUseEver);
        var open = ImGui.Begin("HDM##hdmbar", ref _showPuppetBar,
                               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse);
        ImGui.PopStyleColor(2);
        if (!open) { ImGui.End(); return; }
        try
        {
            DrawBarHeader(self, subject, isSelf, possessing);
            if (isSelf) DrawBarSelf(self, guised);
            else DrawBarPuppet(subject, (ushort)subject.ObjectIndex, possessing && _possession.PossessedIndex == subject.ObjectIndex);
        }
        finally { ImGui.End(); }
    }

    private const float BarGap = 6f;
    private float BarCol => (ImGui.GetContentRegionAvail().X - BarGap * 2f) / 3f;
    private float BarResetW => ImGui.CalcTextSize("1x").X + ImGui.GetStyle().FramePadding.X * 2f + 8f;

    // Header: status dot + who, and < > through You and the cast (not while driving: the bar follows the drive).
    private void DrawBarHeader(ICharacter self, ICharacter subject, bool isSelf, bool possessing)
    {
        var idx = (ushort)subject.ObjectIndex;
        var driving = possessing && _possession.PossessedIndex == idx;
        CastDot(idx);
        ImGui.AlignTextToFramePadding();
        if (driving) ImGui.TextColored(new Vector4(1f, 0.75f, 0.20f, 1f), $"Driving {PuppetName(idx)}");
        else if (isSelf) ImGui.TextUnformatted(_wornGuise != null && (_guise.IsGuised(idx) || _humanGuise.IsGuised(idx)) ? $"You · {_wornGuise.DisplayName}" : "You");
        else ImGui.TextUnformatted(PuppetName(idx));

        var cast = _spawn.Spawned.ToList();
        if (driving || cast.Count == 0) return;
        var arrowW = ImGui.GetFrameHeight();
        ImGui.SameLine(ImGui.GetContentRegionMax().X - arrowW * 2f - BarGap);
        // Ring: You, puppet 1, puppet 2, ...
        var at = isSelf ? -1 : cast.IndexOf(idx);
        void Go(int i) { if (i < 0) SelectSelf(); else SelectPuppet(cast[i]); }
        if (ImGui.ArrowButton("##barprev", ImGuiDir.Left)) Go(at == -1 ? cast.Count - 1 : at - 1);
        ImGui.SameLine(0, BarGap);
        if (ImGui.ArrowButton("##barnext", ImGuiDir.Right)) Go(at + 1 >= cast.Count ? -1 : at + 1);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Step through you and your {cast.Count} puppet{(cast.Count == 1 ? "" : "s")}.");
    }

    private void DrawBarSelf(ICharacter self, bool guised)
    {
        var acc = _accent.Primary;
        var idx = (ushort)self.ObjectIndex;
        var colW = BarCol;

        // Row 1: Revert · Freeze · Reset (b57, DM: Reset in Hide's place)
        using (new Disabled(!guised))
        {
            PushRed();
            if (ImGui.Button("Revert##bar", new Vector2(colW, 0f)) && guised) RevertGuise(self);
            PopColors();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(guised ? "Drop the disguise: your real model and size again." : "Not disguised.");
        ImGui.SameLine(0, BarGap);
        var frozen = _anim.IsFrozen(idx);
        if (HmUi.AccentButton(frozen ? "Frozen" : "Freeze", "barfreeze", frozen, acc, colW)) ApplyFreeze(self, !frozen);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(frozen ? "Resume your animation." : "Hold on the current frame.");
        ImGui.SameLine(0, BarGap);
        if (ImGui.Button("Reset##bar", new Vector2(colW, 0f))) ResetToNormal(self);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Back to a normal, unlocked idle (clears a stuck or held animation; keeps the look).");

        // Row 2: size. Your size follows the Catalog's Scale setting, so the bar writes the same setting. A human disguise
        // (or none) is sized by Glamourer, not here.
        var monster = _guise.IsGuised(idx);
        var resetW = BarResetW;
        var sliderW = ImGui.GetContentRegionAvail().X - resetW - BarGap;
        using (new Disabled(!monster))
        {
            var sc = monster ? _guise.GetScale(self) : 1f;
            ImGui.SetNextItemWidth(sliderW);
            if (ImGui.SliderFloat("##barscale", ref sc, 0.1f, 10f, "size x%.2f") && monster)
            {
                _config.ScaleMode = 2;
                _config.ScaleCustom = sc;
                ApplyScaleLive(commit: false);
            }
            if (ImGui.IsItemDeactivatedAfterEdit() && monster) { _pi.SavePluginConfig(_config); ApplyScaleLive(commit: true); }
            ImGui.SameLine(0, BarGap);
            if (ImGui.Button("1x##barscale", new Vector2(resetW, 0f)) && monster) SetCustom(1f);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(monster ? "Back to x1." : "Size works on a monster or demihuman disguise.");

        // Row 3: height
        DrawBarHeight(self, idx, sliderW, resetW);

        // Row 4: Release · Catalog · Stage (b57, DM: Release in Wisp's place)
        var possessing = _possession.IsPossessing;
        using (new Disabled(!possessing))
        {
            PushRed();
            if (ImGui.Button("Release##barself", new Vector2(colW, 0f)) && possessing) _possession.Release();
            PopColors();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(possessing ? "Stop driving the puppet (Esc does the same)." : "Not driving a puppet.");
        ImGui.SameLine(0, BarGap);
        if (ImGui.Button("Catalog##bar", new Vector2(colW, 0f))) { IsOpen = true; _focusCatalogTab = true; }
        ImGui.SameLine(0, BarGap);
        if (ImGui.Button("Stage##bar", new Vector2(colW, 0f))) { SelectSelf(); IsOpen = true; _focusStageTab = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Open HDM's Stage on you (animations, poses, everything else).");
    }

    private void DrawBarPuppet(ICharacter pup, ushort idx, bool driving)
    {
        var acc = _accent.Primary;
        var colW = BarCol;

        // Row 1: Possess/Release · Freeze · Despawn
        if (driving)
        {
            PushRed();
            if (ImGui.Button("Release##bar", new Vector2(colW, 0f))) _possession.Release();
            PopColors();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Stop driving it (Esc does the same). It stays where you left it.");
        }
        else
        {
            var can = _possession.CanPossess(idx);
            using (new Disabled(!can))
                if (HmUi.AccentButton("Possess", "barposs", true, acc, colW) && can) { SelectPuppet(idx); _possession.Possess(idx); }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(can ? "Drive it with WASD from an orbit camera. Space up · Ctrl down · Esc release."
                                     : "Another player's puppet. Config > Possession can allow driving others' puppets.");
        }
        ImGui.SameLine(0, BarGap);
        var frozen = _anim.IsFrozen(idx);
        if (HmUi.AccentButton(frozen ? "Frozen" : "Freeze", "barfreeze", frozen, acc, colW))
        {
            _puppetSpeed[idx] = frozen ? 1f : 0f;
            ApplyFreeze(pup, !frozen);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(frozen ? "Resume its animation." : "Hold it on the current frame.");
        ImGui.SameLine(0, BarGap);
        PushRed();
        var despawn = ImGui.Button("Despawn##bar", new Vector2(colW, 0f));
        PopColors();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Despawn this puppet (releases it first if you're driving it).");

        // Row 2: size (live while dragging, synced on release) + 1x
        var resetW = BarResetW;
        var sliderW = ImGui.GetContentRegionAvail().X - resetW - BarGap;
        var scl = _puppetScale.TryGetValue(idx, out var scv) ? scv : _guise.GetScale(pup);
        ImGui.SetNextItemWidth(sliderW);
        if (ImGui.SliderFloat("##barscale", ref scl, 0.1f, 10f, "size x%.2f")) { _puppetScale[idx] = scl; ApplyScaleTo(pup, scl, commit: false); }
        if (ImGui.IsItemDeactivatedAfterEdit()) ApplyScaleTo(pup, scl, commit: true);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Resize (0.1x to 10x). Ctrl+click to type a value.");
        ImGui.SameLine(0, BarGap);
        if (ImGui.Button("1x##barscale", new Vector2(resetW, 0f))) { _puppetScale[idx] = 1f; ApplyScaleTo(pup, 1f, commit: true); }

        // Row 3: height
        DrawBarHeight(pup, idx, sliderW, resetW);

        // Row 4: Bring to me · Face me · Stage (not while driving: WASD owns the position then)
        using (new Disabled(driving))
        {
            if (ImGui.Button("Bring to me##bar", new Vector2(colW, 0f)))
            { _spawn.MoveToLocalPlayer(idx); if (_spawn.TryGetTransform(idx, out var p, out var r)) ReportPuppetMove(idx, p, r); }
            ImGui.SameLine(0, BarGap);
            if (ImGui.Button("Face me##bar", new Vector2(colW, 0f)))
            { _spawn.FaceLocalPlayer(idx); if (_spawn.TryGetTransform(idx, out var p, out var r)) ReportPuppetMove(idx, p, r); }
        }
        ImGui.SameLine(0, BarGap);
        if (ImGui.Button("Stage##bar", new Vector2(colW, 0f))) { SelectPuppet(idx); IsOpen = true; _focusStageTab = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Open HDM's Stage on this puppet (animations, looks, everything else).");

        // Act on despawn last, after every control above has used the live puppet this frame.
        if (despawn)
        {
            if (driving) _possession.Release();
            DespawnPuppet(idx);
        }
    }

    private void DrawBarHeight(ICharacter who, ushort idx, float sliderW, float resetW)
    {
        var voff = _guise.GetVerticalOffset(idx);
        ImGui.SetNextItemWidth(sliderW);
        if (ImGui.SliderFloat("##barelev", ref voff, -20f, 20f, "height %+.2f")) ApplyElevationTo(who, voff, commit: false);
        if (ImGui.IsItemDeactivatedAfterEdit()) ApplyElevationTo(who, voff, commit: true);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Raise or lower where the model is drawn. Ctrl+click to type a value.");
        ImGui.SameLine(0, BarGap);
        if (ImGui.Button("0##barelev", new Vector2(resetW, 0f))) ApplyElevationTo(who, 0f, commit: true);
    }
}
