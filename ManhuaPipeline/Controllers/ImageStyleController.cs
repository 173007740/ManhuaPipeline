using Microsoft.AspNetCore.Mvc;
using ManhuaPipeline.Services;

namespace ManhuaPipeline.Controllers;

/// <summary>
/// 图片风格库。数据落在 ImageStyles 表，无限画布节点的「图片风格」下拉直接读这里 ——
/// 跟所属项目无关，不挂项目的独立灵感板也能挑风格。
///
/// 风格图（StyleImageUrl）只作预览缩略图：挑的时候看着图挑，出图只用风格描述那段文本。
/// </summary>
[ApiController]
[Route("api/image-style")]
public class ImageStyleController : ControllerBase
{
    private readonly DbService _db;

    public ImageStyleController(DbService db) { _db = db; }

    private int GetUserId() => HttpContext.Session.GetInt32("UserId") ?? 0;

    [HttpGet]
    public IActionResult GetList()
    {
        if (GetUserId() == 0) return Unauthorized();
        return Ok(_db.GetImageStyles());
    }

    [HttpPost]
    public IActionResult Save([FromBody] SaveImageStyleRequest req)
    {
        if (GetUserId() == 0) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.StyleName))
            return BadRequest(new { message = "风格名称不能为空" });
        if (string.IsNullOrWhiteSpace(req.StyleDesc))
            return BadRequest(new { message = "风格描述不能为空 —— 出图时拼进提示词的就是这段" });

        var id = _db.SaveImageStyle(req.StyleId, req.StyleName.Trim(), req.StyleDesc.Trim(),
                                    req.StyleImageUrl,
                                    string.IsNullOrWhiteSpace(req.StyleNegative) ? null : req.StyleNegative!.Trim(),
                                    string.IsNullOrWhiteSpace(req.Category) ? null : req.Category!.Trim());
        return Ok(new { styleId = id, message = "保存成功" });
    }

    /// <summary>单独上传风格图（弹窗里选了新图就先传这个，拿到 URL 再随表单一起保存）。</summary>
    [HttpPost("upload")]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile file)
    {
        if (GetUserId() == 0) return Unauthorized();
        if (file == null || file.Length == 0) return BadRequest(new { message = "没有选到图片" });

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".bmp"))
            return BadRequest(new { message = "风格图仅支持 jpg / png / webp / gif / bmp" });

        var dir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "styles");
        Directory.CreateDirectory(dir);
        var fileName = "style_" + Guid.NewGuid().ToString("N")[..12] + ext;
        await using (var fs = new FileStream(Path.Combine(dir, fileName), FileMode.Create))
            await file.CopyToAsync(fs);

        return Ok(new { url = "/uploads/styles/" + fileName });
    }

    [HttpDelete("{styleId}")]
    public IActionResult Delete(int styleId)
    {
        if (GetUserId() == 0) return Unauthorized();
        _db.DeleteImageStyle(styleId);
        return Ok(new { message = "删除成功" });
    }
}

public class SaveImageStyleRequest
{
    public int? StyleId { get; set; }
    public string StyleName { get; set; } = "";
    public string StyleDesc { get; set; } = "";
    /// <summary>分类（可空）：只用来给挑风格时分组，不进提示词。</summary>
    public string? Category { get; set; }
    /// <summary>这条风格自带的反向提示词（可空：老风格没有，出图时负面词照旧）。</summary>
    public string? StyleNegative { get; set; }
    public string? StyleImageUrl { get; set; }
}
