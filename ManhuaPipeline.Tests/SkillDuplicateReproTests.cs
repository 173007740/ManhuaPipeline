using System.Text.RegularExpressions;
using ManhuaPipeline.Models;
using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

/// <summary>
/// 复现 Stage5 分镜「Skills 双份」问题：从真实日志提取 LLM 返回，
/// 走 AutoFixer.Fix + 复刻 SaveStoryboardFramesFromResult 解析状态机，
/// 检查每个镜头帧的 Skills 是否被拼成双份。
/// </summary>
public class SkillDuplicateReproTests
{
    private const string LogPath = @"d:\CodexProject\ManhuaPipeline\logs\storyboard_planning_p30_s5_20260825_222519_313.log";

    private static string ExtractUnitText(string unitNumber, string completeMarker)
    {
        var lines = File.ReadAllLines(LogPath);
        var start = Array.FindIndex(lines, l => l.StartsWith("===== Unit " + unitNumber + " 开始"));
        var end = Array.FindIndex(lines, l => l.StartsWith(completeMarker));
        if (start < 0 || end < 0) throw new InvalidOperationException($"markers not found: {unitNumber}");
        // 取「开始」到「完成」之间最后一次 LLM 返回之后的内容
        int llmStart = -1;
        for (var i = start; i < end; i++)
        {
            if (lines[i].StartsWith("---- LLM返回 ----")) llmStart = i;
        }
        var from = llmStart >= 0 ? llmStart + 1 : start + 1;
        return string.Join("\n", lines.Skip(from).Take(end - from));
    }

    public static IEnumerable<object[]> Units()
    {
        // 注意：1.7 等返工片段的日志提取内容可能不含技能行，无法验证双份，故不纳入。
        yield return new object[] { "===== Unit 1.2 完成", "1.2", 1, "打斗/动作" };
        yield return new object[] { "===== Unit 1.3 完成", "1.3", 1, "打斗/动作" };
    }

    [Fact]
    public void Repro12_ExactLLMReturn_RealSkillNames_ShouldBeSingle()
    {
        // 黑盒复现：精确提取 1.2 的 LLM 返回（去掉 LogCall 的 ==== 分隔线），
        // 用真实技能名跑真实 Fix + 解析，检查帧3 Skills 是否双份。
        var raw = ExtractUnitText("1.2", "===== Unit 1.2 完成");
        var eqIdx = raw.IndexOf("====", StringComparison.Ordinal);
        if (eqIdx > 0) raw = raw.Substring(0, eqIdx).TrimEnd();

        var skillNames = new[]
        {
            "碎金劲", "玄冰细剑", "擒龙手", "寒魄封天", "霜刃千葬", "凝月冰牢",
            "赤炎天坠", "凤羽燎原", "焚星火轮", "九曜火莲", "烬影焚城", "炽阳焚界",
            "冰凰裂空", "玄冰龙卷", "雪魄镇魂", "九霄雷罚", "雷龙贯日", "紫电裂岳",
            "天枢雷印", "玄雷破阵", "震霆灭影", "万剑归宗", "青霄剑瀑", "星河剑阵",
            "斩月惊鸿", "天罡剑域", "归墟一剑", "风啸九天", "苍岚裂空", "逐影风刃",
            "天游旋岚", "青翼踏云", "罡风绝杀", "幽冥蚀月", "魔影千袭", "玄煞噬魂",
            "黑渊锁天", "血月断界", "夜魇终临", "圣光诛邪", "天门镇魔", "九曜神罚",
            "金翎破晓", "灵霄镇岳", "太虚归一", "碎金撼岳", "山河震界", "破法天罡",
            "武极金身", "法天象地·武圣法相", "力破万法", "踏天步", "赤金气血", "舍身一拳",
            "镇岳崩", "法天象地·暗金山岳", "流星坠地", "拳风", "七曜诛圣阵", "火墙", "御剑诀"
        }.Select(n => new SkillLibraryItem { Name = n }).ToList();

        var fixer = new StoryboardAutoFixer();
        var fixedText = fixer.Fix(raw, "", skillNames, out var fixes);

        var skillLineCount = Regex.Matches(fixedText, @"(?m)^\s*-\s*\*\*技能\*\*\s*[:：]").Count;
        var frames = ParseFrames(fixedText, "1.2", 1, "打斗/动作");
        var summary = string.Join("\n", frames.Select(f => $"{f.ShotNumber}: [{(f.Skills ?? "<null>")}]"));
        File.WriteAllText(@"d:\CodexProject\ManhuaPipeline\logs\repro_out.txt",
            "SKILL_LINES=" + skillLineCount + "\nFIXES:\n" + string.Join("\n", fixes) + "\n=====\nFIXED_TEXT:\n" + fixedText + "\n=====\nFRAMES:\n" + summary,
            new System.Text.UTF8Encoding(false));

        Assert.True(skillLineCount == 1, $"Fix 后技能行应为 1 行，实际 {skillLineCount}：\n{fixedText}");
        Assert.True(
            frames.All(f => string.Equals(f.Skills, "碎金劲, 玄冰细剑", StringComparison.Ordinal)),
            $"Skills 出现双份或异常：\n{summary}");
    }

