using System.Text.RegularExpressions;

namespace XScribeDesktop.PrWalker;

public static class LineRangeParser
{
    private static readonly Regex RangeRe = new(@"(\d+)\s*(?:→|->|—|-)\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex SingleRe = new(@"\b(\d+)\b", RegexOptions.Compiled);

    public static List<LineRange> Parse(string lines)
    {
        if (string.IsNullOrWhiteSpace(lines)) return new();

        var lower = lines.ToLowerInvariant();
        if (lower.Contains("typecheck") || lower.Contains("vitest") || lower.Contains("biome")
            || lower.Contains("build") || lower.Contains("clean") || lower.Contains("checklist"))
            return new();

        var result = new List<LineRange>();

        foreach (Match m in RangeRe.Matches(lines))
        {
            var a = int.Parse(m.Groups[1].Value);
            var b = int.Parse(m.Groups[2].Value);
            result.Add(new LineRange { Start = Math.Min(a, b), End = Math.Max(a, b) });
        }

        if (result.Count == 0)
        {
            foreach (var part in lines.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var sm = SingleRe.Match(part);
                if (sm.Success)
                {
                    var n = int.Parse(sm.Groups[1].Value);
                    result.Add(new LineRange { Start = n, End = n });
                }
            }
        }

        return result;
    }
}
