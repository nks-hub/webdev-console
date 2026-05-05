// No namespace - Program.cs uses top-level statements compiled into global scope.

internal static class PostgreSqlHelper
{
    public static string? ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
            return "newPassword is required";
        if (password.Length < 8)
            return "newPassword must be at least 8 characters";
        if (password.Contains('\0'))
            return "newPassword must not contain null bytes";
        if (password.Contains('"') || password.Contains('\'') || password.Contains('\\'))
            return "newPassword must not contain quote or backslash characters";
        if (password.Length > 128)
            return "newPassword must not exceed 128 characters";
        return null;
    }

    public static string GetPayloadValue(
        IReadOnlyDictionary<string, string>? body,
        params string[] names)
    {
        if (body is null) return "";

        foreach (var name in names)
        {
            if (body.TryGetValue(name, out var value))
                return value ?? "";
        }

        foreach (var pair in body)
        {
            if (names.Any(name => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)))
                return pair.Value ?? "";
        }

        return "";
    }

    public static string? ResolveSiblingTool(string postgresPath, string toolName)
    {
        var dir = Path.GetDirectoryName(postgresPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        var candidate = Path.Combine(dir, toolName + ext);
        return File.Exists(candidate) ? candidate : null;
    }

    public static string BuildAlterUserSql(string password)
    {
        var escaped = password.Replace("'", "''");
        return $"ALTER USER postgres WITH PASSWORD '{escaped}';";
    }
}
