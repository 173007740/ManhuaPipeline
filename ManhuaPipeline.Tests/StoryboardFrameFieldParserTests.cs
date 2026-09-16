using ManhuaPipeline.Models;
using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

public class StoryboardFrameFieldParserTests
{
    private static StoryboardFrameFieldParser.ContinuationState NewCont() => new();

    [Fact]
    public void Timeline_EmptyHeaderThenIndentedLines_GoesToTimelineNotDescription()
    {
        var frame = new StoryboardFrame();
        string? unitType = null;
        var cont = NewCont();

        Assert.True(StoryboardFrameFieldParser.TryApplyField(frame, "- **镜头时间轴**:", cont, ref unitType));
        Assert.True(cont.TimelineOpen);

        Assert.True(StoryboardFrameFieldParser.TryAppendContinuation(frame, "0-2s: 拳锋与阵光僵持，光屑飞溅；", cont));
        Assert.True(StoryboardFrameFieldParser.TryAppendContinuation(frame, "2-5s: 急拉至大远景展示合围形成。", cont));

        Assert.StartsWith("0-2s:", frame.Timeline);
        Assert.Contains("2-5s:", frame.Timeline);
        Assert.Null(frame.Description);
    }

    [Fact]
    public void Timeline_NextField_ClosesContinuation()
    {
        var frame = new StoryboardFrame();
        string? unitType = null;
        var cont = NewCont();

        StoryboardFrameFieldParser.TryApplyField(frame, "- **镜头时间轴**:", cont, ref unitType);
        Assert.True(cont.TimelineOpen);

        Assert.True(StoryboardFrameFieldParser.TryApplyField(frame, "- **镜头描述**: 开局", cont, ref unitType));
        Assert.False(cont.TimelineOpen);
        Assert.Equal("开局", frame.Description);

        StoryboardFrameFieldParser.TryAppendContinuation(frame, "普通正文", cont);
        Assert.Equal("开局 普通正文", frame.Description);
    }

    [Fact]
    public void Timeline_InlineValue_IsStored()
    {
        var frame = new StoryboardFrame();
        string? unitType = null;
        var cont = NewCont();

        Assert.True(StoryboardFrameFieldParser.TryApplyField(frame, "- **镜头时间轴**: 0-2s: 对峙", cont, ref unitType));
        Assert.Equal("0-2s: 对峙", frame.Timeline);
        Assert.True(cont.TimelineOpen);
    }

    [Fact]
    public void Timeline_MarkdownBulletSubItems_AreCollectedUntilNextField()
    {
        var frame = new StoryboardFrame();
        string? unitType = null;
        var cont = NewCont();

        Assert.True(StoryboardFrameFieldParser.TryApplyField(frame, "- **镜头时间轴**:", cont, ref unitType));
        Assert.True(cont.TimelineOpen);

        Assert.True(StoryboardFrameFieldParser.TryAppendContinuation(frame, "  - 0-1.5s: 七宗宗主各据阵位同催「七曜诛圣阵」，七根阵柱亮起；", cont));
        Assert.True(StoryboardFrameFieldParser.TryAppendContinuation(frame, "  - 1.5-3s: 前世岳沉天战斗态（岳沉罡本体）银白长发被法光气浪向后拉直；", cont));
        Assert.True(StoryboardFrameFieldParser.TryAppendContinuation(frame, "  - 3-4.5s: 七色法光压至岳沉罡身前三丈，赤金气血光晕浮出体表；", cont));
        Assert.True(StoryboardFrameFieldParser.TryAppendContinuation(frame, "  - 4.5-5s: 法光短暂凝滞、气压蓄满，合围完成。", cont));

        Assert.StartsWith("0-1.5s:", frame.Timeline);
        Assert.Contains("1.5-3s:", frame.Timeline);
        Assert.Contains("4.5-5s:", frame.Timeline);
        Assert.Null(frame.Description);

        Assert.True(StoryboardFrameFieldParser.TryApplyField(frame, "- **镜头描述**: 低角度仰拍开场，葬天台四周七根阵柱同亮。", cont, ref unitType));
        Assert.False(cont.TimelineOpen);
        Assert.Equal("低角度仰拍开场，葬天台四周七根阵柱同亮。", frame.Description);

        Assert.False(StoryboardFrameFieldParser.TryAppendContinuation(frame, "  - 描述区 bullet 不归入正文", cont));
        Assert.Equal("低角度仰拍开场，葬天台四周七根阵柱同亮。", frame.Description);
    }

