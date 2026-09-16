using System.Text;
using System.Text.RegularExpressions;

namespace ManhuaPipeline.Services;

/// <summary>
/// Stage 9 技能实体关系守卫：法天象地类技能（人物+法相+技能特效）统一为
/// 「人物本尊在前，法相巨相立于人物身后同轴同步动作」。
/// 改写生成提示词中「法相立于眉心」「法相在前」等错误空间关系，
/// 并在缺失正确关系表述时于最后一个时间块末尾追加实体关系约束。
/// </summary>
public static class PromptSkillEntityGuard
{
    private static readonly Regex ShotHeaderRegex = new(
        @"(?=【第\d+集】\s*【单元[\d.]+[a-zA-Z]?】\s*【镜头[\d.]+[a-zA-Z]?\-\d+】)");

    private static readonly Regex TimeBlockRegex = new(@"^\s*\[(\d+)-(\d+)\s*s\]", RegexOptions.Compiled);

    // 法相类技能关键词（人物+法相+特效类技能的特征词）
    private static readonly string[] EntitySkillMarkers =
    {
        "法相", "巨相", "法身", "道身", "神魔之躯"
    };

    // 错误空间关系表述 → 正确表述（仅当镜头含法相类关键词时替换）
    private static readonly (string Pattern, string Replacement)[] WrongRelationFixes =
    {
        ("立于眉心", "立于人物身后同轴"),
        ("自眉心", "自人物身后"),
        ("从眉心", "从人物身后"),
        ("于眉心", "于人物身后"),
        ("眉心之处", "身后"),
        ("眉心处", "身后"),
        ("眉心凝聚", "在人物身后凝聚"),
        ("眉心显现", "在人物身后显现"),
        ("眉心浮现", "在人物身后浮现"),
        ("眉心升腾", "在人物身后升腾"),
        ("眉心透出", "在人物身后透出"),
        ("眉心射出", "在人物身后射出"),
    };

    // 正确关系词：镜头正文出现任一即认为已符合标准，不再追加约束
    private static readonly string[] CorrectRelationMarkers =
    {
        "身后", "背后", "其后", "同轴", "后上方", "身后同轴同步"
    };

    public static string Apply(string promptText)
    {
        if (string.IsNullOrWhiteSpace(promptText)) return promptText;
        promptText = SeedancePromptParser.NormalizeEpisodeMarkers(promptText);
        var parts = ShotHeaderRegex.Split(promptText);
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            builder.Append(ProcessShot(part));
        }
        return builder.ToString();
    }

    private static string ProcessShot(string shotText)
    {
        if (!EntitySkillMarkers.Any(m => shotText.Contains(m, StringComparison.Ordinal)))
            return shotText;

        // 1) 改写错误空间关系
        var fixedText = shotText;
        foreach (var (pattern, replacement) in WrongRelationFixes)
        {
            if (fixedText.Contains(pattern, StringComparison.Ordinal))
                fixedText = fixedText.Replace(pattern, replacement);
        }

        // 2) 若仍无正确关系表述，在最后一个时间块末尾追加标准关系约束
        if (CorrectRelationMarkers.Any(m => fixedText.Contains(m, StringComparison.Ordinal)))
            return fixedText;

        var lines = fixedText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
        var lastTimeBlockIdx = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (TimeBlockRegex.IsMatch(lines[i])) lastTimeBlockIdx = i;
        }
        if (lastTimeBlockIdx < 0) return fixedText;

        var suffix = "；实体关系约束：人物本尊在前，法相巨相立于人物身后同轴同步动作，禁止法相出现在人物前方或眉心";
        lines[lastTimeBlockIdx] = lines[lastTimeBlockIdx].TrimEnd() + suffix;
        return string.Join("\n", lines);
    }
}
