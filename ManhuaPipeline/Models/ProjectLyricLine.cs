namespace ManhuaPipeline.Models;

/// <summary>MV 项目的一句歌词及其在真歌中的起止时间（秒）。
/// 用途：对口型 MV 的镜头时长与切点必须服从真歌时间戳，
/// 不能按说话语速的字数上限推算（唱歌比说话慢，按语速算会导致口型与真歌对不上）。</summary>
public class ProjectLyricLine
{
    public int LineId { get; set; }
    public int ProjectId { get; set; }
    public int LineIndex { get; set; }
    public decimal StartSec { get; set; }
    public decimal EndSec { get; set; }
    public string Text { get; set; } = "";
}
