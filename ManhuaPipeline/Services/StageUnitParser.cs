using System.Text.RegularExpressions;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services;

public static class StageUnitParser
{
    private const string FieldPattern =
        @"\r?\n\s*(?:类型|时长|地点|核心动作/情绪|起始状态|结束状态|对话/台词|关键元素)\s*[:：]";

    public static List<StageUnit> Parse(string text)
    {
        var result = new List<StageUnit>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        text = SeedancePromptParser.NormalizeEpisodeMarkers(text);
        var unitMatches = Regex.Matches(text, @"【单元([\d.]+[a-zA-Z]?)】").Cast<Match>().ToList();
        if (unitMatches.Count == 0) return result;

        for (var i = 0; i < unitMatches.Count; i++)
        {
            var match = unitMatches[i];
            var blockStart = match.Index;
            var blockEnd = i + 1 < unitMatches.Count ? unitMatches[i + 1].Index : text.Length;
            var block = text.Substring(blockStart, blockEnd - blockStart).Trim();
            block = Regex.Replace(block, @"(?m)^\s*#{0,6}\s*【第\s*[一二三四五六七八九十百零\d]+\s*集】\s*$", "").Trim();
            if (block.Length == 0) continue;

            var unitNumber = match.Groups[1].Value.Trim();
            var unit = new StageUnit
            {
                UnitNumber = unitNumber,
                EpisodeNumber = GetEpisodeNumber(text, blockStart, unitNumber),
                Type = ExtractField(block, "类型"),
                Duration = ParseDuration(ExtractField(block, "时长")),
                Location = ExtractField(block, "地点"),
                CoreAction = ExtractField(block, "核心动作/情绪"),
                StartState = ExtractField(block, "起始状态"),
                EndState = ExtractField(block, "结束状态"),
                Dialogue = ExtractField(block, "对话/台词"),
                KeyElements = ExtractField(block, "关键元素"),
                DirectorTemplate = ExtractDirectorTemplate(block),
                RawText = block
            };
            // 时长由分集细化决定：5 秒同样允许承载多个快速攻防回合，禁止人为升档或降频。
            result.Add(unit);
        }

        return result;
    }

    public static string ToPlanText(StageUnit unit)
    {
        var ep = unit.EpisodeNumber;
        if (ep <= 0 && unit.UnitNumber.Contains('.'))
            int.TryParse(unit.UnitNumber.Split('.')[0], out ep);
        if (ep <= 0) ep = 1;
        var raw = Regex.Replace(unit.RawText.Trim(), @"(?m)^\s*#{0,6}\s*【第\s*[一二三四五六七八九十百零\d]+\s*集】\s*$", "").Trim();
        return "【第" + ep + "集】\n" + raw;
    }

    private static int GetEpisodeNumber(string text, int unitStart, string unitNumber)
    {
        var before = text.Substring(0, unitStart);
        var eps = Regex.Matches(before, @"【第(\d+)集】");
        if (eps.Count > 0)
            return int.Parse(eps[eps.Count - 1].Groups[1].Value);
        var first = unitNumber.Split('.')[0];
        return int.TryParse(first, out var n) ? n : 0;
    }

    private static string ExtractField(string block, string label)
    {
        var pattern = label + @"\s*[:：]\s*(.*?)(?=" + FieldPattern + @"|\z)";
        var match = Regex.Match(block, pattern, RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    private static string ExtractDirectorTemplate(string block)
    {
        var match = Regex.Match(block, @"指定模板\s*[:：]\s*([^\r\n]+)");
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    private static int ParseDuration(string text)
    {
        var match = Regex.Match(text ?? "", @"(\d+)\s*秒");
        return match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : 0;
    }

}
