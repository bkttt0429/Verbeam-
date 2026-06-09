namespace YomiBridge.Core.Services;

public static class PathResolver
{
    public static string Resolve(string contentRoot, string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var current = new DirectoryInfo(contentRoot);
        while (current is not null)
        {
            var candidate = Path.GetFullPath(Path.Combine(current.FullName, configuredPath));
            if (Directory.Exists(candidate) || File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return Path.GetFullPath(Path.Combine(contentRoot, configuredPath));
    }
}
