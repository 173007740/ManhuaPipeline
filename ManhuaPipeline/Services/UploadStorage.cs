namespace ManhuaPipeline.Services;

public static class UploadStorage
{
    public static void DeleteUploadFile(string? url, string? requiredPrefix = null)
    {
        if (TryGetSafeUploadPath(url, requiredPrefix, out var fullPath) && fullPath is not null)
            File.Delete(fullPath);
    }

    public static bool IsSafeUploadUrl(string? url, string? requiredPrefix = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return true;

        return TryGetUploadRoot(url, requiredPrefix, out _);
    }

    private static bool TryGetSafeUploadPath(string? url, string? requiredPrefix, out string? fullPath)
    {
        fullPath = null;
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith('/')) return false;
        if (!TryGetUploadRoot(url, requiredPrefix, out fullPath)) return false;
        return File.Exists(fullPath);
    }

    private static bool TryGetUploadRoot(string? url, string? requiredPrefix, out string? fullPath)
    {
        fullPath = null;
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith('/')) return false;

        var webRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"));
        var uploadRoot = string.IsNullOrWhiteSpace(requiredPrefix)
            ? Path.Combine(webRoot, "uploads")
            : Path.GetFullPath(Path.Combine(webRoot, requiredPrefix.TrimStart('/')));

        var resolved = Path.GetFullPath(Path.Combine(webRoot, url.TrimStart('/')));
        if (!IsWithin(resolved, uploadRoot)) return false;

        fullPath = resolved;
        return true;
    }

    private static bool IsWithin(string path, string root)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var pathFull = Path.GetFullPath(path);
        return pathFull.Equals(rootFull, StringComparison.OrdinalIgnoreCase)
            || pathFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
