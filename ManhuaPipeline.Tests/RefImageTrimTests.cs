using System.Reflection;
using System.Text.RegularExpressions;
using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

public class RefImageTrimTests
{
    private static readonly MethodInfo TrimMethod = typeof(AgentService)
        .GetMethod("EnsureMaxNineRefImages", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("EnsureMaxNineRefImages not found");

    private const string Shot1 =
        """
        【第1集】【单元1.1】【镜头1.1-1】
        参考图:第一张(@图1)为[前世岳沉罡]形象参考，保持外貌一致；第二张(@图2)为[前世岳沉罡战斗态]形象参考，保持外貌一致；第三张(@图3)为[太虚圣主]形象参考，保持外貌一致；第四张(@图4)为[七宗宗主]群像参考，保持七位宗主一致；第五张(@图5)为[镇岳拳带]道具参考，保持外观一致；第六张(@图6)为[七曜诛圣阵]特效参考，保持形态一致；第七张(@图7)为[圣光诛邪]特效参考，保持形态一致；第八张(@图8)为[天门镇魔]特效参考，保持形态一致；第九张(@图9)为[寒魄封天]特效参考，保持形态一致；第十张(@图10)为[破军]特效参考，保持形态一致；第十一张(@图11)为[灭世]特效参考，保持形态一致；倒数第二张(@图12)为[葬天台]场景参考，保持空间布局一致；最后一张(@图13)为画面风格参考（光影、色调、材质质感）
        [组合:葬天台-夜-5s]
        @角色引用:[前世岳沉罡][前世岳沉罡战斗态][太虚圣主][七宗宗主]
        @道具引用:[镇岳拳带]
        场景锚定引用:葬天台
        """;

    private static string Shot2 => Shot1.Replace("【镜头1.1-1】", "【镜头1.1-2】");

    [Fact]
    public void ThirteenRefs_TrimToNine_KeepCharactersSceneStyleAndProps()
    {
        var output = Trim(Shot1);

        Assert.Equal(9, CountRefs(output));
        Assert.Contains("[七宗宗主]群像参考", output);
        Assert.Contains("[镇岳拳带]道具参考", output);
        Assert.Contains("[葬天台]场景参考", output);
        Assert.Contains("画面风格参考", output);
        Assert.DoesNotContain("[破军]特效参考", output);
    }

    [Fact]
    public void EveryShotInMultiShotPrompt_IsTrimmedToNine()
    {
        var output = Trim(Shot1 + "\n\n" + Shot2);

        var refLines = RefLines(output);
        Assert.Equal(2, refLines.Length);
        Assert.All(refLines, line => Assert.Equal(9, CountRefs(line)));
    }

    [Fact]
    public void ChineseEpisodeHeaders_AreNormalizedBeforeSplitting()
    {
        var input = (Shot1 + "\n\n" + Shot2).Replace("【第1集】", "【第一集】");
        var output = Trim(input);

        var refLines = RefLines(output);
        Assert.Equal(2, refLines.Length);
        Assert.All(refLines, line => Assert.Equal(9, CountRefs(line)));
    }

    [Fact]
    public void SceneStaysSecondToLast_AndStyleLast()
    {
        var output = Trim(Shot1);

        Assert.Matches(@"倒数第二张\(@图8\)为\[葬天台\]场景参考", output);
        Assert.Matches(@"最后一张\(@图9\)为画面风格参考", output);
    }

    private static string Trim(string prompt) =>
        (string)TrimMethod.Invoke(null, new[] { prompt })!;

    private static int CountRefs(string text) =>
        Regex.Matches(text, @"@图(?:\d+|X)").Count;

    private static string[] RefLines(string text) =>
        text.Split('\n').Where(l => l.TrimStart().StartsWith("参考图")).ToArray();

    [Fact]
    public void AtImageLine_OverNine_TrimmedAndRenumbered()
    {
        var prompt = """
        【第1集】【单元1.1】【镜头1.1-1】
        @图1 前世岳沉天人物；@图2 前世岳沉天战斗态；@图3 七宗宗主群像；@图4 镇岳拳带道具；@图5 七曜诛圣阵特效；@图6 圣光诛邪特效；@图7 天门镇魔特效；@图8 寒魄封天特效；@图9 破军特效；@图10 灭世特效；@图11 葬天台场景；@图12 光影质感。
        """;

        var output = Trim(prompt);

        Assert.Equal(9, CountRefs(output));
        Assert.DoesNotContain("[灭世]特效参考", output);
        Assert.DoesNotContain("@图10", output);
        Assert.Contains("@图9 [光影质感]光影质感参考", output);
    }
}
