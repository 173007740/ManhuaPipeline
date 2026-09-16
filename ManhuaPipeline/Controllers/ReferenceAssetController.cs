using Microsoft.AspNetCore.Mvc;
using ManhuaPipeline.Services;

namespace ManhuaPipeline.Controllers;

[ApiController]
[Route("api/reference")]
public class ReferenceAssetController : ControllerBase
{
    private readonly DbService _db;
    public ReferenceAssetController(DbService db) { _db = db; }

    private int GetUserId() => HttpContext.Session.GetInt32("UserId") ?? 0;
    private string GetUploadDir()
    {
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "reference");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return dir;
    }

    [HttpGet("list")]
    public IActionResult GetList([FromQuery] string? category, [FromQuery] string? subCategory, [FromQuery] string? search, [FromQuery] string? tag, [FromQuery] string? tags)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        List<string>? tagList = null;
        if (!string.IsNullOrWhiteSpace(tags))
        {
            tagList = new List<string>();
            foreach (var part in tags.Split(','))
            {
                var t = part.Trim();
                if (t.Length > 0) tagList.Add(t);
            }
        }
        return Ok(_db.GetReferenceAssets(uid, category, subCategory, search, tag, tagList));
    }

    [HttpPost("upload")]
    [RequestSizeLimit(UploadValidation.MaxLibraryImageBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = UploadValidation.MaxLibraryImageBytes)]
    public async Task<IActionResult> Upload(IFormFile file, [FromForm] string category, [FromForm] string subCategory, [FromForm] string? tags = null)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!UploadValidation.TryValidateImage(file, UploadValidation.MaxLibraryImageBytes, out var extension, out var error))
            return BadRequest(new { message = error });

        var dir = GetUploadDir();
        var fileName = $"{Guid.NewGuid()}{extension}";
        var filePath = Path.Combine(dir, fileName);
        using (var stream = new FileStream(filePath, FileMode.Create))
            await file.CopyToAsync(stream);

        var localPath = $"/uploads/reference/{fileName}";
        var id = _db.SaveReferenceAsset(uid, file.FileName, localPath, category, subCategory, tags, file.Length);
        return Ok(new { assetId = id, localPath = localPath, message = "上传成功" });
    }

    [HttpPost("upload-batch")]
    [RequestSizeLimit(200L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 200L * 1024 * 1024)]
    public async Task<IActionResult> UploadBatch(List<IFormFile> files, [FromForm] string category, [FromForm] string subCategory, [FromForm] string? tags = null)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (files == null || files.Count == 0)
            return BadRequest(new { message = "请选择文件" });

        var results = new List<object>();
        var errors = new List<string>();
        var dir = GetUploadDir();

        foreach (var file in files)
        {
            try
            {
                if (!UploadValidation.TryValidateImage(file, UploadValidation.MaxLibraryImageBytes, out var extension, out var validationError))
                { errors.Add(file.FileName + ": " + validationError); continue; }

                var fileName = $"{Guid.NewGuid()}{extension}";
                var filePath = Path.Combine(dir, fileName);
                using (var stream = new FileStream(filePath, FileMode.Create))
                    await file.CopyToAsync(stream);

                var localPath = $"/uploads/reference/{fileName}";
                var id = _db.SaveReferenceAsset(uid, file.FileName, localPath, category, subCategory, tags, file.Length);
                results.Add(new { assetId = id, fileName = file.FileName, localPath });
            }
            catch (Exception ex)
            { errors.Add(file.FileName + ": " + ex.Message); }
        }

        return Ok(new { success = results.Count, failed = errors.Count, results, errors });
    }

    [HttpDelete("{assetId}")]
    public IActionResult Delete(int assetId)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();

        var asset = _db.GetReferenceAsset(assetId);
        if (asset == null) return NotFound();
        if (asset.UserId != uid) return Unauthorized();

        // Delete local file
        UploadStorage.DeleteUploadFile(asset.LocalPath, "/uploads/reference/");

        _db.DeleteReferenceAsset(assetId, uid);
        return Ok(new { message = "删除成功" });
    }
    
    [HttpPut("{assetId}/rename")]
    public IActionResult Rename(int assetId, [FromBody] RenameRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.FileName))
            return BadRequest(new { message = "文件名不能为空" });
        _db.RenameReferenceAsset(assetId, uid, req.FileName.Trim(), req.Category, req.SubCategory, req.Tags);
        return Ok(new { message = "修改成功" });
    }

    // 编辑资产：可同时更新名称/分类/标签，并可选携带 file 替换原图片
    [HttpPut("{assetId}/edit")]
    [RequestSizeLimit(UploadValidation.MaxLibraryImageBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = UploadValidation.MaxLibraryImageBytes)]
    public async Task<IActionResult> Edit(int assetId,
        [FromForm] string fileName,
        [FromForm] string? category,
        [FromForm] string? subCategory,
        [FromForm] string? tags = null,
        IFormFile? file = null)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (string.IsNullOrWhiteSpace(fileName))
            return BadRequest(new { message = "文件名不能为空" });

        var asset = _db.GetReferenceAsset(assetId);
        if (asset == null) return NotFound(new { message = "资产不存在" });
        if (asset.UserId != uid) return Unauthorized();

        string? newLocalPath = null;
        long? newSize = null;
        if (file != null && file.Length > 0)
        {
            if (!UploadValidation.TryValidateImage(file, UploadValidation.MaxLibraryImageBytes, out var extension, out var error))
                return BadRequest(new { message = error });

            var dir = GetUploadDir();
            var newFileName = $"{Guid.NewGuid()}{extension}";
            var filePath = Path.Combine(dir, newFileName);
            using (var stream = new FileStream(filePath, FileMode.Create))
                await file.CopyToAsync(stream);

            newLocalPath = $"/uploads/reference/{newFileName}";
            newSize = file.Length;
        }

        _db.UpdateReferenceAsset(assetId, uid, fileName.Trim(), category, subCategory, tags, newLocalPath, newSize);

        // 旧文件在新文件已落盘、DB 已更新后再删除，删除失败不影响结果
        if (newLocalPath != null)
        {
            try { UploadStorage.DeleteUploadFile(asset.LocalPath, "/uploads/reference/"); }
            catch { /* 旧文件删除失败可忽略 */ }
        }

        return Ok(new { message = "保存成功", localPath = newLocalPath });
    }
}

public class RenameRequest
{
    public string FileName { get; set; } = "";
    public string? Category { get; set; }
    public string? SubCategory { get; set; }
    public string? Tags { get; set; }
}
