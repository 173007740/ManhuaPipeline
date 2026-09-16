using System.Text.RegularExpressions;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services;

/// <summary>
/// Stage 5 分镜单行字段解析：负责字段值识别，以及“字段冒号后为空、
/// 内容在下一行缩进”时的续行归属（典型场景为镜头时间轴、出镜角色列表）。
/// </summary>
public static class StoryboardFrameFieldParser
{
    private static readonly Regex FieldRegex = new(
        @"^[-*\s]*(\*\*)?(?<field>节拍序号|战斗节拍|节拍|CombatBeatIndex|镜头时间轴|时间轴|Timeline|单元类型|技能|镜头描述|描述|构图方式|景别|镜头运动|运镜|出镜角色及表情|角色及表情|出镜角色|对话/台词|对话|台词|镜头时长|时长|起始画面|结束画面|起始状态|start_state|单一动作|single_action|结束状态|end_state|衔接下一镜|next_connection|禁止变化|forbidden_changes|新信息|new_information|场景|地点|Scene|Location)(\*\*)?\s*[:：]\s*(?<value>.*)$",
        RegexOptions.IgnoreCase);

    private static readonly Regex UnitNoteRegex = new(
        @"^[-*\s]*(\*\*)?单元类型(\*\*)?\s*[:：]\s*[^（(]*[（(](?<note>[^）)]+)[）)]",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// 多行字段续行状态：字段标题冒号后为空、内容写在下一行缩进子项时，
    /// 把后续子项行归入对应字段，直到下一个字段标题/新镜头出现。
    /// 时间轴、出镜角色、台词需分开记录，因为三者续行内容归入不同列。
    /// </summary>
    public sealed class ContinuationState
    {
        public bool TimelineOpen;
        public bool CharacterListOpen;
        public bool DialogueOpen;
    }

    public static bool TryApplyField(StoryboardFrame frame, string line, ContinuationState cont, ref string? unitType)
    {
        if (frame == null || string.IsNullOrEmpty(line)) return false;
        if (cont == null) throw new ArgumentNullException(nameof(cont));

        var fieldMatch = FieldRegex.Match(line);
        if (!fieldMatch.Success) return false;

        var field = fieldMatch.Groups["field"].Value.Trim();
        var value = fieldMatch.Groups["value"].Value.Trim();
        var isTimelineField = field is "镜头时间轴" or "时间轴" or "Timeline";
        var isCharacterField = field is "出镜角色及表情" or "角色及表情" or "出镜角色";
        var isDialogueField = field is "对话/台词" or "对话" or "台词";
        switch (field)
        {
            case "节拍序号":
            case "战斗节拍":
            case "节拍":
            case "CombatBeatIndex":
                frame.CombatBeatIndex = StoryboardFrameParser.TryGetCombatBeatIndex(value);
                frame.CombatBeatIds = StoryboardFrameParser.TryGetCombatBeatIds(value);
                break;
            case "镜头时间轴":
            case "时间轴":
            case "Timeline":
                frame.Timeline = AppendValue(frame.Timeline, value);
                break;
            case "技能":
                // 帧内技能行（LLM 回声/补充）直接作为该帧技能声明覆盖，
                // 避免与帧创建时继承的单元级 currentUnitSkills 拼接成双份。
                frame.Skills = NormalizeSkillList(value);
                break;
            case "单元类型":
                unitType = NormalizeUnitType(value);
                break;
            case "镜头描述":
            case "描述":
                frame.Description = StripBracketedNotes(AppendValue(frame.Description, value));
                break;
            case "构图方式":
                frame.Composition = AppendValue(frame.Composition, value);
                break;
            case "景别":
                frame.ShotSize = value;
                break;
            case "镜头运动":
            case "运镜":
                frame.Camera = AppendValue(frame.Camera, value);
                break;
            case "出镜角色及表情":
            case "角色及表情":
            case "出镜角色":
                // 冒号后同行有值时直接落字段；为空时由下方打开角色续行，内容来自下一行子项。
                if (!string.IsNullOrWhiteSpace(value))
                    frame.Characters = AppendValue(frame.Characters, value);
                break;
            case "对话/台词":
            case "对话":
            case "台词":
                // 冒号后同行有值时直接落字段；为空时由下方打开台词续行，内容来自下一行子项。
                if (!string.IsNullOrWhiteSpace(value))
                    frame.Dialogue = AppendValue(frame.Dialogue, value);
                break;
            case "镜头时长":
            case "时长":
                frame.Duration = AppendValue(frame.Duration, value);
                break;
            case "起始画面":
                frame.StartScene = AppendValue(frame.StartScene, value);
                break;
            case "结束画面":
                frame.EndScene = AppendValue(frame.EndScene, value);
                break;
            // ===== L4 镜头状态机六字段 =====
            case "起始状态":
            case "start_state":
                frame.StartState = AppendValue(frame.StartState, value);
                break;
            case "单一动作":
            case "single_action":
                frame.SingleAction = AppendValue(frame.SingleAction, value);
                break;
            case "结束状态":
            case "end_state":
                frame.EndState = AppendValue(frame.EndState, value);
                break;
            case "衔接下一镜":
            case "next_connection":
                frame.NextConnection = AppendValue(frame.NextConnection, value);
                break;
            case "禁止变化":
            case "forbidden_changes":
                frame.ForbiddenChanges = AppendValue(frame.ForbiddenChanges, value);
                break;
            case "新信息":
            case "new_information":
                frame.NewInformation = AppendValue(frame.NewInformation, value);
                break;
            case "场景":
            case "地点":
            case "Scene":
            case "Location":
                frame.Scene = StripBracketedNotes(value);
                break;
        }

        // 任一字段标题出现都先关闭全部续行，再按需打开当前字段的续行，避免跨字段串列。
        cont.TimelineOpen = isTimelineField;
        cont.CharacterListOpen = isCharacterField && string.IsNullOrWhiteSpace(value);   // 空标题 → 后续子项行归入出镜角色
        cont.DialogueOpen = isDialogueField && string.IsNullOrWhiteSpace(value);         // 空标题 → 后续子项行归入台词
        return true;
    }

    /// <summary>字段值续行：时间轴/出镜角色/台词字段后不带字段名的正文行归入当前字段，支持 Markdown bullet 子项。</summary>
    public static bool TryAppendContinuation(StoryboardFrame frame, string line, ContinuationState cont)
    {
        if (frame == null || string.IsNullOrEmpty(line)) return false;
        if (cont == null) throw new ArgumentNullException(nameof(cont));
        if (line.StartsWith("【") || line.StartsWith("#")) return false;

        var trimmedStart = line.TrimStart();
        var isBulletLine = trimmedStart.StartsWith("-") || trimmedStart.StartsWith("*");
        if (cont.TimelineOpen)
        {
            frame.Timeline = AppendValue(frame.Timeline, isBulletLine ? trimmedStart.TrimStart('-', '*', ' ') : line);
            return true;
        }

        if (cont.CharacterListOpen)
        {
            // 出镜角色空标题后的子项（通常为 bullet 列表，形如「- 遐蝶：表情描述」）归入出镜角色，
            // 避免角色名被当镜头描述或直接丢弃，导致帧级资产绑定漏绑角色。
            var content = isBulletLine ? trimmedStart.TrimStart('-', '*', ' ') : line;
            frame.Characters = AppendValue(frame.Characters, content.Trim());
            return true;
        }

        if (cont.DialogueOpen)
        {
            // 「对话/台词」空标题后的子项（形如「- 遐蝶：书名呢？」）归入台词。
            // LLM 在一单元内出现多人多句时偏好写成 Markdown 列表，此前这些台词会被整段丢弃。
            var content = TryGetDialogueContinuation(line);
            if (content != null)
            {
                frame.Dialogue = AppendValue(frame.Dialogue, content);
                return true;
            }
        }

        if (isBulletLine) return false;

        frame.Description = (frame.Description ?? "") + " " + line;
        return true;
    }

    /// <summary>
    /// 技能列表规范化：
    /// 1) 剥离技能名后的括号备注（LLM 常加，如 法天象地·武圣法相（背景法相光焰, 聚焦于拳脚肉搏）），只保留技能名；
    /// 2) 中文逗号/顿号/分号/全角空格统一为英文逗号+空格；
    /// 保证所有帧的技能串在字符串层面一致，且可被逗号安全拆分。
    /// </summary>
    public static string? NormalizeSkillList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var text = value.Trim();
        // 先剥离括号备注（备注内可能含逗号，必须在分隔符统一之前处理）。
        text = Regex.Replace(text, @"[（(][^（()）]*[）)]", " ");
        text = text
            .Replace('，', ',')
            .Replace('、', ',')
            .Replace('；', ',')
            .Replace(';', ',');
        // 逗号前后空白（含全角空格）统一为单个空格；空项与末尾逗号剔除。
        text = Regex.Replace(text, @"\s*,\s*", ", ");
        text = Regex.Replace(text, @"\s{2,}", " ").Trim();
        return text.TrimEnd(',').Trim();
    }

