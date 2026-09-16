using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

public class PromptDialogueVerbatimGuardTests
{
    [Fact]
    public void WrongDialogueName_IsReplacedWithStage5Text()
    {
        var unitContext = """
        - **镜头编号**: 1.2-1
        - **对话/台词**: 太虚圣主：“岳沉天，你一人再强，也强不过天下法统。”
        """;
        var prompt = """
        【第1集】【单元1.2】【镜头1.2-1】
        类型:高潮/对决
        @图1 [前世岳沉天]人物；@图2 [葬天台]场景；@图3 [光影质感]光影质感
        [0-2s]大远景俯拍缓推：太虚圣主立于云端，神色冷漠高傲，说"前世岳沉天，你一人再强，也强不过天下法统。"，语速低沉轻慢。
        """;

        var result = PromptDialogueVerbatimGuard.Apply(prompt, unitContext);

        Assert.Contains("说\"岳沉天，你一人再强，也强不过天下法统。\"，语速低沉轻慢", result);
        Assert.DoesNotContain("前世岳沉天，你一人再强", result);
    }

    [Fact]
    public void MultipleShots_UseTheirOwnStage5Dialogue()
    {
        var unitContext = """
        - **镜头编号**: 1.2-1
        - **对话/台词**: 太虚圣主：“岳沉天，你一人再强，也强不过天下法统。”
        - **镜头编号**: 1.2-2
        - **对话/台词**: 岳沉天：“七个人，借三城百姓的命，才敢站在我面前。”
        """;
        var prompt = """
        【第1集】【单元1.2】【镜头1.2-1】
        [0-2s]太虚圣主说"前世岳沉天，你一人再强，也强不过天下法统。"

        【第1集】【单元1.2】【镜头1.2-2】
        [0-2s]岳沉天说"七个人，借三城百姓的命，才敢站在我面前。"
        """;

        var result = PromptDialogueVerbatimGuard.Apply(prompt, unitContext);

        Assert.Contains("【镜头1.2-1】", result);
        Assert.Contains("说\"岳沉天，你一人再强，也强不过天下法统。\"", result);
        Assert.Contains("说\"七个人，借三城百姓的命，才敢站在我面前。\"", result);
        Assert.DoesNotContain("前世岳沉天，你一人再强", result);
    }

    [Fact]
    public void NoStage5Dialogue_LeavesPromptUnchanged()
    {
        var unitContext = """
        - **镜头编号**: 1.1-1
        - **对话/台词**: 无
        """;
        var prompt = """
        【第1集】【单元1.1】【镜头1.1-1】
        [0-2s]七宗宗主合围逼近，无台词。
        """;

        var result = PromptDialogueVerbatimGuard.Apply(prompt, unitContext);

        Assert.Equal(prompt, result);
    }

    [Fact]
    public void SurroundingNarration_IsPreservedAfterReplacement()
    {
        var unitContext = """
        - **镜头编号**: 1.2-1
        - **对话/台词**: 太虚圣主：“岳沉天，你一人再强，也强不过天下法统。”
        """;
        var prompt = """
        【第1集】【单元1.2】【镜头1.2-1】
        [0-2s]镜头缓推下压，太虚圣主立于云端，白金圣袍猎猎，居高临下俯视阵心，说"前世岳沉天，你一人再强，也强不过天下法统。"，语速低沉轻慢。
        """;

        var result = PromptDialogueVerbatimGuard.Apply(prompt, unitContext);

        Assert.Contains("镜头缓推下压，太虚圣主立于云端", result);
        Assert.Contains("说\"岳沉天，你一人再强，也强不过天下法统。\"，语速低沉轻慢", result);
    }

