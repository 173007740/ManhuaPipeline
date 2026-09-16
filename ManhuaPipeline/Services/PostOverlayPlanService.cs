using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services;

/// <summary>
/// L5 后期叠加清单：按镜头汇总「需要在后期叠加文字」的画面元素，供剪辑/后期按条施工。
///
/// 数据来源：阶段 9 提示词（<see cref="SeedancePrompt.PromptText"/>）；提示词还没生成时回退到
/// 阶段 5 分镜（<see cref="StoryboardFrame.Description"/> 等）。
/// 只读派生，不落库、不调 LLM —— 清单内容永远跟提示词保持一致，避免两处状态不一致。
/// </summary>
public class PostOverlayPlanService
{
    private readonly DbService _db;
    public PostOverlayPlanService(DbService db) { _db = db; }

    /// <summary>单条后期叠加任务。</summary>
    public sealed class OverlayItem
    {
        public int EpisodeNumber { get; set; }
        public string UnitName { get; set; } = "";
        public string ShotLabel { get; set; } = "";
        /// <summary>叠加分类，如「手机短信 / 新闻推送」。</summary>
        public string Categories { get; set; } = "";
        /// <summary>命中的触发词，便于快速确认依据。</summary>
        public string Cues { get; set; } = "";
        /// <summary>提示词/分镜中的上下文摘录，人工据此判断要叠什么文字。</summary>
        public string Excerpt { get; set; } = "";
        /// <summary>该镜头提示词是否已带留白约束（未带=需要重跑阶段 9）。</summary>
        public bool Guarded { get; set; }
    }

    public sealed class OverlayPlan
    {
        public List<OverlayItem> Items { get; set; } = [];
        /// <summary>分类 → 条目数。</summary>
        public Dictionary<string, int> CategoryCounts { get; set; } = new();
        public int ShotCount { get; set; }
        public int UnguardedCount { get; set; }
        public string RuleText { get; set; } = PostOverlayAnalyzer.RuleText;
    }

    public OverlayPlan Build(int projectId)
    {
        var plan = new OverlayPlan();

        var prompts = SafeGetPrompts(projectId);
        if (prompts.Count > 0)
        {
            plan.ShotCount = prompts.Count;
            foreach (var p in prompts)
            {
                var text = StripRefLines(p.PromptText ?? "");
                var hits = PostOverlayAnalyzer.Detect(text);
                if (hits.Count == 0) continue;
                var guarded = (p.PromptText ?? "").Contains(PostOverlayAnalyzer.RuleText, StringComparison.Ordinal);
                if (!guarded) plan.UnguardedCount++;
                plan.Items.Add(new OverlayItem
                {
                    EpisodeNumber = p.EpisodeNumber,
                    UnitName = p.UnitName?.Trim() ?? "",
                    ShotLabel = p.ShotLabel?.Trim() ?? "",
                    Categories = PostOverlayAnalyzer.Describe(hits),
                    Cues = string.Join("、", hits.Select(h => h.Cue).Distinct()),
                    Excerpt = BuildExcerpt(text, hits.Select(h => h.Cue)),
                    Guarded = guarded
                });
            }
        }
        else
        {
            var frames = SafeGetFrames(projectId);
            plan.ShotCount = frames.Count;
            foreach (var f in frames)
            {
                var text = string.Join(" ", new[] { f.Description, f.Dialogue, f.Composition, f.Timeline, f.StartScene, f.EndScene }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
                var hits = PostOverlayAnalyzer.Detect(text);
                if (hits.Count == 0) continue;
                plan.Items.Add(new OverlayItem
                {
                    EpisodeNumber = f.EpisodeNumber ?? 0,
                    UnitName = f.UnitNumber?.Trim() ?? "",
                    ShotLabel = f.ShotNumber?.Trim() ?? "",
                    Categories = PostOverlayAnalyzer.Describe(hits),
                    Cues = string.Join("、", hits.Select(h => h.Cue).Distinct()),
                    Excerpt = BuildExcerpt(text, hits.Select(h => h.Cue)),
                    Guarded = false
                });
            }
        }

        plan.Items = plan.Items
            .OrderBy(i => i.EpisodeNumber)
            .ThenBy(i => i.UnitName, StringComparer.Ordinal)
            .ThenBy(i => i.ShotLabel, StringComparer.Ordinal)
            .ToList();

        foreach (var item in plan.Items)
        {
            foreach (var c in item.Categories.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                plan.CategoryCounts[c] = plan.CategoryCounts.GetValueOrDefault(c) + 1;
        }
        return plan;
    }

    /// <summary>@图N 绑定行里的资产名不算文字类元素（如道具卡「密函」），摘录与判定都要剔除。</summary>
    private static string StripRefLines(string text) =>
        Regex.Replace(text, @"@图\s*\d+\s*\[[^\]\r\n]*\][^\r\n]*", " ");

    /// <summary>取第一个命中触发词的上下文窗口，便于人工确认叠加内容。</summary>
    private static string BuildExcerpt(string text, IEnumerable<string> cues)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var idx = -1;
        foreach (var cue in cues)
        {
            var at = text.IndexOf(cue, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && (idx < 0 || at < idx)) idx = at;
        }
        if (idx < 0) idx = 0;
        var start = Math.Max(0, idx - 30);
        var len = Math.Min(text.Length - start, 70);
        var excerpt = text.Substring(start, len).Replace("\r", " ").Replace("\n", " ").Trim();
        return (start > 0 ? "…" : "") + excerpt + (start + len < text.Length ? "…" : "");
    }

    private List<SeedancePrompt> SafeGetPrompts(int projectId)
    {
        try { return _db.GetPrompts(projectId) ?? []; }
        catch { return []; }
    }

    private List<StoryboardFrame> SafeGetFrames(int projectId)
    {
        try { return _db.GetAllFrames(projectId) ?? []; }
        catch { return []; }
    }
}