    [Fact]
    public void Repro12_RealStage4Text_ShouldKeepSkillsSingle()
    {
        // 决定性实验：用数据库真实 Stage4 V4 全文作为 stage4Text（真实 onChunk 传非空 V4），
        // 走真实 Fix + 解析，验证 stage4Text 不会让技能行/技能值双份。
        var raw = ExtractUnitText("1.2", "===== Unit 1.2 完成");
        var eqIdx = raw.IndexOf("====", StringComparison.Ordinal);
        if (eqIdx > 0) raw = raw.Substring(0, eqIdx).TrimEnd();

        var stage4Text = File.ReadAllText(@"d:\CodexProject\ManhuaPipeline\logs\stage4_v4.txt");
        Assert.Contains("1.2", stage4Text);

        var skillNames = new[]
        {
            "碎金劲", "玄冰细剑", "擒龙手", "寒魄封天", "霜刃千葬", "凝月冰牢",
            "赤炎天坠", "凤羽燎原", "焚星火轮", "九曜火莲", "烬影焚城", "炽阳焚界",
            "冰凰裂空", "玄冰龙卷", "雪魄镇魂", "九霄雷罚", "雷龙贯日", "紫电裂岳",
            "天枢雷印", "玄雷破阵", "震霆灭影", "万剑归宗", "青霄剑瀑", "星河剑阵",
            "斩月惊鸿", "天罡剑域", "归墟一剑", "风啸九天", "苍岚裂空", "逐影风刃",
            "天游旋岚", "青翼踏云", "罡风绝杀", "幽冥蚀月", "魔影千袭", "玄煞噬魂",
            "黑渊锁天", "血月断界", "夜魇终临", "圣光诛邪", "天门镇魔", "九曜神罚",
            "金翎破晓", "灵霄镇岳", "太虚归一", "碎金撼岳", "山河震界", "破法天罡",
            "武极金身", "法天象地·武圣法相", "力破万法", "踏天步", "赤金气血", "舍身一拳",
            "镇岳崩", "法天象地·暗金山岳", "流星坠地", "拳风", "七曜诛圣阵", "火墙", "御剑诀"
        }.Select(n => new SkillLibraryItem { Name = n }).ToList();

        var fixer = new StoryboardAutoFixer();
        var fixedText = fixer.Fix(raw, stage4Text, skillNames, out var fixes);

        var skillLineCount = Regex.Matches(fixedText, @"(?m)^\s*-\s*\*\*技能\*\*\s*[:：]").Count;
        var frames = ParseFrames(fixedText, "1.2", 1, "打斗/动作");
        var summary = string.Join("\n", frames.Select(f => $"{f.ShotNumber}: [{(f.Skills ?? "<null>")}]"));
        File.WriteAllText(@"d:\CodexProject\ManhuaPipeline\logs\repro_real_v4.txt",
            "SKILL_LINES=" + skillLineCount + "\nFIXES:\n" + string.Join("\n", fixes) + "\n=====\nFIXED_TEXT:\n" + fixedText + "\n=====\nFRAMES:\n" + summary,
            new System.Text.UTF8Encoding(false));

        Assert.True(skillLineCount == 1, $"Fix(V4全文) 后技能行应为 1 行，实际 {skillLineCount}：\n{fixedText}");
        Assert.True(
            frames.All(f => string.Equals(f.Skills, "碎金劲, 玄冰细剑", StringComparison.Ordinal)),
            $"Skills 出现双份或异常：\n{summary}");
    }

