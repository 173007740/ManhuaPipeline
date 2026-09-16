using System;
using System.Collections.Generic;

namespace ManhuaPipeline.Models;

/// <summary>
/// L3 关键帧层：一个「剧情节点」对应一张关键帧。
///
/// 目的（对应 short-drama-agent 节 15）：把一条集/单元拆成 8-16 个不可省的剧情节点，
/// 每个节点锁定「角色站位 / 道具状态 / 场景朝向 / 线索可见性 / 镜头衔接」，
/// 作为分镜与视频生成之间的锚点 —— 关键帧定死的东西，后续镜头不得漂移。
///
/// 落库表：ProjectKeyframes（见 Database\Upgrade_关键帧层.sql）。
/// 生成方式是<b>人工触发</b>（项目页关键帧页的「生成关键帧方案」按钮）：
/// 只有已生成关键帧的项目才在阶段 9 注入【关键帧锚定】，未生成的项目行为与以前完全一致。
/// </summary>
public class ProjectKeyframe
{
    public int KeyframeId { get; set; }
    public int ProjectId { get; set; }

    /// <summary>集号（0 = 全片级节点）。</summary>
    public int EpisodeNumber { get; set; }

    /// <summary>排序，决定关键帧在时间线上的先后。</summary>
    public int SortOrder { get; set; }

    /// <summary>剧情节点标签，如「开场钩子」「首次揭示」「道具状态变化」「收尾悬念」。</summary>
    public string? NodeLabel { get; set; }

    /// <summary>为什么选这个节点（该节点承载的信息增量 / 情绪转折 / 因果拐点）。</summary>
    public string? NodeReason { get; set; }

    /// <summary>锚定的分镜镜头编号（如 2.3-1），用于与阶段 9 提示词对齐。</summary>
    public string? ShotLabel { get; set; }

    /// <summary>所属单元编号（如 2.3）。</summary>
    public string? UnitNumber { get; set; }

    /// <summary>画面构图：机位、景别、前后中景、主体在画面中的位置。</summary>
    public string? Composition { get; set; }

    /// <summary>锁定的角色站位与朝向（谁在画面左/右，面向哪，与谁对视）。</summary>
    public string? LockedCharacters { get; set; }

    /// <summary>锁定的道具状态（在哪只手、什么状态、磨损/破损程度、位置）。</summary>
    public string? LockedProps { get; set; }

    /// <summary>锁定的场景空间与朝向（入口出口、光源方向、动作轴线）。</summary>
    public string? LockedSceneDirection { get; set; }

    /// <summary>线索可见性：本节点观众必须看到 / 必须看不到的线索。</summary>
    public string? ClueVisible { get; set; }

    /// <summary>与下一张关键帧的衔接（谁/什么状态延续到下一节点）。</summary>
    public string? NextConnection { get; set; }

    /// <summary>可直接用于文生图的关键帧图像提示词。</summary>
    public string? ImagePrompt { get; set; }

    /// <summary>状态：draft（方案）/ accepted（人工确认锁定）。</summary>
    public string Status { get; set; } = "draft";

    /// <summary>来源：llm（模型生成）/ fallback（模型失败后按分镜降级拼装）。</summary>
    public string Source { get; set; } = "llm";

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>关键帧方案生成用的 LLM JSON 载荷（字段名与提示词约定一致）。</summary>
public class KeyframePlanPayload
{
    public List<KeyframeNodeDto>? Keyframes { get; set; }
}

/// <summary>LLM 返回的单个关键帧节点。</summary>
public class KeyframeNodeDto
{
    public string? NodeLabel { get; set; }
    public string? NodeReason { get; set; }
    public string? ShotLabel { get; set; }
    public string? UnitNumber { get; set; }
    public string? Composition { get; set; }
    public string? LockedCharacters { get; set; }
    public string? LockedProps { get; set; }
    public string? LockedSceneDirection { get; set; }
    public string? ClueVisible { get; set; }
    public string? NextConnection { get; set; }
    public string? ImagePrompt { get; set; }
}

/// <summary>
/// 候选剧情节点（系统先从分镜/单元里挑出来，再交给模型写成关键帧）。
/// 选点是确定性的系统行为，目的：保证「8-16 张、覆盖全集、不靠模型随手挑」。
/// </summary>
public class KeyframeCandidate
{
    public int EpisodeNumber { get; set; }
    public string UnitNumber { get; set; } = "";
    public string ShotLabel { get; set; } = "";
    /// <summary>系统给出的候选理由（如「单元首镜」「道具状态变化」「新信息」「打斗节点」「收尾镜」）。</summary>
    public string Reason { get; set; } = "";
    /// <summary>优先级，越小越先入选（用于裁剪到 8-16 张）。</summary>
    public int Priority { get; set; }
    /// <summary>节点原文（分镜描述 + 六字段），作为模型写关键帧的唯一依据。</summary>
    public string SourceText { get; set; } = "";
}
