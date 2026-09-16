using System.Text.RegularExpressions;

namespace ManhuaPipeline.Services;

/// <summary>
/// Stage 9 出镜角色参考图完整性守卫：时间块正文中实际出镜的角色
/// 若漏绑 @图N 基础卡，自动补上，避免换一批生成结果时角色卡随机丢失。
/// </summary>
public static class PromptCharacterRefGuard
{
    private static readonly Regex ShotHeaderRegex = new(
        @"(?=【第\d+集】\s*【单元[\d.]+[a-zA-Z]?】\s*【镜头[\d.]+[a-zA-Z]?\-\d+】)");

    private static readonly Regex DialogueRegex = new(@"[“""「][^”""」]*[”""」]");

    private static readonly string[] GroupMarkers =
    {
        "宗", "门", "派", "帮", "众", "群", "弟子", "长老", "阁", "族", "军", "队"
    };

    public static string Apply(string promptText, IEnumerable<string>? assetNames)
    {
        if (string.IsNullOrWhiteSpace(promptText)) return promptText;

        var baseNames = BuildBaseNames(assetNames);
        if (baseNames.Count == 0) return promptText;

        var parts = ShotHeaderRegex.Split(promptText);
        return string.Join("", parts.Select(p => ProcessShot(p, baseNames)));
    }

    private static string ProcessShot(string shotText, List<BaseName> baseNames)
    {
        if (!shotText.Contains("@图", StringComparison.Ordinal)) return shotText;

        var lines = shotText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
        var atImageIndex = lines.FindIndex(SeedanceAtImageLine.IsAtImageLine);
        if (atImageIndex < 0) return shotText;

        var segments = SeedanceAtImageLine.Parse(lines[atImageIndex]);
        if (segments.Count == 0) return shotText;

        var body = string.Join("\n", lines.Where((_, i) => i != atImageIndex));
        body = DialogueRegex.Replace(body, "");

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

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seg in characters)
        {
            var name = seg.Category == "战斗态"
                ? Regex.Replace(seg.Name, @"战斗态$", "")
                : seg.Name;
            existing.Add(name);
        }

        foreach (var baseName in baseNames.OrderByDescending(n => n.Name.Length))
        {
            if (existing.Contains(baseName.Name)) continue;
            if (!body.Contains(baseName.Name, StringComparison.OrdinalIgnoreCase)) continue;
            characters.Add(new SeedanceAtImageLine.Segment(0, baseName.Name, baseName.Category));
            existing.Add(baseName.Name);
        }

        var ordered = new List<SeedanceAtImageLine.Segment>();
        ordered.AddRange(characters);
        ordered.AddRange(props);
        ordered.AddRange(effects);
        if (scene != null) ordered.Add(scene);
        if (style != null) ordered.Add(style);

        if (ordered.Count == 0) return shotText;
        lines[atImageIndex] = SeedanceAtImageLine.Rebuild(ordered);
        return string.Join("\n", lines);
    }

    private static List<BaseName> BuildBaseNames(IEnumerable<string>? assetNames)
    {
        var result = new List<BaseName>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (assetNames ?? Array.Empty<string>()))
        {
            var name = (raw ?? "").Trim();
            if (name.Length == 0) continue;
            if (name.EndsWith("战斗态", StringComparison.Ordinal)) continue;
            if (name.EndsWith("群像", StringComparison.Ordinal))
                name = name.Substring(0, name.Length - "群像".Length);
            if (name.Length == 0 || !seen.Add(name)) continue;
            result.Add(new BaseName(name, CategoryFor(name)));
        }
        return result;
    }

    private static string CategoryFor(string name)
    {
        return GroupMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase))
            ? "群像"
            : "人物";
    }

    private sealed record BaseName(string Name, string Category);
}
