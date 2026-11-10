namespace SupermarketErp.Api.Infrastructure;

/// <summary>Loads a developer-local .env file without overriding environment variables.</summary>
public static class LocalEnvironmentFile
{
    public static void Load()
    {
        var filePath = FindFile();
        if (filePath is null)
        {
            return;
        }

        foreach (var rawLine in File.ReadLines(filePath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            if (!string.IsNullOrWhiteSpace(name) && Environment.GetEnvironmentVariable(name) is null)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    private static string? FindFile()
    {
        DirectoryInfo? current = new(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            var projectFile = Path.Combine(current.FullName, "backend-.net", "SupermarketErp.Api", ".env");
            if (File.Exists(projectFile))
            {
                return projectFile;
            }

            var direct = Path.Combine(current.FullName, ".env");
            if (File.Exists(direct))
            {
                return direct;
            }

            current = current.Parent;
        }

        return null;
    }
}
