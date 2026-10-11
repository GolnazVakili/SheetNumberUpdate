using System.IO;
using System.Text.Json;

namespace SheetNumber.Services;

public static class ValueMapFile
{
    public static Dictionary<string, string> Read(string path)
    {
        Dictionary<string, string>? parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
        if (parsed == null || parsed.Count == 0)
            throw new InvalidDataException("The mapping file has no values.");

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> pair in parsed)
        {
            string current = pair.Key.Trim();
            if (current.Length == 0)
                continue;

            map[current] = pair.Value?.Trim() ?? "";
        }

        if (map.Count == 0)
            throw new InvalidDataException("The mapping file has no values.");

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
