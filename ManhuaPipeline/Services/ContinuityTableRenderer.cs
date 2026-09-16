using System.Text;
using System.Text.Json;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services;

/// <summary>
/// 把连续性表的结构化内容渲染成可注入下游提示词的纯文本。
/// 渲染结果同时写入 ProjectContinuityTables.ContentText，下游直接取用、无需重复渲染。
/// </summary>
public static class ContinuityTableRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>按表类型渲染（入参为落库的 ContentJson）。解析失败或为空返回空字符串。</summary>
    public static string Render(string tableType, string? contentJson)
    {
        if (string.IsNullOrWhiteSpace(contentJson) || contentJson == "{}") return "";
        try
        {
            return tableType switch
            {
                ContinuityTableTypes.SceneSpace => RenderSceneSpace(Deserialize<SceneSpaceEntry>(contentJson)),
                ContinuityTableTypes.CharacterContinuity => RenderCharacterContinuity(Deserialize<CharacterContinuityEntry>(contentJson)),
                ContinuityTableTypes.PropState => RenderPropState(Deserialize<PropStateTimelineEntry>(contentJson)),
                ContinuityTableTypes.ClueReveal => RenderClueReveal(Deserialize<ClueRevealEntry>(contentJson)),
                ContinuityTableTypes.ActionCausality => RenderActionCausality(Deserialize<ActionCausalityEntry>(contentJson)),
                ContinuityTableTypes.TransitionMotive => RenderTransitionMotive(Deserialize<TransitionMotiveEntry>(contentJson)),
                _ => ""
            };
        }
        catch
        {
            return "";
        }
    }

    private static List<T> Deserialize<T>(string json) => JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? new List<T>();

    public static string RenderSceneSpace(IEnumerable<SceneSpaceEntry>? entries)
    {
        var list = entries?.Where(e => e != null && !string.IsNullOrWhiteSpace(e.Scene)).ToList() ?? new List<SceneSpaceEntry>();
        if (list.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var e in list)
        {
            sb.AppendLine("- 【场景】" + e.Scene.Trim());
            AppendField(sb, "  左侧", e.Left);
            AppendField(sb, "  右侧", e.Right);
            AppendField(sb, "  中后方", e.MidBack);
            AppendField(sb, "  前景", e.Foreground);
            AppendField(sb, "  入口", e.Entrance);
            AppendField(sb, "  出口", e.Exit);
            AppendField(sb, "  危险方向", e.DangerDirection);
            AppendField(sb, "  逃生方向", e.EscapeDirection);
            AppendField(sb, "  动作轴线", e.ActionAxis);
            AppendList(sb, "  固定道具", e.FixedProps);
            AppendList(sb, "  禁止改变", e.ForbiddenChanges);
        }
        return sb.ToString().TrimEnd();
    }

    public static string RenderCharacterContinuity(IEnumerable<CharacterContinuityEntry>? entries)
    {
        var list = entries?.Where(e => e != null && !string.IsNullOrWhiteSpace(e.Character)).ToList() ?? new List<CharacterContinuityEntry>();
        if (list.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var e in list)
        {
            sb.AppendLine("- 【角色】" + e.Character.Trim());
            AppendField(sb, "  外貌锚定", e.AppearanceAnchor);
            AppendField(sb, "  服装", e.Costume);
            AppendField(sb, "  随身物", e.WeaponOrProp);
            AppendField(sb, "  伤势", e.InjuryState);
            AppendList(sb, "  禁止变化", e.ForbiddenChanges);
        }
        return sb.ToString().TrimEnd();
    }

    public static string RenderPropState(IEnumerable<PropStateTimelineEntry>? entries)
    {
        var list = entries?.Where(e => e != null && !string.IsNullOrWhiteSpace(e.Prop)).ToList() ?? new List<PropStateTimelineEntry>();
        if (list.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var e in list)
        {
            sb.AppendLine("- 【道具】" + e.Prop.Trim());
            var states = e.States?.Where(s => s != null && (!string.IsNullOrWhiteSpace(s.At) || !string.IsNullOrWhiteSpace(s.State))).ToList()
                         ?? new List<PropStatePoint>();
            if (states.Count > 0)
                sb.AppendLine("  状态：" + string.Join(" → ", states.Select(s => (string.IsNullOrWhiteSpace(s.At) ? "" : s.At.Trim() + " ") + (s.State ?? "").Trim()).Where(s => s.Length > 0)));
            AppendList(sb, "  必须出现", e.MustAppear);
            AppendList(sb, "  禁止", e.Forbidden);
        }
        return sb.ToString().TrimEnd();
    }

    public static string RenderClueReveal(IEnumerable<ClueRevealEntry>? entries)
    {
        var list = entries?.Where(e => e != null && !string.IsNullOrWhiteSpace(e.Clue)).OrderBy(e => e.RevealOrder).ToList()
                   ?? new List<ClueRevealEntry>();
        if (list.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var e in list)
        {
            sb.AppendLine("- 【线索" + e.RevealOrder + "】" + e.Clue.Trim());
            AppendField(sb, "  揭示位置", e.RevealAt);
            AppendField(sb, "  观众必须注意到", e.AudienceMustNotice);
            AppendField(sb, "  禁止提前", e.MustNotRevealBefore);
        }
        return sb.ToString().TrimEnd();
    }

    public static string RenderActionCausality(IEnumerable<ActionCausalityEntry>? entries)
    {
        var list = entries?.Where(e => e != null && !string.IsNullOrWhiteSpace(e.UnitNumber)).ToList() ?? new List<ActionCausalityEntry>();
        if (list.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var e in list)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(e.NewInformation)) parts.Add("新信息：" + e.NewInformation.Trim());
            if (!string.IsNullOrWhiteSpace(e.BecauseOf)) parts.Add("前因：" + e.BecauseOf.Trim());
            if (!string.IsNullOrWhiteSpace(e.LeadsTo)) parts.Add("导向：" + e.LeadsTo.Trim());
            if (parts.Count == 0) continue;
            sb.AppendLine("- " + e.UnitNumber.Trim() + "  " + string.Join(" ｜ ", parts));
        }
        return sb.ToString().TrimEnd();
    }

    public static string RenderTransitionMotive(IEnumerable<TransitionMotiveEntry>? entries)
    {
        var list = entries?.Where(e => e != null && (!string.IsNullOrWhiteSpace(e.FromUnit) || !string.IsNullOrWhiteSpace(e.ToUnit))).ToList()
                   ?? new List<TransitionMotiveEntry>();
        if (list.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var e in list)
        {
            var head = (e.FromUnit ?? "").Trim() + " → " + (e.ToUnit ?? "").Trim();
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(e.MotiveType)) parts.Add("动机：" + e.MotiveType.Trim());
            if (!string.IsNullOrWhiteSpace(e.Detail)) parts.Add("说明：" + e.Detail.Trim());
            sb.AppendLine("- " + head + "  " + string.Join(" ｜ ", parts));
        }
        return sb.ToString().TrimEnd();
    }

    private static void AppendField(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) sb.AppendLine(label + "：" + value.Trim());
    }

    private static void AppendList(StringBuilder sb, string label, List<string>? values)
    {
        var list = values?.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
        if (list == null || list.Count == 0) return;
        sb.AppendLine(label + "：" + string.Join("；", list));
    }
}
