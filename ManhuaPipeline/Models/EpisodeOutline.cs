namespace ManhuaPipeline.Models;

/// <summary>
/// 一集的分集提纲，来自 P0 产出里的「N 集分集卡点 + Cliffhanger」表。
///
/// 它是漫剧级的中间产物：立项（Dramas）与单集项目（Projects）之间就靠它衔接——
/// 项目名取 Title，项目简介取 Outline，集号取 EpisodeNumber。
/// 存成 json 挂在 Dramas 那一行，所以逐集跑剧本时能随手取出来当上下文。
/// </summary>
public class EpisodeOutline
{
    public int EpisodeNumber { get; set; }
    public string Title { get; set; } = "";
    /// <summary>三幕骨架：建置 / 发展 / 收束。</summary>
    public string? Outline { get; set; }
    /// <summary>本集卡点（Cliffhanger）。</summary>
    public string? Cliffhanger { get; set; }
}
