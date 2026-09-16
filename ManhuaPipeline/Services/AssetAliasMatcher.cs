using System.Collections.Generic;
using System.Linq;

namespace ManhuaPipeline.Services;

/// <summary>
/// 资产名的「全称 + 别名词」匹配，供 Stage 4（单元↔资产）与 Stage 5（镜头↔资产）两套解析器共用。
///
/// 背景：分镜正文常写简称（「蓝色面板」「悬浮面板」「轿车」），资产库里是全称（「回声系统面板」「黑色轿车」）。
/// 只按全称匹配会静默漏绑 —— 同一件东西在「首次出现」和「再次出现」的两个镜头里用上两套视觉来源，
/// 出视频时长相就对不上。
///
/// 兜底策略：取资产名末尾 2~4 字作为核心词（中文资产多为「修饰语 + 核心词」结构，如「回声系统面板」→「面板」）。
/// 防误绑的两道约束：
///   1. 核心词长度 ≥ 2，且不在通用类别词黑名单内（系统/道具/场景/画面…）；
///   2. 核心词在本项目同类资产名中必须唯一 —— 若「面板」同时是别的资产名结尾（如「控制面板」），则放弃别名，
///      两边都不绑（宁缺毋滥，与既有解析器一致）。
/// </summary>
public static class AssetAliasMatcher
{
    /// <summary>把多个字段拼成待匹配的归一化文本（去空白 + 小写，中文不受影响）。</summary>
    public static string Normalize(IEnumerable<string?> fields) =>
        string.Concat(fields.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f!.Trim()))
              .Replace(" ", "")
              .ToLowerInvariant();

    /// <summary>资产全称命中。</summary>
    public static bool HitByName(string hay, string? name)
    {
        var n = Normalize(new[] { name });
        return n.Length > 0 && hay.Contains(n, System.StringComparison.Ordinal);
    }

    /// <summary>资产名唯一后缀核心词命中（如「回声系统面板」在正文写「面板」时也能绑上）。</summary>
    public static bool HitByAlias(string hay, string? name, IReadOnlyList<string> allNames)
    {
        var n = (name ?? "").Trim();
        if (n.Length < 3) return false;
        for (var len = 2; len <= System.Math.Min(4, n.Length - 1); len++)
        {
            var suffix = n.Substring(n.Length - len);
            if (GenericSuffixWords.Contains(suffix)) continue;
            var clash = allNames.Any(other =>
                !string.Equals(other, n, System.StringComparison.Ordinal) && other.EndsWith(suffix, System.StringComparison.Ordinal));
            if (clash) continue;
            var a = Normalize(new[] { suffix });
            if (a.Length >= 2 && hay.Contains(a, System.StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>全称或别名命中。</summary>
    public static bool Hit(string hay, string? name, IReadOnlyList<string> allNames) =>
        HitByName(hay, name) || HitByAlias(hay, name, allNames);

    /// <summary>收集道具名 + 特效名，作为别名唯一性的判定范围（跨两类查冲突，避免道具与特效互相误绑）。</summary>
    public static List<string> CollectNames(IEnumerable<string?> propNames, IEnumerable<string?> effectNames) =>
        propNames.Concat(effectNames)
                 .Select(x => (x ?? "").Trim())
                 .Where(x => x.Length > 0)
                 .ToList();

    /// <summary>通用类别词：不做别名，避免「系统」「道具」这类词把不相关资产绑上来。</summary>
    private static readonly HashSet<string> GenericSuffixWords = new(System.StringComparer.Ordinal)
    {
        "系统", "道具", "物品", "东西", "场景", "画面", "镜头", "特效", "背景",
        "环境", "声音", "灯光", "光线", "影子", "人物", "角色", "部分", "装置", "机器", "设备"
    };
}