    [Fact]
    public void Repro_SingleShotBest_ShouldKeepSkillsSingle()
    {
        // 决定性实验：1.12/1.13 的 best（校验耗尽保留版本）是「单镜头 + 一行技能行」结构，
        // 数据库却 Skills 双份。直接解析这两份文本验证。
        var cases = new[]
        {
            // (单元号, 开始行, 结束行, 期望技能)
            ("1.12", 27526, 27553, "血祭仙尊"),
            ("1.13", 27733, 27759, "赤金气血, 力破万法, 碎金劲, 法天象地·武圣法相"),
        };
        var lines = File.ReadAllLines(LogPath);
        foreach (var (unitNumber, fromLine, toLine, expected) in cases)
        {
            // 行号 1-based，数组 0-based
            var raw = string.Join("\n", lines.Skip(fromLine - 1).Take(toLine - fromLine + 1).Where(l => !l.StartsWith("====")));
            var stage4Text = File.ReadAllText(@"d:\CodexProject\ManhuaPipeline\logs\stage4_v4.txt");
            var fixer = new StoryboardAutoFixer();
            var fixedText = fixer.Fix(raw, stage4Text, new List<SkillLibraryItem>(), out _);
            var skillLineCount = Regex.Matches(fixedText, @"(?m)^\s*-\s*\*\*技能\*\*\s*[:：]").Count;
            var frames = ParseFrames(fixedText, unitNumber, 1, "打斗/动作");
            var summary = string.Join("\n", frames.Select(f => $"{f.ShotNumber}: [{(f.Skills ?? "<null>")}]"));
            File.AppendAllText(@"d:\CodexProject\ManhuaPipeline\logs\repro_real_v4.txt",
                $"\n===== Unit {unitNumber} (best single-shot) =====\nSKILL_LINES={skillLineCount}\nFRAMES:\n{summary}\n",
                new System.Text.UTF8Encoding(false));
            Assert.True(skillLineCount == 1, $"Unit {unitNumber}: Fix 后技能行应为 1 行，实际 {skillLineCount}");
            Assert.True(frames.Count == 1, $"Unit {unitNumber}: 应解析出 1 帧，实际 {frames.Count}");
            // 期望值取 Fix 后唯一的技能行；帧值必须与之一致（无双份）。
            var skillLine = Regex.Match(fixedText, @"(?m)^\s*-\s*\*\*技能\*\*\s*[:：]\s*(.*)$");
            var actualExpected = skillLine.Success ? skillLine.Groups[1].Value.Trim() : null;
            Assert.True(
                frames.All(f => string.Equals(f.Skills, actualExpected, StringComparison.Ordinal)),
                $"Unit {unitNumber}: Skills 出现双份或异常：\n{summary}");
        }
    }

    [Fact]
    public void Repro12_JoinedBlock_RealFix_ShouldNotAppendSkillLine()
    {
        // 决定性实验：构造拼接文本的 1.2 块（1.2 result + 1.3 result 开头 ### 【第1集】），
        // 跑真实 Fix，检查是否在块末尾追加第二行技能行（LlmResponse 1.2 块确有 2 行）。
        var raw12 = ExtractUnitText("1.2", "===== Unit 1.2 完成");
        var eqIdx = raw12.IndexOf("====", StringComparison.Ordinal);
        if (eqIdx > 0) raw12 = raw12.Substring(0, eqIdx).TrimEnd();

        var skillNames = new[]
        {
            "碎金劲", "玄冰细剑", "擒龙手", "寒魄封天", "霜刃千葬", "凝月冰牢",
            "赤炎天坠", "凤羽燎原", "焚星火轮", "九曜火莲", "烬影焚城", "炽阳焚界",
            "冰凰裂空", "玄冰龙卷", "雪魄镇魂", "九霄雷罚", "雷龙贯日", "紫电裂岳",
            "天枢雷印", "玄雷破阵", "震霆灭影", "万剑归宗", "青霄剑瀑", "星河剑阵",
            "斩月惊鸿", "天罡剑域", "归墟一剑", "风啸九天", "苍岚裂空", "逐影风刃",
            "天游旋岚", "青翼踏云", "罡风绝杀", "幽冥蚀月", "魔影千袭", "玄煞噬魂",
            "黑渊锁天", "血月断界", "夜魇终临", "圣光诛邪", "天门镇魔", "九曜神罚",
            "金翎破晓", "灵霄镇岳", "太虚归一", "碎金撼岳", "山河震界", "破法天罡",
            "武极金身", "法天象地·武圣法相", "力破万法", "踏天步", "赤金气血", "舍身一拳",
            "镇岳崩", "法天象地·暗金山岳", "流星坠地", "拳风", "七曜诛圣阵", "火墙", "御剑诀"
        }.Select(n => new SkillLibraryItem { Name = n }).ToList();

        // 拼接文本的 1.2 块 = 1.2 result + \n\n + "### 【第1集】"（1.3 result 开头）+ \n\n
        var joined = raw12 + "\n\n### 【第1集】\n\n";

        var fixer = new StoryboardAutoFixer();
        var fixedText = fixer.Fix(joined, "", skillNames, out var fixes);

        var skillLines = Regex.Matches(fixedText, @"(?m)^\s*-\s*\*\*技能\*\*\s*[:：]").Count;
        File.WriteAllText(@"d:\CodexProject\ManhuaPipeline\logs\repro_joined.txt",
            "INPUT_JOINED:\n" + joined + "\n=====\nFIXED:\n" + fixedText + "\n=====\nSKILL_LINES=" + skillLines + "\nFIXES:\n" + string.Join("\n", fixes),
            new System.Text.UTF8Encoding(false));

        // 判断：如果拼接块被 Fix 末尾追加技能行，说明追加分支触发（块内技能行被丢弃）；
        // 如果 1 行，说明追加未发生。
        Assert.True(skillLines == 1,
            $"Fix(拼接1.2块) 技能行应为 1 行；实际 {skillLines}（说明末尾追加分支被触发）");
    }