    [Fact]
    public void UnitType_Field_UpdatesUnitType()
    {
        var frame = new StoryboardFrame();
        string? unitType = "文戏";
        var cont = NewCont();

        Assert.True(StoryboardFrameFieldParser.TryApplyField(frame, "- **单元类型**: 高潮/对决", cont, ref unitType));
        Assert.Equal("高潮/对决", unitType);
    }

    [Fact]
    public void UnitType_ParentheticalNote_IsStripped()
    {
        var frame = new StoryboardFrame();
        string? unitType = null;
        var cont = NewCont();

        Assert.True(StoryboardFrameFieldParser.TryApplyField(frame, "- **单元类型**: 文戏/情感（觉醒前奏，异象初现，情绪由压抑转震撼）", cont, ref unitType));
        Assert.Equal("文戏/情感", unitType);
    }

    [Fact]
    public void TryGetUnitNote_ReturnsParentheticalNote()
    {
        var note = StoryboardFrameFieldParser.TryGetUnitNote("- **单元类型**: 文戏/情感（觉醒前奏，异象初现，情绪由压抑转震撼）");

        Assert.Equal("觉醒前奏，异象初现，情绪由压抑转震撼", note);
    }

    [Fact]
    public void TryGetUnitNote_PlainType_ReturnsNull()
    {
        Assert.Null(StoryboardFrameFieldParser.TryGetUnitNote("- **单元类型**: 高潮/对决"));
        Assert.Null(StoryboardFrameFieldParser.TryGetUnitNote("镜头描述: 无括号备注"));
    }

    [Fact]
    public void Skills_InlineSkillLine_OverwritesNotAppends()
    {
        // 回归：帧创建后再次出现技能行（LLM 回声）应覆盖而非拼接，避免 Skills 双份。
        var frame = new StoryboardFrame { Skills = "碎金劲, 玄冰细剑" };
        string? unitType = null;
        var cont = NewCont();

        Assert.True(StoryboardFrameFieldParser.TryApplyField(frame, "- **技能**: 碎金劲, 玄冰细剑", cont, ref unitType));
        Assert.Equal("碎金劲, 玄冰细剑", frame.Skills);
    }

