using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ManhuaPipeline.Services;

/// <summary>
/// Stage 9 内心独白兜底：把「旁白/画外音」说话前缀转成「内心独白-角色名」，
/// 并保证含内心独白的时间块写明角色闭嘴（嘴巴闭合/嘴唇紧抿），避免生成开口口型。
/// </summary>
public static class PromptInnerMonologueGuard
{
    private static readonly Regex ShotRegex = new(
        @"(?=【第\d+集】\s*【单元[\d.]+[a-zA-Z]?】\s*【镜头[\d.]+[a-zA-Z]?\-\d+】)",
        RegexOptions.Compiled);

    private static readonly Regex BlockStartRegex = new(
        @"^\[(?<start>\d+(?:\.\d+)?)\s*[-–]\s*(?<end>\d+(?:\.\d+)?)\s*s\]",
        RegexOptions.Compiled);

    private static readonly Regex NarrationQuoteRegex = new(
        @"(?:旁白|画外音)(?:\s*说)?\s*[:：]?\s*(?=[“""])",
        RegexOptions.Compiled);

    private static readonly Regex NarrationColonRegex = new(
        @"(?:旁白|画外音)(?:\s*说)?\s*[:：]",
        RegexOptions.Compiled);

    private static readonly Regex NarrationQuoteNormalizeRegex = new(
        @"(内心独白[^：:]*：)[""]([^""]*?)[""]",
        RegexOptions.Compiled);

    private static readonly Regex InnerMonoRegex = new(
        @"内心独白\s*[-－—]?\s*([^：:，。；;“”""]+)",
        RegexOptions.Compiled);

    private static readonly string[] MouthClosedKeywords =
    {
        "嘴巴闭合", "嘴唇紧抿", "闭嘴", "未开口", "不开口", "未出声", "不出声", "无声"
    };

    public static string Apply(string promptText)
    {
        if (string.IsNullOrWhiteSpace(promptText)) return promptText;
        promptText = SeedancePromptParser.NormalizeEpisodeMarkers(promptText);
        var parts = ShotRegex.Split(promptText);
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
        var mainChar = ExtractMainCharacter(shotText);
        var lines = shotText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var changed = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!BlockStartRegex.IsMatch(line.TrimStart())) continue;
            if (line.IndexOf("旁白", StringComparison.Ordinal) < 0
                && line.IndexOf("画外音", StringComparison.Ordinal) < 0
                && line.IndexOf("内心独白", StringComparison.Ordinal) < 0)
                continue;

            var rebuilt = line;
            if (mainChar.Length > 0)
            {
                rebuilt = NarrationQuoteRegex.Replace(rebuilt, "内心独白-" + mainChar + "：");
                rebuilt = NarrationColonRegex.Replace(rebuilt, "内心独白-" + mainChar + "：");
                rebuilt = Regex.Replace(rebuilt, @"内心独白\s*[-－—]?\s*主角(?=[：:“”""])", "内心独白-" + mainChar);
                rebuilt = NarrationQuoteNormalizeRegex.Replace(rebuilt, "$1“$2”");
            }

            var monoMatch = InnerMonoRegex.Match(rebuilt);
            if (monoMatch.Success)
            {
                var name = monoMatch.Groups[1].Value.Trim();
                if (name.Length > 0
                    && !MouthClosedKeywords.Any(k => rebuilt.Contains(k, StringComparison.Ordinal)))
                {
                    rebuilt += "，" + name + "嘴巴闭合、嘴唇紧抿、未开口，仅以眼神与表情传达内心情绪";
                }
            }

            if (!string.Equals(rebuilt, line, StringComparison.Ordinal))
            {
                lines[i] = rebuilt;
                changed = true;
            }
        }
        return changed ? string.Join("\n", lines) : shotText;
    }

    private static string ExtractMainCharacter(string shotText)
    {
        foreach (var line in shotText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            if (!SeedanceAtImageLine.IsAtImageLine(line)) continue;
            var person = SeedanceAtImageLine.Parse(line)
                .FirstOrDefault(s => s.Category == "人物" && s.Name != "光影质感");
            if (person != null) return person.Name;
        }
        return "";
    }
}
