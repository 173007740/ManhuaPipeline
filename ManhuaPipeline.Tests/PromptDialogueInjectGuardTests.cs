using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

public class PromptDialogueInjectGuardTests
{
    [Fact]
    public void MissingDialogue_IsInjectedIntoTimelineSpeechBlock()
    {
        var unitContext = """
        【第1集】【单元1.2】
        - **镜头编号**: 1.2-1
        - **镜头时间轴**: 0-2s: 太虚圣主开口压阵，俯拍全景显示合围；2-4s: 岳沉天低头；4-5s: 岳沉天握拳怒视。
        - **对话/台词**: 太虚圣主：“岳沉天，你一人再强，也强不过天下法统。”
        """;
        var prompt = """
        【第1集】【单元1.2】【镜头1.2-1】
        类型:文戏/情感
        @图1 [前世岳沉天]人物；@图2 [光影质感]光影质感
        写实3D动画电影，4K，24fps，浅景深，无字幕无BGM，葬天台+白天。
        [0-3s]高空俯拍大远景固定机位缓推下压：葬天台悬于云海，太虚圣主立于云端居高临下俯视阵心。
        [3-5s]硬切至阵心低机位固定近景：前世岳沉天低头望向脚下。
        灯光：惨白天光。
        约束：语速从容自然。
        """;

        var result = PromptDialogueInjectGuard.Apply(prompt, unitContext);

        Assert.Contains("太虚圣主说\"岳沉天，你一人再强，也强不过天下法统。\"", result);
        Assert.Contains("[0-3s]高空俯拍大远景固定机位缓推下压：葬天台悬于云海，太虚圣主立于云端居高临下俯视阵心。；太虚圣主说", result);
        var lines = result.Split("\n");
        Assert.Contains("太虚圣主说", lines[4]);
        Assert.DoesNotContain("太虚圣主说", lines[5]);
    }

    [Fact]
    public void MissingDialogue_WithoutTimelineSpeechClue_UsesSpeakerActionBlock()
    {
        var unitContext = """
        【第1集】【单元1.2】
        - **镜头编号**: 1.2-3
        - **镜头时间轴**: 0-2s: 太虚圣主其余法身虚影被逼退半步；2-4s: 岳沉天爆发赤金气血；4-6s: 冲击波扩散。
        - **对话/台词**: 岳沉天：“七个人，借三城百姓的命，才敢站在我面前。”
        """;
        var prompt = """
        【第1集】【单元1.2】【镜头1.2-3】
        类型:高潮/对决
        @图1 [前世岳沉天]人物；@图2 [光影质感]光影质感
        写实3D动画电影，4K，24fps，浅景深，无字幕无BGM，葬天台+白天。
        [0-2s]正面广角固定全景：太虚圣主其余法身虚影被逼退半步，合围松动。
        [2-4s]硬切至拳锋金纹特写：前世岳沉天猛然轰出重拳，赤金气血随拳劲喷发。
        [4-5s]慢动作0.3秒后升空拉远：环形气浪继续扩散。
        灯光：赤金暖光。
        约束：前世岳沉天台词在出拳瞬间吼出。
        """;

        var result = PromptDialogueInjectGuard.Apply(prompt, unitContext);

        Assert.Contains("前世岳沉天猛然轰出重拳，赤金气血随拳劲喷发。；岳沉天说\"七个人，借三城百姓的命，才敢站在我面前。\"", result);
    }

