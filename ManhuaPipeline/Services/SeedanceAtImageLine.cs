using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace ManhuaPipeline.Services;

/// <summary>
/// Stage 9 紧凑模板 @图N 绑定行解析与重建，供技能过滤、战斗态双卡、
/// 技能参考图补全和数量裁剪共用。
/// 格式：@图1 [前世岳沉天]人物形象参考，保持外貌、发型、服装、气质一致；@图2 [前世岳沉天战斗态]战斗姿态参考，保持外貌一致；…
/// 资产名用方括号写在 @图N 后，类别（人物/战斗态/群像/特效/道具/场景/光影质感）紧跟方括号，说明写在类别后。
/// 解析同时兼容无括号格式「@图N 资产名类别」。
/// </summary>
public static class SeedanceAtImageLine
{
    public const string CategoryPattern = "人物|战斗姿态|战斗态|群像|特效|道具|场景|光影质感|光影";

    public sealed record Segment(int Index, string Name, string Category, string? Description = null);

    public static bool IsAtImageLine(string line)
    {
        return !string.IsNullOrWhiteSpace(line)
            && line.TrimStart().StartsWith("@图", StringComparison.Ordinal);
    }

    public static List<Segment> Parse(string line)
    {
        var result = new List<Segment>();
        if (!IsAtImageLine(line)) return result;
        foreach (var part in line.Split(new[] { '；', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = part.Trim().TrimEnd('。', '.', '！', '!');
            if (seg.Length == 0) continue;
            var head = Regex.Match(seg, @"^@图(?<idx>\d+)\s+(?<body>.+)$");
            if (!head.Success) continue;
            var index = int.Parse(head.Groups["idx"].Value);
            var body = head.Groups["body"].Value.Trim();

            string name;
            string remainder;
            var bracket = Regex.Match(body, @"^\[(?<name>[^\]]+)\](?<rest>.*)$");
            if (bracket.Success)
            {
                name = bracket.Groups["name"].Value.Trim();
                remainder = bracket.Groups["rest"].Value.Trim();
            }
            else
            {
                name = "";
                remainder = body;
            }

            string category;
            string description;
            var catMatch = Regex.Match(remainder, @"^(?<cat>" + CategoryPattern + @")(?<desc>[\s\S]*)$");
            if (catMatch.Success)
            {
                category = catMatch.Groups["cat"].Value;
                description = catMatch.Groups["desc"].Value.Trim().TrimStart('，', ',');
            }
            else if (bracket.Success)
            {
                category = "";
                description = "";
            }
            else
            {
                var bare = Regex.Match(remainder, @"^(?<name>.+?)(?<cat>" + CategoryPattern + @")(?<desc>[\s\S]*)$");
                if (bare.Success)
                {
                    name = bare.Groups["name"].Value.Trim();
                    category = bare.Groups["cat"].Value;
                    description = bare.Groups["desc"].Value.Trim().TrimStart('，', ',');
                }
                else
                {
                    name = remainder;
                    category = "";
                    description = "";
                }
            }

            // 新版写法把战斗态写作“战斗姿态”，内部统一收敛为“战斗态”。
            if (category == "战斗姿态") category = "战斗态";
            // “光影参考”等价于“光影质感参考”，统一收敛类别。
            if (category == "光影") category = "光影质感";

            if (name.Length == 0 && category.Length > 0)
                name = category;

            if (category.Length == 0)
            {
                if (name.EndsWith("战斗态", StringComparison.Ordinal)) category = "战斗态";
                else if (string.Equals(name, "光影质感", StringComparison.OrdinalIgnoreCase)) category = "光影质感";
                else category = "人物";
            }
            // LLM 有时会在资产名后重复追加“战斗态”，解析后名字统一收敛为基础名。
            if (category == "战斗态")
            {
                name = Regex.Replace(name, @"(?:战斗态)+$", "");
                if (name.Length == 0) name = "战斗态";
            }
            // 光影质感是固定风格卡名，LLM 按场景自创名称（如 冷月雨夜、葬天台冷暗光影）会破坏参考图匹配。
            if (category == "光影质感")
                name = "光影质感";
            result.Add(new Segment(index, name, category, description));
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<Segment>();
        foreach (var segment in result)
            if (seen.Add(segment.Name + "|" + segment.Category)) unique.Add(segment);
        return unique;
    }

    public static string Rebuild(IEnumerable<Segment> segments)
    {
        var index = 1;
        return string.Join("；", segments.Select(s => "@图" + index++ + " " + FormatSegment(s)));
    }

    private static string FormatSegment(Segment segment)
    {
        var name = (segment.Name ?? "").Trim();
        var desc = (segment.Description ?? "").Trim().TrimStart('，', ',');
        desc = NormalizeDescription(segment.Category, desc);
        switch (segment.Category)
        {
            case "人物":
                if (desc.StartsWith("人物", StringComparison.Ordinal)) desc = desc.Substring("人物".Length);
                return "[" + name + "]人物" + (desc.Length > 0 ? desc : "形象参考，保持外貌、发型、服装、气质一致");
            case "战斗态":
                var baseName = Regex.Replace(name, @"(?:战斗态)+$", "");
                if (baseName.Length == 0) baseName = name;
                desc = Regex.Replace(desc, @"^战斗态+", "");
                if (desc.StartsWith("姿态", StringComparison.Ordinal)) desc = desc.Substring("姿态".Length);
                return "[" + baseName + "战斗态]战斗姿态" + (desc.Length > 0 ? desc : "参考，保持外貌、发型、服装、气质一致");
            case "群像":
                var groupName = Regex.Replace(name, @"(?:群像)+$", "");
                if (groupName.Length == 0) groupName = name;
                if (desc.StartsWith("群像", StringComparison.Ordinal)) desc = desc.Substring("群像".Length);
                return "[" + groupName + "]群像" + (desc.Length > 0 ? desc : "参考，保持整体造型一致");
            case "特效":
                if (desc.StartsWith("特效", StringComparison.Ordinal)) desc = desc.Substring("特效".Length);
                return "[" + name + "]特效" + (desc.Length > 0 ? desc : "参考，保持形态、色调、氛围一致");
            case "道具":
                if (desc.StartsWith("道具", StringComparison.Ordinal)) desc = desc.Substring("道具".Length);
                return "[" + name + "]道具" + (desc.Length > 0 ? desc : "参考，保持外观、材质、大小一致");
            case "场景":
                if (desc.StartsWith("场景", StringComparison.Ordinal)) desc = desc.Substring("场景".Length);
                return "[" + name + "]场景" + (desc.Length > 0 ? desc : "参考，保持空间布局、色调一致");
            case "光影质感":
                if (desc.StartsWith("光影质感", StringComparison.Ordinal)) desc = desc.Substring("光影质感".Length);
                return "[光影质感]光影质感" + (desc.Length > 0 ? desc : "参考，保持冷月光、体积光、强明暗对比、电影级材质一致");
            default:
                return "[" + name + "]" + segment.Category + (desc.Length > 0 ? desc : "");
        }
    }

    /// <summary>
    /// 归一化类别后的说明文字，确保「人物形象」「战斗姿态」「特效」等写法
    /// 都补齐「参考」二字，避免页面解析器把整段误当成资产名。
    /// </summary>
    private static string NormalizeDescription(string category, string desc)
    {
        desc = (desc ?? "").Trim();
        if (desc.Length == 0) return "";
        if (category == "人物")
        {
            if (desc.StartsWith("形象参考", StringComparison.Ordinal)) return desc;
            if (desc.StartsWith("形象", StringComparison.Ordinal))
                return "形象参考" + desc.Substring("形象".Length);
            if (desc.StartsWith("参考", StringComparison.Ordinal))
                return "形象" + desc;
            return "形象参考，" + desc;
        }
        if (category == "战斗态")
        {
            if (desc.StartsWith("姿态参考", StringComparison.Ordinal)) return desc;
            if (desc.StartsWith("姿态", StringComparison.Ordinal))
                return "姿态参考" + desc.Substring("姿态".Length);
            if (desc.StartsWith("参考", StringComparison.Ordinal))
                return "姿态" + desc;
            return "姿态参考，" + desc;
        }
        if (desc.StartsWith("参考", StringComparison.Ordinal)) return desc;
        return "参考，" + desc;
    }
}
