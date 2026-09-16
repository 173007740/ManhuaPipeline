using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services;

/// <summary>
/// 镜头音色参考解析：把「该镜头里出场、且已配置音色参考音频的角色」解析成有序清单。
///
/// 该清单是两处的唯一权威，必须由同一函数产出以保证「提示词里的编号」与「实际传入的音频」严格对齐：
///   1) H3 提示词里的 &lt;Audio n&gt; 声明（AgentService，供模型书写"以参考 &lt;Audio n&gt; 的音色说话"）；
///   2) 提交 ComfyUI 时 ref_audios 槽位的注入顺序（VideoController → ComfyService）。
/// </summary>
public static class VoiceRefResolver
{
    /// <summary>MiniMax H3 节点的 ref_audios 槽位上限（官方：最多 3 段、总时长 ≤15 秒）。</summary>
    public const int MaxRefAudios = 3;

    /// <summary>一条解析结果：AudioIndex 即提示词里的 &lt;Audio n&gt; 编号（从 1 起）。</summary>
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
    public static List<ShotVoiceRef> Resolve(DbService db, int projectId, int? frameId, IReadOnlyList<string>? refNamesInOrder)
    {
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
            if (result.Count >= MaxRefAudios) break;
        }
        return result;
    }
}