    [Fact]
    public void Repro_FullJoin_1_1_to_1_34_RealFix()
    {
        // 决定性实验：完整模拟 PlanAsync 拼接（34 个单元 best + \n\n 连接）+ 真实 Fix，
        // 检查 1.2 块是否出现两行技能行。
        var lines = File.ReadAllLines(LogPath);
        var parts = new List<string>();
        for (var n = 1; n <= 34; n++)
        {
            var unitNumber = n == 1 ? "1.1" : "1." + n;
            var start = Array.FindIndex(lines, l => l.StartsWith("===== Unit " + unitNumber + " 开始"));
            var end = Array.FindIndex(lines, l => l.StartsWith("===== Unit " + unitNumber + " 完成"));
            if (start < 0 || end < 0 || end <= start) continue;
            int llmStart = -1;
            for (var i = start; i < end; i++)
                if (lines[i].StartsWith("---- LLM返回 ----")) llmStart = i;
            var from = llmStart >= 0 ? llmStart + 1 : start + 1;
            var raw = string.Join("\n", lines.Skip(from).Take(end - from));
            var eqIdx = raw.IndexOf("====", StringComparison.Ordinal);
            if (eqIdx > 0) raw = raw.Substring(0, eqIdx).TrimEnd();
            var trimmed = raw.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            parts.Add(trimmed);
        }

        var joined = string.Join("\n\n", parts);
        File.WriteAllText(@"d:\CodexProject\ManhuaPipeline\logs\joined_full_34.txt", joined, new System.Text.UTF8Encoding(false));

        var stage4Text = File.ReadAllText(@"d:\CodexProject\ManhuaPipeline\logs\stage4_db.txt").Trim();
        var skillNames = new[]
        {
            "碎金劲", "玄冰细剑", "擒龙手", "寒魄封天", "霜刃千葬", "凝月冰牢",
            "赤炎天坠", "凤羽燎原", "焚星火轮", "九曜火莲", "烬影焚城", "炽阳焚界",
            "冰凰裂空", "玄冰龙卷", "雪魄镇魂", "九霄雷罚", "雷龙贯日", "紫电裂岳",
            "天枢雷印", "玄雷破阵", "震霆灭影", "万剑归宗", "青霄剑瀑", "星河剑阵",
            "斩月惊鸿", "天罡剑域", "归墟一剑", "风啸九天", "苍岚裂空", "逐影风刃",
            "天游旋岚", "青翼踏云", "罡风绝杀", "幽冥蚀月", "魔影千袭", "玄煞噬魂",
            "黑渊锁天", "血月断界", "夜魇终临", "圣光诛邪", "天门镇魔", "九曜神罚",
            "金翎破晓", "灵霄镇岳", "太虚归一", "碎金撼岳", "山河震界", "破法天罡",
            "武极金身", "法天象地·武圣法相", "力破万法", "踏天步", "赤金气血", "舍身一拳",
            "镇岳崩", "法天象地·暗金山岳", "流星坠地", "拳风", "七曜诛圣阵", "火墙", "御剑诀"
        }.Select(n => new SkillLibraryItem { Name = n }).ToList();

        var fixer = new StoryboardAutoFixer();
        var fixedText = fixer.Fix(joined, stage4Text, skillNames, out var fixes);
        File.WriteAllText(@"d:\CodexProject\ManhuaPipeline\logs\fixed_full_34.txt",
            "FIXES:\n" + string.Join("\n", fixes) + "\n=====\nFIXED:\n" + fixedText,
            new System.Text.UTF8Encoding(false));

        var m12 = Regex.Match(fixedText, @"(?s)【单元1\.2】.*?(?=【单元1\.3】)");
        var block12 = m12.Success ? m12.Value : "";
        var skill12 = Regex.Matches(block12, @"(?m)^\s*-\s*\*\*技能\*\*\s*[:：]").Count;
        File.AppendAllText(@"d:\CodexProject\ManhuaPipeline\logs\fixed_full_34.txt",
            $"\n===== 1.2 block skill lines = {skill12}\n{block12}", new System.Text.UTF8Encoding(false));
        Assert.True(skill12 == 1, $"Fix(完整34单元拼接) 后 1.2 块技能行应为 1 行，实际 {skill12}");
    }

