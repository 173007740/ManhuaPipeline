using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services;

/// <summary>
/// 资产卡 ← 资源库图片 的自动绑定。
///
/// 场景：无限画布里把图做好 → 图片上的「存资源库」把图存进用户级参考图库（存的时候标签选了项目名、
/// 文件名默认就是节点标题，也就是资产名）。回到角色/道具/环境/特效资产页点「自动绑定图片」，
/// 就按 项目名过滤资源库 → 资产名匹配图片名 → 把命中的图片 URL 写回资产卡的 ImageUrl。
///
/// 匹配规则（打分，只取分最高的那张）：
///   200  资源库记录的 SourceKey 正好是这条资产的自动出图来源（asset:{projectId}:{category}:{assetId}）
///   100  图片文件名（去扩展名）与资产名完全相同
///    80  文件名以资产名开头（「林越_01」「林越 正面」这类一张资产出多张的命名）
///    60  文件名包含资产名
///    45  资产名包含文件名（文件名是资产名的简称，如「面板」对「回声系统面板」）
///     再 +5：图片的子类型与当前分类一致（角色/道具/环境/特效）
/// 同分时取后存入的那张（AssetId 更大 = 更晚），保证「重新存一张同名图」能顶掉旧的。
/// 低于 45 分不绑 —— 宁可列出来让人手动绑，也不要把不相干的图挂到资产上。
/// </summary>
public static class AssetImageAutoBinder
{
    public const string Characters = "characters";
    public const string Props = "props";
    public const string Environments = "environments";
    public const string Effects = "effects";

    /// <summary>绑定结果里的一条明细（Bound / Skipped / Unmatched 共用）。</summary>
    public sealed class AssetImageBindItem
    {
        public int AssetId { get; set; }
        public string AssetName { get; set; } = "";
        public string ImageUrl { get; set; } = "";
        public string FileName { get; set; } = "";
        public int Score { get; set; }
        /// <summary>为真表示这次覆盖掉了一张旧图（原本 ImageUrl 就有值）。</summary>
        public bool Replaced { get; set; }
        public string Reason { get; set; } = "";
    }

    public sealed class AssetImageBindResult
    {
        public string Category { get; set; } = "";
        public string ProjectName { get; set; } = "";
        /// <summary>实际用来过滤资源库的标签：优先项目名，项目名过滤为空时退化成项目标签。</summary>
        public string FilterTag { get; set; } = "";
        public int LibraryCount { get; set; }
        public int Total { get; set; }
        public List<AssetImageBindItem> Bound { get; } = new();
        public List<AssetImageBindItem> Skipped { get; } = new();
        public List<AssetImageBindItem> Unmatched { get; } = new();
        /// <summary>为空表示正常执行；非空是一句可直接显示给用户的话（资源库里没有本项目的图等）。</summary>
        public string? Warning { get; set; }
    }

    /// <summary>分类名归一化；不是四类之一时返回 null（路由里已经保证了，这里再兜一层）。</summary>
    public static string? NormalizeCategory(string? category)
    {
        var c = (category ?? "").Trim().ToLowerInvariant();
        return c switch
        {
            Characters or "character" or "角色" => Characters,
            Props or "prop" or "道具" => Props,
            Environments or "environment" or "环境" => Environments,
            Effects or "effect" or "特效" => Effects,
            _ => null
        };
    }

    public static string SubCategoryOf(string category) => category switch
    {
        Characters => "角色",
        Environments => "环境",
        Effects => "特效",
        _ => "道具"
    };

