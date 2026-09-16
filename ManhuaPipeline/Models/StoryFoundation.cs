using System.Collections.Generic;

namespace ManhuaPipeline.Models;

/// <summary>
/// L1 故事层：一次 LLM 调用产出的「节 1-5」结构化故事基线（对标 short-drama-agent 的节 1-5）。
/// 阶段号不变，落库映射：
///   节 1 故事核心 + 节 2 主线因果链  → 阶段 1（创意构思）
///   节 3 观众必须看懂的信息 + 节 4 情绪曲线 → 阶段 2（故事分析）
///   节 5 视觉锚点 + 分集大纲          → 阶段 3（全局蓝图）
/// 目的：把原先「阶段1 → 阶段2 → 阶段3」三次串联调用（每次都重新转述上一阶段产物）压成一次调用，
/// 避免逐段转述导致的故事信息衰减。结构化产物落在 StageData.StructuredJson（阶段 1 行）上，供阶段 2/3 复用渲染。
/// </summary>
public class StoryFoundation
{
    /// <summary>节 1：一句话故事核心（谁 + 处境 + 核心冲突）。</summary>
    public string? StoryCore { get; set; }

    /// <summary>节 2：主线因果链，每条为「因 → 果」，按剧情顺序。</summary>
    public List<string>? MainChain { get; set; }

    /// <summary>节 3：观众必须看懂的信息（漏掉就看不懂剧情的关键前提 / 伏笔 / 规则）。</summary>
    public List<string>? AudienceMustKnow { get; set; }

    /// <summary>节 4：情绪曲线（位置 → 情绪 → 承载节拍）。</summary>
    public List<EmotionCurvePoint>? EmotionCurve { get; set; }

    /// <summary>节 5：视觉锚点（反复出现、必须保持一致的标志性画面 / 道具 / 造型 / 光影）。</summary>
    public List<string>? VisualAnchors { get; set; }

    /// <summary>分集大纲：由阶段 3 渲染成「## 第N集: 标题 / 概要：…」并落库 Episodes 表。</summary>
    public List<StoryEpisodeOutline>? Episodes { get; set; }
}

/// <summary>节 4 情绪曲线上的一个点。</summary>
public class EmotionCurvePoint
{
    /// <summary>位置，如「第1集 前段」。</summary>
    public string? Position { get; set; }

    /// <summary>情绪标签，如「压抑 / 爆发 / 释然」。</summary>
    public string? Emotion { get; set; }

    /// <summary>承载该情绪的剧情节拍。</summary>
    public string? Beat { get; set; }
}

/// <summary>分集大纲中的一集。</summary>
public class StoryEpisodeOutline
{
    /// <summary>集号（从 1 连续递增）。</summary>
    public int EpisodeNumber { get; set; }

    /// <summary>集标题。</summary>
    public string? Title { get; set; }

    /// <summary>一句话概要。</summary>
    public string? Summary { get; set; }
}
