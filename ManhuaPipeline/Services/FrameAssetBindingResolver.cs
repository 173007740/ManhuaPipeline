using System.Text.RegularExpressions;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services;

/// <summary>
/// 逐帧把 StoryboardFrame 的「出镜角色 / 场景 / 道具 / 特效」解析成对项目资产库的确定引用。
/// 规则与 Stage 9 旧逻辑（BuildH3ReferenceMaterialList）的关键差异：
///   1. 命中范围限定在「本镜结构化字段」，绝不退化为全量资产名单（消灭 LLM 自选/错位）；
///   2. 角色：在「出镜角色」字段内走 CharacterNameMatcher（规范名/别名正向包含 + 名单片段反向兜底），
///      兼容「遐蝶——表情」「遐蝶（表情）；玻吕茜亚（表情）」「遐蝶、玻吕茜亚」，
///      以及正文写简称「洛伊娅」而资产规范名是「洛伊娅维娜」的自由写法；
///      片段能对应多个角色资产时判为歧义，不绑并报问题；绝不拿台词/镜头描述等其它字段去猜角色（避免引用镜头外人物）；
///   3. 任何"没对上"都输出问题，而不是静默退化；
///   4. 单元继承：传入该镜头所属单元的 Stage 4 绑定（UnitAssetBindings）时，单元已绑资产必须出现在
///      本单元拆出的每一个镜头里（不论该单元被拆成多少个镜头），本镜自身字段只在单元绑定之外做补充。
/// </summary>
public static class FrameAssetBindingResolver
{
    public sealed class ResolveResult
    {
        public List<FrameAssetBinding> Bindings { get; } = new();
        public List<string> Issues { get; } = new();
    }

