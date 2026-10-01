using Microsoft.Extensions.Logging;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services;

/// <summary>
/// 资产出图共用逻辑：取资产字段 / 拼最终提示词 / 同步参考图库。
/// 后台队列（AssetImageRunner）用这套；出图提示词模版按「本剧 → 账号默认 → 出厂默认」取。
/// </summary>
public static class AssetImageSupport
{
    /// <summary>资产卡上可用于出图的字段。</summary>
    public sealed record AssetImageFields(
        string Name, string? Description, string? Attributes, string? ImagePrompt, string? NegativePrompt);

    public static AssetImageFields? ResolveAsset(DbService db, int projectId, string category, int assetId)
    {
        switch (category)
        {
            case "characters":
            {
                var a = db.GetCharacterAssets(projectId).FirstOrDefault(x => x.AssetId == assetId);
                return a == null ? null : new AssetImageFields(a.Name, a.Description, a.Attributes, a.ImagePrompt, a.NegativePrompt);
            }
            case "props":
            {
                var a = db.GetPropAssets(projectId).FirstOrDefault(x => x.AssetId == assetId);
                return a == null ? null : new AssetImageFields(a.Name, a.Description, null, a.ImagePrompt, a.NegativePrompt);
            }
            case "environments":
            {
                var a = db.GetEnvAssets(projectId).FirstOrDefault(x => x.AssetId == assetId);
                return a == null ? null : new AssetImageFields(a.Name, a.Description, null, a.ImagePrompt, a.NegativePrompt);
            }
            case "effects":
            {
                var a = db.GetEffectAssets(projectId).FirstOrDefault(x => x.AssetId == assetId);
                return a == null ? null : new AssetImageFields(a.Name, a.Description, null, a.ImagePrompt, a.NegativePrompt);
            }
            default:
                return null;
        }
    }

