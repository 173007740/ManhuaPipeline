using ManhuaPipeline.Services;
using System.Linq;
using Xunit;

namespace ManhuaPipeline.Tests;

public class StageUnitParserTests
{
    [Fact]
    public void CombatUnitAtFiveSeconds_StaysFiveSeconds()
    {
        var text =
            """
            【第1集】
            【单元1.1】
            类型：打斗/动作
            时长：5秒
            地点：葬天台
            核心动作/情绪：两人近身缠斗，拳脚对拼
            起始状态：双方对峙
            结束状态：A 击中 B
            对话/台词：无
            关键元素：人物/环境
            """;

        var unit = StageUnitParser.Parse(text).Single();

        Assert.Equal(5, unit.Duration);
        Assert.Contains("时长：5秒", unit.RawText);
    }

    [Fact]
    public void QuickKillCombatUnit_StaysFiveSeconds()
    {
        var text =
            """
            【第1集】
            【单元1.1】
            类型：打斗/动作
            时长：5秒
            地点：葬天台
            核心动作/情绪：A 一招秒杀 B，干脆利落
            起始状态：A 逼近
            结束状态：B 倒地
            对话/台词：无
            关键元素：人物/环境
            """;

        var unit = StageUnitParser.Parse(text).Single();

        Assert.Equal(5, unit.Duration);
    }

    [Fact]
    public void SuffixedUnitMarkers_AreRenumberedSequentially()
    {
        var text =
            """
            【第1集】
            【单元1.1-1】
            类型：打斗/动作
            时长：11秒
            地点：葬天台
            核心动作/情绪：七宗宗主发动大阵
            起始状态：双方对峙
            结束状态：法光压向岳沉天
            对话/台词：无
            关键元素：人物/环境
            【单元1.1-2】
            类型：文戏/情感
            时长：5秒
            地点：葬天台
            核心动作/情绪：太虚圣主开口压阵
            起始状态：岳沉天被法光笼罩
            结束状态：岳沉天握拳
            对话/台词：太虚圣主：你一人再强，也强不过天下法统。
            关键元素：人物/环境
            【单元1.2-1】
            类型：打斗/动作
            时长：11秒
            地点：荒郊破庙
            核心动作/情绪：赤金气血冲云
            起始状态：岳沉天睁眼
            结束状态：威压尽收
            对话/台词：无
            关键元素：人物/环境
            """;

        var units = StageUnitParser.Parse(text);

        Assert.Equal(3, units.Count);
        Assert.Equal(new[] { "1.1", "1.2", "1.3" }, units.Select(u => u.UnitNumber));
    }
}