    [Fact]
    public void Repro_SingleUnit_Fix_Best12()
    {
        // 单单元实验：Fix(best(1.2))，检查 1.2 块尾是否出现追加技能行
        var lines = File.ReadAllLines(LogPath);
        var start = Array.FindIndex(lines, l => l.StartsWith("===== Unit 1.2 开始"));
        var end = Array.FindIndex(lines, l => l.StartsWith("===== Unit 1.2 完成"));
        int llmStart = -1;
        for (var i = start; i < end; i++)
            if (lines[i].StartsWith("---- LLM返回 ----")) llmStart = i;
        var from = llmStart >= 0 ? llmStart + 1 : start + 1;
        var raw = string.Join("\n", lines.Skip(from).Take(end - from));
        var eqIdx = raw.IndexOf("====", StringComparison.Ordinal);
        if (eqIdx > 0) raw = raw.Substring(0, eqIdx).TrimEnd();
        var best12 = raw.Trim();
        File.WriteAllText(@"d:\CodexProject\ManhuaPipeline\logs\best12_raw.txt", best12, new System.Text.UTF8Encoding(false));

        var stage4Text = File.ReadAllText(@"d:\CodexProject\ManhuaPipeline\logs\stage4_v4.txt");
        var skillNames = new[]
        {
            "碎金劲", "玄冰细剑", "擒龙手", "寒魄封天", "霜刃千葬", "凝月冰牢",
            "赤炎天坠", "凤羽燎原", "焚星火轮", "九曜火莲", "烬影焚城", "炽阳焚界",
            "冰凰裂空", "玄冰龙卷", "雪魄镇魂", "九霄雷罚", "雷龙贯日", "紫电裂岳",
            "天枢雷印", "玄雷破阵", "震霆灭影", "万剑归宗", "青霄剑瀑", "星河剑阵",
            "斩月惊鸿", "天罡剑域", "归墟一剑", "风啸九天", "苍岚裂空", "逐影风刃",
            "天游旋岚", "青翼踏云", "罡风绝杀", "幽冥蚀月", "魔影千袭", "玄煞噬魂",
            "黑渊锁天", "血月断界", "夜魇终临", "圣光诛邪", "天门镇魔", "九曜神罚",
            "金翎破晓", "灵霄镇岳", "太虚归一", "碎金撼岳", "山河震界", "破法天罡",
            "武极金身", "法天象地·武圣法相", "力破万法", "踏天步", "赤金气血", "舍身一拳",
            "镇岳崩", "法天象地·暗金山岳", "流星坠地", "拳风", "七曜诛圣阵", "火墙", "御剑诀"
        }.Select(n => new SkillLibraryItem { Name = n }).ToList();
        var fixer = new StoryboardAutoFixer();
        var fixedText = fixer.Fix(best12, stage4Text, skillNames, out var fixes);
        File.WriteAllText(@"d:\CodexProject\ManhuaPipeline\logs\best12_fixed.txt",
            "FIXES:\n" + string.Join("\n", fixes) + "\n=====\nFIXED:\n" + fixedText,
            new System.Text.UTF8Encoding(false));

        var tail = fixedText.Substring(Math.Max(0, fixedText.Length - 400));
        Assert.Contains("- **技能**: 碎金劲, 玄冰细剑", fixedText);
        // 输出尾部（1.2-3 帧后）不应出现第二行技能行
        var tailSkillCount = Regex.Matches(tail, @"^\s*-\s*\*\*技能\*\*\s*[:：]", RegexOptions.Multiline).Count;
        File.AppendAllText(@"d:\CodexProject\ManhuaPipeline\logs\best12_fixed.txt",
            $"\n===== tail skill lines = {tailSkillCount}\n{tail}", new System.Text.UTF8Encoding(false));
        Assert.True(tailSkillCount == 0, $"Fix(best(1.2)) 尾部(1.2-3帧后)不应有技能行，实际 {tailSkillCount}");
    }