    [Theory]
    [InlineData("赤金气血， 霜刃千葬， 碎金撼岳， 碎金劲", "赤金气血, 霜刃千葬, 碎金撼岳, 碎金劲")]
    [InlineData("碎金劲，玄冰细剑", "碎金劲, 玄冰细剑")]
    [InlineData("血祭仙尊", "血祭仙尊")]
    [InlineData("  A　,  B ， C、D ", "A, B, C, D")]
    [InlineData("法天象地·武圣法相（背景法相光焰, 聚焦于拳脚肉搏）, 赤金气血, 寒魄封天", "法天象地·武圣法相, 赤金气血, 寒魄封天")]
    [InlineData("碎金劲（全力一击）", "碎金劲")]
    [InlineData("凝月冰牢(封冻全场)", "凝月冰牢")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void NormalizeSkillList_UnifiesDelimiters(string? input, string? expected)
    {
        Assert.Equal(expected, StoryboardFrameFieldParser.NormalizeSkillList(input));
    }

    [Fact]
    public void Description_StripsLlmBracketedDirectorNote()
    {
        var frame = new StoryboardFrame();
        string? unitType = null;
        var cont = NewCont();

        Assert.True(StoryboardFrameFieldParser.TryApplyField(frame, "- **镜头描述**: 【导演注意】节奏慢、情绪压抑。岳沉天垂目凝视阵纹", cont, ref unitType));
        Assert.Equal("岳沉天垂目凝视阵纹", frame.Description);
    }

    [Fact]
    public void Description_KeepsSystemAuthoritativeNote()
    {
        var text = "【导演注意】本单元情绪任务：高潮/爆发（9/10）；快切顿帧营造情绪爆发。岳沉天垂目凝视阵纹";

        Assert.Equal(text, StoryboardFrameFieldParser.StripBracketedNotes(text));
    }

    // ===== 回归：出镜角色“空标题 + 下一行缩进子列表”写法（如单元1.20-1丢遐蝶案例）=====

    [Fact]
    public void CharacterList_EmptyHeaderThenBulletSubItems_GoToCharactersNotDescription()
    {
        var frame = new StoryboardFrame();
        string? unitType = null;
        var cont = NewCont();

        Assert.True(StoryboardFrameFieldParser.TryApplyField(frame, "- **出镜角色及表情**:", cont, ref unitType));
        Assert.True(cont.CharacterListOpen);

        Assert.True(StoryboardFrameFieldParser.TryAppendContinuation(frame, "  - 遐蝶：静默注视，眼底悲意一闪而过", cont));
        Assert.True(StoryboardFrameFieldParser.TryAppendContinuation(frame, "  - 紫蝶（生物点缀，非对弈主体）：绕蝶盘旋", cont));

        Assert.StartsWith("遐蝶：静默注视", frame.Characters);
        Assert.Contains("紫蝶（生物点缀，非对弈主体）：绕蝶盘旋", frame.Characters);
        Assert.Null(frame.Description);
    }

    [Fact]
    public void CharacterList_EmptyHeaderThenNonBulletContinuation_IsCollected()
    {
        var frame = new StoryboardFrame();
        string? unitType = null;
        var cont = NewCont();

        Assert.True(StoryboardFrameFieldParser.TryApplyField(frame, "- 出镜角色及表情:", cont, ref unitType));
        Assert.True(cont.CharacterListOpen);

        Assert.True(StoryboardFrameFieldParser.TryAppendContinuation(frame, "遐蝶：独自立于阵心，紫蝶绕身", cont));

        Assert.Equal("遐蝶：独自立于阵心，紫蝶绕身", frame.Characters);
        Assert.Null(frame.Description);
    }

    [Fact]
    public void CharacterList_InlineValue_StoredAndListClosed()
    {
        var frame = new StoryboardFrame();
        string? unitType = null;
        var cont = NewCont();

        Assert.True(StoryboardFrameFieldParser.TryApplyField(frame, "- **出镜角色及表情**: 遐蝶（静默）；玻吕茜亚（含笑）", cont, ref unitType));
        Assert.Equal("遐蝶（静默）；玻吕茜亚（含笑）", frame.Characters);
        Assert.False(cont.CharacterListOpen);
    }

    [Fact]
    public void CharacterList_NextField_ClosesListAndDoesNotStealFollowingLines()
    {
        var frame = new StoryboardFrame();
        string? unitType = null;
        var cont = NewCont();

        Assert.True(StoryboardFrameFieldParser.TryApplyField(frame, "- 出镜角色及表情:", cont, ref unitType));
        Assert.True(cont.CharacterListOpen);
        Assert.True(StoryboardFrameFieldParser.TryAppendContinuation(frame, "  - 遐蝶：被阵光笼罩，缓缓抬头", cont));

        // 下一个字段标题出现 → 角色续行关闭
        Assert.True(StoryboardFrameFieldParser.TryApplyField(frame, "- **镜头描述**: 阵光自脚底漫上", cont, ref unitType));
        Assert.False(cont.CharacterListOpen);
        Assert.False(cont.TimelineOpen);
        Assert.Equal("阵光自脚底漫上", frame.Description);

        // 描述区后续 bullet 行不再被吞进出镜角色
        Assert.False(StoryboardFrameFieldParser.TryAppendContinuation(frame, "  - 描述区子弹不归角色", cont));
        Assert.Equal("遐蝶：被阵光笼罩，缓缓抬头", frame.Characters);
    }

    [Fact]
    public void CharacterList_EmptyHeaderThenNextShot_StateDoesNotLeak()
    {
        var frame = new StoryboardFrame();
        string? unitType = null;
        var cont = NewCont();

        Assert.True(StoryboardFrameFieldParser.TryApplyField(frame, "- 出镜角色及表情:", cont, ref unitType));
        Assert.True(cont.CharacterListOpen);

        // 模拟主循环遇到新镜头标题：重置续行状态
        cont.TimelineOpen = false;
        cont.CharacterListOpen = false;

        Assert.True(StoryboardFrameFieldParser.TryApplyField(frame, "- **镜头描述**: 新镜头开场", cont, ref unitType));
        Assert.Equal("新镜头开场", frame.Description);
    }

    [Fact]
    public void Dialogue_EmptyHeaderThenIndentedBullets_GoesToDialogue()
    {
        var frame = new StoryboardFrame();
        string? unitType = null;
        var cont = NewCont();

        Assert.True(StoryboardFrameFieldParser.TryApplyField(frame, "- **对话/台词**:", cont, ref unitType));
        Assert.True(cont.DialogueOpen);
        Assert.False(cont.TimelineOpen);
        Assert.False(cont.CharacterListOpen);

        Assert.True(StoryboardFrameFieldParser.TryAppendContinuation(frame, "  - 玻吕茜亚：那第三个线索呢？", cont));
        Assert.True(StoryboardFrameFieldParser.TryAppendContinuation(frame, "  - 管理员：是否会睡着，需要你们自行验证。", cont));
        Assert.True(StoryboardFrameFieldParser.TryAppendContinuation(frame, "  - 遐蝶：那就从第一本开始。", cont));

        Assert.Equal("玻吕茜亚：那第三个线索呢？ 管理员：是否会睡着，需要你们自行验证。 遐蝶：那就从第一本开始。", frame.Dialogue);
        Assert.Null(frame.Description);
    }

    [Fact]
    public void Dialogue_EmptyHeader_IsClosedByNextField()
    {
        var frame = new StoryboardFrame();
        string? unitType = null;
        var cont = NewCont();

        StoryboardFrameFieldParser.TryApplyField(frame, "- **镜头时间轴**: 0-2s: 玻吕茜亚开口询问", cont, ref unitType);
        Assert.True(cont.TimelineOpen);

        // 时间轴之后出现空台词标题 → 台词续行打开，时间轴续行关闭，两者不串列
        Assert.True(StoryboardFrameFieldParser.TryApplyField(frame, "- **对话/台词**:", cont, ref unitType));
        Assert.True(cont.DialogueOpen);
        Assert.False(cont.TimelineOpen);

        Assert.True(StoryboardFrameFieldParser.TryAppendContinuation(frame, "  - 遐蝶：这本读起来真费劲。", cont));

        Assert.True(StoryboardFrameFieldParser.TryApplyField(frame, "- **镜头时长**: 11秒", cont, ref unitType));
        Assert.False(cont.DialogueOpen);

        Assert.Equal("11秒", frame.Duration);
        Assert.Equal("遐蝶：这本读起来真费劲。", frame.Dialogue);
        Assert.Equal("0-2s: 玻吕茜亚开口询问", frame.Timeline);
        Assert.Null(frame.Description);
    }

    [Fact]
    public void Dialogue_InlineValue_DoesNotOpenContinuation()
    {
        var frame = new StoryboardFrame();
        string? unitType = null;
        var cont = NewCont();

        Assert.True(StoryboardFrameFieldParser.TryApplyField(frame, "- **对话/台词**: 遐蝶：“书名呢？”", cont, ref unitType));
        Assert.Equal("遐蝶：“书名呢？”", frame.Dialogue);
        Assert.False(cont.DialogueOpen);
    }

    [Fact]
    public void Dialogue_PlainSubItemWithoutBullet_IsAcceptedOnlyWhenItLooksLikeDialogue()
    {
        var frame = new StoryboardFrame();
        string? unitType = null;
        var cont = NewCont();

        StoryboardFrameFieldParser.TryApplyField(frame, "- **对话/台词**:", cont, ref unitType);

        Assert.True(StoryboardFrameFieldParser.TryAppendContinuation(frame, "遐蝶：那就从第一本开始。", cont));
        Assert.Equal("遐蝶：那就从第一本开始。", frame.Dialogue);

        // 不像台词的正文行不吞进台词，仍按镜头正文累积
        Assert.True(StoryboardFrameFieldParser.TryAppendContinuation(frame, "镜头缓缓推近，露出书架深处。", cont));
        Assert.Equal("遐蝶：那就从第一本开始。", frame.Dialogue);
        Assert.Contains("镜头缓缓推近", frame.Description);
    }

    [Theory]
    [InlineData("- **镜头时长**: 11秒", null)]
    [InlineData("- **对话/台词**:", null)]
    [InlineData("- 遐蝶：书名呢？", "遐蝶：书名呢？")]
    [InlineData("遐蝶：书名呢？", "遐蝶：书名呢？")]
    [InlineData("镜头缓缓推近，露出书架深处。", null)]
    [InlineData("- ", null)]
    public void TryGetDialogueContinuation_OnlyAcceptsDialogueSubItems(string line, string? expected)
    {
        Assert.Equal(expected, StoryboardFrameFieldParser.TryGetDialogueContinuation(line));
    }

    [Theory]
    [InlineData("遐蝶：“书名呢？”", "遐蝶", "书名呢？")]
    [InlineData("玻吕茜亚：“我以前会折借阅卡？”", "玻吕茜亚", "我以前会折借阅卡？")]
    [InlineData("遐蝶：那第三个线索呢？", "遐蝶", "那第三个线索呢？")]
    [InlineData("旁白：能放，也能收。", "旁白", "能放，也能收。")]
    [InlineData("遐蝶，画外音：“原来站在一起。”", "遐蝶，画外音", "原来站在一起。")]
    public void ExtractDialogueSegments_SingleSpeaker(string value, string speaker, string text)
    {
        var segments = StoryboardFrameFieldParser.ExtractDialogueSegments(value);

        var segment = Assert.Single(segments);
        Assert.Equal(speaker, segment.Speaker);
        Assert.Equal(text, segment.Text);
    }

    [Fact]
    public void ExtractDialogueSegments_MultipleSpeakers_SplitsBySpeakerColon()
    {
        var segments = StoryboardFrameFieldParser.ExtractDialogueSegments(
            "遐蝶：\"不是梦。你只是忘了它的名字。\" 玻吕茜亚：\"这一次不折了。\"");

        Assert.Equal(2, segments.Count);
        Assert.Equal("遐蝶", segments[0].Speaker);
        Assert.Equal("不是梦。你只是忘了它的名字。", segments[0].Text);
        Assert.Equal("玻吕茜亚", segments[1].Speaker);
        Assert.Equal("这一次不折了。", segments[1].Text);
    }

    [Fact]
    public void ExtractDialogueSegments_QuotedColonInsideSpeech_IsNotASpeakerBoundary()
    {
        var segments = StoryboardFrameFieldParser.ExtractDialogueSegments("岳沉天：“我说：你错了。”");

        var segment = Assert.Single(segments);
        Assert.Equal("岳沉天", segment.Speaker);
        Assert.Equal("我说：你错了。", segment.Text);
    }

    [Fact]
    public void ExtractDialogueSegments_NoDialogue_ReturnsEmpty()
    {
        Assert.Empty(StoryboardFrameFieldParser.ExtractDialogueSegments("无"));
        Assert.Empty(StoryboardFrameFieldParser.ExtractDialogueSegments(null));
        Assert.Empty(StoryboardFrameFieldParser.ExtractDialogueSegments("   "));
    }
}
