using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ManhuaPipeline.Services;

/// <summary>
/// Stage 9 提示词角色名归一化：把 LLM 输出的裸名、错名、留音和
/// 无角色出镜标记修正为角色资产名单中的正式名称。
/// </summary>
public static class PromptNameNormalizer
{
    private static readonly string[] AliasPrefixes = { "倒影中", "镜像中", "虚影中", "画面中" };
    private static readonly string[] AliasSuffixes = { "留音", "的声音", "之声" };

    private static readonly Regex DialogueQuoteRegex = new(@"[“""][^“”""]*[”""]", RegexOptions.Compiled);

    private static readonly string[] PastContextClues =
    {
        "前世岳沉天", "前世战斗态", "前世武圣", "前世镇世武圣", "葬天台", "七曜诛圣阵",
        "太虚圣主", "七宗合击", "七宗宗主", "七色法光", "武极金身", "三城生机",
        "岳沉天尸体", "岳沉天倒在阵心"
    };
    private static readonly string[] PresentContextClues =
    {
        "少年岳沉天", "岳沉天（少年", "岳沉天(少年", "少年", "荒郊破庙", "枯井遗府",
        "乱葬岗", "青云宗", "杂役", "旧拳带", "镇岳拳带"
    };

    public static string DefaultCurrentForm(IEnumerable<string> assetNames)
    {
        var main = MainNames(assetNames).ToList();
        if (main.Count == 0) return "";
        return main.FirstOrDefault(n => n.Contains("少年", StringComparison.OrdinalIgnoreCase)) ?? main[0];
    }

