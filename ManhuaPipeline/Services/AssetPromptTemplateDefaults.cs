using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services;

/// <summary>
/// 资产出图提示词模版的出厂默认值（四类：角色 / 道具 / 环境 / 特效）。
/// 内容取自「资产提示词模版」的通用写法：统一视觉风格 + 类别规则 + 统一负面提示词。
/// 用户没在「API 配置 → 资产提示词模版」里改过时，提取与出图都用这里的默认值。
/// </summary>
public static class AssetPromptTemplateDefaults
{
    public static AssetPromptTemplate Create(string category)
    {
        var c = (category ?? "").Trim().ToLowerInvariant();
        return new AssetPromptTemplate
        {
            Category = c,
            Enabled = true,
            StyleLock = StyleLock(c),
            NegativePrompt = NegativePrompt(c),
            RuleText = RuleText(c),
        };
    }

    /// <summary>
    /// 统一视觉风格。这里故意不写死任何具体 IP / 剧集名：
    /// 每部剧风格不同，请在「项目 → 本剧提示词模版」里按剧覆盖，
    /// 或从项目画风（VideoStyles）一键带入，改完对已有资产立即生效。
    /// </summary>
    public static string StyleLock(string category) => category switch
    {
        AssetPromptTemplate.CategoryCharacters =>
            "电影级日系动画电影质感，厚涂插画与精致渲染结合，精细五官，通透肤色，细腻发丝与衣料纹理，复杂金属饰件，高质量瞳孔高光，柔和轮廓光，高饱和低对比统一色板，轻微胶片颗粒与辉光，4K角色设定图。",

        AssetPromptTemplate.CategoryProps =>
            "电影级日系动画质感，厚涂插画与精致渲染结合，结构清晰，材质细腻，高饱和低对比统一色板，轻微柔和辉光，纯色浅暖灰背景，单一道具居中完整展示，无人物，无手部，无环境，无其他物品，无文字，无水印，4K。",

        AssetPromptTemplate.CategoryEnvironments =>
            "电影级日系动画电影质感，厚涂插画与精致渲染结合，精细背景材质，大气透视，丰富空间层次，强烈侧逆光、轮廓光与体积光，明暗关系富有张力但色调统一，高饱和低对比色板，轻微胶片颗粒，柔和辉光，克制光晕，4K超清环境概念图。",

        AssetPromptTemplate.CategoryEffects =>
            "电影级日系动画特效，半透明能量质感，晶体般通透，细腻星尘尾迹，柔和辉光，结构精致，边缘清晰，4K特效素材。",

        _ => ""
    };

    public static string NegativePrompt(string category) => category switch
    {
        AssetPromptTemplate.CategoryCharacters =>
            "真人、cosplay、低幼Q版、扁平赛璐璐、廉价3D、塑料皮肤、角色换脸、发型改变、瞳色错误、饰品缺失、肢体畸形、额外手指、文字、水印、logo。",

        AssetPromptTemplate.CategoryProps =>
            "人物，手部，厨房，房间，桌面，灶台，橱柜，餐桌，环境背景，其他食材，其他餐具，组合摆拍，使用动作，动态过程，镜头描述，文字，数字，品牌，logo，水印，真人摄影，低幼Q版，廉价3D，低模，塑料感，结构畸形，裁切主体。",

        AssetPromptTemplate.CategoryEnvironments =>
            "人物，角色，人体，手部，人影，围裙，人物动作，对话字幕，漫画格，真人摄影，廉价摄影棚，低幼Q版，扁平卡通，廉价3D，塑料材质，脏乱，垃圾，明火，浓黑烟，过曝，死黑，乱码文字，品牌logo，水印。",

        AssetPromptTemplate.CategoryEffects =>
            "人物，手部，房间，厨房，食物，家具，花朵，草地，写实昆虫，昆虫绒毛，蛾子，鸟类，机械蝴蝶，实体宠物，恐怖生物，破损翅膀，翅膀数量错误，身体畸形，过量光污染，浓雾，火焰，文字，logo，水印。",

        _ => ""
    };

    public static string RuleText(string category) => category switch
    {
        AssetPromptTemplate.CategoryCharacters =>
            "角色卡规范：浅暖灰（或透明）纯色角色设定背景，单人出镜；角色卡要能直接作为后续所有镜头的形象母版。"
            + "提示词只写这个角色本身：外貌、脸型、瞳色、发色、发型与发饰、服装层次与材质、金属与宝石饰件、气质与静态表情；"
            + "随身配件（杯子、雨伞、厨具、手提包等）可以作为角色卡的一部分出现，但要说明是配件。"
            + "禁止写成剧情画面：不要打斗、奔跑、翻锅等动作过程，不要对白、运镜、景别、环境故事；"
            + "不要出现第二个人物；不要出现文字、水印、logo。"
            + "同一角色的多个视图（正面全身、左右3/4、背面、表情、局部特写）必须保持脸型、发型、瞳色、饰件与服装结构完全一致。",

        AssetPromptTemplate.CategoryProps =>
            "道具提示词只描述这一件道具本体：外形、结构、比例、材质、颜色、表面质感与细节。"
            + "纯色浅暖灰背景、单一道具居中完整展示；"
            + "禁止出现人物、手部、环境、其他食材或其他道具、组合摆拍、使用动作、动态过程、运镜、景别和叙事情节；"
            + "容器保持为空，内容物与容器分别作为独立资产；"
            + "同一道具的多状态（例如未烘烤 / 焦化 / 半片）各写一条独立提示词，尺寸、轮廓、颜色与材质必须保持一致；"
            + "不要出现文字、数字、品牌、logo。",

        AssetPromptTemplate.CategoryEnvironments =>
            "环境提示词只描述固定空间、静态陈设、光线、色彩与空气氛围：空间结构与材质、家具与陈设位置、光源方向、主色调、空气效果。"
            + "一律是无人环境，禁止出现人物、人体部位、人影、人物服装；"
            + "同一场景的多套环境必须保持空间结构、门窗、家具与光源方向一致；"
            + "不写人物动作、不写运镜与景别、不写剧情；需要准确文字的界面/投影请另建独立 UI 资产，环境底图只保留投影光与位置。",

        AssetPromptTemplate.CategoryEffects =>
            "特效提示词只描述这一个特效本体：形态、构成元素、颜色、光效、发光结构与运动轨迹。"
            + "使用纯黑或透明背景，方便后期抠像与叠加；"
            + "每条提示词只呈现一种动作状态（例如盘旋 / 四散 / 停落 / 掠过），同一特效的多种状态必须保持造型、尺寸与发光结构一致；"
            + "禁止出现人物、手部、厨房、食物、家具、场景、文字；"
            + "不要做成写实昆虫、实体宠物或恐怖生物，不写运镜与景别。",

        _ => ""
    };
}
