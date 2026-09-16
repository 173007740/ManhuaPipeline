using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ManhuaPipeline.Models;
using Microsoft.Extensions.Logging;

namespace ManhuaPipeline.Services;

/// <summary>
/// L3 关键帧层（每剧情节点一张，8-16 张/集）。
///
/// 职责划分（关键设计）：
///   • <b>选点是系统确定性行为</b>（<see cref="SelectCandidates"/>）：不靠模型随手挑，
///     而是按「开场/单元首镜/状态变化/信息增量/打斗/收尾」规则从分镜里挑，再裁剪到 8-16 张，
///     保证覆盖全集、每集张数可控、重跑结果稳定。
///   • <b>写细是模型的事</b>：模型只把候选节点写成构图 + 锁定项 + 图像提示词；
///     模型失败或数量对不上时降级为按分镜字段拼装（Source=fallback），页面照常可见。
///
/// 生成方式为人工触发（关键帧页按钮）：未生成关键帧的项目，阶段 9 行为与以前完全一致。
/// 表结构见 Database/Upgrade_关键帧层.sql。
/// </summary>
public class KeyframePlanService
{
    /// <summary>每集最少/最多关键帧张数（对标 short-drama-agent 节 15：8-16 张）。</summary>
    public const int MinPerEpisode = 8;
    public const int MaxPerEpisode = 16;

    private readonly DbService _db;
    private readonly LLMService _llm;
    private readonly ContinuityExtractionService _continuity;
    private readonly ILogger<KeyframePlanService> _logger;

    public KeyframePlanService(DbService db, LLMService llm, ContinuityExtractionService continuity, ILogger<KeyframePlanService> logger)
    {
        _db = db;
        _llm = llm;
        _continuity = continuity;
        _logger = logger;
    }

    // ========== 选点（确定性） ==========

    /// <summary>
    /// 从分镜里挑出候选剧情节点：每集 8-16 个，按时间线顺序返回。
    /// 规则优先级：开场/收尾(P1) &gt; 状态变化/信息增量/打斗(P2) &gt; 单元首镜(P3) &gt; 均匀补点(P9)。
    /// </summary>
    public List<KeyframeCandidate> SelectCandidates(List<StoryboardFrame> frames)
    {
        var result = new List<KeyframeCandidate>();
        if (frames == null || frames.Count == 0) return result;

        var episodes = frames
            .GroupBy(f => f.EpisodeNumber ?? 0)
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var ep in episodes)
        {
            var ordered = ep
                .OrderBy(f => f.UnitOrder)
                .ThenBy(f => f.SortOrder)
                .ThenBy(f => f.FrameNumber)
                .ThenBy(f => f.FrameId)
                .ToList();
            if (ordered.Count == 0) continue;

            // frameId → 候选（同帧被多条规则命中时保留优先级最小的那条）
            var picked = new Dictionary<int, KeyframeCandidate>();
            void Add(StoryboardFrame f, string reason, int priority)
            {
                if (picked.TryGetValue(f.FrameId, out var exist))
                {
                    if (exist.Priority <= priority) return;
                }
                picked[f.FrameId] = new KeyframeCandidate
                {
                    EpisodeNumber = f.EpisodeNumber ?? 0,
                    UnitNumber = f.UnitNumber?.Trim() ?? "",
                    ShotLabel = f.ShotNumber?.Trim() ?? f.FrameNumber.ToString(),
                    Reason = reason,
                    Priority = priority,
                    SourceText = BuildSourceText(f)
                };
            }

            Add(ordered[0], "开场钩子（第一镜）", 1);
            if (ordered.Count > 1) Add(ordered[^1], "收尾悬念（最后一镜）", 1);

            foreach (var unit in ordered.GroupBy(f => f.UnitNumber ?? ""))
            {
                var unitFrames = unit.ToList();
                var first = unitFrames[0];
                var isCombatUnit = IsCombat(unitFrames);
                if (isCombatUnit)
                {
                    // 打斗单元的节点取中段（交锋最密集处），比首镜更能锁住动作链
                    Add(unitFrames[unitFrames.Count / 2], "打斗节点（单元交锋中段）", 2);
                }
                else
                {
                    Add(first, "单元首镜（进入新场景/新状态）", 3);
                }
            }

            foreach (var f in ordered)
            {
                if (!string.IsNullOrWhiteSpace(f.StartState) || !string.IsNullOrWhiteSpace(f.EndState)
                    || !string.IsNullOrWhiteSpace(f.ForbiddenChanges))
                    Add(f, "状态锚点（起止状态/禁止变化）", 2);

                var info = f.NewInformation?.Trim();
                if (!string.IsNullOrWhiteSpace(info) && info != "无")
                    Add(f, "信息增量节点", 2);

                if (!string.IsNullOrWhiteSpace(f.CombatBeatIds))
                    Add(f, "打斗节拍节点", 2);
            }

            var selected = Trim(picked.Values.ToList(), ordered);
            // 按时间线顺序回填，保证关键帧顺序与剧情顺序一致
            foreach (var c in selected.OrderBy(c => FindIndex(ordered, c))) result.Add(c);
        }

