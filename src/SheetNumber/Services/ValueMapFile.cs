using System.IO;

namespace SheetNumber.Services;

public static class ValueMapFile
{
    public static Dictionary<string, string> Read(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in File.ReadAllLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            char separator = line.Contains(';') ? ';' : ',';
            string[] parts = line.Split(separator);
            if (parts.Length < 2)
                continue;

            string current = parts[0].Trim().Trim('"');
            string mapped = parts[1].Trim().Trim('"');
            if (current.Length == 0)
                continue;

            map[current] = mapped;
        }

        return map;
    }

    public static string MappedValue(
        IReadOnlyDictionary<string, string>? map,
        string? parameterName,
        string? mappedParameter,
        string value)
    {
        if (map == null
            || string.IsNullOrEmpty(parameterName)
            || string.IsNullOrEmpty(mappedParameter)
            || !string.Equals(parameterName, mappedParameter, StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return map.TryGetValue(value, out string? mapped) ? mapped : value;
    }
}
