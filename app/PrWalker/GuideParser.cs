using System.IO;
using System.Text.RegularExpressions;

namespace XScribeDesktop.PrWalker;

public static class GuideParser
{
    public static List<WalkerStep> ParseFromHtml(string htmlPath)
    {
        var html = File.ReadAllText(htmlPath);
        var match = Regex.Match(html, @"const\s+STEPS\s*=\s*\[", RegexOptions.Singleline);
        if (!match.Success)
            throw new InvalidOperationException("No const STEPS = [...] found in guide HTML");

        var result = new List<WalkerStep>();

        var stepMatches = Regex.Matches(html,
            @"title:\s*""((?:[^""\\]|\\.)*)""[\s\S]*?blocks:\s*\[([\s\S]*?)\]\s*\}",
            RegexOptions.Singleline);

        foreach (Match stepMatch in stepMatches)
        {
            var title = Unescape(stepMatch.Groups[1].Value);
            var blocksSection = stepMatch.Groups[2].Value;

            var blockMatches = Regex.Matches(blocksSection,
                @"file:\s*""((?:[^""\\]|\\.)*)""\s*,\s*fname:\s*""((?:[^""\\]|\\.)*)""[\s\S]*?methods:\s*\[([\s\S]*?)\]",
                RegexOptions.Singleline);

            foreach (Match blockMatch in blockMatches)
            {
                var file = Unescape(blockMatch.Groups[1].Value);
                var fname = Unescape(blockMatch.Groups[2].Value);
                var methodsSection = blockMatch.Groups[3].Value;
                var path = CombinePath(file, fname);

                var isLocal = path.StartsWith("local", StringComparison.OrdinalIgnoreCase)
                    || fname.Contains("checklist", StringComparison.OrdinalIgnoreCase);

                var methodMatches = Regex.Matches(methodsSection,
                    @"name:\s*""((?:[^""\\]|\\.)*)""\s*,\s*lines:\s*""((?:[^""\\]|\\.)*)""");

                foreach (Match methodMatch in methodMatches)
                {
                    var name = Unescape(methodMatch.Groups[1].Value);
                    var lines = Unescape(methodMatch.Groups[2].Value);
                    var ranges = LineRangeParser.Parse(lines);
                    var navigable = !isLocal && ranges.Count > 0 && path.Contains('.');

                    result.Add(new WalkerStep
                    {
                        Source = WalkerStepSource.Guide,
                        Title = title,
                        Label = name,
                        Path = navigable ? path : "",
                        LineRanges = ranges,
                        Status = navigable ? WalkerStepStatus.Ready : WalkerStepStatus.NoNavigate,
                    });
                }
            }
        }

        if (result.Count == 0)
            throw new InvalidOperationException("No steps extracted — guide format may have changed");

        return result;
    }

    static string Unescape(string s) =>
        s.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\t", "\t");

    static string CombinePath(string file, string fname)
    {
        file = file.Replace('\\', '/').Trim();
        fname = fname.Trim();
        if (string.IsNullOrEmpty(fname)) return file;
        if (file.EndsWith(fname, StringComparison.OrdinalIgnoreCase)) return file.TrimEnd('/');
        return $"{file.TrimEnd('/')}/{fname}";
    }
}
