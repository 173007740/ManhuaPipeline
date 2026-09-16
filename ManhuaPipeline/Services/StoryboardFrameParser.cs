using System.Linq;
using System.Text.RegularExpressions;

namespace ManhuaPipeline.Services;

/// <summary>
/// 分镜文本中的镜头标题识别。只匹配行首的镜头标题，
/// 避免把“承接镜头1结束画面”这类正文误判成新镜头。
/// </summary>
public static class StoryboardFrameParser
{
    private static readonly Regex ShotNumberFieldRegex = new(
        @"^\s*[-–—*\s]*\s*(?:\*\*)?镜头编号(?:\*\*)?\s*[:：]\s*([\d.]+(?:-\d+)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ShotHeaderRegex = new(
        @"^\s*[-–—*\s]*\s*【?镜头\s*([\d.]+(?:-\d+)?)\s*】?\s*(?:[:：]|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ShotBracketRegex = new(
        @"^\s*[#\-–—*\s]*\s*【镜头\s*([\d.]+(?:-\d+)?)\s*】\s*(?:[:：]|[（(]|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ShotWordRegex = new(
        @"^\s*[-–—*\s]*\s*Shot\s*([\d.]+(?:-\d+)?)\s*(?:[:：]|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CombatBeatValueRegex = new(
        @"^\s*[-–—*\s]*\s*(?:\*\*)?(?:节拍序号|战斗节拍|节拍|CombatBeatIndex)(?:\*\*)?\s*[:：]\s*(.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BeatPrefixedNumberRegex = new(
        @"Beat\s*(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BareBeatNumberRegex = new(
        @"(?<=^|[\s,，、;；和及])(\d+)(?=$|[\s,，、;；和及])",
        RegexOptions.Compiled);

    private static readonly Regex CombatBeatListWholeTextRegex = new(
        @"^(?:Beat\s*)?\d+(?:\s*[,，、;；和及]\s*(?:Beat\s*)?\d+)*\s*(?:[（(\[].*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string? TryGetShotNumber(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var match = ShotNumberFieldRegex.Match(line);
        if (match.Success) return match.Groups[1].Value.Trim();

        match = ShotHeaderRegex.Match(line);
        if (match.Success) return match.Groups[1].Value.Trim();

        match = ShotBracketRegex.Match(line);
        if (match.Success) return match.Groups[1].Value.Trim();

        match = ShotWordRegex.Match(line);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    /// <summary>整段分镜是否至少包含一个可识别的镜头标题，用于丢弃“0 镜头”废响应。</summary>
    public static bool HasAnyShot(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Replace("\r\n", "\n")
            .Split('\n')
            .Any(line => TryGetShotNumber(line) != null);
    }

    /// <summary>解析单拍或多拍，返回去重排序后的编号列表，如 "1,3"；无法识别时返回 null。</summary>
    public static string? TryGetCombatBeatIds(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var text = line.Trim();
        var field = CombatBeatValueRegex.Match(text);
        if (field.Success) return ParseBeatTokenList(field.Groups[1].Value.Trim());
        // 非字段行必须整行都是节拍编号，避免把模板行/正文里的数字误判成 Beat。
        if (!CombatBeatListWholeTextRegex.IsMatch(text)) return null;
        return ParseBeatTokenList(text);

    }

    private static string? ParseBeatTokenList(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var ids = new List<int>();
        foreach (Match m in BeatPrefixedNumberRegex.Matches(text))
        {
            if (int.TryParse(m.Groups[1].Value, out var index) && index > 0) ids.Add(index);
        }
        foreach (Match m in BareBeatNumberRegex.Matches(text))
        {
            if (int.TryParse(m.Groups[1].Value, out var index) && index > 0) ids.Add(index);
        }
        ids = ids.Distinct().OrderBy(x => x).ToList();
        return ids.Count == 0 ? null : string.Join(",", ids);
    }

    public static int? TryGetCombatBeatIndex(string line)
    {
        var ids = TryGetCombatBeatIds(line);
        if (ids != null && int.TryParse(ids.Split(',')[0], out var index)) return index;
        return null;
    }
}