    /// <summary>给一个分类做自动绑定。overwrite=false 时已有图的资产跳过不动。</summary>
    public static AssetImageBindResult Bind(DbService db, int userId, int projectId, string category, bool overwrite)
    {
        var result = new AssetImageBindResult { Category = category };

        var project = db.GetProjectById(projectId);
        var projectName = (project?.Title ?? "").Trim();
        result.ProjectName = projectName;

        // 一次把该用户的资源库全取回来，在内存里按标签精确匹配。
        // 不走 SQL 的 Tags LIKE：那是模糊匹配，「纸上山河」会把「纸上山河2」的图也算进来，
        // 跨项目串图比少绑几张更难排查。
        var pool = db.GetReferenceAssets(userId);
        var byProjectName = string.IsNullOrEmpty(projectName)
            ? new List<ReferenceAsset>()
            : pool.Where(img => SplitTags(img.Tags).Any(t => Eq(t, projectName))).ToList();

        var filtered = byProjectName;
        var filterTag = projectName;
        if (filtered.Count == 0)
        {
            // 退化：项目名一个都没命中时，再按项目标签（项目页「项目标签」输入框里那串）试一次。
            // 老项目存图时可能只打了内容标签、没打项目名。
            var projectTags = SplitTags(project?.Tags);
            if (projectTags.Count > 0)
            {
                filtered = pool.Where(img => SplitTags(img.Tags).Any(t => projectTags.Any(pt => Eq(t, pt)))).ToList();
                filterTag = string.Join(",", projectTags);
            }
        }
        result.FilterTag = filterTag ?? "";

        var rows = LoadAssets(db, projectId, category, out var apply);
        result.Total = rows.Count;

        if (filtered.Count == 0)
        {
            result.Warning = string.IsNullOrEmpty(filterTag)
                ? "没有可用于过滤的标签（项目名和项目标签都是空的）。在画布里存资源库时，标签选本项目名即可。"
                : $"资源库里没有标签为「{filterTag}」的图片。\n在画布里点图片上的「存资源库」，标签选本项目名、文件名用资产名，就能自动绑上。";
            foreach (var r in rows) result.Unmatched.Add(new AssetImageBindItem { AssetId = r.AssetId, AssetName = r.Name });
            return result;
        }
        result.LibraryCount = filtered.Count;

        var sub = SubCategoryOf(category);
        foreach (var row in rows)
        {
            if (!overwrite && !string.IsNullOrWhiteSpace(row.ImageUrl))
            {
                result.Skipped.Add(new AssetImageBindItem
                {
                    AssetId = row.AssetId,
                    AssetName = row.Name,
                    ImageUrl = row.ImageUrl ?? "",
                    Reason = "已有图片（勾选「覆盖已有」才会替换）"
                });
                continue;
            }

            var sourceKey = $"asset:{projectId}:{category}:{row.AssetId}";
            ReferenceAsset? best = null;
            var bestScore = 0;
            foreach (var img in filtered)
            {
                var score = ScoreOf(img, row.Name, sub, sourceKey);
                if (score <= 0) continue;
                // 同分取后存入的（AssetId 更大的那张），重新存一张同名图能顶掉旧的
                if (score > bestScore || (score == bestScore && best != null && img.AssetId > best.AssetId))
                {
                    best = img;
                    bestScore = score;
                }
            }

            if (best == null || bestScore < 45)
            {
                result.Unmatched.Add(new AssetImageBindItem
                {
                    AssetId = row.AssetId,
                    AssetName = row.Name,
                    Reason = best == null ? "资源库里没有名字对得上的图片" : $"最像的一张「{best.FileName}」只有 {bestScore} 分，没到阈值"
                });
                continue;
            }

            var url = best.LocalPath;
            if (string.IsNullOrWhiteSpace(url))
            {
                result.Unmatched.Add(new AssetImageBindItem { AssetId = row.AssetId, AssetName = row.Name, Reason = "命中的资源库记录没有文件路径" });
                continue;
            }

            apply(row.AssetId, url);
            result.Bound.Add(new AssetImageBindItem
            {
                AssetId = row.AssetId,
                AssetName = row.Name,
                ImageUrl = url,
                FileName = best.FileName,
                Score = bestScore,
                Replaced = !string.IsNullOrWhiteSpace(row.ImageUrl)
            });
        }

        return result;
    }

