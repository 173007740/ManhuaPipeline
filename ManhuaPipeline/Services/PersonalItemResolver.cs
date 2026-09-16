using System.Text.RegularExpressions;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services;

/// <summary>
/// 角色卡「随身物品」解析。
/// 约定：角色卡的「属性」字段里写一行「随身物品：挎包、手机、怀表」，
/// 表示这些物件在角色卡出图时已画进同一张角色卡（角色卡即形象母版，物件外观以该卡为唯一权威）。
/// 前缀「随身物品：」可省略——属性里若没有任何「键：值」结构，整段就按随身物品清单理解
/// （如只写「深紫便携布包」，或「粉色智能手机、浅金备用丝带」）。
/// 声明过的物件：
///   1. 不再作为独立道具参与 Stage 4 单元绑定 / Stage 5 帧绑定（不占参考图槽）；
///   2. 生成 H3 提示词时随所属人物那一行一起交代（「同图附带其随身物品（A、B）」）；
///   3. 需要单独出镜、单独特写或被交接的剧情道具不要写进这一行，仍走独立道具卡。
/// </summary>
public static class PersonalItemResolver
{
    private static readonly Regex PersonalItemsLineRegex = new(
        @"随身物品\s*[:：]\s*([^\r\n]+)", RegexOptions.Compiled);

    /// <summary>回退解析护栏：整段/单行超过该长度就当成自由描述，不再猜随身物品。</summary>
    private const int FallbackMaxTotalLength = 60;
    private const int FallbackMaxNameLength = 20;

    /// <summary>写成这些字样的条目视为「没有随身物品」，不当作物品名。</summary>
    private static readonly string[] EmptyMarkers = { "无", "没有", "暂无", "略", "none", "n/a", "-" };

    private static readonly char[] NameSeparators = { '、', '，', ',', '；', ';', '|', '/' };
    private static readonly char[] NameTrims = { '。', '.', '，', ',', '；', ';', '、', '"', '"', '\'', '\'' };

    /// <summary>解析单个角色卡属性里声明的随身物品名（按书写顺序去重）。
    /// 优先取「随身物品：A、B」行；属性里完全没有「键：值」结构时，整段按随身物品清单理解，
    /// 兼容直接在属性框里写物品名的用法。</summary>
    public static List<string> Parse(string? attributes)
    {
        var items = new List<string>();
        if (string.IsNullOrWhiteSpace(attributes)) return items;

        foreach (Match m in PersonalItemsLineRegex.Matches(attributes))
            AddItems(items, m.Groups[1].Value);
        if (items.Count > 0) return items;
        // 写了「随身物品：」但值为空（或写「无」）：属于明确声明「没有」，不做回退
        if (PersonalItemsLineRegex.IsMatch(attributes)) return items;

        // 回退：属性里出现任何「键：值」行，说明它是结构化描述（身高/体重/性格…），不猜；
        // 只有整段都是自由列举（如「深紫便携布包」「粉色智能手机、浅金备用丝带」）才按随身物品清单收下。
        var lines = attributes.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) return items;
        if (lines.Any(l => l.Contains(':') || l.Contains('：'))) return items;
        if (attributes.Trim().Length > FallbackMaxTotalLength) return items;
        if (lines.Any(l => l.Length > FallbackMaxNameLength)) return items;

        foreach (var line in lines)
            AddItems(items, line);
        return items;
    }

    /// <summary>按分隔符拆出一行里的物品名，去重后追加到 items。</summary>
    private static void AddItems(List<string> items, string raw)
    {
        foreach (var part in raw.Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = part.Trim().Trim(NameTrims).Trim();
            if (name.Length == 0) continue;
            if (EmptyMarkers.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase))) continue;
            if (!items.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)))
                items.Add(name);
        }
    }

    /// <summary>角色名 → 随身物品名（只收录声明了随身物品的角色）。</summary>
    public static Dictionary<string, List<string>> BuildByCharacter(IReadOnlyList<CharacterAsset>? characters)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in characters ?? Array.Empty<CharacterAsset>())
        {
            if (c == null || string.IsNullOrWhiteSpace(c.Name)) continue;
            var items = Parse(c.Attributes);
            if (items.Count > 0) map[c.Name.Trim()] = items;
        }
        return map;
    }

    /// <summary>
    /// 被角色卡吸收的道具：道具资产 Id → 声明它的角色名。
    /// 匹配口径与绑定解析一致（归一化后互相包含），兼容角色卡里写简称、资产库用规范全名的写法。
    /// </summary>
    public static Dictionary<int, string> BuildAbsorbedPropMap(
        IReadOnlyList<CharacterAsset>? characters, IReadOnlyList<PropAsset>? props)
    {
        var absorbed = new Dictionary<int, string>();
        var byCharacter = BuildByCharacter(characters);
        if (byCharacter.Count == 0) return absorbed;
        foreach (var p in props ?? Array.Empty<PropAsset>())
        {
            if (p == null) continue;
            var propHay = Hay(p.Name);
            if (propHay.Length == 0) continue;
            foreach (var kv in byCharacter)
            {
                if (kv.Value.Any(item => NameMatches(item, propHay)))
                {
                    absorbed[p.AssetId] = kv.Key;
                    break;
                }
            }
        }
        return absorbed;
    }

    /// <summary>该角色声明的随身物品名（无声明返回空列表）。</summary>
    public static List<string> ForCharacter(IReadOnlyDictionary<string, List<string>> byCharacter, string? characterName)
    {
        var name = (characterName ?? "").Trim();
        if (name.Length == 0 || byCharacter == null) return new List<string>();
        return byCharacter.TryGetValue(name, out var items) ? items : new List<string>();
    }

    private static bool NameMatches(string itemName, string propHay)
    {
        var item = Hay(itemName);
        if (item.Length == 0) return false;
        return propHay.Contains(item, StringComparison.Ordinal) || item.Contains(propHay, StringComparison.Ordinal);
    }

    private static string Hay(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "" : s.Replace(" ", "").Replace("\u3000", "").Trim().ToLowerInvariant();
}