    public static ResolveResult Resolve(
        StoryboardFrame frame,
        IReadOnlyList<CharacterAsset> characters,
        IReadOnlyList<EnvironmentAsset> environments,
        IReadOnlyList<PropAsset> props,
        IReadOnlyList<EffectAsset> effects,
        IReadOnlyList<UnitAssetBinding>? unitBindings = null)
    {
        var result = new ResolveResult();
        if (frame == null || frame.FrameId <= 0) return result;
        var shotTag = $"镜头 {frame.ShotNumber ?? frame.FrameNumber.ToString()}（{frame.UnitNumber ?? "?"}）";
        var bound = new HashSet<(string Category, int AssetId)>();
        int order = 0;
        // 已并入某角色卡「随身物品」的道具：外观随角色卡一起提供，本镜不再单独绑定（不占参考图槽）
        var absorbedProps = PersonalItemResolver.BuildAbsorbedPropMap(characters, props);

        void Add(string category, int assetId, string name, bool hasImage)
        {
            if (bound.Contains((category, assetId))) return;
            bound.Add((category, assetId));
            result.Bindings.Add(new FrameAssetBinding
            {
                ProjectId = frame.ProjectId,
                FrameId = frame.FrameId,
                Category = category,
                AssetId = assetId,
                Name = name,
                HasImage = hasImage,
                SortOrder = order++
            });
        }

        // ---- 0) 单元继承：分集细化该单元已绑定的资产，必须出现在本单元拆分出的每一个镜头里 ----
        // 顺序即 @图N 顺序：先按单元绑定顺序铺底（同一单元的所有镜头共享同一前缀），再由本镜字段补充未覆盖的资产。
        if (unitBindings != null && unitBindings.Count > 0)
        {
            foreach (var ub in unitBindings.OrderBy(b => b.SortOrder))
            {
                if (string.IsNullOrWhiteSpace(ub.Name)) continue;
                // 历史数据可能仍带着已并入角色卡的随身道具：这里再兜一层，保证帧绑定与提示词一致
                if (ub.Category == "Prop" && absorbedProps.ContainsKey(ub.AssetId)) continue;
                Add(ub.Category, ub.AssetId, ub.Name, ub.HasImage);
            }
        }

        // ---- 1) 角色：只认 Characters 字段 ----
        // 出镜角色是自由文本（可能为「遐蝶——表情描述」「遐蝶（表情）；玻吕茜亚（表情）」「遐蝶、玻吕茜亚」等），
        // 统一交给 CharacterNameMatcher：规范名/别名正向整段包含 + 名单片段反向兜底
        // （正文写「洛伊娅」而资产规范名是「洛伊娅维娜」也能绑上），与单元绑定(UnitAssetBindingResolver)同口径。
        var charText = (frame.Characters ?? "").Trim();
        if (CharNormalize(charText).Length > 0 && !IsNoCharacterPlaceholder(charText))
        {
            var charMatch = CharacterNameMatcher.Match(characters, charText, charText);
            foreach (var c in charMatch.Matched)
                Add("Character", c.AssetId, c.Name, HasImage(c.ImageUrl));
            if (charMatch.Matched.Count == 0)
                result.Issues.Add($"{shotTag}：出镜角色名单「{Truncate(charText, 30)}」没有命中任何角色资产（可为群像/配角，届时走自然语言兜底，不绑参考图）");
            foreach (var issue in charMatch.Issues)
                result.Issues.Add($"{shotTag}：{issue}");
        }

        // ---- 2/3/4) 环境 / 道具 / 特效：在本镜结构化文本里精确命中所属类别资产 ----
        // 命中区 = 本镜的结构化描述区。故意不含台词/对白，避免引用到镜头外的名字。
        var fields = new[] { frame.StartScene, frame.Scene, frame.EndScene, frame.Composition, frame.Timeline, frame.Description };
        var hay = HayNormalize(fields);

        if (!string.IsNullOrWhiteSpace(hay))
        {
            // 别名词兜底：分镜正文常写简称（「蓝色面板」「轿车」），资产库里是全称（「回声系统面板」「黑色轿车」），
            // 只按全称匹配会静默漏绑，导致该镜头拿不到参考图、跨镜头外观不一致。
            var aliasScope = AssetAliasMatcher.CollectNames(
                (props ?? new List<PropAsset>()).Select(p => p.Name),
                (effects ?? new List<EffectAsset>()).Select(e => e.Name));

            var matchedEnv = false;
            foreach (var e in environments ?? new List<EnvironmentAsset>())
            {
                var n = HayNormalize(new[] { e.Name });
                if (n.Length > 0 && hay.Contains(n, StringComparison.Ordinal))
                {
                    matchedEnv = true;
                    Add("Environment", e.AssetId, e.Name, HasImage(e.ImageUrl));
                }
            }
            var sceneText = HayNormalize(new[] { frame.StartScene, frame.Scene, frame.EndScene });
            // 镜头明确写了场景但环境资产一个都没命中 → 提示（场景名未入资产库）
            if (!matchedEnv && !string.IsNullOrWhiteSpace(sceneText) && (environments?.Count ?? 0) > 0)
            {
                var preview = (frame.Scene ?? frame.StartScene ?? frame.EndScene ?? "").Trim();
                result.Issues.Add($"{shotTag}：场景字段「{Truncate(preview, 24)}」没有命中任何环境资产，该镜将无场景参考图");
            }

            foreach (var p in props ?? new List<PropAsset>())
            {
                if (absorbedProps.ContainsKey(p.AssetId)) continue;
                if (AssetAliasMatcher.Hit(hay, p.Name, aliasScope))
                    Add("Prop", p.AssetId, p.Name, HasImage(p.ImageUrl));
            }
            foreach (var fx in effects ?? new List<EffectAsset>())
            {
                if (AssetAliasMatcher.Hit(hay, fx.Name, aliasScope))
                    Add("Effect", fx.AssetId, fx.Name, HasImage(fx.ImageUrl));
            }
        }

        foreach (var b in result.Bindings)
        {
            if (!b.HasImage)
                result.Issues.Add($"{shotTag}：绑定资产「{b.Name}」参考图未就绪（资产卡无图，生成提示词前需出图）");
        }

        return result;
    }

    /// <summary>是否空角色占位（如「无」），空镜不绑角色也不报缺资产。</summary>
    private static bool IsNoCharacterPlaceholder(string text)
    {
        var hay = CharNormalize(text);
        return Regex.IsMatch(hay, @"^(无|无出镜|无角色|无人物|无出镜角色|无出镜人物|无面部出镜|无人|没有|空|黑屏|黑场|none|n/a|-|—|~|—{2,})$", RegexOptions.IgnoreCase);
    }

    private static bool HasImage(string? url) =>
        !string.IsNullOrWhiteSpace(url);

    private static string HayNormalize(IEnumerable<string?> fields) =>
        string.Concat(fields.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f!.Trim())).Replace(" ", "").ToLowerInvariant();

    /// <summary>角色名单用归一化：去掉括号段、空白、小写，便于比较别名与形态变体。</summary>
    private static string CharNormalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = Regex.Replace(s, @"[（(][^)）]*[)）]", "");
        return t.Replace(" ", "").Trim().ToLowerInvariant();
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Length <= max ? t : t.Substring(0, max) + "…";
    }
}
