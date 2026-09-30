namespace ManhuaPipeline.Models;

/// <summary>
/// 角色音色参考音频：一个项目里一个角色一条（上传即覆盖）。
/// 按镜头里的出场角色取用，写进提示词并接到生成接口的音频入参——
/// H3 写 &lt;Audio n&gt; 接 ComfyUI 的 ref_audios 槽位，SD 写 @音频n 接火山方舟 content 里的
/// reference_audio；两边编号同源，都由 VoiceRefResolver 产出。
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