    [Fact]
    public void DuplicateDialogueInMultipleBlocks_IsDeduplicated()
    {
        var unitContext = """
        - **镜头编号**: 1.9-2
        - **对话/台词**: 岳沉天：“十八年前，他们杀了岳沉天。”
        """;
        var prompt = """
        【第1集】【单元1.9】【镜头1.9-2】
        类型:文戏/情感
        @图1 [少年岳沉天]人物；@图2 [光影质感]光影质感
        写实3D动画电影，4K，24fps，浅景深，无字幕无BGM，荒郊破庙+青灰夜色。
        [0-3s]纵摇上移，少年岳沉天自腰间取出一圈陈旧泛黄的布质拳带。
        [3-7s]近景缓推，低沉念出“十八年前，他们杀了岳沉天。”，指尖将拳带一端绕上左手腕。
        [7-11s]固定机位近景，呼吸平稳，眼神愈加深邃坚定，缠至第二圈时低声收束“十八年前，他们杀了岳沉天。”，旧拳带已缠上左手腕。
        灯光：冷月顶光。
        约束：语速从容自然。
        """;

        var result = PromptDialogueVerbatimGuard.Apply(prompt, unitContext);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result, "十八年前，他们杀了岳沉天。"));
        Assert.Contains("低沉念出“十八年前，他们杀了岳沉天。”", result);
        Assert.DoesNotContain("低声收束“十八年前", result);
        Assert.Contains("旧拳带已缠上左手腕", result);
    }

    [Fact]
    public void UnquotedNarration_IsUsedAsVerbatimQuoteSource()
    {
        var unitContext = """
        - **镜头编号**: 1.8-1
        - **对话/台词**: 旁白：能放，也能收。
        """;
        var prompt = """
        【第1集】【单元1.8】【镜头1.8-1】
        [0-3s]岳沉天低头缠绕拳带，内心独白-岳沉天：“能放，也能收哦。”
        """;

        var result = PromptDialogueVerbatimGuard.Apply(prompt, unitContext);

        Assert.Contains("内心独白-岳沉天：“能放，也能收。”", result);
        Assert.DoesNotContain("能放，也能收哦", result);
    }

    [Fact]
    public void EmptyDialogueHeaderWithIndentedBullets_IsUsedAsVerbatimSource()
    {
        var unitContext = """
        - **镜头编号**: 1.6-1
        - **对话/台词**:
          - 遐蝶："这本读起来真费劲。"
          - 玻吕茜亚："但它从来不睡觉。"
        - **镜头时长**: 11秒
        """;
        var prompt = """
        【第1集】【单元1.6】【镜头1.6-1】
        [0-3s]遐蝶说"这本读起来真费劲哦。"
        [4-8s]玻吕茜亚说"但它从不睡觉。"
        """;

        var result = PromptDialogueVerbatimGuard.Apply(prompt, unitContext);

        Assert.Contains("说\"这本读起来真费劲。\"", result);
        Assert.Contains("说\"但它从来不睡觉。\"", result);
        Assert.DoesNotContain("真费劲哦", result);
        Assert.DoesNotContain("但它从不睡觉", result);
    }

    [Fact]
    public void UnquotedSubItems_AreUsedAsVerbatimSource()
    {
        var unitContext = """
        - **镜头编号**: 1.6-1
        - **对话/台词**:
          - 玻吕茜亚：那就从第一本开始吧。
        """;
        var prompt = """
        【第1集】【单元1.6】【镜头1.6-1】
        [0-3s]玻吕茜亚说"那就从第一本书开始。"
        """;

        var result = PromptDialogueVerbatimGuard.Apply(prompt, unitContext);

        Assert.Contains("说\"那就从第一本开始吧。\"", result);
        Assert.DoesNotContain("第一本书", result);
    }

    [Fact]
    public void EmptyDialogueHeaderWithoutSubItems_LeavesPromptUnchanged()
    {
        var unitContext = """
        - **镜头编号**: 1.7-1
        - **对话/台词**:
        - **镜头时长**: 11秒
        """;
        var prompt = """
        【第1集】【单元1.7】【镜头1.7-1】
        [0-3s]遐蝶说"自己编的台词。"
        """;

        var result = PromptDialogueVerbatimGuard.Apply(prompt, unitContext);

        Assert.Equal(prompt, result);
    }
}
