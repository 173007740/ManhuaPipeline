using Microsoft.AspNetCore.Mvc;
using ManhuaPipeline.Models;
using ManhuaPipeline.Services;

namespace ManhuaPipeline.Controllers;

[ApiController]
[Route("api/skill")]
public class SkillController : ControllerBase
{
    private readonly DbService _db;
    public SkillController(DbService db) { _db = db; }

    private int GetUserId() => HttpContext.Session.GetInt32("UserId") ?? 0;

    private string GetUploadDir()
    {
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "skill");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return dir;
    }

    [HttpGet("list")]
    public IActionResult GetList([FromQuery] string? element, [FromQuery] int? tier, [FromQuery] string? search, [FromQuery] int? projectId, [FromQuery] string? tags, [FromQuery] string? owner)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        return Ok(_db.GetSkills(uid, element, tier, search, projectId, tags, owner));
    }

    [HttpGet("elements")]
    public IActionResult GetElements()
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        return Ok(_db.GetSkillElements(uid));
    }

    [HttpPost("elements")]
    public IActionResult CreateElement([FromBody] SkillElementRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var name = req.Name?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name)) return BadRequest(new { message = "系别名不能为空" });
        if (name.Length > 20) return BadRequest(new { message = "系别名最多 20 个字符" });
        if (_db.GetSkillElements(uid).Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
            return Conflict(new { message = "系别已存在" });
        var id = _db.SaveSkillElement(uid, name, Math.Max(0, req.SortOrder));
        return Ok(new { success = true, elementId = id });
    }

    [HttpPut("elements/{elementId}")]
    public IActionResult UpdateElement(int elementId, [FromBody] SkillElementRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var element = _db.GetSkillElement(elementId);
        if (element == null || element.UserId != uid) return NotFound(new { message = "系别不存在" });
        var name = req.Name?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name)) return BadRequest(new { message = "系别名不能为空" });
        if (name.Length > 20) return BadRequest(new { message = "系别名最多 20 个字符" });
        if (_db.GetSkillElements(uid).Any(e => e.ElementId != elementId && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
            return Conflict(new { message = "系别已存在" });
        if (!_db.UpdateSkillElement(elementId, uid, name, Math.Max(0, req.SortOrder), element.Name))
            return NotFound(new { message = "系别不存在" });
        return Ok(new { success = true });
    }

    [HttpDelete("elements/{elementId}")]
    public IActionResult DeleteElement(int elementId)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var element = _db.GetSkillElement(elementId);
        if (element == null || element.UserId != uid) return NotFound(new { message = "系别不存在" });
        var usage = _db.GetSkillElementUsageCount(elementId, uid);
        if (usage > 0) return BadRequest(new { message = $"该系别已被 {usage} 个技能使用，请先调整这些技能" });
        if (!_db.DeleteSkillElement(elementId, uid)) return NotFound(new { message = "系别不存在" });
        return Ok(new { success = true });
    }

    [HttpPost]
    public IActionResult Create([FromBody] SkillRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { message = "技能名不能为空" });
        if (req.ProjectId.HasValue && !_db.ProjectBelongsToUser(req.ProjectId.Value, uid))
            return BadRequest(new { message = "关联项目不存在" });
        var projectId = req.ProjectId > 0 ? req.ProjectId : null;
        var id = _db.SaveSkill(uid, req.Name.Trim(), req.Element ?? "", req.Tier, req.PromptImage ?? "", req.PromptVideo ?? "", req.Tags, projectId, req.OwnerCharacter);
        return Ok(new { success = true, skillId = id });
    }

    [HttpPut("{skillId}")]
    public IActionResult Update(int skillId, [FromBody] SkillRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { message = "技能名不能为空" });
        if (req.ProjectId.HasValue && !_db.ProjectBelongsToUser(req.ProjectId.Value, uid))
            return BadRequest(new { message = "关联项目不存在" });
        var skill = _db.GetSkill(skillId);
        if (skill == null || skill.UserId != uid) return NotFound(new { message = "技能不存在" });
        var projectId = req.ProjectId > 0 ? req.ProjectId : null;
        _db.UpdateSkill(skillId, uid, req.Name.Trim(), req.Element ?? "", req.Tier, req.PromptImage ?? "", req.PromptVideo ?? "", req.Tags, projectId);
        _db.UpdateSkill(skillId, uid, req.Name.Trim(), req.Element ?? "", req.Tier, req.PromptImage ?? "", req.PromptVideo ?? "", req.Tags, projectId, req.OwnerCharacter);
        return Ok(new { success = true });
    }

    [HttpPost("{skillId}/image")]
    [RequestSizeLimit(UploadValidation.MaxProfileImageBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = UploadValidation.MaxProfileImageBytes)]
    public async Task<IActionResult> UploadImage(int skillId, IFormFile file)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var skill = _db.GetSkill(skillId);
        if (skill == null || skill.UserId != uid) return NotFound(new { message = "技能不存在" });
        if (!UploadValidation.TryValidateImage(file, UploadValidation.MaxProfileImageBytes, out var extension, out var error))
            return BadRequest(new { message = error });

        var dir = GetUploadDir();
        var fileName = $"{Guid.NewGuid()}{extension}";
        var filePath = Path.Combine(dir, fileName);
        using (var stream = new FileStream(filePath, FileMode.Create))
            await file.CopyToAsync(stream);

        var localPath = $"/uploads/skill/{fileName}";
        _db.UpdateSkillImage(skillId, uid, localPath);
        return Ok(new { imageUrl = localPath, message = "上传成功" });
    }

    [HttpDelete("{skillId}")]
    public IActionResult Delete(int skillId)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var skill = _db.GetSkill(skillId);
        if (skill == null || skill.UserId != uid) return NotFound(new { message = "技能不存在" });
        UploadStorage.DeleteUploadFile(skill.ImageUrl, "/uploads/skill/");
        _db.DeleteSkill(skillId, uid);
        return Ok(new { message = "删除成功" });
    }
}

public class SkillRequest
{
    public string Name { get; set; } = "";
    public string? Element { get; set; }
    public int Tier { get; set; } = 4;
    public string? PromptImage { get; set; }
    public string? PromptVideo { get; set; }
    public string? Tags { get; set; }
    public int? ProjectId { get; set; }
    public string? OwnerCharacter { get; set; }
}

public class SkillElementRequest
{
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
}
