namespace ManhuaPipeline.Models;

public class User
{
    public int UserId { get; set; }
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string? Nickname { get; set; }
    public string? Avatar { get; set; }
    public string Role { get; set; } = "user";
    public bool IsActive { get; set; } = true;
    public string ActiveLLMProvider { get; set; } = "deepseek";
    public string ActiveVideoEngine { get; set; } = "volcano";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? LastLoginAt { get; set; }
}

public class LoginRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public class RegisterRequest
{
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
}