    [Fact]
    public void ExistingDialogue_IsNotDuplicated()
    {
        var unitContext = """
        【第1集】【单元1.3】
        - **镜头编号**: 1.3-2
        - **对话/台词**: 岳沉天：“也配叫天下？”
        """;
        var prompt = """
        【第1集】【单元1.3】【镜头1.3-2】
        类型:高潮/对决
        @图1 [前世岳沉天]人物；@图2 [光影质感]光影质感
        写实3D动画电影，4K，24fps，浅景深，无字幕无BGM，葬天台+白天。
        [0-5s]低角度极速推镜：前世岳沉天突进，拳锋赤金光芒拖曳如彗尾；太虚圣主说"也配叫天下？"
        灯光：暖金光辉。
        约束：语速从容自然。
        """;

        var result = PromptDialogueInjectGuard.Apply(prompt, unitContext);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result, "也配叫天下"));
    }

    [Fact]
    public void OldTemplateNoDialogueField_IsReplaced()
    {
        var unitContext = """
        【第1集】【单元1.2】
        - **镜头编号**: 1.2-1
        - **对话/台词**: 太虚圣主：“岳沉天，你一人再强，也强不过天下法统。”
        """;
        var prompt = """
        【第1集】【单元1.2】【镜头1.2-1】
        0-5秒[1]
        时长:5秒
        动作:太虚圣主居高临下开口压阵
        对话:无
        姿态:太虚圣主神态倨傲
        """;

        var result = PromptDialogueInjectGuard.Apply(prompt, unitContext);

        Assert.Contains("对话:太虚圣主说\"岳沉天，你一人再强，也强不过天下法统。\"", result);
        Assert.DoesNotContain("对话:无", result);
    }

    [Fact]
    public void VoiceOnlySpeaker_IsPreservedForLaterNormalization()
    {
        var unitContext = """
        【第1集】【单元1.11】
        - **镜头编号**: 1.11-2
        - **对话/台词**: 顾残山留音：“若武印已经解开，你不需要我教任何东西。”
        """;
        var prompt = """
        【第1集】【单元1.11】【镜头1.11-2】
        类型:文戏/情感
        @图1 [少年岳沉天]人物；@图2 [光影质感]光影质感
        写实3D动画电影，4K，24fps，浅景深，无字幕无BGM，枯井遗府+昏黄灯火初燃。
        [0-3s]幽暗中地宫深处旧灯灯芯骤然跳起火苗。
        [3-6s]镜头自遗骨缓缓横摇至木案，再落定在留音石上。
        [6-9s]留音石表面涟漪扩散，顾残山苍老声音在寂静地宫中响起。
        [9-11s]少年岳沉天停步凝望。
        灯光：旧灯暖光。
        约束：语速从容自然。
        """;

        var result = PromptDialogueInjectGuard.Apply(prompt, unitContext);

        Assert.Contains("顾残山留音说\"若武印已经解开，你不需要我教任何东西。\"", result);
    }

    [Fact]
    public void UnquotedNarration_IsInjectedAsInnerMonologuePlaceholder()
    {
        var unitContext = """
        【第1集】【单元1.8】
        - **镜头编号**: 1.8-1
        - **对话/台词**: 旁白：能放，也能收。
        """;
        var prompt = """
        【第1集】【单元1.8】【镜头1.8-1】
        类型:文戏/情感
        @图1 [岳沉天]人物；@图2 [光影质感]光影质感
        [0-3s]固定机位近景：岳沉天低头缠绕拳带。
        [3-5s]特写：他缓缓抬头。
        """;

        var result = PromptDialogueInjectGuard.Apply(prompt, unitContext);

        Assert.Contains("内心独白-主角：“能放，也能收。”", result);
    }

    [Fact]
    public void MissingDialogue_FromIndentedBullets_IsInjectedIntoTimeBlocks()
    {
        var unitContext = """
        【第1集】【单元1.6】
        - **镜头编号**: 1.6-1
        - **镜头时间轴**: 0-2s: 玻吕茜亚开口询问；2-4s: 管理员平静回应；4-11s: 遐蝶开口决定。
        - **对话/台词**:
          - 玻吕茜亚："那第三个线索呢？"
          - 管理员："是否会睡着，需要你们自行验证。"
        - **镜头时长**: 11秒
        """;
        var prompt = """
        【第1集】【单元1.6】【镜头1.6-1】
        类型:日常/喜剧
        @图1 [遐蝶]人物；@图2 [图书馆门厅]场景
        [0-3s]固定中景：玻吕茜亚立于咨询台左侧。
        [3-6s]中景：管理员保持职业化站姿。
        [6-11s]镜头跟移：遐蝶点头转身。
        灯光：暖色局部光。
        约束：语速自然。
        """;

        var result = PromptDialogueInjectGuard.Apply(prompt, unitContext);

        Assert.Contains("玻吕茜亚说\"那第三个线索呢？\"", result);
        Assert.Contains("管理员说\"是否会睡着，需要你们自行验证。\"", result);
    }

    [Fact]
    public void MissingDialogue_FromUnquotedSubItems_IsInjected()
    {
        var unitContext = """
        【第1集】【单元1.6】
        - **镜头编号**: 1.6-2
        - **镜头时间轴**: 0-2s: 遐蝶开口决定。
        - **对话/台词**:
          - 遐蝶：那就从第一本开始。
        """;
        var prompt = """
        【第1集】【单元1.6】【镜头1.6-2】
        类型:日常/喜剧
        @图1 [遐蝶]人物；@图2 [图书馆门厅]场景
        [0-2s]镜头跟移：遐蝶点头转身。
        灯光：暖色局部光。
        约束：语速自然。
        """;

        var result = PromptDialogueInjectGuard.Apply(prompt, unitContext);

        Assert.Contains("遐蝶说\"那就从第一本开始。\"", result);
    }

    [Fact]
    public void EmptyDialogueHeaderWithoutSubItems_InjectsNothing()
    {
        var unitContext = """
        【第1集】【单元1.7】
        - **镜头编号**: 1.7-1
        - **镜头时间轴**: 0-2s: 遐蝶开口说话。
        - **对话/台词**:
        - **镜头时长**: 5秒
        """;
        var prompt = """
        【第1集】【单元1.7】【镜头1.7-1】
        类型:日常/喜剧
        @图1 [遐蝶]人物；@图2 [图书馆门厅]场景
        [0-2s]固定中景：遐蝶开口说话。
        灯光：暖色局部光。
        约束：语速自然。
        """;

        var result = PromptDialogueInjectGuard.Apply(prompt, unitContext);

        Assert.Equal(prompt, result);
    }
}