    public static string Normalize(
        string promptText,
        string? unitContext,
        IEnumerable<string> assetNames,
        ref string currentForm)
    {
        if (string.IsNullOrWhiteSpace(promptText)) return promptText;

        var names = CleanNames(assetNames);
        UpdateCurrentForm(unitContext, promptText, names, ref currentForm);

        var lines = promptText.Split('\n').ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith("@角色引用", StringComparison.Ordinal) || line.StartsWith("@角色引用：", StringComparison.Ordinal))
                lines[i] = NormalizeRoleReferenceLine(line, names, currentForm);
            else if (line.StartsWith("对话", StringComparison.Ordinal) || line.StartsWith("对话：", StringComparison.Ordinal))
                lines[i] = NormalizeDialogueLine(line, names, currentForm);
            else if (line.StartsWith("参考图", StringComparison.Ordinal) || line.StartsWith("参考图：", StringComparison.Ordinal))
                lines[i] = NormalizeReferenceLine(line, names, currentForm)!;
            else if (SeedanceAtImageLine.IsAtImageLine(line))
                lines[i] = NormalizeAtImageLine(line, names, currentForm);
            else
                lines[i] = NormalizeBodyText(line, names, currentForm);
        }

        return string.Join("\n", lines.Where(l => l != null));
    }

    private static void UpdateCurrentForm(
        string? unitContext,
        string promptText,
        IReadOnlyCollection<string> names,
        ref string currentForm)
    {
        var main = MainNames(names).ToList();
        if (string.IsNullOrWhiteSpace(currentForm) || !ContainsName(names, currentForm))
            currentForm = DefaultCurrentForm(names);

        if (!string.IsNullOrWhiteSpace(unitContext))
        {
            if (unitContext.Contains("少年岳沉天", StringComparison.OrdinalIgnoreCase)
                || unitContext.Contains("岳沉天（少年", StringComparison.OrdinalIgnoreCase)
                || unitContext.Contains("岳沉天(少年", StringComparison.OrdinalIgnoreCase))
                currentForm = PresentForm(main);
            else if (unitContext.Contains("前世岳沉天", StringComparison.OrdinalIgnoreCase)
                || unitContext.Contains("前世战斗态", StringComparison.OrdinalIgnoreCase)
                || unitContext.Contains("前世镇世武圣", StringComparison.OrdinalIgnoreCase))
                currentForm = PastForm(main);
            else if (HasContextClue(unitContext, PastContextClues))
                currentForm = PastForm(main);
            else if (HasContextClue(unitContext, PresentContextClues))
                currentForm = PresentForm(main);
        }

        if (!string.IsNullOrWhiteSpace(currentForm)) return;
        if (promptText.Contains("少年岳沉天", StringComparison.OrdinalIgnoreCase))
            currentForm = PresentForm(main);
        else if (promptText.Contains("前世岳沉天", StringComparison.OrdinalIgnoreCase)
            || promptText.Contains("前世战斗态", StringComparison.OrdinalIgnoreCase))
            currentForm = PastForm(main);
    }

    private static string NormalizeDialogueLine(
        string line,
        IReadOnlyCollection<string> names,
        string currentForm)
    {
        // 台词必须原样保留剧本里的人称（如“岳沉天，你一人再强...”），
        // 只归一说话者标签（如“顾残山留音说”→“顾残山说”）。
        var m = Regex.Match(line, @"^(对话[:：]\s*)([^说：""“]+)(说)([“""].*)$");
        if (!m.Success) return line;
        var speaker = MapName(m.Groups[2].Value.Trim(), names, currentForm);
        if (speaker.Length == 0 || string.Equals(speaker, m.Groups[2].Value.Trim(), StringComparison.OrdinalIgnoreCase))
            return line;
        return m.Groups[1].Value + speaker + m.Groups[3].Value + m.Groups[4].Value;
    }

    private static string NormalizeRoleReferenceLine(
        string line,
        IReadOnlyCollection<string> names,
        string currentForm)
    {
        var idx = line.IndexOfAny(new[] { ':', '：' });
        var prefix = idx >= 0 ? line.Substring(0, idx + 1) : "@角色引用:";
        var raw = idx >= 0 ? line.Substring(idx + 1) : line;
        var tokens = Regex.Matches(raw, @"\[([^\]\r\n]+)\]")
            .Cast<Match>()
            .Select(m => MapName(m.Groups[1].Value, names, currentForm))
            .Where(n => n.Length > 0 && !string.Equals(n, "无角色正面出镜", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (tokens.Count == 0) return null!;
        return prefix + string.Concat(tokens.Select(n => "[" + n + "]"));
    }

    private static string NormalizeAtImageLine(
        string line,
        IReadOnlyCollection<string> names,
        string currentForm)
    {
        var segments = SeedanceAtImageLine.Parse(line);
        if (segments.Count == 0 || string.IsNullOrWhiteSpace(currentForm)) return line;
        var currentBase = currentForm.EndsWith("战斗态", StringComparison.Ordinal)
            ? currentForm.Substring(0, currentForm.Length - "战斗态".Length)
            : currentForm;
        if (currentBase.Length == 0) return line;
        var updated = new List<SeedanceAtImageLine.Segment>();
        foreach (var seg in segments)
        {
            var name = seg.Name;
            if (seg.Category == "人物" || seg.Category == "战斗态")
            {
                var baseName = seg.Category == "战斗态"
                    ? seg.Name.EndsWith("战斗态", StringComparison.Ordinal)
                        ? seg.Name.Substring(0, seg.Name.Length - "战斗态".Length)
                        : seg.Name
                    : seg.Name;
                if (IsSameCharacterVariant(baseName, currentBase, names))
                    name = currentBase;
            }
            updated.Add(new SeedanceAtImageLine.Segment(seg.Index, name, seg.Category, seg.Description));
        }
        if (updated.SequenceEqual(segments)) return line;
        return SeedanceAtImageLine.Rebuild(updated);
    }

    private static string? NormalizeReferenceLine(
        string line,
        IReadOnlyCollection<string> names,
        string currentForm)
    {
        var colonIdx = line.IndexOfAny(new[] { ':', '：' });
        var prefix = colonIdx >= 0 ? line.Substring(0, colonIdx + 1) : "参考图:";
        var body = colonIdx >= 0 ? line.Substring(colonIdx + 1) : line;
        var segments = body.Split(new[] { '；', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        var kept = new List<string>();
        foreach (var segment in segments)
        {
            var mapped = Regex.Replace(segment, @"为\[([^\]\r\n]+)\](形象参考|群像参考|道具参考|场景参考|特效参考)", m =>
            {
                var mappedName = MapName(m.Groups[1].Value, names, currentForm);
                if (mappedName.Length == 0 || string.Equals(mappedName, "无角色正面出镜", StringComparison.OrdinalIgnoreCase))
                    return "";
                return "为[" + mappedName + "]" + m.Groups[2].Value;
            });
            if (string.IsNullOrWhiteSpace(mapped)) continue;
            kept.Add(mapped.Trim());
        }

        if (kept.Count == 0) return null;
        return prefix + string.Join("；", kept);
    }

    private static string NormalizeBodyText(
        string line,
        IReadOnlyCollection<string> names,
        string currentForm)
    {
        // 时间块正文里的台词引号必须逐字保留，只归一引号外的叙事文本，
        // 避免“岳沉天”被改写成“前世岳沉天/少年岳沉天”破坏逐字照抄规则。
        var builder = new StringBuilder();
        var last = 0;
        foreach (Match quote in DialogueQuoteRegex.Matches(line))
        {
            builder.Append(NormalizeBodySegment(line.Substring(last, quote.Index - last), names, currentForm));
            builder.Append(quote.Value);
            last = quote.Index + quote.Length;
        }
        builder.Append(NormalizeBodySegment(line.Substring(last), names, currentForm));
        return builder.ToString();
    }

    private static string NormalizeBodySegment(
        string segment,
        IReadOnlyCollection<string> names,
        string currentForm)
    {
        segment = Regex.Replace(segment, @"\[([^\]\r\n]+)\]", m =>
        {
            var content = m.Groups[1].Value.Trim();
            if (content.StartsWith("组合:", StringComparison.OrdinalIgnoreCase)) return m.Value;
            var mapped = MapName(content, names, currentForm);
            return mapped.Length == 0 ? m.Value : "[" + mapped + "]";
        });

        var patterns = BuildPatterns(names).OrderByDescending(p => p.Length).Select(Regex.Escape).ToList();
        if (patterns.Count == 0) return segment;
        var regex = new Regex(string.Join("|", patterns));
        return regex.Replace(segment, m =>
        {
            var mapped = MapName(m.Value, names, currentForm);
            return mapped.Length == 0 ? m.Value : mapped;
        });
    }

    private static List<string> BuildPatterns(IReadOnlyCollection<string> names)
    {
        var main = MainNames(names).ToList();
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in names) result.Add(n);
        foreach (var n in main)
        {
            if (n.Length <= 2) continue;
            foreach (var prefix in AliasPrefixes) result.Add(prefix + n);
            foreach (var suffix in AliasSuffixes) result.Add(n + suffix);
            foreach (var form in new[] { "少年", "前世", "今生" })
            {
                if (n.StartsWith(form, StringComparison.Ordinal) && n.Length > form.Length)
                    result.Add(n.Substring(form.Length));
            }
        }
        return result.ToList();
    }

    private static List<string> MainNames(IEnumerable<string> assetNames)
    {
        return CleanNames(assetNames)
            .Select(n => n.EndsWith("群像", StringComparison.Ordinal) ? n.Substring(0, n.Length - 2) : n)
            .Where(n => !n.Contains("战斗态", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool HasContextClue(string text, string[] clues)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return clues.Any(c => text.Contains(c, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> CleanNames(IEnumerable<string> assetNames)
    {
        return (assetNames ?? Array.Empty<string>())
            .Select(n => (n ?? "").Trim())
            .Where(n => n.Length > 0)
            .Select(n => Regex.Replace(n, @"[（(][^）)]*[）)]$", "").Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
    private static bool ContainsName(IReadOnlyCollection<string> names, string name)
    {
        foreach (var n in names)
        {
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string MapName(
        string token,
        IReadOnlyCollection<string> names,
        string currentForm)
    {
        if (string.IsNullOrWhiteSpace(token)) return "";
        token = token.Trim();
        if (string.Equals(token, "无角色正面出镜", StringComparison.OrdinalIgnoreCase)) return "";

        var variantMapped = MapVariantToCurrentForm(token, names, currentForm);
        if (variantMapped.Length > 0) return variantMapped;

        var exact = names.FirstOrDefault(n => string.Equals(n, token, StringComparison.OrdinalIgnoreCase));
        if (exact != null && !IsBareMainNameOfFormalAsset(token, names)) return exact;

        // 裸名/别名（如“岳沉天”“顾残山留音”“倒影中少年岳沉天”）即使被自动补进名单，
        // 也优先映射到当前形态或最长的正式资产名，否则归一化和战斗态双卡会被裸名短路。
        if (IsBareMainNameOfFormalAsset(token, names))
        {
            var bareCore = StripBareAliasCore(token);
            if (currentForm.Length > 0 && ContainsName(names, currentForm)
                && (bareCore.Length == 0 || currentForm.Contains(bareCore, StringComparison.OrdinalIgnoreCase)))
                return currentForm;
            var canonical = names.FirstOrDefault(n => string.Equals(n, bareCore, StringComparison.OrdinalIgnoreCase));
            if (canonical != null) return canonical;
            return names.Where(n => n.Length > bareCore.Length
                    && !n.Contains("战斗态", StringComparison.OrdinalIgnoreCase)
                    && n.Contains(bareCore, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(n => n.Length)
                .FirstOrDefault() ?? token;
        }

        if (string.Equals(token, "岳沉天", StringComparison.OrdinalIgnoreCase)
            && names.Any(n => string.Equals(n, currentForm, StringComparison.OrdinalIgnoreCase)))
            return currentForm;

        foreach (var prefix in AliasPrefixes)
        {
            if (token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && token.Length > prefix.Length)
            {
                var inner = token.Substring(prefix.Length);
                var mapped = MapName(inner, names, currentForm);
                if (mapped.Length > 0 && !string.Equals(mapped, inner, StringComparison.OrdinalIgnoreCase))
                    return mapped;
            }
        }

        foreach (var suffix in AliasSuffixes)
        {
            if (token.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && token.Length > suffix.Length)
            {
                var inner = token.Substring(0, token.Length - suffix.Length);
                var mapped = MapName(inner, names, currentForm);
                if (mapped.Length > 0 && !string.Equals(mapped, inner, StringComparison.OrdinalIgnoreCase))
                    return mapped;
            }
        }

        if (token.EndsWith("战斗态", StringComparison.Ordinal)
            && currentForm.Length > 0
            && names.Any(n => string.Equals(n, currentForm + "战斗态", StringComparison.OrdinalIgnoreCase)))
            return currentForm + "战斗态";

        foreach (var n in names.OrderByDescending(n => n.Length))
        {
            if (token.Contains(n, StringComparison.OrdinalIgnoreCase)) return n;
        }

        foreach (var n in names.OrderByDescending(n => n.Length))
        {
            if (token.Length >= 2 && n.Contains(token, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(token, "岳沉天", StringComparison.OrdinalIgnoreCase))
                return n;
        }

        return token;
    }

    private static bool IsBareMainNameOfFormalAsset(string token, IReadOnlyCollection<string> names)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length < 2) return false;
        var core = StripBareAliasCore(token);
        if (string.IsNullOrWhiteSpace(core)) return false;
        if (names.Any(n => string.Equals(n, core, StringComparison.OrdinalIgnoreCase))) return true;
        return names.Any(n => n.Length > core.Length
            && !n.Contains("战斗态", StringComparison.OrdinalIgnoreCase)
            && n.Contains(core, StringComparison.OrdinalIgnoreCase));
    }

    private static string StripBareAliasCore(string token)
    {
        var core = token;
        foreach (var prefix in AliasPrefixes)
        {
            if (core.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && core.Length > prefix.Length)
                core = core.Substring(prefix.Length);
        }
        foreach (var suffix in AliasSuffixes)
        {
            if (core.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && core.Length > suffix.Length)
                core = core.Substring(0, core.Length - suffix.Length);
        }
        return core;
    }

    private static bool IsSameCharacterVariant(string name, string currentBase, IReadOnlyCollection<string> names)
    {
        var core = StripFormPrefix(name);
        var currentCore = StripFormPrefix(currentBase);
        if (string.IsNullOrWhiteSpace(core)
            || !string.Equals(core, currentCore, StringComparison.OrdinalIgnoreCase))
            return false;
        return string.Equals(name, currentBase, StringComparison.OrdinalIgnoreCase)
            || names.Any(n => string.Equals(n, currentBase, StringComparison.OrdinalIgnoreCase));
    }

    private static string MapVariantToCurrentForm(string token, IReadOnlyCollection<string> names, string currentForm)
    {
        var currentBase = currentForm.EndsWith("战斗态", StringComparison.Ordinal)
            ? currentForm.Substring(0, currentForm.Length - "战斗态".Length)
            : currentForm;
        var isBattle = token.EndsWith("战斗态", StringComparison.Ordinal);
        var tokenBase = isBattle ? token.Substring(0, token.Length - "战斗态".Length) : token;
        if (IsSameCharacterVariant(tokenBase, currentBase, names))
            return currentBase + (isBattle ? "战斗态" : "");
        return "";
    }

    private static string StripFormPrefix(string name)
    {

        foreach (var form in new[] { "前世", "少年", "今生" })
        {
            if (name.StartsWith(form, StringComparison.OrdinalIgnoreCase) && name.Length > form.Length)
                return name.Substring(form.Length);
        }
        return name;
    }

    private static string PresentForm(List<string> mainNames)
    {
        return mainNames.FirstOrDefault(n => n.Contains("少年", StringComparison.OrdinalIgnoreCase)) ?? mainNames.FirstOrDefault() ?? "";
    }

    private static string PastForm(List<string> mainNames)
    {
        return mainNames.FirstOrDefault(n => n.Contains("前世", StringComparison.OrdinalIgnoreCase)) ?? mainNames.FirstOrDefault() ?? "";
    }
}