    [Fact]
    public void Repro_Idempotency_LlmResponse()
    {
        // 幂等实验：真实 LlmResponse 作为 Fix 输入，看 Fix 是否改变它
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var llm = System.Text.Encoding.GetEncoding("GB2312")
            .GetString(System.IO.File.ReadAllBytes(@"d:\CodexProject\ManhuaPipeline\logs\llmresponse_s178.txt"));
        var stage4Text = File.ReadAllText(@"d:\CodexProject\ManhuaPipeline\logs\stage4_v4.txt");
        var skillNames = new[]
        {
            "碎金劲", "玄冰细剑", "擒龙手", "寒魄封天", "霜刃千葬", "凝月冰牢",
            "赤炎天坠", "凤羽燎原", "焚星火轮", "九曜火莲", "烬影焚城", "炽阳焚界",
            "冰凰裂空", "玄冰龙卷", "雪魄镇魂", "九霄雷罚", "雷龙贯日", "紫电裂岳",
            "天枢雷印", "玄雷破阵", "震霆灭影", "万剑归宗", "青霄剑瀑", "星河剑阵",
            "斩月惊鸿", "天罡剑域", "归墟一剑", "风啸九天", "苍岚裂空", "逐影风刃",
            "天游旋岚", "青翼踏云", "罡风绝杀", "幽冥蚀月", "魔影千袭", "玄煞噬魂",
            "黑渊锁天", "血月断界", "夜魇终临", "圣光诛邪", "天门镇魔", "九曜神罚",
            "金翎破晓", "灵霄镇岳", "太虚归一", "碎金撼岳", "山河震界", "破法天罡",
            "武极金身", "法天象地·武圣法相", "力破万法", "踏天步", "赤金气血", "舍身一拳",
            "镇岳崩", "法天象地·暗金山岳", "流星坠地", "拳风", "七曜诛圣阵", "火墙", "御剑诀"
        }.Select(n => new SkillLibraryItem { Name = n }).ToList();
        var fixer = new StoryboardAutoFixer();
        var fixedText = fixer.Fix(llm, stage4Text, skillNames, out var fixes);
        System.IO.File.WriteAllText(@"d:\CodexProject\ManhuaPipeline\logs\fix_llmresponse.txt",
            "FIXES:\n" + string.Join("\n", fixes) + "\n=====\n" + fixedText,
            new System.Text.UTF8Encoding(false));
        var identical = string.Equals(llm, fixedText, StringComparison.Ordinal);
        // 对比 1.2 块
        var i1 = llm.IndexOf("#### 【单元1.2】"); var i2 = llm.IndexOf("#### 【单元1.3】");
        var blockIn = llm.Substring(i1, i2 - i1);
        var j1 = fixedText.IndexOf("#### 【单元1.2】"); var j2 = fixedText.IndexOf("#### 【单元1.3】");
        var blockOut = fixedText.Substring(j1, j2 - j1);
        System.IO.File.AppendAllText(@"d:\CodexProject\ManhuaPipeline\logs\fix_llmresponse.txt",
            $"\n===== IDENTICAL={identical} =====\nIN:\n{blockIn}\n=====\nOUT:\n{blockOut}",
            new System.Text.UTF8Encoding(false));
    }

