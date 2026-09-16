using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services.Director;

/// <summary>
/// Director V2 RepairPlanner：把结构化违规转成定向返工反馈。
/// 核心是“保留合规镜头，只修复违规 Beat/Shot”，而不是整段重新随机生成。
/// </summary>
public static class DirectorRepairPlanner
{
    public static List<string> BuildRepairFeedback(DirectorValidationResult validation, string originalText)
    {
        if (validation == null || validation.Violations.Count == 0) return [];

        var feedback = new List<string>();
        var shotIds = ExtractShotIds(originalText);
        if (shotIds.Count > 0)
        {
            feedback.Add(
                "保留以下镜头（不得删除或整体重写）：" + string.Join("、", shotIds) +
                "；仅在这些镜头之间/结尾补充或修改违规内容。");
        }

        foreach (var violation in validation.Violations)
        {
            var prefix = string.Equals(violation.Severity, "Error", StringComparison.OrdinalIgnoreCase)
                ? "[违规]"
                : "[警告]";
            var line = $"{prefix} {violation.Message}";
            if (!string.IsNullOrWhiteSpace(violation.Expected))
                line += $"（期望:{violation.Expected}）";
            if (!string.IsNullOrWhiteSpace(violation.Actual))
                line += $"（实际:{violation.Actual}）";
            if (!string.IsNullOrWhiteSpace(violation.RepairInstruction))
                line += "；修复:" + violation.RepairInstruction;
            feedback.Add(line);
        }

        return feedback;
    }

    private static List<string> ExtractShotIds(string text)
    {
        var ids = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return ids;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var shotId = StoryboardFrameParser.TryGetShotNumber(line);
            if (shotId == null || ids.Contains(shotId, StringComparer.OrdinalIgnoreCase)) continue;
            ids.Add(shotId);
        }
        return ids;
    }
}