        return result;
    }

    private static int FindIndex(List<StoryboardFrame> ordered, KeyframeCandidate c)
    {
        for (var i = 0; i < ordered.Count; i++)
        {
            var f = ordered[i];
            var label = f.ShotNumber?.Trim() ?? f.FrameNumber.ToString();
            if (f.EpisodeNumber == c.EpisodeNumber
                && string.Equals(f.UnitNumber?.Trim() ?? "", c.UnitNumber, StringComparison.Ordinal)
                && label == c.ShotLabel) return i;
        }
        return int.MaxValue;
    }

    /// <summary>裁剪到 [8,16]：先保 P1，再 P2、P3；不足 8 张时按均匀间隔补点。</summary>
    private static List<KeyframeCandidate> Trim(List<KeyframeCandidate> picked, List<StoryboardFrame> ordered)
    {
        var list = picked.ToList();
        if (list.Count > MaxPerEpisode)
        {
            var keepFirst = list.Where(c => c.Priority == 1).ToList();
            var rest = list.Where(c => c.Priority != 1).OrderBy(c => c.Priority).Take(MaxPerEpisode - keepFirst.Count).ToList();
            list = keepFirst.Concat(rest).ToList();
        }

        if (list.Count < MinPerEpisode && ordered.Count > list.Count)
        {
            var existing = new HashSet<string>(list.Select(c => c.EpisodeNumber + "|" + c.UnitNumber + "|" + c.ShotLabel));
            var need = MinPerEpisode - list.Count;
            var step = Math.Max(1, ordered.Count / MinPerEpisode);
            for (var i = 0; i < ordered.Count && need > 0; i += step)
            {
                var f = ordered[i];
                var label = f.ShotNumber?.Trim() ?? f.FrameNumber.ToString();
                var key = (f.EpisodeNumber ?? 0) + "|" + (f.UnitNumber?.Trim() ?? "") + "|" + label;
                if (existing.Contains(key)) continue;
                existing.Add(key);
                list.Add(new KeyframeCandidate
                {
                    EpisodeNumber = f.EpisodeNumber ?? 0,
                    UnitNumber = f.UnitNumber?.Trim() ?? "",
                    ShotLabel = label,
                    Reason = "常规节点（为覆盖全集补点）",
                    Priority = 9,
                    SourceText = BuildSourceText(f)
                });
                need--;
            }
        }
        return list;
    }

    private static bool IsCombat(List<StoryboardFrame> unitFrames)
    {
        if (unitFrames.Any(f => !string.IsNullOrWhiteSpace(f.CombatBeatIds))) return true;
        var type = unitFrames.Select(f => f.UnitType ?? "").FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "";
        return type.Contains("打斗", StringComparison.Ordinal) || type.Contains("战斗", StringComparison.Ordinal)
            || type.Contains("动作", StringComparison.Ordinal) || type.Contains("对决", StringComparison.Ordinal);
    }

    private static string BuildSourceText(StoryboardFrame f)
    {
        var sb = new StringBuilder();
        sb.AppendLine("第" + (f.EpisodeNumber ?? 0) + "集 单元" + (f.UnitNumber ?? "-") + " 镜头" + (f.ShotNumber ?? f.FrameNumber.ToString())
            + "（" + (f.UnitType ?? "-") + "，" + (f.Duration ?? "-") + "，景别" + (f.ShotSize ?? "-") + "）");
        void Line(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) sb.AppendLine("  " + label + "：" + value.Trim());
        }
        Line("场景", f.Scene);
        Line("镜头描述", f.Description);
        Line("构图方式", f.Composition);
        Line("出镜角色及表情", f.Characters);
        Line("对话/台词", f.Dialogue);
        Line("镜头运动", f.Camera);
        Line("单一动作", f.SingleAction);
        Line("起始画面", f.StartScene);
        Line("结束画面", f.EndScene);
        Line("起始状态", f.StartState);
        Line("结束状态", f.EndState);
        Line("衔接下一镜", f.NextConnection);
        Line("禁止变化", f.ForbiddenChanges);
        Line("新信息", f.NewInformation);
        Line("时间轴", f.Timeline);
        return sb.ToString().TrimEnd();
    }

    // ========== 生成（模型写细 + 降级） ==========

    /// <summary>
    /// 生成某项目的关键帧方案并整表落库。返回（写入行数, 说明）。
    /// 表不存在时返回 0 并给出提示，不抛异常（与阶段流程解耦）。
    /// </summary>
    public async Task<(int written, string message)> GenerateAsync(
        int projectId, string apiUrl, string apiKey, string model, string? thinkingMode = null)
    {
        if (!_db.KeyframeTableExists())
            return (0, "关键帧表不存在，请先执行 Database/Upgrade_关键帧层.sql 并重启应用");

        var frames = _db.GetAllFrames(projectId);
        if (frames.Count == 0)
            return (0, "项目还没有分镜脚本，请先完成阶段 5（分镜脚本）");

        var candidates = SelectCandidates(frames);
        if (candidates.Count == 0)
            return (0, "分镜为空，无法挑选剧情节点");

        var candidateText = string.Join("\n\n", candidates.Select((c, i) =>
            "【节点" + (i + 1) + "】" + c.Reason + "\n" + c.SourceText));

        var storyContext = BuildStoryContext(projectId);
        string? continuityText = null;
        try { continuityText = _continuity.BuildContinuityText(projectId); } catch { continuityText = null; }
        var assetCatalog = BuildAssetCatalog(projectId);

        KeyframePlanPayload? payload = null;
        try
        {
            payload = await _llm.GenerateKeyframes(candidateText, storyContext, Truncate(continuityText, 6000), assetCatalog,
                apiUrl, apiKey, model, thinkingMode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[关键帧层] 生成失败，降级为按分镜拼装。ProjectId={ProjectId}", projectId);
        }

        var rows = new List<ProjectKeyframe>();
        var perEpisodeSeq = new Dictionary<int, int>();

        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            var dto = payload?.Keyframes != null && i < payload.Keyframes.Count ? payload.Keyframes[i] : null;
            // SortOrder 在集内递增（页面按集分组展示 #1..#N），跨集顺序由 EpisodeNumber 保证
            perEpisodeSeq.TryGetValue(c.EpisodeNumber, out var seq);
            perEpisodeSeq[c.EpisodeNumber] = ++seq;
            var row = new ProjectKeyframe
            {
                ProjectId = projectId,
                EpisodeNumber = c.EpisodeNumber,
                SortOrder = seq,
                NodeLabel = FirstNonEmpty(dto?.NodeLabel, c.Reason),
                NodeReason = FirstNonEmpty(dto?.NodeReason, c.Reason),
                ShotLabel = FirstNonEmpty(dto?.ShotLabel, c.ShotLabel),
                UnitNumber = FirstNonEmpty(dto?.UnitNumber, c.UnitNumber),
                Composition = dto?.Composition,
                LockedCharacters = dto?.LockedCharacters,
                LockedProps = dto?.LockedProps,
                LockedSceneDirection = dto?.LockedSceneDirection,
                ClueVisible = dto?.ClueVisible,
                NextConnection = dto?.NextConnection,
                ImagePrompt = dto?.ImagePrompt,
                Status = "draft",
                Source = dto == null ? "fallback" : "llm"
            };

            if (dto == null) FillFallbackFromSource(row, c);
            rows.Add(row);
        }

        // 只要有任何一行是模型写的，整体就记录 llm，便于页面区分；逐行 source 已单独标注
        var written = _db.ReplaceKeyframes(projectId, rows);
        var llmCount = rows.Count(r => r.Source == "llm");
        var message = written == 0
            ? "关键帧方案写入失败（表不可用）"
            : "已生成 " + written + " 张关键帧（模型 " + llmCount + " 张 / 降级 " + (written - llmCount) + " 张）";
        return (written, message);
    }

    /// <summary>模型没返回或回退时，用分镜字段拼一份可用（但不精细）的关键帧。</summary>
    private static void FillFallbackFromSource(ProjectKeyframe row, KeyframeCandidate c)
    {
        var lines = c.SourceText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        string Get(string label)
        {
            var prefix = label + "：";
            var hit = lines.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
            return hit == null ? "" : hit.Substring(prefix.Length).Trim();
        }

        row.Composition = FirstNonEmpty($"【{Get("景别")}】{Get("构图方式")}", Get("镜头描述"));
        row.LockedCharacters = Get("出镜角色及表情");
        row.LockedProps = "无";
        row.LockedSceneDirection = Get("场景");
        row.ClueVisible = FirstNonEmpty(Get("新信息"), "无");
        row.NextConnection = FirstNonEmpty(Get("衔接下一镜"), Get("结束画面"));
        row.ImagePrompt = "关键帧（降级拼装）：" + string.Join("；", new[]
        {
            Get("场景"), Get("构图方式"), Get("出镜角色及表情"), Get("镜头描述")
        }.Where(s => !string.IsNullOrWhiteSpace(s))) +
            "。无字幕无文字，画面不出现可读文字（屏幕/纸面一律留白）。";
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private string? BuildStoryContext(int projectId)
    {
        var parts = new List<string>();
        var stage2 = SafeStage(projectId, 2);
        if (!string.IsNullOrWhiteSpace(stage2)) parts.Add("◆ 故事分析\n" + Truncate(stage2, 2500));
        var stage3 = SafeStage(projectId, 3);
        if (!string.IsNullOrWhiteSpace(stage3)) parts.Add("◆ 分集蓝图\n" + Truncate(stage3, 2500));
        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    private string? SafeStage(int projectId, int stageNumber)
    {
        try
        {
            var d = _db.GetStageData(projectId, stageNumber);
            var text = d?.LlmResponse ?? d?.Content;
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch { return null; }
    }

    private string? BuildAssetCatalog(int projectId)
    {
        try
        {
            var parts = new List<string>();
            var chars = _db.GetCharacterAssets(projectId).Select(a => a.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
            if (chars.Count > 0) parts.Add("角色：" + string.Join("、", chars));
            var envs = _db.GetEnvAssets(projectId).Select(a => a.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
            if (envs.Count > 0) parts.Add("场景：" + string.Join("、", envs));
            var props = _db.GetPropAssets(projectId).Select(a => a.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
            if (props.Count > 0) parts.Add("道具：" + string.Join("、", props));
            return parts.Count == 0 ? null : string.Join("\n", parts);
        }
        catch { return null; }
    }

    private static string? Truncate(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        return text.Length <= max ? text : text.Substring(0, max) + "…";
    }

    // ========== 阶段 9 锚定注入 ==========

    /// <summary>
    /// 把某集的关键帧方案渲染成注入阶段 9 的锚定文本；该集没有关键帧时返回 null（阶段 9 行为不变）。
    /// </summary>
    public string? BuildAnchorText(int projectId, int episodeNumber)
    {
        try
        {
            var list = _db.GetKeyframes(projectId);
            if (list.Count == 0) return null;

            var scoped = list
                .Where(k => k.EpisodeNumber == episodeNumber || k.EpisodeNumber == 0)
                .OrderBy(k => k.EpisodeNumber)
                .ThenBy(k => k.SortOrder)
                .ToList();
            if (scoped.Count == 0) return null;

            var sb = new StringBuilder();
            sb.AppendLine("本集已锁定 " + scoped.Count + " 张关键帧（L3 层）。关键帧定死的东西，后续镜头禁止漂移：角色站位与朝向、道具状态、场景朝向、线索可见性、镜头衔接一律以本表为准。");
            foreach (var k in scoped)
            {
                sb.Append("◆ 节点" + (k.SortOrder > 0 ? k.SortOrder.ToString() : "-") + " ");
                sb.AppendLine((k.NodeLabel ?? "关键帧") + "（镜头 " + (k.ShotLabel ?? "-") + "）");
                void Line(string label, string? value)
                {
                    if (!string.IsNullOrWhiteSpace(value)) sb.AppendLine("  " + label + "：" + value.Trim());
                }
                Line("构图", k.Composition);
                Line("锁定站位", k.LockedCharacters);
                Line("锁定道具", k.LockedProps);
                Line("场景朝向", k.LockedSceneDirection);
                Line("线索可见性", k.ClueVisible);
                Line("衔接下一帧", k.NextConnection);
            }
            return sb.ToString().TrimEnd();
        }
        catch
        {
            return null;
        }
    }
}
