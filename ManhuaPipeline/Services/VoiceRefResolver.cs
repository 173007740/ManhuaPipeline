using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services;

/// <summary>
/// 镜头音色参考解析：把「该镜头里出场、且已配置音色参考音频的角色」解析成有序清单。
///
/// 该清单是三处的唯一权威，必须由同一函数产出以保证「提示词里的编号」与「实际传入的音频」严格对齐：
///   1) H3 提示词里的 &lt;Audio n&gt; 声明（供模型书写"以参考 &lt;Audio n&gt; 的音色说话"）；
///   2) SD 提示词里的 @音频n 标注（同一套编号，写法不同）；
///   3) 提交时的音频入参顺序：ComfyUI 的 ref_audios 槽位，或火山方舟 content 里
///      role=reference_audio 的 audio_url 顺序（VideoController → ComfyService / VideoService）。
/// 编号必须同源——提示词写 @音频2、实际第 2 段传的是别人的声音，就是张冠李戴。
/// </summary>
public static class VoiceRefResolver
{
    /// <summary>音频参考段数上限（最严的一档）：MiniMax H3 与 SD 2.0 都是 3 段、总时长 ≤15 秒。</summary>
    public const int MaxRefAudios = 3;

    /// <summary>SD 2.5 放宽到 10 段、总时长 ≤30 秒。</summary>
    public const int MaxRefAudiosSd25 = 10;

    /// <summary>
    /// 按模型名判断这次最多能带几段音色参考。
    /// 火山方舟的模型名形如 doubao-seedance-2-0-mini-260615 / ...-2-5-...：只认版本号，
    /// 2.5 给 10 段，其余（SD 2.0、H3、认不出的）一律按最严的 3 段——多传会被接口拒。
    /// </summary>
    public static int MaxAudiosForModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return MaxRefAudios;
        var m = model.Replace('-', '.').Replace('_', '.');
        return m.Contains("2.5", StringComparison.OrdinalIgnoreCase) ? MaxRefAudiosSd25 : MaxRefAudios;
    }

    /// <summary>一条解析结果：AudioIndex 即提示词里的音色编号（从 1 起）——
    /// H3 写成 &lt;Audio n&gt;，SD 写成 @音频n，两边同一个编号。</summary>
    public sealed record ShotVoiceRef(int AudioIndex, string CharacterName, string AudioUrl);

    /// <summary>
    /// 角色名归一化：全角转半角、去掉空白与标点后转小写。
    /// 与 H3 绑定一致性校验同源——库里资产名带中文引号时，帧绑定名与音色表名仍能匹配上。
    /// </summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name.Trim())
        {
            // 全角 ASCII（！-～）转半角，全角空格转普通空格
            var c = ch;
            if (c >= '\uFF01' && c <= '\uFF5E') c = (char)(c - 0xFEE0);
            else if (c == '\u3000') c = ' ';
            if (char.IsWhiteSpace(c)) continue;
            var cat = char.GetUnicodeCategory(c);
            if (cat is System.Globalization.UnicodeCategory.ConnectorPunctuation
                or System.Globalization.UnicodeCategory.DashPunctuation
                or System.Globalization.UnicodeCategory.OpenPunctuation
                or System.Globalization.UnicodeCategory.ClosePunctuation
                or System.Globalization.UnicodeCategory.InitialQuotePunctuation
                or System.Globalization.UnicodeCategory.FinalQuotePunctuation
                or System.Globalization.UnicodeCategory.OtherPunctuation
                or System.Globalization.UnicodeCategory.MathSymbol
                or System.Globalization.UnicodeCategory.CurrencySymbol
                or System.Globalization.UnicodeCategory.ModifierSymbol
                or System.Globalization.UnicodeCategory.OtherSymbol)
                continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 解析该镜头的音色参考清单（已按顺序编号，最多 <see cref="MaxRefAudios"/> 条）。
    /// </summary>
    /// <param name="db">数据访问</param>
    /// <param name="projectId">项目 Id</param>
    /// <param name="frameId">该镜头对应的分镜帧 Id；无帧绑定的老数据传 null</param>
    /// <param name="refNamesInOrder">
    /// 无帧绑定时的兜底候选顺序：该镜头参考图绑定行的资产名（即提示词 @图片1..N 的顺序）。
    /// </param>
    /// <param name="maxAudios">
    /// 本次最多能带几段。ComfyUI（H3）与 SD 2.0 是 3 段，SD 2.5 是 10 段，
    /// 由调用方用 <see cref="MaxAudiosForModel"/> 算出来传进来。
    /// </param>
    public static List<ShotVoiceRef> Resolve(DbService db, int projectId, int? frameId, IReadOnlyList<string>? refNamesInOrder,
                                             int maxAudios = MaxRefAudios)
    {
        if (maxAudios <= 0) maxAudios = MaxRefAudios;
        var result = new List<ShotVoiceRef>();
        if (projectId <= 0) return result;

        List<VoiceReference> voices;
        try { voices = db.GetVoiceReferences(projectId); }
        catch { return result; }   // 表缺失/读取失败：不加音色参考，保持原有行为
        if (voices.Count == 0) return result;

        var byName = new Dictionary<string, VoiceReference>(StringComparer.Ordinal);
        foreach (var v in voices)
        {
            var key = NormalizeName(v.CharacterName);
            if (key.Length == 0 || string.IsNullOrWhiteSpace(v.AudioUrl)) continue;
            byName[key] = v;
        }
        if (byName.Count == 0) return result;

        // 候选顺序：优先帧绑定的出场角色（SortOrder 即 @图片N 的编号顺序）；
        // 无帧绑定时退化为调用方给出的参考图绑定名顺序。
        var order = new List<string>();
        if (frameId is > 0)
        {
            try
            {
                order.AddRange(db.GetFrameAssetBindings(projectId, frameId.Value)
                    .Where(b => string.Equals(b.Category, "Character", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(b => b.SortOrder)
                    .Select(b => b.Name));
            }
            catch { /* 帧绑定表缺失：走文本兜底 */ }
        }
        if (order.Count == 0 && refNamesInOrder is { Count: > 0 }) order.AddRange(refNamesInOrder);

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in order)
        {
            var key = NormalizeName(name);
            if (key.Length == 0 || !used.Add(key)) continue;
            if (!byName.TryGetValue(key, out var v)) continue;
            result.Add(new ShotVoiceRef(result.Count + 1, v.CharacterName, v.AudioUrl));
            if (result.Count >= maxAudios) break;
        }
        return result;
    }
}