    /// <summary>单元类型只保留标准类型，去掉 LLM 追加的括号备注（如 文戏/情感（觉醒前奏…））。</summary>
    private static string NormalizeUnitType(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var text = value.Trim();
        var noteIndex = text.IndexOfAny(new[] { '（', '(' });
        return noteIndex > 0 ? text.Substring(0, noteIndex).Trim() : text;
    }

    /// <summary>提取单元类型行里的括号备注（如 文戏/情感（觉醒前奏…）），没有备注返回 null。</summary>
    public static string? TryGetUnitNote(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var match = UnitNoteRegex.Match(line.Trim());
        return match.Success ? match.Groups["note"].Value.Trim() : null;
    }

    /// <summary>去掉镜头描述里 LLM 自带的【导演注意】等方括号备注，权威注意只由系统注入一次。</summary>
    public static string? StripBracketedNotes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        // 只清理 LLM 自写的【导演注意】…备注，保留系统注入的「本单元情绪任务」权威注意。
        var cleaned = Regex.Replace(text, @"【导演注意】(?!本单元情绪任务)[^。！？!?\n]*[。！？!?]?", " ").Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    /// <summary>该行是否为分镜字段标题行（如「- **镜头时长**: 11秒」），用于判定续行是否结束。</summary>
    public static bool IsFieldHeaderLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        return FieldRegex.IsMatch(line.Trim());
    }

    /// <summary>
    /// 「对话/台词」冒号后为空时，取后续子项行作为台词内容；
    /// 不是台词子项（字段标题、非台词的普通正文）返回 null，由调用方关闭续行。
    /// </summary>
    public static string? TryGetDialogueContinuation(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || IsFieldHeaderLine(trimmed)) return null;

        var trimmedStart = trimmed.TrimStart();
        var isBulletLine = trimmedStart.StartsWith('-') || trimmedStart.StartsWith('*');
        var content = (isBulletLine ? trimmedStart.TrimStart('-', '*', ' ') : trimmed).Trim();
        if (content.Length == 0) return null;
        // 非 bullet 行只有形如「说话人：台词」时才收，避免把后续普通正文吞成台词。
        if (!isBulletLine && !LooksLikeDialogue(content)) return null;
        return content;
    }

    /// <summary>一条台词：说话人（识别不出时为空串）+ 已剥离外层引号的台词正文。</summary>
    public sealed record DialogueSegment(string Speaker, string Text);

    /// <summary>
    /// 从「对话/台词」字段值中切出台词正文，兼容 LLM 的多种写法：
    /// 1) 单句「角色：“台词”」；2) 一单元内多说话人「角色A：“…” 角色B：“…”」；
    /// 3) 无引号「角色：台词」。引号内的冒号不参与说话人切分。
    /// </summary>
    public static List<DialogueSegment> ExtractDialogueSegments(string? value)
    {
        var result = new List<DialogueSegment>();
        if (string.IsNullOrWhiteSpace(value)) return result;
        var text = value.Trim();
        if (text.Length == 0 || text == "无") return result;

        // 引号内的冒号（如「岳沉天：“我说：你错了。”」）不是说话人分隔符，先屏蔽再定位边界。
        var boundaries = SpeakerBoundaryRegex.Matches(MaskQuotedSpans(text));
        if (boundaries.Count == 0)
        {
            // 没有「说话人：」结构（如整句被引号包裹）时，退化为按引号切片。
            foreach (Match quote in QuoteRegex.Matches(text))
            {
                var content = quote.Groups[1].Value.Trim();
                if (content.Length > 0) result.Add(new DialogueSegment("", content));
            }
            return result;
        }

        for (var i = 0; i < boundaries.Count; i++)
        {
            var speakerGroup = boundaries[i].Groups["speaker"];
            var speaker = text.Substring(speakerGroup.Index, speakerGroup.Length).Trim();
            var speakerEnd = speakerGroup.Index + speakerGroup.Length;
            var colon = text.IndexOfAny(new[] { ':', '：' }, speakerEnd);
            var bodyStart = colon >= 0 ? colon + 1 : speakerEnd;
            var bodyEnd = i + 1 < boundaries.Count
                ? boundaries[i + 1].Groups["speaker"].Index
                : text.Length;
            if (bodyEnd <= bodyStart) continue;
            var body = StripWrappingQuotes(
                text.Substring(bodyStart, bodyEnd - bodyStart).Trim().TrimEnd('；', ';', '，', ','));
            if (body.Length > 0) result.Add(new DialogueSegment(speaker, body));
        }
        return result;
    }

    private static bool LooksLikeDialogue(string text)
    {
        var colon = text.IndexOfAny(new[] { ':', '：' });
        return colon > 0 && colon <= 12;
    }

    /// <summary>
    /// 把引号包裹的内容（含引号本身）替换为换行符：长度与索引保持不变，
    /// 但引号内的冒号不再参与说话人边界匹配。
    /// </summary>
    private static string MaskQuotedSpans(string text)
    {
        var chars = text.ToCharArray();
        foreach (Match quote in QuoteRegex.Matches(text))
        {
            for (var i = quote.Index; i < quote.Index + quote.Length; i++) chars[i] = '\n';
        }
        return new string(chars);
    }

    /// <summary>只剥离整体包裹的成对引号（「“台词”」→「台词」），台词内部的引号保留。</summary>
    private static string StripWrappingQuotes(string body)
    {
        if (body.Length < 2) return body;
        var wrapped = (body[0], body[^1]) switch
        {
            ('“', '”') => true,
            ('"', '"') => true,
            ('「', '」') => true,
            _ => false
        };
        return wrapped ? body.Substring(1, body.Length - 2).Trim() : body;
    }

    private static readonly Regex QuoteRegex = new(@"[“""]([^“”""]*)[”""]", RegexOptions.Compiled);

    private static readonly Regex SpeakerBoundaryRegex = new(
        @"(?<speaker>[^：:。！？；\r\n]{1,12}?)\s*[：:]\s*", RegexOptions.Compiled);

    private static string AppendValue(string? current, string value)
    {
        if (string.IsNullOrWhiteSpace(current)) return value;
        return current + " " + value;
    }
}
