using System.Text;
using System.Text.RegularExpressions;

namespace ManhuaPipeline.Services;

/// <summary>
/// Stage 9 战斗态双卡校验：战斗镜头强制保留「基础卡+战斗态卡」，
/// 非战斗镜头只保留基础卡，并在调整后重新编号参考图。
/// </summary>
public static class PromptCombatStateGuard
{
    private static readonly string[] CombatTypes =
    {
        "打斗/动作", "追逐/逃亡", "高潮/对决"
    };

    public static string Apply(string promptText, string? unitContext, IEnumerable<string> assetNames)
    {
        if (string.IsNullOrWhiteSpace(promptText)) return promptText;

        var names = (assetNames ?? Array.Empty<string>())
            .Select(n => (n ?? "").Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var battleNames = names
            .Where(n => n.EndsWith("战斗态", StringComparison.Ordinal))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseNames = battleNames
            .Select(n => n.Substring(0, n.Length - "战斗态".Length))
            .Where(names.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (baseNames.Count == 0) return promptText;

        var shotRegex = new Regex(@"(?=【第\d+集】\s*【单元[\d.]+[a-zA-Z]?】\s*【镜头[\d.]+[a-zA-Z]?\-\d+】)");
        var parts = shotRegex.Split(promptText);
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            var combat = IsCombat(part, unitContext);
            builder.Append(ProcessShot(part, combat, baseNames, battleNames));
        }
        return builder.ToString();
    }

    private static bool IsCombat(string shotText, string? unitContext)
    {
        var typeMatch = Regex.Match(shotText, @"类型\s*[:：]\s*([^\r\n]+)");
        if (typeMatch.Success && CombatTypes.Any(t => typeMatch.Groups[1].Value.Contains(t, StringComparison.OrdinalIgnoreCase)))
            return true;
        if (!string.IsNullOrWhiteSpace(unitContext))
        {
            if (Regex.IsMatch(unitContext, @"(?:\*\*)?控制模式(?:\*\*)?\s*[:：]\s*[^\r\n]*打斗"))
                return true;
            if (Regex.IsMatch(unitContext, @"(?:\*\*)?打斗模板(?:\*\*)?\s*[:：]"))
                return true;
        }
        return shotText.Contains("技能特效参考", StringComparison.Ordinal);
    }

    private static string ProcessShot(
        string shotText,
        bool combat,
        HashSet<string> baseNames,
        HashSet<string> battleNames)
    {
        var lines = shotText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (SeedanceAtImageLine.IsAtImageLine(lines[i]))
            {
                lines[i] = NormalizeAtImageLine(lines[i], shotText, combat, baseNames, battleNames);
                break;
            }
            if (lines[i].StartsWith("参考图", StringComparison.Ordinal) || lines[i].StartsWith("参考图：", StringComparison.Ordinal))
            {
                lines[i] = NormalizeReferenceLine(lines[i], shotText, combat, baseNames, battleNames);
                break;
            }
        }
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith("@角色引用", StringComparison.Ordinal) || lines[i].StartsWith("@角色引用：", StringComparison.Ordinal))
            {
                lines[i] = NormalizeRoleReferenceLine(lines[i], shotText, combat, baseNames, battleNames);
                break;
            }
        }
        return string.Join("\n", lines);
    }

    private static string BattleBaseName(string name)
    {
        return Regex.Replace(name ?? "", @"战斗态$", "");
    }

    private static string NormalizeAtImageLine(
        string line,
        string shotText,
        bool combat,
        HashSet<string> baseNames,
        HashSet<string> battleNames)
    {
        var segments = SeedanceAtImageLine.Parse(line);
        if (segments.Count == 0) return line;

        var characters = new List<SeedanceAtImageLine.Segment>();
        var props = new List<SeedanceAtImageLine.Segment>();
        var effects = new List<SeedanceAtImageLine.Segment>();
        SeedanceAtImageLine.Segment? scene = null;
        SeedanceAtImageLine.Segment? style = null;
        foreach (var seg in segments)
        {
            switch (seg.Category)
            {
                case "人物":
                case "战斗态":
                case "群像":
                    characters.Add(seg);
                    break;
                case "道具":
                    props.Add(seg);
                    break;
                case "特效":
                    effects.Add(seg);
                    break;
                case "场景":
                    scene = seg;
                    break;
                case "光影质感":
                    style = seg;
                    break;
            }
        }

        var ordered = new List<SeedanceAtImageLine.Segment>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (combat)
        {
            foreach (var baseName in baseNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                if (!shotText.Contains(baseName, StringComparison.OrdinalIgnoreCase)) continue;
                var battleName = baseName + "战斗态";
                if (!battleNames.Contains(battleName)) continue;
                var baseSeg = characters.FirstOrDefault(s => s.Category == "人物" && string.Equals(s.Name, baseName, StringComparison.OrdinalIgnoreCase));
                var battleSeg = characters.FirstOrDefault(s => s.Category == "战斗态" && string.Equals(BattleBaseName(s.Name), baseName, StringComparison.OrdinalIgnoreCase));
                if (baseSeg == null)
                    baseSeg = new SeedanceAtImageLine.Segment(0, baseName, "人物");
                if (battleSeg == null)
                    battleSeg = new SeedanceAtImageLine.Segment(0, baseName, "战斗态");
                else
                    battleSeg = new SeedanceAtImageLine.Segment(0, baseName, "战斗态", battleSeg.Description);
                if (seen.Add(baseName + "|人物")) ordered.Add(baseSeg);
                if (seen.Add(battleName + "|战斗态")) ordered.Add(battleSeg);
            }
            foreach (var seg in characters)
            {
                if (battleNames.Contains(seg.Name)) continue;
                if (seg.Category == "战斗态" && seen.Contains(BattleBaseName(seg.Name) + "战斗态|战斗态")) continue;
                if (seen.Add(seg.Name + "|" + seg.Category)) ordered.Add(seg);
            }
        }
        else
        {
            foreach (var seg in characters)
            {
                if (seg.Category == "战斗态") continue;
                if (seen.Add(seg.Name + "|" + seg.Category)) ordered.Add(seg);
            }
        }

        ordered.AddRange(props);
        ordered.AddRange(effects);
        if (scene != null) ordered.Add(scene);
        if (style != null) ordered.Add(style);

        if (ordered.Count == 0) return line;
        return SeedanceAtImageLine.Rebuild(ordered);
    }
    private static string NormalizeReferenceLine(
        string line,
        string shotText,
        bool combat,
        HashSet<string> baseNames,
        HashSet<string> battleNames)
    {
        var colonIdx = line.IndexOfAny(new[] { ':', '：' });
        var prefix = colonIdx >= 0 ? line.Substring(0, colonIdx + 1) : "参考图:";
        var body = colonIdx >= 0 ? line.Substring(colonIdx + 1) : line;
        var segments = body.Split(new[] { '；', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        var characters = new List<string>();
        var props = new List<string>();
        var effects = new List<string>();
        string? scene = null;
        string? style = null;
        foreach (var seg in segments)
        {
            if (seg.Contains("画面风格参考", StringComparison.Ordinal)) style = seg;
            else if (seg.Contains("场景参考", StringComparison.Ordinal)) scene = seg;
            else if (seg.Contains("形象参考", StringComparison.Ordinal) || seg.Contains("群像参考", StringComparison.Ordinal)) characters.Add(seg);
            else if (seg.Contains("道具参考", StringComparison.Ordinal)) props.Add(seg);
            else effects.Add(seg);
        }
        var handledSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (combat)
        {
            foreach (var baseName in baseNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                if (!shotText.Contains(baseName, StringComparison.OrdinalIgnoreCase)) continue;
                var battleName = baseName + "战斗态";
                if (!battleNames.Contains(battleName)) continue;
                var baseSeg = characters.FirstOrDefault(s => s.Contains("[" + baseName + "]形象参考", StringComparison.OrdinalIgnoreCase));
                var battleSeg = characters.FirstOrDefault(s => s.Contains("[" + battleName + "]形象参考", StringComparison.OrdinalIgnoreCase));
                if (baseSeg == null)
                    baseSeg = "为[" + baseName + "]形象参考，保持外貌、发型、服装、气质一致";
                if (battleSeg == null)
                    battleSeg = "为[" + battleName + "]形象参考，保持外貌、发型、服装与战损气血状态一致";
                if (seen.Add(baseName)) { ordered.Add(baseSeg); handledSegments.Add(baseSeg); }
                if (seen.Add(battleName)) { ordered.Add(battleSeg); handledSegments.Add(battleSeg); }
            }
        }
        else
        {
            foreach (var seg in characters)
            {
                if (battleNames.Any(b => seg.Contains("[" + b + "]形象参考", StringComparison.OrdinalIgnoreCase))) continue;
                if (handledSegments.Add(seg)) ordered.Add(seg);
            }
        }

        foreach (var seg in characters)
        {
            if (handledSegments.Contains(seg)) continue;
            if (!combat && battleNames.Any(b => seg.Contains("[" + b + "]形象参考", StringComparison.OrdinalIgnoreCase))) continue;
            ordered.Add(seg);
            handledSegments.Add(seg);
        }
        ordered.AddRange(props);
        ordered.AddRange(effects);
        if (scene != null) ordered.Add(scene);
        if (style != null) ordered.Add(style);

        if (ordered.Count == 0) return prefix;
        return prefix + RebuildSegments(ordered, style != null, scene != null);
    }

    private static string NormalizeRoleReferenceLine(
        string line,
        string shotText,
        bool combat,
        HashSet<string> baseNames,
        HashSet<string> battleNames)
    {
        var idx = line.IndexOfAny(new[] { ':', '：' });
        var prefix = idx >= 0 ? line.Substring(0, idx + 1) : "@角色引用:";
        var raw = idx >= 0 ? line.Substring(idx + 1) : line;
        var tokens = Regex.Matches(raw, @"\[([^\]\r\n]+)\]")
            .Cast<Match>()
            .Select(m => m.Groups[1].Value.Trim())
            .Where(t => t.Length > 0)
            .ToList();

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (combat)
        {
            foreach (var baseName in baseNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                if (!shotText.Contains(baseName, StringComparison.OrdinalIgnoreCase)) continue;
                var battleName = baseName + "战斗态";
                if (!battleNames.Contains(battleName)) continue;
                if (seen.Add(baseName)) result.Add(baseName);
                if (seen.Add(battleName)) result.Add(battleName);
            }
            foreach (var token in tokens)
            {
                if (battleNames.Contains(token) && !result.Contains(token)) continue;
                if (baseNames.Contains(token) && result.Contains(token)) continue;
                if (seen.Add(token)) result.Add(token);
            }
        }
        else
        {
            foreach (var token in tokens)
            {
                if (battleNames.Contains(token)) continue;
                if (seen.Add(token)) result.Add(token);
            }
        }

        if (result.Count == 0) return prefix;
        return prefix + string.Concat(result.Select(n => "[" + n + "]"));
    }

    private static string RebuildSegments(List<string> segments, bool hasStyle, bool hasScene)
    {
        var prefixRegex = new Regex(@"^\s*(?:第[一二三四五六七八九十X]+张|倒数第[一二三]+张|最后一张)\(@图(?:\d+|X)\)");
        var builder = new StringBuilder();
        for (var j = 0; j < segments.Count; j++)
        {
            var cleaned = prefixRegex.Replace(segments[j], "").Trim();
            string ordinal;
            if (hasStyle && j == segments.Count - 1) ordinal = "最后一张(@图" + segments.Count + ")";
            else if (hasScene && j == segments.Count - 2) ordinal = "倒数第二张(@图" + (segments.Count - 1) + ")";
            else ordinal = "第" + ChineseNumber(j + 1) + "张(@图" + (j + 1) + ")";
            if (j > 0) builder.Append('；');
            builder.Append(ordinal).Append(cleaned);
        }
        return builder.ToString();
    }

    private static string ChineseNumber(int n)
    {
        var digits = new[] { "零", "一", "二", "三", "四", "五", "六", "七", "八", "九", "十" };
        if (n is >= 1 and <= 10) return digits[n];
        if (n is >= 11 and <= 19) return "十" + digits[n - 10];
        if (n is >= 20 and <= 99) return digits[n / 10] + "十" + (n % 10 == 0 ? "" : digits[n % 10]);
        return n.ToString();
    }
}
