namespace ManhuaPipeline.Models;

/// <summary>
/// 角色音色参考音频：一个项目里一个角色一条（上传即覆盖）。
/// 生成 H3 视频时按镜头里的出场角色取用，写进提示词的 &lt;Audio n&gt; 并接到 ComfyUI 的 ref_audios 槽位。
/// </summary>
public class VoiceReference
{
    public int VoiceId { get; set; }
    public int ProjectId { get; set; }
    /// <summary>角色名（与 FrameAssetBindings 中 Category=Character 的 Name 对应）。</summary>
    public string CharacterName { get; set; } = "";
    /// <summary>音频文件可访问路径，如 /uploads/voices/xxx.wav</summary>
    public string AudioUrl { get; set; } = "";
    /// <summary>用户上传时的原始文件名（仅用于界面展示）。</summary>
    public string? OriginalFileName { get; set; }
    public DateTime CreatedAt { get; set; }
}