    [Fact]
    public void Repro_Joined_1_1_to_1_3_RealFix()
    {
        // 决定性实验：真实拼接文本（best(1.1)+best(1.2)+best(1.3)，\n\n 连接，与 PlanAsync 相同）
        // + 真实 Stage4 + 真实技能库，运行 Fix 后检查 1.2 块技能行数。
        var joined = File.ReadAllText(@"d:\CodexProject\ManhuaPipeline\logs\best_extract\joined_1_1_to_1_3.txt");
        var stage4Text = File.ReadAllText(@"d:\CodexProject\ManhuaPipeline\logs\stage4_v4.txt");
        Assert.Contains("1.1", joined);
        Assert.Contains("1.2", joined);
        Assert.Contains("1.3", joined);

        var skillNames = new[]
        {
            "碎金劲", "玄冰细剑", "擒龙手", "寒魄封天", "霜刃千葬", "凝月冰牢",
            "赤炎天坠", "凤羽燎原", "焚星火轮", "九曜火莲", "烬影焚城", "炽阳焚界",
            "冰凰裂空", "玄冰龙卷", "雪魄镇魂", "九霄雷罚", "雷龙贯日", "紫电裂岳",
            "天枢雷印", "玄雷破阵", "震霆灭影", "万剑归宗", "青霄剑瀑", "星河剑阵",
            "斩月惊鸿", "天罡剑域", "归墟一剑", "风啸九天", "苍岚裂空", "逐影风刃",
            "天游旋岚", "青翼踏云", "罡风绝杀", "幽冥蚀月", "魔影千袭", "玄煞噬魂",
            "黑渊锁天", "血月断界", "夜魇终临", "圣光诛邪", "天门镇魔", "九曜神罚",
            "金翎破晓", "灵霄镇岳", "太虚归一", "碎金撼岳", "山河震界", "破法天罡",
            "武极金身", "法天象地·武圣法相", "力破万法", "踏天步", "赤金气血", "舍身一拳",
            "镇岳崩", "法天象地·暗金山岳", "流星坠地", "拳风", "七曜诛圣阵", "火墙", "御剑诀"
        }.Select(n => new SkillLibraryItem { Name = n }).ToList();

        var fixer = new StoryboardAutoFixer();
        var fixedText = fixer.Fix(joined, stage4Text, skillNames, out var fixes);

        var skillLines = Regex.Matches(fixedText, @"(?m)^\s*-\s*\*\*技能\*\*\s*[:：]").Count;
        File.WriteAllText(@"d:\CodexProject\ManhuaPipeline\logs\repro_joined_113.txt",
            "INPUT_JOINED:\n" + joined + "\n=====\nFIXED:\n" + fixedText + "\n=====\nSKILL_LINES=" + skillLines + "\nFIXES:\n" + string.Join("\n", fixes),
            new System.Text.UTF8Encoding(false));

        // 定位 1.2 块（#### 【单元1.2】 到 #### 【单元1.3】 之间），统计技能行
        var m12 = Regex.Match(fixedText, @"(?s)【单元1\.2】.*?(?=【单元1\.3】)");
        var block12 = m12.Success ? m12.Value : "";
        var skill12 = Regex.Matches(block12, @"(?m)^\s*-\s*\*\*技能\*\*\s*[:：]").Count;
        Assert.True(skill12 == 1, $"Fix(完整拼接) 后 1.2 块技能行应为 1 行，实际 {skill12}（块内容）：\n{block12}");
    }

    [Theory]
    [MemberData(nameof(Units))]
    public void Parse_Should_KeepSkillsSingle(
        string completeMarker, string unitNumber, int episodeNumber, string unitType)
    {
        var raw = ExtractUnitText(unitNumber, completeMarker);
        // 返工后的 LLM 返回可能不带「【单元 x】」块头，靠 ParseFrames 的 defaultUnitNumber 兜底。

        var fixer = new StoryboardAutoFixer();
        var fixedText = fixer.Fix(raw, "", new List<SkillLibraryItem>(), out _);

        var skillLineCount = Regex.Matches(fixedText, @"(?m)^\s*-\s*\*\*技能\*\*\s*[:：]").Count;
        Assert.True(skillLineCount == 1, $"Fix 后技能行应为 1 行，实际 {skillLineCount}：\n{fixedText}");

        // 期望值取 Fix 后唯一的技能行（日志 LLM 返回与 DB 最终值可能不同，不能用 DB 值断言）。
        var skillLine = Regex.Match(fixedText, @"(?m)^\s*-\s*\*\*技能\*\*\s*[:：]\s*(.*)$");
        var expected = skillLine.Success ? skillLine.Groups[1].Value.Trim() : null;

        var frames = ParseFrames(fixedText, unitNumber, episodeNumber, unitType);
        Assert.NotEmpty(frames);

        var summary = string.Join("\n", frames.Select(f => $"{f.ShotNumber}: [{(f.Skills ?? "<null>")}]"));
        File.WriteAllText(@"d:\CodexProject\ManhuaPipeline\logs\repro_out.txt",
            "RAW_TEXT:\n" + fixedText + "\n=====\nFRAMES:\n" + summary, new System.Text.UTF8Encoding(false));
        Assert.True(
            frames.All(f => string.Equals(f.Skills, expected, StringComparison.Ordinal)),
            $"Skills 出现双份或异常：\n{summary}");
    }

