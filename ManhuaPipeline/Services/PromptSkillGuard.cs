using System.Text;
using System.Text.RegularExpressions;

namespace ManhuaPipeline.Services;

/// <summary>
/// Stage 9 提示词生成后的技能白名单校验：参考图行中只保留当前单元
/// 分镜锁定的技能特效，未锁定的技能引用删除并重新编号。
/// </summary>
public static class PromptSkillGuard
{
    public static string FilterUnlockedSkills(
        string promptText,
        IReadOnlyCollection<string> allSkillNames,
        IReadOnlyCollection<string> lockedSkillNames)
    {
        if (string.IsNullOrWhiteSpace(promptText)) return promptText;
        promptText = SeedancePromptParser.NormalizeEpisodeMarkers(promptText);

        var knownSkills = new HashSet<string>(allSkillNames, StringComparer.Ordinal);
        var lockedSkills = new HashSet<string>(lockedSkillNames, StringComparer.Ordinal);
        var shotRegex = new Regex(@"(?=【第\d+集】\s*【单元[\d.]+[a-zA-Z]?】\s*【镜头[\d.]+[a-zA-Z]?\-\d+】)");
        var parts = shotRegex.Split(promptText);
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            builder.Append(TrimShotRefImages(part, segment => ShouldKeepEffect(segment, knownSkills, lockedSkills)));
        }
        return builder.ToString();
    }

    private static bool ShouldKeepEffect(
        string segment,
        HashSet<string> knownSkills,
        HashSet<string> lockedSkills)
    {
        var nameMatch = Regex.Match(segment, @"\[([^\]\r\n]+)\]");
        if (!nameMatch.Success) return true;
        var name = nameMatch.Groups[1].Value.Trim();
        return !knownSkills.Contains(name) || lockedSkills.Contains(name);
    }

    private static string TrimShotRefImages(string shotText, Func<string, bool>? keepEffect)
    {
        var lines = shotText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (SeedanceAtImageLine.IsAtImageLine(lines[i]))
            {
                lines[i] = TrimAtImageLine(lines[i], keepEffect);
                break;
            }
            if (!lines[i].StartsWith("参考图", StringComparison.Ordinal)
                && !lines[i].StartsWith("参考图：", StringComparison.Ordinal))
                continue;

            var colonIdx = lines[i].IndexOfAny(new[] { ':', '：' });
            var prefix = colonIdx >= 0 ? lines[i].Substring(0, colonIdx + 1) : "参考图:";
            var body = colonIdx >= 0 ? lines[i].Substring(colonIdx + 1) : lines[i];

            var segments = body.Split(new[] { '；', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
            if (segments.Count == 0) continue;

            var characters = new List<string>();
            var props = new List<string>();
            var effects = new List<string>();
            string? scene = null;
            string? style = null;

            foreach (var seg in segments)
            {
                if (seg.Contains("画面风格参考", StringComparison.Ordinal)) style = seg;
                else if (seg.Contains("场景参考", StringComparison.Ordinal)) scene = seg;
                else if (seg.Contains("形象参考", StringComparison.Ordinal)
                    || seg.Contains("群像参考", StringComparison.Ordinal)) characters.Add(seg);
                else if (seg.Contains("道具参考", StringComparison.Ordinal)) props.Add(seg);
                else if (keepEffect == null || keepEffect(seg)) effects.Add(seg);
            }

            var contentBudget = style == null ? 9 : 8;
            var charactersKept = characters.Take(contentBudget).ToList();
            var keepScene = scene != null && charactersKept.Count < contentBudget;
            var room = contentBudget - charactersKept.Count - (keepScene ? 1 : 0);
            var propsKept = props.Take(room).ToList();
            room -= propsKept.Count;
            var effectsKept = effects.Take(room).ToList();

            var kept = new List<string>();
            kept.AddRange(charactersKept);
            kept.AddRange(propsKept);
            kept.AddRange(effectsKept);
            if (keepScene) kept.Add(scene!);
            if (style != null) kept.Add(style);

            var prefixRegex = new Regex(@"^\s*(?:第[一二三四五六七八九十X]+张|倒数第[一二三]+张|最后一张)\(@图(?:\d+|X)\)");
            var rebuilt = new StringBuilder(prefix);
            for (var j = 0; j < kept.Count; j++)
            {
                var cleaned = prefixRegex.Replace(kept[j], "").Trim();
                string ordinal;
                if (style != null && j == kept.Count - 1) ordinal = "最后一张(@图" + kept.Count + ")";
                else if (keepScene && j == kept.Count - 2) ordinal = "倒数第二张(@图" + (kept.Count - 1) + ")";
                else ordinal = "第" + ChineseNumber(j + 1) + "张(@图" + (j + 1) + ")";
                if (j > 0) rebuilt.Append('；');
                rebuilt.Append(ordinal).Append(cleaned);
            }
            lines[i] = rebuilt.ToString();
            break;
        }
        return string.Join("\n", lines);
    }

    private static string TrimAtImageLine(string line, Func<string, bool>? keepEffect)
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
                    if (keepEffect == null || keepEffect("[" + seg.Name + "]")) effects.Add(seg);
                    break;
                case "场景":
                    scene = seg;
                    break;
                case "光影质感":
                    style = seg;
                    break;
            }
        }

        var kept = new List<SeedanceAtImageLine.Segment>();
        kept.AddRange(characters);
        kept.AddRange(props);
        kept.AddRange(effects);
        if (scene != null) kept.Add(scene);
        if (style != null) kept.Add(style);
        if (kept.Count == segments.Count && kept.SequenceEqual(segments)) return line;
        return SeedanceAtImageLine.Rebuild(kept);
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
