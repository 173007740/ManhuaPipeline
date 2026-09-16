using System.Text;
using System.Text.RegularExpressions;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services;

/// <summary>
/// Stage 5 生成后的自动校验与修正：负责非法时长、误锁技能、
/// 体修错误术语和技能描述词污染，避免每集都要人工检查分镜。
/// </summary>
public sealed class StoryboardAutoFixer
{
    private static readonly HashSet<int> AllowedDurations = new() { 5, 11, 15 };
    private static readonly string[] GenericSkillTokens =
    {
        "七宗合击", "合击", "阵法", "法阵", "大招", "技能", "瞬移", "闪现", "灵力"
    };

    public string Fix(string text, string stage4Text, List<SkillLibraryItem>? skills, out List<string> fixes)
    {
        fixes = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return text;

        var skillNames = new HashSet<string>(
            skills?.Select(s => s.Name?.Trim() ?? "").Where(n => n.Length > 0)
                ?? Enumerable.Empty<string>(),
            StringComparer.Ordinal);

        var stage4Units = ParseStage4Units(stage4Text ?? "");
        var blocks = SplitUnits(text);

        if (blocks.Count == 0)
        {
            var fixedText = FixDurations(text, fixes);
            fixedText = FixPhrases(fixedText, new HashSet<string>(StringComparer.Ordinal), "", fixes);
            return fixedText;
        }

        var sb = new StringBuilder();
        foreach (var block in blocks)
        {
            stage4Units.TryGetValue(block.UnitNumber, out var stage4Unit);
            var unitText = block.Text;
            unitText = FixDurations(unitText, fixes);

            var (skillLine, lockedSkills) = BuildSkillLine(unitText, stage4Unit ?? "", skillNames, block.UnitNumber, fixes);
            unitText = ReplaceSkillLine(unitText, skillLine);
            unitText = FixPhrases(unitText, lockedSkills, block.UnitNumber, fixes);

            sb.Append(unitText);
        }

        return sb.ToString();
    }

    private static Dictionary<string, string> ParseStage4Units(string stage4Text)
    {
        return StageUnitParser.Parse(stage4Text)
            .GroupBy(u => u.UnitNumber, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => string.Join("\n", g.Select(u => u.RawText)),
                StringComparer.OrdinalIgnoreCase);
    }

    private static List<UnitBlock> SplitUnits(string text)
    {
        var matches = Regex.Matches(text, @"(?m)^\s*#{0,6}\s*【单元\s*([\d.]+[a-zA-Z]?)\s*】")
            .Cast<Match>()
            .ToList();
        var blocks = new List<UnitBlock>();
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            blocks.Add(new UnitBlock(matches[i].Groups[1].Value.Trim(), text.Substring(start, end - start)));
        }