    /// <summary>取项目画风提示词（出图时拼到提示词末尾，保证资产图与成片风格一致）。</summary>
    public static string? GetProjectStylePrompt(DbService db, ILogger logger, int projectId)
    {
        try
        {
            var project = db.GetProjectById(projectId);
            if (project?.StyleId is int styleId)
            {
                var style = db.GetVideoStyle(styleId);
                if (!string.IsNullOrWhiteSpace(style?.StylePrompt)) return style!.StylePrompt;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[AssetImage] 取项目画风失败 projectId={ProjectId}", projectId);
        }
        return null;
    }

    /// <summary>
    /// 取项目「资产画风」提示词。跟视频画风（StyleId → VideoStyles）分开维护：
    /// 资产图走 ImageStyleId → ImageStyles.StyleDesc，改它不会影响出视频那边的【项目风格】。
    /// 项目没设资产画风时退回视频画风，老项目行为不变。
    /// </summary>
    public static string? GetAssetStylePrompt(DbService db, ILogger logger, int projectId)
    {
        try
        {
            var project = db.GetProjectById(projectId);
            if (project?.ImageStyleId is int imageStyleId)
            {
                var imageStyle = db.GetImageStyle(imageStyleId);
                if (!string.IsNullOrWhiteSpace(imageStyle?.StyleDesc)) return imageStyle!.StyleDesc;
            }

            /* 项目上没设过资产画风：立项那一栏「画风方向」是给整部漫剧定的，存在 Dramas.ArtStyleId。
               逐集的项目是立项之后自动长出来的，除第 1 集（当初手工选过一次）之外没人再选过，
               于是第 2 集起资产出图一直退回视频画风——12 集跑下来画风是断的。
               这里上溯一级按漫剧那份来；项目上单独设过的值仍然优先，立项只是兜底。 */
            var dramaId = db.GetDramaIdByProject(projectId);
            if (dramaId > 0)
            {
                var brief = db.GetDramaBrief(dramaId);
                if (brief?.ArtStyleId is int artStyleId)
                {
                    var artStyle = db.GetImageStyle(artStyleId);
                    if (!string.IsNullOrWhiteSpace(artStyle?.StyleDesc)) return artStyle!.StyleDesc;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[AssetImage] 取项目资产画风失败 projectId={ProjectId}", projectId);
        }
        return GetProjectStylePrompt(db, logger, projectId);
    }

    /// <summary>
    /// 取项目「资产画风」自带的反向提示词（ImageStyles.StyleNegative），取法与正向那条完全一致。
    /// 风格预设里每段正向都配了段反向（该避开什么：真人照片 / 蜡像皮肤 / 塑料材质 / 错误肢体 / 水印…），
    /// 只把正向拼进去、反向留在库里等于丢一半。老风格没有这一列，返回 null，负面词照旧。
    /// </summary>
    public static string? GetAssetStyleNegative(DbService db, ILogger logger, int projectId)
    {
        try
        {
            var project = db.GetProjectById(projectId);
            if (project?.ImageStyleId is int imageStyleId)
            {
                var imageStyle = db.GetImageStyle(imageStyleId);
                if (!string.IsNullOrWhiteSpace(imageStyle?.StyleNegative)) return imageStyle!.StyleNegative!.Trim();
            }

            // 同上：项目上没设就上溯漫剧那份（立项的「画风方向」）
            var dramaId = db.GetDramaIdByProject(projectId);
            if (dramaId > 0)
            {
                var brief = db.GetDramaBrief(dramaId);
                if (brief?.ArtStyleId is int artStyleId)
                {
                    var artStyle = db.GetImageStyle(artStyleId);
                    if (!string.IsNullOrWhiteSpace(artStyle?.StyleNegative)) return artStyle!.StyleNegative!.Trim();
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[AssetImage] 取项目资产画风反向提示词失败 projectId={ProjectId}", projectId);
        }
        return null;
    }

    /// <summary>
    /// 拼最终出图提示词：资产卡正文（或临时覆盖）→ 模版风格锁 + 负面词 + 项目画风 + 额外要求。
    /// 资产还没有提示词正文时退回「名称+描述+属性」的旧拼法。
    /// </summary>
    public static string ComposeFinalPrompt(
        DbService db, int userId, int projectId, string category, AssetImageFields asset,
        string? projectStyle, string? promptOverride, string? negativeOverride, string? extraPrompt,
        string? styleNegative = null)
    {
        var tpl = db.GetAssetPromptTemplate(userId, projectId, category);
        var bodyPrompt = string.IsNullOrWhiteSpace(promptOverride) ? asset.ImagePrompt : promptOverride!.Trim();
        var assetNegative = string.IsNullOrWhiteSpace(negativeOverride) ? asset.NegativePrompt : negativeOverride!.Trim();
        var negative = string.IsNullOrWhiteSpace(assetNegative)
            ? (tpl.Enabled ? tpl.NegativePrompt : null)
            : assetNegative;

        /* 画风自带的反向词接在后面：它说的是「这种画风该避开什么」，
           跟资产卡上那条、模版里那条通用负面说的是两回事，拼在一起比互相覆盖有用。 */
        if (!string.IsNullOrWhiteSpace(styleNegative))
            negative = string.IsNullOrWhiteSpace(negative)
                ? styleNegative!.Trim()
                : negative.Trim().TrimEnd('。', '.', '，', ',') + "，" + styleNegative!.Trim();

        if (!string.IsNullOrWhiteSpace(bodyPrompt))
        {
            return ImageService.ComposeAssetImagePrompt(
                bodyPrompt, tpl.Enabled ? tpl.StyleLock : null, negative, projectStyle, extraPrompt);
        }

        var prompt = ImageService.BuildAssetImagePrompt(
            category, asset.Name, asset.Description, asset.Attributes, projectStyle, extraPrompt);
        if (!string.IsNullOrWhiteSpace(negative))
            prompt += "负面提示词（画面中禁止出现）：" + negative.Trim().TrimEnd('。', '.') + "。";
        return prompt;
    }

    /// <summary>
    /// 把生成图同步进用户级参考图库。
    /// 库里的 FileName 用「资产名+扩展名」，因为前端「自动绑定参考图」是按文件名与资产名匹配的；
    /// 「标签」写当前项目名称，「类型」取项目上设置的资产库类型（没设则沿用资产分类），
    /// 「子类型」仍是资产分类（角色/道具/环境/特效）。
    /// 重新出图时按 SourceKey（来源标记）精确替换上一次自动生成的同一张；
    /// 手动上传的图没有来源标记、不会被误删，标签也不再被内部标记占用。
    /// </summary>
    public static int SyncToReferenceLibrary(
        DbService db, ILogger logger, int userId, int projectId, string category, int assetId, string assetName,
        string localPath, long fileSize)
    {
        var sourceKey = $"asset:{projectId}:{category}:{assetId}";
        try
        {
            foreach (var old in db.GetReferenceAssets(userId, sourceKey: sourceKey))
            {
                try { UploadStorage.DeleteUploadFile(old.LocalPath, "/uploads/reference/"); } catch { }
                try { db.DeleteReferenceAsset(old.AssetId, userId); } catch { }
            }

            // 兼容历史数据：早期版本的自动出图记录没有 SourceKey，靠「自动出图」标签 + 资产名识别；
            // 这里按文件名顺手清掉同一张资产的旧记录，避免重新出图后图库里新旧各留一张。
            // （等旧记录清完，这段可以删掉。）
            var legacyFileName = assetName + Path.GetExtension(localPath);
            foreach (var old in db.GetReferenceAssets(userId, tag: "自动出图"))
            {
                if (!string.IsNullOrEmpty(old.SourceKey)) continue;
                if (!string.Equals(old.FileName, legacyFileName, StringComparison.OrdinalIgnoreCase)) continue;
                try { UploadStorage.DeleteUploadFile(old.LocalPath, "/uploads/reference/"); } catch { }
                try { db.DeleteReferenceAsset(old.AssetId, userId); } catch { }
            }

            var subCategory = category switch
            {
                "characters" => "角色",
                "environments" => "环境",
                "effects" => "特效",
                _ => "道具"
            };

            var project = db.GetProjectById(projectId);
            var projectName = project?.Title?.Trim();
            var libraryCategory = string.IsNullOrWhiteSpace(project?.LibraryCategory)
                ? subCategory
                : project!.LibraryCategory!.Trim();

            var fileName = assetName + Path.GetExtension(localPath);
            return db.SaveReferenceAsset(
                userId, fileName, localPath, libraryCategory, subCategory,
                string.IsNullOrWhiteSpace(projectName) ? null : projectName, fileSize, sourceKey);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[AssetImage] 同步参考图库失败 userId={UserId} {Category}#{AssetId}", userId, category, assetId);
            return 0;
        }
    }
}
