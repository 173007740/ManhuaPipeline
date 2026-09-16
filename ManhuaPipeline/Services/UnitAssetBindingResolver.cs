using System.Text.RegularExpressions;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services;

/// <summary>
/// 分集细化单元（Stage 4 的 StageUnit）→ 项目资产的确定引用解析。
/// Stage 4 完成后对每个【单元X.Y】执行，把结果整表替换进 UnitAssetBindings，
/// 供 Stage 5 分镜继承（本单元只允许使用已绑资产），防止分镜/提示词引入单元外资产。
/// 规则：
///   1. 场景只认「地点」字段（地点缺省时才回落到核心动作/关键元素等结构化字段），不拿整段文本猜；
///   2. 角色交给 CharacterNameMatcher：规范名/别名正向命中，再用「关键元素」名单里的简称做反向兜底
///      （正文写「洛伊娅」而资产规范名是「洛伊娅维娜」也能绑上）；片段能对应多个资产时判为歧义，不绑并报问题；
///   3. 道具/特效在单元的结构化内容里精确命中资产库规范名（歧义不处理，宁缺毋滥）；
///   4. 与帧绑定保持一致：AssetId 仅作库内外键，正文与提示词只引用规范资产名。
/// </summary>
public static class UnitAssetBindingResolver
{
    public static List<UnitAssetBinding> Resolve(
        int projectId,
        StageUnit unit,
        IReadOnlyList<CharacterAsset> characters,
        IReadOnlyList<EnvironmentAsset> environments,
        IReadOnlyList<PropAsset> props,
        IReadOnlyList<EffectAsset> effects)
        => Resolve(projectId, unit, characters, environments, props, effects, out _);

    /// <summary>同上，并输出「角色引用待确认」问题（歧义片段、关键元素未命中角色）。</summary>
    public static List<UnitAssetBinding> Resolve(
        int projectId,
        StageUnit unit,
        IReadOnlyList<CharacterAsset> characters,
        IReadOnlyList<EnvironmentAsset> environments,
        IReadOnlyList<PropAsset> props,
        IReadOnlyList<EffectAsset> effects,
        out List<string> issues)
    {
        issues = new List<string>();
        var result = new List<UnitAssetBinding>();
        if (unit == null) return result;

        var bound = new HashSet<(string Category, int AssetId)>();
        int order = 0;
        // 已并入某角色卡「随身物品」的道具：外观已随该角色卡一起提供，本单元不再单独绑定、不占参考图槽
        var absorbedProps = PersonalItemResolver.BuildAbsorbedPropMap(characters, props);

        void Add(string category, int assetId, string name, bool hasImage)
        {
            if (bound.Contains((category, assetId))) return;
            bound.Add((category, assetId));
            result.Add(new UnitAssetBinding
            {
                ProjectId = projectId,
                EpisodeNumber = unit.EpisodeNumber,
                UnitNumber = unit.UnitNumber,
                Category = category,
                AssetId = assetId,
                Name = name,
                HasImage = hasImage,
                SortOrder = order++
            });
        }

        // 结构化内容区（去掉类型/时长/模板等元信息，避免把元信息里的名字当出镜资产）。
        var contentFields = new[] { unit.Location, unit.CoreAction, unit.StartState, unit.EndState, unit.Dialogue, unit.KeyElements };
        var contentHay = HayNormalize(contentFields);
        // 场景回溯区：地点缺失时用它兜底（只用于场景判断）。
        var sceneHay = string.IsNullOrWhiteSpace(unit.Location)
            ? HayNormalize(new[] { unit.CoreAction, unit.KeyElements, unit.StartState, unit.EndState })
            : "";

        if (!string.IsNullOrWhiteSpace(contentHay))
        {
            // 角色：规范名/别名正向命中；未命中的资产再用「关键元素」名单里的简称做反向兜底（多义不绑）。
            var unitTag = $"单元{unit.UnitNumber}";
            var charText = string.Join("\n", contentFields.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f!));
            var charMatch = CharacterNameMatcher.Match(characters, charText, unit.KeyElements);
            foreach (var c in charMatch.Matched)
                Add("Character", c.AssetId, c.Name, HasImage(c.ImageUrl));
            foreach (var issue in charMatch.Issues)
                issues.Add($"{unitTag}：{issue}");
            if (charMatch.Matched.Count == 0 && ExpectsCharacter(unit))
                issues.Add($"{unitTag}：关键元素「{Truncate(unit.KeyElements, 24)}」没有命中任何角色资产，该单元不会引用角色参考图");

            // 与 Stage 5 帧绑定同一套口径：全称命中 + 唯一后缀核心词兜底（单元摘要写「蓝色面板」也能绑上「回声系统面板」），
            // 否则 Stage 4 漏绑、Stage 5 只在个别镜头补绑，同一件东西首尾两个镜头仍会用两套视觉来源。
            var aliasScope = AssetAliasMatcher.CollectNames(
                (props ?? new List<PropAsset>()).Select(p => p.Name),
                (effects ?? new List<EffectAsset>()).Select(e => e.Name));

            foreach (var p in props ?? new List<PropAsset>())
            {
                if (!AssetAliasMatcher.Hit(contentHay, p.Name, aliasScope)) continue;
                if (absorbedProps.TryGetValue(p.AssetId, out var ownerName))
                {
                    issues.Add($"{unitTag}：道具「{p.Name}」已并入角色卡「{ownerName}」的随身物品，本单元不再单独绑定参考图");
                    continue;
                }
                Add("Prop", p.AssetId, p.Name, HasImage(p.ImageUrl));
            }
            foreach (var fx in effects ?? new List<EffectAsset>())
            {
                if (AssetAliasMatcher.Hit(contentHay, fx.Name, aliasScope))
                    Add("Effect", fx.AssetId, fx.Name, HasImage(fx.ImageUrl));
            }
        }

