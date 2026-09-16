namespace ManhuaPipeline.Models.Combat;

/// <summary>最小动作事件：一个连续镜头里的单步动作/响应/特效/运镜/环境反馈。</summary>
public class MicroAction
{
    public string Id { get; set; } = "";
    public string BeatId { get; set; } = "";
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    /// <summary>Action / OpponentReaction / Vfx / Camera / Environment / Performance</summary>
    public string Kind { get; set; } = "Action";
    /// <summary>true 表示可与主动作并行发生（镜头/特效/环境反馈），不是严格先后关系。</summary>
    public bool IsParallel { get; set; }
}

/// <summary>由多个连续 MicroAction 组成的一条完整动作链，可直接压进一个视频镜头。</summary>
public class ActionChain
{
    public string Id { get; set; } = "";
    public List<string> CombatBeatIds { get; set; } = [];
    public List<MicroAction> Actions { get; set; } = [];
    public string StartState { get; set; } = "";
    public string EndState { get; set; } = "";
    public string PrimarySubjectId { get; set; } = "";
    public List<string> ParticipantIds { get; set; } = [];
    public string FlowDirection { get; set; } = "";
    public string Intensity { get; set; } = "";
    public bool CanPackTogether { get; set; } = true;
}

/// <summary>镜头内部时间轴片段。多个片段属于同一个视频镜头，禁止拆成独立镜头。</summary>
public class ShotTimelineSegment
{
    public int SegmentIndex { get; set; }
    public double StartSecond { get; set; }
    public double EndSecond { get; set; }
    public List<string> SequentialActions { get; set; } = [];
    public List<string> ParallelEvents { get; set; } = [];
    public string EndState { get; set; } = "";
}

/// <summary>ShotPacker 产物：一个高密度视频镜头，可覆盖多个 CombatBeat / ActionChain。</summary>
public class PackedShot
{
    public string ShotId { get; set; } = "";
    /// <summary>建议时长（5/11/15），仅作参考；最终由 LLM 按密度自行决定。</summary>
    public int DurationSeconds { get; set; }
    public List<string> CombatBeatIds { get; set; } = [];
    public List<string> ActionChainIds { get; set; } = [];
    public List<ShotTimelineSegment> Timeline { get; set; } = [];
    public string StartState { get; set; } = "";
    public string EndState { get; set; } = "";
}

/// <summary>导演对本单元“5 秒里发生多少事”的密度决策。</summary>
public class ShotDensityProfile
{
    /// <summary>Low / Medium / High / Extreme</summary>
    public string ActionDensity { get; set; } = "HIGH";
    /// <summary>Low / Medium / High / Extreme</summary>
    public string VisualDensity { get; set; } = "MEDIUM";
    /// <summary>Low / Medium / High / Extreme</summary>
    public string VfxDensity { get; set; } = "MEDIUM";
    /// <summary>Slow / Normal / Fast / Burst / SlowToBurst</summary>
    public string ShotRhythm { get; set; } = "Fast";
    /// <summary>动作复杂度 1-5</summary>
    public int ActionComplexity { get; set; } = 3;
    /// <summary>空间变化 1-5</summary>
    public int SpatialChange { get; set; } = 2;
    public int ParticipantCount { get; set; } = 2;
}
