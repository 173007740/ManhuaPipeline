using System;
using System.Collections.Generic;
using System.Linq;

namespace ManhuaPipeline.Services;

/// <summary>
/// L5 后期叠加：识别「应由后期叠加而不是交给视频模型生成」的文字类画面元素。
///
/// 背景：手机短信、新闻推送、文件、公告、地图、监控画面、时间码这类内容，
/// 交给视频模型生成必然出现乱码或错字，只能让模型画出「干净留白的屏幕/纸面」，
/// 文字在后期叠加。规则出处：short-drama-agent 节 16B / 19。
///
/// 本类只做「识别 + 分类」，不落库、不调 LLM：
/// - <see cref="PromptPostOverlayGuard"/> 在阶段 9 提示词里补留白约束；
/// - 项目页「后期叠加清单」按镜头汇总成人工后期任务。
/// </summary>
public static class PostOverlayAnalyzer
{
    /// <summary>写给视频模型的标准留白规则原文（阶段 9 提示词与清单展示共用，避免两处措辞漂移）。</summary>
    public const string RuleText = "视频中只保留屏幕留白，文字后期叠加";

    /// <summary>一类后期叠加对象：分类名 + 触发词。</summary>
    public sealed record OverlayCategory(string Category, string[] Cues);

    /// <summary>
    /// 后期叠加分类表。触发词一律用「词形不易误伤」的写法：
    /// 例如用「书信/信件/密信」而不用单个「信」，避免命中「相信」「信息」。
    /// </summary>
    public static readonly OverlayCategory[] Categories =
    [
        new("手机短信", ["短信", "微信", "聊天记录", "未读消息", "私信", "弹窗消息"]),
        new("来电通话", ["来电", "通话记录", "未接来", "手机铃", "电话铃"]),
        new("新闻推送", ["新闻", "头条", "热搜", "播报", "推送", "通缉令"]),
        new("文件信件", ["文件", "卷宗", "档案", "密函", "书信", "信件", "密信", "信纸", "纸条", "便签", "契约", "圣旨", "诏书"]),
        new("地图监控", ["地图", "舆图", "监控画面", "监控录像", "录像画面", "直播画面", "行车记录"]),
        new("公告榜单", ["榜单", "榜文", "告示", "布告", "招募令", "悬赏令", "公告栏"]),
        new("报纸刊物", ["报纸", "刊物", "版面", "杂志"]),
        new("屏幕界面", ["屏幕", "手机屏", "悬浮面板", "系统面板", "提示框", "对话框", "时间码", "弹幕", "血条", "进度条"]),
    ];

    /// <summary>命中项：分类 + 命中的触发词（同镜头内同类只保留首个触发词）。</summary>
    public sealed record OverlayHit(string Category, string Cue);

    /// <summary>
    /// 从一段文本（镜头分镜文本或提示词正文）中识别需要后期叠加的文字类元素。
    /// 同分类只返回一次，便于清单去重。
    /// </summary>
    public static List<OverlayHit> Detect(string? text)
    {
        var hits = new List<OverlayHit>();
        if (string.IsNullOrWhiteSpace(text)) return hits;
        foreach (var rule in Categories)
        {
            var cue = rule.Cues.FirstOrDefault(c => text.Contains(c, StringComparison.OrdinalIgnoreCase));
            if (cue != null) hits.Add(new OverlayHit(rule.Category, cue));
        }
        return hits;
    }

    /// <summary>该文本是否涉及需要后期叠加的文字类元素。</summary>
    public static bool HasOverlay(string? text) => Detect(text).Count > 0;

    /// <summary>把命中项拼成一行分类名（如「手机短信 / 新闻推送」）。</summary>
    public static string Describe(IEnumerable<OverlayHit> hits)
    {
        var names = hits.Select(h => h.Category).Distinct().ToList();
        return names.Count == 0 ? "" : string.Join(" / ", names);
    }
}
