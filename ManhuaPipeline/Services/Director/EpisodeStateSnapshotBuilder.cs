using System.Text.Json;
using System.Text.RegularExpressions;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services.Director;

/// <summary>
/// Director V4 第二批：把已通过校验的分镜落成结构化 Unit 结束快照。
/// 快照以整集导演的 UnitEndState 为底座，用真实分镜的最后一个镜头覆盖关键事实。
/// </summary>
public static class EpisodeStateSnapshotBuilder
{
    private static readonly Regex FieldValueRegex = new(
        @"^\s*[-–—*\s]*\s*(?:\*\*)?(?<field>结束画面|场景|运镜|特效|灯光)(?:\*\*)?\s*[:：]\s*(?<value>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ShotHeaderRegex = new(
        @"^\s*[-–—*\s]*\s*(?:\*\*)?(?:镜头|Shot)\s*[\d.]+(?:-\d+)?\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static EpisodeUnitStateSnapshot Build(
        int projectId,
        StageUnit unit,
        EpisodeDirectorPlan? episodePlan,
        DirectorPlan? plan,
        string storyboard)
    {
        var episodeNumber = GetEpisodeNumber(unit);
        var state = FindEndState(episodePlan, unit) ?? new UnitEndState { UnitNumber = unit.UnitNumber };
        var lastShot = GetLastShot(storyboard);

        var endScene = ExtractField(lastShot, "结束画面");
        var scene = ExtractField(lastShot, "场景");
        var camera = ExtractField(lastShot, "运镜");
        var vfx = ExtractField(lastShot, "特效");

        state.LastActionState = FirstNonEmpty(
            endScene,
            ParseActionPlanEndingState(plan?.ActionPlan),
            unit.EndState,
            state.LastActionState,
            plan?.ActionStrategy,
            unit.CoreAction);
        state.EnvironmentState = FirstNonEmpty(
            scene,
            endScene,
            state.EnvironmentState,
            unit.EndState);
        state.CameraDirection = FirstNonEmpty(
            camera,
            plan?.CameraStrategy,
            state.CameraDirection);
        state.ActiveVfxState = FirstNonEmpty(
            vfx,
            plan?.VfxStrategy,
            state.ActiveVfxState);

        var nextTransition = episodePlan?.UnitTransitions?.FirstOrDefault(t =>
            string.Equals(
                EpisodeDirectorPlanParser.NormalizeUnitToken(t.FromUnit),
                EpisodeDirectorPlanParser.NormalizeUnitToken(unit.UnitNumber),
                StringComparison.OrdinalIgnoreCase));
        state.EmotionalCarry = FirstNonEmpty(
            state.EmotionalCarry,
            nextTransition?.CarryEmotion);
        state.NarrativeCarry = FirstNonEmpty(
            state.NarrativeCarry,
            nextTransition?.NarrativeCarry,
            unit.EndState);

        return new EpisodeUnitStateSnapshot
        {
            ProjectId = projectId,
            EpisodeNumber = episodeNumber,
            UnitNumber = unit.UnitNumber,
            Source = "storyboard",
            State = state
        };
    }

    private static UnitEndState? FindEndState(EpisodeDirectorPlan? episodePlan, StageUnit unit)
    {
        if (episodePlan == null || string.IsNullOrWhiteSpace(unit.UnitNumber)) return null;
        return episodePlan.UnitEndStates?.FirstOrDefault(s =>
            string.Equals(
                EpisodeDirectorPlanParser.NormalizeUnitToken(s.UnitNumber),
                EpisodeDirectorPlanParser.NormalizeUnitToken(unit.UnitNumber),
                StringComparison.OrdinalIgnoreCase));
    }

    private static int GetEpisodeNumber(StageUnit unit)
    {
        if (unit.EpisodeNumber > 0) return unit.EpisodeNumber;
        if (unit.UnitNumber.Contains('.'))
        {
            var first = unit.UnitNumber.Split('.')[0];
            if (int.TryParse(first, out var ep)) return ep;
        }
        return 0;
    }

    private static string GetLastShot(string storyboard)
    {
        if (string.IsNullOrWhiteSpace(storyboard)) return "";
        var lines = storyboard.Replace("\r\n", "\n").Split('\n');
        var current = new List<string>();
        var last = new List<string>();
        foreach (var rawLine in lines)
        {
            if (ShotHeaderRegex.IsMatch(rawLine))
            {
                last = current;
                current = new List<string>();
            }
            current.Add(rawLine);
        }
        if (current.Count > 0) last = current;
        return string.Join("\n", last);
    }

    private static string ExtractField(string shot, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(shot)) return "";
        var result = "";
        foreach (var line in shot.Replace("\r\n", "\n").Split('\n'))
        {
            var match = FieldValueRegex.Match(line);
            if (!match.Success || !string.Equals(match.Groups["field"].Value.Trim(), fieldName, StringComparison.OrdinalIgnoreCase))
                continue;
            var value = match.Groups["value"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(value)) result = value;
        }
        return result;
    }

    private static string ParseActionPlanEndingState(string? actionPlanJson)
    {
        if (string.IsNullOrWhiteSpace(actionPlanJson)) return "";
        try
        {
            using var doc = JsonDocument.Parse(actionPlanJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("endingState", out var endingState) &&
                endingState.ValueKind == JsonValueKind.String)
            {
                return endingState.GetString() ?? "";
            }
        }
        catch
        {
            // 旧数据 ActionPlan 可能是非 JSON 文本，忽略即可。
        }
        return "";
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return "";
    }
}
