namespace ManhuaPipeline.Models;

public class Episode
{
    public int EpisodeId { get; set; }
    public int ProjectId { get; set; }
    public int UserId { get; set; }
    public int EpisodeNumber { get; set; }
    public string Title { get; set; } = "";
    public string? Summary { get; set; }
    public string? Content { get; set; }
    public int BatchNumber { get; set; } = 1;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public class StoryboardFrame
{
    public int FrameId { get; set; }
    public int EpisodeId { get; set; }
    public int ProjectId { get; set; }
    public int FrameNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public string? UnitNumber { get; set; }
    public string? UnitType { get; set; }
    public int UnitOrder { get; set; }
    public string? ShotNumber { get; set; }
    public int? CombatBeatIndex { get; set; }
    public string? CombatBeatIds { get; set; }
    public string? Skills { get; set; }
    public string? Timeline { get; set; }
    public string? ShotSize { get; set; }
    public string? Description { get; set; }
    public string? Composition { get; set; }
    public string? Characters { get; set; }
    public string? Dialogue { get; set; }
    public string? Camera { get; set; }
    public string? Duration { get; set; }
    public string? StartScene { get; set; }
    public string? EndScene { get; set; }
    public string? Scene { get; set; }

    // ===== L4 镜头状态机六字段（对应 short-drama-agent 节 16「视频镜头任务卡」）=====
    // 目的：让分镜从「描述文本集合」变成「可校验的状态机」——
    // 每镜有明确的起始/结束状态、只做一件事、说明为何进入下一镜、列出不得变化的项、并交代观众的新信息增量。
    /// <summary>起始状态：本镜开始时角色/道具/空间的状态，必须接住上一镜的结束状态。</summary>
    public string? StartState { get; set; }

    /// <summary>单一动作：本镜只做一件什么事（一镜一动作，禁止多动作并置）。</summary>
    public string? SingleAction { get; set; }

    /// <summary>结束状态：本镜结束时的状态，供下一镜接住。</summary>
    public string? EndState { get; set; }

    /// <summary>衔接下一镜：为什么从这一镜进入下一镜（转场动机：声音桥/视线/动作/信息问题/空间）。</summary>
    public string? NextConnection { get; set; }

    /// <summary>禁止变化：本镜内不得改变的空间关系、服装、伤势、道具位置等。</summary>
    public string? ForbiddenChanges { get; set; }

    /// <summary>新信息：观众在这一镜获得了什么新信息（信息增量，空转镜头应写「无」，供阶段 9 判空转）。</summary>
    public string? NewInformation { get; set; }

    public int BatchNumber { get; set; } = 1;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public class CoherenceCheck
{
    public int CheckId { get; set; }
    public int ProjectId { get; set; }
    public string? Issues { get; set; }
    public string Status { get; set; } = "pending";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
