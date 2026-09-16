namespace ManhuaPipeline.Models;

/// <summary>
/// Director V3 运行时状态：记录本集已经用过的镜头/动作/特效套路与高潮次数，
/// 供后续 Unit Director 生成和 Validator 做全局重复控制。
/// </summary>
public class EpisodeDirectorState
{
    public int EpisodeDirectorStateId { get; set; }
    public int ProjectId { get; set; }
    public int EpisodeNumber { get; set; }

    public Dictionary<string, int> CameraPatternCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> CombatPatternCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> VfxPatternCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int SlowMotionCount { get; set; }
    public int MajorExplosionCount { get; set; }
    public int CurrentPeakIntensity { get; set; }

    // Director V4：小/中/大高潮已用次数
    public int SmallClimaxCount { get; set; }
    public int MidClimaxCount { get; set; }
    public int LargeClimaxCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
