using ManhuaPipeline.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace ManhuaPipeline.Services;

/// <summary>
/// 「分镜帧 ↔ 资产」绑定的重算。以前这段逻辑只活在 StageController 里，
/// 由老流水线（Stage 5 完成 / Stage 9 生成前 / 手动保存分镜）触发；
/// 而 skill-studio 那条新流水线（P3 导演分镜）只写 StoryboardFrames，不重算绑定——
/// 于是分镜页「引用资产」那一列永远是空的，可同一个项目在终稿提示词页却看得到引用。
/// 抽出来，两条流水线共用同一套算法。
/// </summary>
public static class FrameBindingService
{
    public static void RebindFrames(DbService db, ILogger logger, int projectId)
    {
        if (projectId <= 0) return;
        try
        {
            var frames = db.GetAllFrames(projectId);
            if (frames.Count == 0) return;               // 还没排分镜，没什么可绑

            // 镜头号跟单元号对不上时先修正：绑定是按单元号继承的，编号乱了会继承错
            int normalized = 0;
            try { normalized = db.NormalizeFrameShotNumbers(projectId); }
            catch (Exception ex) { logger.LogWarning(ex, "[分镜绑定] 镜头号规范化失败，按原编号继续。项目 {ProjectId}", projectId); }
            if (normalized > 0)
            {
                logger.LogWarning("[分镜绑定] 项目 {ProjectId} 修正了 {Count} 条镜头号与单元号不一致的分镜帧", projectId, normalized);
                frames = db.GetAllFrames(projectId);
            }

            var characters = db.GetCharacterAssets(projectId);
            var environments = db.GetEnvAssets(projectId);
            var props = db.GetPropAssets(projectId);
            var effects = db.GetEffectAssets(projectId);
            var unitBindingsByUnit = LoadUnitBindingsByUnit(db, logger, projectId);

            var all = new List<FrameAssetBinding>();
            foreach (var frame in frames)
                all.AddRange(FrameAssetBindingResolver.Resolve(
                    frame, characters, environments, props, effects,
                    UnitBindingsForFrame(unitBindingsByUnit, frame)).Bindings);

            db.ReplaceFrameAssetBindings(projectId, all);

            var catCount = string.Join(", ", all.GroupBy(b => b.Category).Select(g => $"{g.Key}={g.Count()}"));
            logger.LogInformation("[分镜绑定] 项目 {ProjectId} 已按 {FrameCount} 个分镜帧重算资产绑定：共 {BindingCount} 条（有图 {ImageCount}）[{Category}]，继承 {UnitCount} 个单元的绑定",
                projectId, frames.Count, all.Count, all.Count(b => b.HasImage), catCount, unitBindingsByUnit.Count);
        }
        catch (SqlException ex) when (ex.Number is 208 or 2812)
        {
            logger.LogWarning(ex, "[分镜绑定] FrameAssetBindings 表不存在，已跳过自动绑定。请先执行 ManhuaPipeline/Database/Upgrade_FrameAssetBindings.sql。项目 {ProjectId}", projectId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[分镜绑定] 重算分镜资产绑定失败（不影响分镜本身）。项目 {ProjectId}", projectId);
        }
    }

    /// <summary>按单元号索引「单元↔资产」绑定（单元号形如 1.3，已含集号），供逐镜头继承。
    /// 表缺失或读取失败时返回空字典，帧绑定退回「只按本镜字段解析」的旧行为。</summary>
    private static Dictionary<string, List<UnitAssetBinding>> LoadUnitBindingsByUnit(
        DbService db, ILogger logger, int projectId)
    {
        var map = new Dictionary<string, List<UnitAssetBinding>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var b in db.GetUnitAssetBindings(projectId))
            {
                var key = (b.UnitNumber ?? "").Trim();
                if (key.Length == 0) continue;
                if (!map.TryGetValue(key, out var list)) { list = new List<UnitAssetBinding>(); map[key] = list; }
                list.Add(b);
            }
            foreach (var list in map.Values)
                list.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
        }
        catch (SqlException ex) when (ex.Number is 208 or 2812)
        {
            logger.LogWarning(ex, "[分镜绑定] UnitAssetBindings 表不存在，镜头绑定不继承单元资产。请先执行 ManhuaPipeline/Database/Upgrade_UnitAssetBindings.sql。项目 {ProjectId}", projectId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[分镜绑定] 读取单元资产绑定失败，镜头绑定不继承单元资产。项目 {ProjectId}", projectId);
        }
        return map;
    }

    /// <summary>取某镜头所属单元的绑定；单元号缺失或单元未绑定时返回 null（退回旧行为）。</summary>
    private static IReadOnlyList<UnitAssetBinding>? UnitBindingsForFrame(
        IReadOnlyDictionary<string, List<UnitAssetBinding>> unitBindingsByUnit, StoryboardFrame frame)
    {
        var key = (frame.UnitNumber ?? "").Trim();
        return key.Length > 0 && unitBindingsByUnit.TryGetValue(key, out var list) ? list : null;
    }
}