    // ========== 复刻 SaveStoryboardFramesFromResult 核心状态机（无 DB 保存） ==========
    private static List<StoryboardFrame> ParseFrames(string text, string defaultUnitNumber, int defaultEpisodeNumber, string defaultUnitType)
    {
        var lines = text.Split('\n');
        int currentEpisodeNum = defaultEpisodeNumber;
        string? currentUnitNumber = string.IsNullOrWhiteSpace(defaultUnitNumber) ? null : defaultUnitNumber.Trim();
        string? currentUnitType = string.IsNullOrWhiteSpace(defaultUnitType) ? null : defaultUnitType.Trim();
        string? currentUnitNote = null;
        string? currentUnitSkills = null;
        var currentFrames = new List<StoryboardFrame>();
        StoryboardFrame? currentFrame = null;
        var continuation = new StoryboardFrameFieldParser.ContinuationState();

        void AddCurrentFrame()
        {
            if (currentFrame == null) return;
            currentFrames.Add(currentFrame);
            currentFrame = null;
        }

        void FlushCurrentUnit()
        {
            AddCurrentFrame();
        }

        foreach (var rawLine in lines)
        {
            var l = rawLine.Trim();
            if (string.IsNullOrEmpty(l)) continue;

            var epMatch = Regex.Match(l, @"【?第\s*(\d+)\s*集】?");
            if (epMatch.Success)
            {
                FlushCurrentUnit();
                currentEpisodeNum = int.Parse(epMatch.Groups[1].Value);
                continue;
            }

            var unitMatch = Regex.Match(l, @"【?单元\s*([\d.]+[a-zA-Z]?)\s*】?");
            if (unitMatch.Success)
            {
                var newUnitNumber = unitMatch.Groups[1].Value.Trim();
                if (!string.Equals(currentUnitNumber, newUnitNumber, StringComparison.OrdinalIgnoreCase))
                {
                    FlushCurrentUnit();
                    currentUnitNumber = newUnitNumber;
                    if (!string.IsNullOrWhiteSpace(defaultUnitType)) currentUnitType = defaultUnitType.Trim();
                    currentUnitNote = null;
                    currentUnitSkills = null;
                }
                continue;
            }

            var shotNumber = StoryboardFrameParser.TryGetShotNumber(l);
            if (shotNumber != null)
            {
                AddCurrentFrame();
                continuation.TimelineOpen = false;
                continuation.CharacterListOpen = false;
                currentFrame = new StoryboardFrame
                {
                    ShotNumber = shotNumber,
                    UnitNumber = currentUnitNumber,
                    EpisodeNumber = currentEpisodeNum > 0 ? currentEpisodeNum : null,
                    Skills = currentUnitSkills,
                    SortOrder = currentFrames.Count
                };
                continue;
            }

            if (currentFrame == null)
            {
                var unitSkillLine = Regex.Match(l, @"^-\s*\*\*技能\*\*\s*[:：]\s*(.*)$");
                if (unitSkillLine.Success)
                {
                    var skillVal = unitSkillLine.Groups[1].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(skillVal))
                        currentUnitSkills = StoryboardFrameFieldParser.NormalizeSkillList(skillVal);
                }
                continue;
            }

            var unitNote = StoryboardFrameFieldParser.TryGetUnitNote(l);
            if (unitNote != null) currentUnitNote = unitNote;

            if (StoryboardFrameFieldParser.TryApplyField(currentFrame, l, continuation, ref currentUnitType))
                continue;

            StoryboardFrameFieldParser.TryAppendContinuation(currentFrame, l, continuation);
        }

        FlushCurrentUnit();
        return currentFrames;
    }
}
