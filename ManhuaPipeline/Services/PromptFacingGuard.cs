using System.Text;
using System.Text.RegularExpressions;

namespace ManhuaPipeline.Services;

/// <summary>
/// Stage 9 对峙站位朝向守卫：打斗/对峙/对话类镜头若时间块正文缺少「正面相对、彼此相向」等
/// 相对朝向关系词，自动在最后一个时间块末尾追加双方相对站位约束，
/// 避免视频生成中角色各摆各的朝向（背对背、侧身错位）。
/// </summary>
public static class PromptFacingGuard
{
    private static readonly Regex ShotHeaderRegex = new(
        @"(?=【第\d+集】\s*【单元[\d.]+[a-zA-Z]?】\s*【镜头[\d.]+[a-zA-Z]?\-\d+】)");

    private static readonly Regex TimeBlockRegex = new(@"^\s*\[(\d+)-(\d+)\s*s\]", RegexOptions.Compiled);

    private static readonly Regex TypeLineRegex = new(@"类型\s*[:：]\s*([^\r\n]+)");

    // 只对明确的正面交战类镜头强制站位，排除追逐/逃亡（一前一后追击未必对峙）
    private static readonly string[] CombatTypes =
    {
        "打斗/动作", "高潮/对决"
    };

    // 已存在相对朝向关系词（出现任一即不再追加）
    private static readonly string[] FacingRelationMarkers =
    {
        "相对而立", "相向而立", "面对面", "彼此面对", "正面相对", "目光相对",
        "视线相接", "四目相对", "相对而峙", "相向而峙", "相对峙", "怒目而视",
        "正面相峙", "对峙而立", "相对而坐", "相向而对", "迎面"
    };

    // 对峙/交战语义词（正文命中任一即视为需要站位约束）
    private static readonly string[] ConfrontationMarkers =
    {
        "对峙", "对望", "对视", "交手", "对战", "战斗", "打斗", "拼杀", "压制",
        "结印", "蓄势", "法相", "相峙", "对决", "轰击", "对拼", "迎击"
    };

    // 群像/背景类角色名标记，不参与「面对面」核心主体选择
    private static readonly string[] GroupMarkers =
    {
        "宗", "门", "派", "帮", "众", "群", "弟子", "长老", "阁", "族", "军", "队", "像", "群像"
    };

    public static string Apply(string promptText, IEnumerable<string>? charNames)
    {
        if (string.IsNullOrWhiteSpace(promptText)) return promptText;

        var names = (charNames ?? Array.Empty<string>())
            .Select(n => (n ?? "").Trim())
            .Where(n => n.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count < 2) return promptText;

        promptText = SeedancePromptParser.NormalizeEpisodeMarkers(promptText);
        var parts = ShotHeaderRegex.Split(promptText);
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            builder.Append(ProcessShot(part, names));
        }
        return builder.ToString();
    }

    private static string ProcessShot(string shotText, List<string> names)
    {
        var typeLine = TypeLineRegex.Match(shotText);
        var isCombat = typeLine.Success && CombatTypes.Any(t => typeLine.Groups[1].Value.Contains(t, StringComparison.OrdinalIgnoreCase));
        var confrontation = isCombat || ConfrontationMarkers.Any(m => shotText.Contains(m, StringComparison.Ordinal));
        if (!confrontation) return shotText;

        // 已存在相对朝向关系词则不处理
        if (FacingRelationMarkers.Any(m => shotText.Contains(m, StringComparison.Ordinal))) return shotText;

        // 只保留核心角色名（过滤群像/背景类），取正文中实际出镜、出现次数最多的前两名
        var coreNames = names
            .Where(n => !GroupMarkers.Any(g => n.Contains(g, StringComparison.Ordinal)))
            .ToList();
        if (coreNames.Count < 2) return shotText;

        var body = Regex.Replace(shotText, @"@图\s*\d+\s*\[[^\]\r\n]*\][^\r\n]*", " ");
        var onScreen = coreNames
            .Select(n => new { Name = n, Count = Regex.Matches(body, Regex.Escape(n)).Count })
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count)
            .Take(2)
            .Select(x => x.Name)
            .ToList();
        if (onScreen.Count < 2) return shotText;

        // 在最后一个时间块正文末尾追加相对站位约束
        var lines = shotText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
        var lastTimeBlockIdx = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (TimeBlockRegex.IsMatch(lines[i])) lastTimeBlockIdx = i;
        }
        if (lastTimeBlockIdx < 0) return shotText;

        var suffix = "；站位朝向约束：" + onScreen[0] + "与" + onScreen[1] +
                     "保持正面相对、彼此相向，视线相接，禁止背对背站立（除非分镜明确要求背面出场）";
        lines[lastTimeBlockIdx] = lines[lastTimeBlockIdx].TrimEnd() + suffix;
        return string.Join("\n", lines);
    }
}