        // 场景：只认「地点」字段；地点缺省时在场景回溯区找环境资产。
        var envHay = string.IsNullOrWhiteSpace(unit.Location) ? sceneHay : HayNormalize(new[] { unit.Location });
        if (!string.IsNullOrWhiteSpace(envHay))
        {
            foreach (var e in environments ?? new List<EnvironmentAsset>())
            {
                var n = HayNormalize(new[] { e.Name });
                if (n.Length > 0 && envHay.Contains(n, StringComparison.Ordinal))
                    Add("Environment", e.AssetId, e.Name, HasImage(e.ImageUrl));
            }
        }

        return result;
    }

    private static bool HasImage(string? url) =>
        !string.IsNullOrWhiteSpace(url);

    private static string HayNormalize(IEnumerable<string?> fields) =>
        string.Concat(fields.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f!.Trim())).Replace(" ", "").ToLowerInvariant();

    /// <summary>「关键元素」照抄模板占位词（人物/道具/环境）的写法。</summary>
    private static readonly Regex TemplatePlaceholderRegex = new(
        @"^(人物|角色|主角|配角|道具|环境|特效)([/、,，]?(人物|角色|主角|配角|道具|环境|特效))*$",
        RegexOptions.Compiled);

    /// <summary>该单元是否「本该有角色却没绑上」：有非空台词，或「关键元素」照抄了输出格式里的占位说明。</summary>
    private static bool ExpectsCharacter(StageUnit unit)
    {
        var key = (unit.KeyElements ?? "").Trim();
        if (key.Length == 0) return false;
        if (TemplatePlaceholderRegex.IsMatch(key)) return true;
        var dialogue = (unit.Dialogue ?? "").Trim();
        return dialogue.Length > 0 && !dialogue.Equals("无", StringComparison.OrdinalIgnoreCase);
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Length <= max ? t : t.Substring(0, max) + "…";
    }
}
