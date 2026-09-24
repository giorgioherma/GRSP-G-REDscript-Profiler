namespace GRedscriptProfiler.Manager;

internal static class PackagePaths
{
    public const string PackageRootEnvironmentVariable = "G_REDSCRIPT_PROFILER_PACKAGE_ROOT";

    private static readonly Lazy<string> ResolvedRoot = new(ResolvePackageRoot);

    public static string PackageRoot => ResolvedRoot.Value;

    private static string ResolvePackageRoot()
    {
        var explicitRoot = Environment.GetEnvironmentVariable(PackageRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitRoot) && Directory.Exists(explicitRoot))
            return Path.GetFullPath(explicitRoot);

        var appRoot = Path.GetFullPath(AppContext.BaseDirectory);
        if (LooksLikePackageRoot(appRoot))
            return appRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var parent = Directory.GetParent(
            appRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName;

        if (!string.IsNullOrWhiteSpace(parent) && LooksLikePackageRoot(parent))
            return Path.GetFullPath(parent);

        // Development/debug fallback. Published builds normally resolve either
        // the launcher-provided root or app\ -> parent package root.
        return appRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool LooksLikePackageRoot(string root) =>
        Directory.Exists(Path.Combine(root, "payload")) ||
        File.Exists(Path.Combine(root, "MANIFEST.json")) ||
        Directory.Exists(Path.Combine(root, "RESULTS"));
}
