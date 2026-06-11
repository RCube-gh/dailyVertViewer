using System.IO;

namespace DailyVertViewer.Wpf.Infrastructure;

public static class PathHelper
{
    public static string? FindUpward(string fileName)
    {
        var candidates = new[]
        {
            new DirectoryInfo(AppContext.BaseDirectory),
            new DirectoryInfo(Environment.CurrentDirectory),
        };

        foreach (var start in candidates)
        {
            var current = start;
            while (current is not null)
            {
                var path = Path.Combine(current.FullName, fileName);
                if (File.Exists(path))
                {
                    return path;
                }

                current = current.Parent;
            }
        }

        return null;
    }
}