        return blocks;
    }

    private static string FixDurations(string text, List<string> fixes)
    {
        return Regex.Replace(
            text,
            @"(?m)^(\s*-\s*\*\*(?:镜头)?时长\*\*\s*[:：]\s*)([\d.]+)\s*秒",
            match =>
            {
                var original = match.Groups[2].Value;
                if (!double.TryParse(original, out var seconds))
                    return match.Value;

                var rounded = (int)Math.Round(seconds);
                if (AllowedDurations.Contains(rounded))
                    return match.Value;

                var fixedSeconds = NearestDuration(rounded);
                fixes.Add($"镜头时长 {original}秒 修正为 {fixedSeconds}秒");
                return match.Groups[1].Value + fixedSeconds + "秒";
            });
    }

    private static int NearestDuration(int seconds)
    {
        if (seconds <= 7) return 5;
        if (seconds <= 13) return 11;
        return 15;
    }

    private static (string? SkillLine, HashSet<string> LockedSkills) BuildSkillLine(
        string unitText,
        string stage4Unit,
        HashSet<string> skillNames,
        string unitNumber,
        List<string> fixes)
    {
        var skillLinePattern = @"(?m)^\s*-\s*\*\*技能\*\*\s*[:：][^\r\n]*$";
        var bodyText = Regex.Replace(unitText, skillLinePattern, "");
        var existingLine = Regex.Match(unitText, @"(?m)^\s*-\s*\*\*技能\*\*\s*[:：]\s*(.*?)\s*$");
        var hadLine = existingLine.Success;
        var rawValue = hadLine ? existingLine.Groups[1].Value.Trim() : "";

        var unitQuoted = ExtractQuotedNames(bodyText, skillNames);
        var stage4Quoted = ExtractQuotedNames(stage4Unit, skillNames);
        var unitBracketed = ExtractBracketNames(bodyText);
        var stage4Bracketed = ExtractBracketNames(stage4Unit);

        var known = new HashSet<string>(skillNames, StringComparer.Ordinal);
        foreach (var q in stage4Quoted) known.Add(q);
        foreach (var b in stage4Bracketed) known.Add(b);

        var existingTokens = SplitSkillTokens(rawValue);
        var kept = new List<string>();
        var removed = new List<string>();
        foreach (var token in existingTokens)
        {
            if (GenericSkillTokens.Contains(token, StringComparer.Ordinal))
            {
                removed.Add(token);
                continue;
            }

            var isMentioned = IsMentioned(token, bodyText, stage4Unit, stage4Quoted, stage4Bracketed);
            var isKnown = known.Contains(token) || (isMentioned && !GenericSkillTokens.Contains(token, StringComparer.Ordinal));
            if (isKnown && isMentioned)
                kept.Add(token);
            else
                removed.Add(token);
        }

        var added = new List<string>();
        foreach (var q in unitQuoted.Concat(stage4Quoted).Distinct(StringComparer.Ordinal))
        {
            if (kept.Contains(q, StringComparer.Ordinal))
                continue;
            if (GenericSkillTokens.Contains(q, StringComparer.Ordinal))
                continue;
            if (!skillNames.Contains(q) && !stage4Quoted.Contains(q))
                continue;
            added.Add(q);
        }
        foreach (var b in unitBracketed.Concat(stage4Bracketed).Distinct(StringComparer.Ordinal))
        {
            if (kept.Contains(b, StringComparer.Ordinal))
                continue;
            if (GenericSkillTokens.Contains(b, StringComparer.Ordinal))
                continue;
            if (!skillNames.Contains(b) && !stage4Bracketed.Contains(b))
                continue;
            added.Add(b);
        }

        foreach (var token in removed)
            fixes.Add($"单元 {unitNumber} 移除误锁技能「{token}」");
        foreach (var token in added)
            fixes.Add($"单元 {unitNumber} 补充技能锁定「{token}」");

        var finalTokens = kept
            .Concat(added)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var locked = new HashSet<string>(finalTokens, StringComparer.Ordinal);

        if (finalTokens.Count == 0)
            return (hadLine ? null : null, locked);

        var lineValue = string.Join(", ", finalTokens);
        return (lineValue, locked);
    }

    private static bool IsMentioned(
        string token,
        string bodyText,
        string stage4Unit,
        IReadOnlyCollection<string> stage4Quoted,
        IReadOnlyCollection<string> stage4Bracketed)
    {
        return stage4Quoted.Contains(token, StringComparer.Ordinal)
            || stage4Bracketed.Contains(token, StringComparer.Ordinal)
            || (!string.IsNullOrEmpty(bodyText) && bodyText.Contains(token, StringComparison.Ordinal))
            || (!string.IsNullOrEmpty(stage4Unit) && stage4Unit.Contains(token, StringComparison.Ordinal));
    }

    private static List<string> ExtractQuotedNames(string text, HashSet<string> skillNames)
    {
        var names = new List<string>();
        CollectQuotedNames(text, names, skillNames);
        return names.Distinct(StringComparer.Ordinal).ToList();
    }

    private static List<string> ExtractBracketNames(string text)
    {
        var names = new List<string>();
        CollectBracketNames(text, names);
        return names.Distinct(StringComparer.Ordinal).ToList();
    }

    private static void CollectQuotedNames(string text, List<string> names, HashSet<string> skillNames)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (Match m in Regex.Matches(text, @"「([^」]+)」"))
        {
            var name = m.Groups[1].Value.Trim();
            if (name.Length == 0 || name.Length > 24)
                continue;
            if (name.Length == 1 && !skillNames.Contains(name))
                continue;
            names.Add(name);
        }
    }

    private static void CollectBracketNames(string text, List<string> names)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (Match m in Regex.Matches(text, @"\[([^\]\r\n]+)\]"))
        {
            var name = m.Groups[1].Value.Trim();
            if (name.Length == 0 || name.Length > 24 || name.StartsWith("组合:", StringComparison.Ordinal) || name.Contains(':'))
                continue;
            names.Add(name);
        }
    }

    private static List<string> SplitSkillTokens(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "无") return new List<string>();
        return value
            .Split(new[] { ',', '，', '、', ';', '；', '|', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string ReplaceSkillLine(string unitText, string? lineValue)
    {
        var linePattern = @"(?m)^\s*-\s*\*\*技能\*\*\s*[:：][^\r\n]*$";
        if (string.IsNullOrEmpty(lineValue))
            return Regex.Replace(unitText, linePattern, "");

        if (Regex.IsMatch(unitText, linePattern))
            return Regex.Replace(unitText, @"(?m)^(\s*-\s*\*\*技能\*\*\s*[:：]\s*)[^\r\n]*$", "${1}" + lineValue);

        var anchor = Regex.Match(unitText, @"(?m)^(\s*-\s*\*\*运镜原子\*\*\s*[:：][^\r\n]*)$");
        if (anchor.Success)
            return unitText.Insert(anchor.Index, "- **技能**: " + lineValue + "\n");

        anchor = Regex.Match(unitText, @"(?m)^(\s*-\s*\*\*单元类型\*\*\s*[:：][^\r\n]*)$");
        if (anchor.Success)
            return unitText.Insert(anchor.Index + anchor.Length, "\n- **技能**: " + lineValue);

        return unitText.TrimEnd() + "\n- **技能**: " + lineValue + "\n";
    }

    private static string FixPhrases(string text, HashSet<string> lockedSkills, string unitNumber, List<string> fixes)
    {
        var original = text;
        if (!lockedSkills.Contains("拳风"))
        {
            text = text
                .Replace("拳风呼啸", "拳势破空")
                .Replace("拳风炸裂", "拳劲炸裂")
                .Replace("拳风破空", "拳劲破空")
                .Replace("拳风扑面", "拳劲扑面")
                .Replace("拳风", "拳劲");
        }

        text = Regex.Replace(text, @"(?<!武极)金身", lockedSkills.Contains("武极金身") ? "武极金身" : "肉身");

        text = text
            .Replace("灵光瞬移", "借力闪身")
            .Replace("灵力炸裂", "气血炸裂")
            .Replace("再次瞬移", "再度借力闪身")
            .Replace("再度瞬移", "再度借力闪身")
            .Replace("拳罡", "拳劲")
            .Replace("气罡", "气劲")
            .Replace("火凤", "火焰");

        if (!string.Equals(original, text, StringComparison.Ordinal) && !string.IsNullOrEmpty(unitNumber))
            fixes.Add($"单元 {unitNumber} 清理技能描述词污染");

        return text;
    }

    private sealed record UnitBlock(string UnitNumber, string Text);
}
