using System.IO;
using DailyVertViewer.Wpf.Infrastructure;

namespace DailyVertViewer.Wpf.Services;

public sealed class AppEnvironment
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public AppEnvironment()
    {
        var envPath = PathHelper.FindUpward(".env");
        if (string.IsNullOrWhiteSpace(envPath))
        {
            return;
        }

        foreach (var rawLine in File.ReadAllLines(envPath))
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

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"');
            _values[key] = value;
        }
    }

    public string? Get(string key)
    {
        return Environment.GetEnvironmentVariable(key)
            ?? (_values.TryGetValue(key, out var value) ? value : null);
    }
}