    /// <summary>一次绑完四个分类（前端没用到，留给批量场景）。</summary>
    public static List<AssetImageBindResult> BindAll(DbService db, int userId, int projectId, bool overwrite) =>
        new[] { Characters, Props, Environments, Effects }
            .Select(c => Bind(db, userId, projectId, c, overwrite))
            .ToList();

    private sealed record AssetRow(int AssetId, string Name, string? ImageUrl);

    /// <summary>读出当前分类的资产，并给出「把图片 URL 写回这条资产」的动作（更新时带原字段，避免把描述清空）。</summary>
    private static List<AssetRow> LoadAssets(DbService db, int projectId, string category, out Action<int, string> apply)
    {
        var rows = new List<AssetRow>();
        switch (category)
        {
            case Characters:
            {
                var list = db.GetCharacterAssets(projectId);
                rows.AddRange(list.Select(a => new AssetRow(a.AssetId, a.Name ?? "", a.ImageUrl)));
                apply = (id, url) =>
                {
                    var a = list.FirstOrDefault(x => x.AssetId == id);
                    if (a == null) return;
                    db.UpdateCharacterAsset(projectId, id, a.Name, a.Description, url, a.Attributes);
                };
                break;
            }
            case Props:
            {
                var list = db.GetPropAssets(projectId);
                rows.AddRange(list.Select(a => new AssetRow(a.AssetId, a.Name ?? "", a.ImageUrl)));
                apply = (id, url) =>
                {
                    var a = list.FirstOrDefault(x => x.AssetId == id);
                    if (a == null) return;
                    db.UpdatePropAsset(projectId, id, a.Name, a.Description, url);
                };
                break;
            }
            case Environments:
            {
                var list = db.GetEnvAssets(projectId);
                rows.AddRange(list.Select(a => new AssetRow(a.AssetId, a.Name ?? "", a.ImageUrl)));
                apply = (id, url) =>
                {
                    var a = list.FirstOrDefault(x => x.AssetId == id);
                    if (a == null) return;
                    db.UpdateEnvAsset(projectId, id, a.Name, a.Description, url);
                };
                break;
            }
            default:
            {
                var list = db.GetEffectAssets(projectId);
                rows.AddRange(list.Select(a => new AssetRow(a.AssetId, a.Name ?? "", a.ImageUrl)));
                apply = (id, url) =>
                {
                    var a = list.FirstOrDefault(x => x.AssetId == id);
                    if (a == null) return;
                    db.UpdateEffectAsset(projectId, id, a.Name, a.Description, url);
                };
                break;
            }
        }
        return rows;
    }

    private static int ScoreOf(ReferenceAsset img, string assetName, string subCategory, string sourceKey)
    {
        // 本资产自动出图同步进库的那张：来源标记精确对应，直接认
        if (!string.IsNullOrEmpty(img.SourceKey) && Eq(img.SourceKey, sourceKey)) return 200;

        var file = Norm(Path.GetFileNameWithoutExtension(img.FileName ?? ""));
        var name = Norm(assetName);
        if (file.Length == 0 || name.Length == 0) return 0;

        int score;
        if (file == name) score = 100;
        else if (file.StartsWith(name, StringComparison.Ordinal)) score = 80;
        else if (file.Contains(name, StringComparison.Ordinal)) score = 60;
        else if (name.Length >= 2 && name.Contains(file, StringComparison.Ordinal)) score = 45;
        else return 0;

        if (Eq((img.SubCategory ?? "").Trim(), subCategory)) score += 5;
        return score;
    }

    /// <summary>归一化：去掉所有空白后转小写（中文不受影响）。「林 越」「林越_01」这类差异靠后面的包含判断兜住。</summary>
    private static string Norm(string s) =>
        new string((s ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

    private static bool Eq(string a, string b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static List<string> SplitTags(string? tags) =>
        (tags ?? "")
            .Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();
}
