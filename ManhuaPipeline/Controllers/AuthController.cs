using Microsoft.AspNetCore.Mvc;
using ManhuaPipeline.Models;
using ManhuaPipeline.Services;
using Microsoft.Extensions.Logging;

namespace ManhuaPipeline.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly DbService _db;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<AuthController> _logger;

    public AuthController(DbService db, IWebHostEnvironment env, ILogger<AuthController> logger) { _db = db; _env = env; _logger = logger; }

    [HttpPost("register")]
    [RequestSizeLimit(UploadValidation.MaxProfileImageBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = UploadValidation.MaxProfileImageBytes)]
    public async Task<IActionResult> Register([FromForm] string username, [FromForm] string email,
        [FromForm] string password, [FromForm] string? nickname, IFormFile? avatar)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(email))
            return BadRequest(new { message = "请填写完整信息" });

        if (_db.GetUserByUsername(username) != null)
            return BadRequest(new { message = "用户名已存在" });

        if (_db.GetUserByEmail(email) != null)
            return BadRequest(new { message = "邮箱已被注册" });

        var hash = BCrypt.Net.BCrypt.HashPassword(password);

        // 处理头像上传
        string? avatarUrl = null;
        if (avatar != null && avatar.Length > 0)
        {
            if (!UploadValidation.TryValidateImage(avatar, UploadValidation.MaxProfileImageBytes, out var extension, out var error))
                return BadRequest(new { message = error });
            avatarUrl = await SaveAvatar(avatar, extension);
        }

        var userId = _db.CreateUser(username, email, hash, nickname);
        _db.InitializeDefaultSkillElements(userId);

        HttpContext.Session.SetInt32("UserId", userId);
        HttpContext.Session.SetString("Username", username);

        return Ok(new { userId, username, nickname, avatar = avatarUrl, message = "注册成功" });
    }

    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { message = "请填写账号和密码" });

        var user = _db.GetUserByUsername(req.Username);
        if (user == null || !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
            return Unauthorized(new { message = "用户名或密码错误" });

        if (!user.IsActive)
            return Unauthorized(new { message = "账号已被禁用" });

        HttpContext.Session.SetInt32("UserId", user.UserId);
        HttpContext.Session.SetString("Username", user.Username);
        _db.UpdateLastLogin(user.UserId);

        return Ok(new { userId = user.UserId, username = user.Username, nickname = user.Nickname, avatar = user.Avatar, message = "登录成功" });
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        HttpContext.Session.Clear();
        return Ok(new { message = "已退出" });
    }

    [HttpGet("me")]
    public IActionResult GetCurrentUser()
    {
        var userId = HttpContext.Session.GetInt32("UserId");
        if (userId == null) return Ok(new { userId = 0 });
        var user = _db.GetUserById(userId.Value);
        if (user == null) return Ok(new { userId = 0 });
        return Ok(new { user.UserId, user.Username, user.Email, user.Nickname, user.Avatar, user.Role });
    }

    [HttpPut("profile")]
    [RequestSizeLimit(UploadValidation.MaxProfileImageBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = UploadValidation.MaxProfileImageBytes)]
    public async Task<IActionResult> UpdateProfile([FromForm] string? nickname, IFormFile? avatar)
    {
        var userId = HttpContext.Session.GetInt32("UserId");
        if (userId == null) return Unauthorized();

        var user = _db.GetUserById(userId.Value);
        if (user == null) return Unauthorized();

        string? avatarUrl = user.Avatar;
        if (avatar != null && avatar.Length > 0)
        {
            if (!UploadValidation.TryValidateImage(avatar, UploadValidation.MaxProfileImageBytes, out var extension, out var error))
                return BadRequest(new { message = error });
            avatarUrl = await SaveAvatar(avatar, extension);
        }

        _db.UpdateUserProfile(userId.Value, nickname, avatarUrl);
        if (avatar != null && avatar.Length > 0 && !string.Equals(user.Avatar, avatarUrl, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                UploadStorage.DeleteUploadFile(user.Avatar, "/uploads/avatars/");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete old avatar for user {UserId}", userId.Value);
            }
        }
        return Ok(new { nickname, avatar = avatarUrl, message = "已更新" });
    }

    private async Task<string> SaveAvatar(IFormFile file, string extension)
    {
        var dir = Path.Combine(_env.WebRootPath, "uploads", "avatars");
        Directory.CreateDirectory(dir);
        var fileName = $"{Guid.NewGuid()}{extension}";
        var filePath = Path.Combine(dir, fileName);
        using var stream = new FileStream(filePath, FileMode.Create);
        await file.CopyToAsync(stream);
        return $"/uploads/avatars/{fileName}";
    }
}
