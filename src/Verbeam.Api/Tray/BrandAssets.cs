namespace Verbeam.Api.Tray;

internal static class BrandAssets
{
    public static Icon LoadAppIcon()
    {
        foreach (var path in CandidateIconPaths())
        {
            try
            {
                if (File.Exists(path))
                {
                    return new Icon(path);
                }
            }
            catch
            {
                // Fall through to the built-in icon if the asset is missing or malformed.
            }
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    private static IEnumerable<string> CandidateIconPaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "wwwroot", "images", "brand", "verbeam.ico");
        yield return Path.Combine(Environment.CurrentDirectory, "wwwroot", "images", "brand", "verbeam.ico");
        yield return Path.Combine(Environment.CurrentDirectory, "app", "src", "Verbeam.Api", "wwwroot", "images", "brand", "verbeam.ico");
    }
}
