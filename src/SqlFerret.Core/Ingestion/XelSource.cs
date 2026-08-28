namespace SqlFerret.Core.Ingestion;

public static class XelSource
{
    public static (IReadOnlyList<string> files, long bytesTotal) Resolve(string path)
    {
        if (Directory.Exists(path))
        {
            var files = Directory.GetFiles(path, "*.xel", SearchOption.TopDirectoryOnly)
                                 .OrderBy(f => f, StringComparer.Ordinal).ToList();
            return (files, files.Sum(f => new FileInfo(f).Length));
        }
        if (File.Exists(path))
            return (new[] { path }, new FileInfo(path).Length);

        throw new FileNotFoundException("XEL path not found", path);
    }
}
