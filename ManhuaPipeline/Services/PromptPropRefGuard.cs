using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ManhuaPipeline.Services;

/// <summary>
/// Stage 9 道具参考图补全：单元分镜中提到、且镜头正文中出镜的道具
/// 若未绑定到 @图N 行，自动插入 [道具名]道具 并重排编号。
/// </summary>
public static class PromptPropRefGuard
{
    public static string Apply(string promptText, string? unitContext, IEnumerable<string> propNames)
    {
        if (string.IsNullOrWhiteSpace(promptText)) return promptText;
        var names = (propNames ?? Array.Empty<string>())
            .Select(n => (n ?? "").Trim())
            .Where(n => n.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0) return promptText;

        promptText = SeedancePromptParser.NormalizeEpisodeMarkers(promptText);
        var shotRegex = new Regex(@"(?=【第\d+集】\s*【单元[\d.]+[a-zA-Z]?】\s*【镜头[\d.]+[a-zA-Z]?\-\d+】)");
        var parts = shotRegex.Split(promptText);
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            builder.Append(EnsureShotPropRefs(part, unitContext, names));
        }
        return builder.ToString();
    }

    private static string EnsureShotPropRefs(string shotText, string? unitContext, List<string> propNames)
    {
        var lines = shotText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
        var atImageIndex = lines.FindIndex(SeedanceAtImageLine.IsAtImageLine);
        if (atImageIndex < 0) return shotText;

        var body = string.Join("\n", lines.Where((l, i) => i != atImageIndex));
        var mentioned = propNames
            .Where(n => body.Contains(n, StringComparison.OrdinalIgnoreCase))
            .ToList();
        // 正文只写“三件遗物”“拳带”等泛指时，用单元分镜上下文补全同镜道具。
        if (!string.IsNullOrWhiteSpace(unitContext))
        {
            mentioned.AddRange(propNames
                .Where(n => unitContext.Contains(n, StringComparison.OrdinalIgnoreCase)
                    && !mentioned.Contains(n, StringComparer.OrdinalIgnoreCase)));
        }
        if (mentioned.Count == 0) return shotText;

        var segments = SeedanceAtImageLine.Parse(lines[atImageIndex]);
        if (segments.Count == 0) return shotText;
        var existing = segments
            .Where(s => s.Category == "道具")
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = mentioned.Where(n => !existing.Contains(n)).ToList();
        if (missing.Count == 0) return shotText;

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
        props.AddRange(missing.Select(n => new SeedanceAtImageLine.Segment(0, n, "道具")));

        var ordered = new List<SeedanceAtImageLine.Segment>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seg in characters.Concat(props).Concat(effects))
        {
            if (seen.Add(seg.Name + "|" + seg.Category)) ordered.Add(seg);
        }
        if (scene != null) ordered.Add(scene);
        if (style != null) ordered.Add(style);

        lines[atImageIndex] = SeedanceAtImageLine.Rebuild(ordered);
        return string.Join("\n", lines);
    }
}
