namespace XScribeDesktop.PrWalker;

public static class IntentDetector
{
    private static readonly string[] Patterns =
    [
        @"\bshow me\b",
        @"\bgo back\b",
        @"\btake me to\b",
        @"\bscroll to\b",
        @"\bopen that\b",
        @"\bwhich file\b",
        @"\bwhere (?:are you|do you|did you|is|was|were)\b",
        @"\bwhere.*\b(set|assign|invok|call|pass|defin|declar|export|import|use)\b",
        @"\bcan you (?:show|find|locate|point)\b",
        @"\blet me see\b",
        @"\bback to (?:the|that|where)\b",
    ];

    public static bool IsLocationIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 8) return false;
        foreach (var p in Patterns)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(text, p,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return true;
        }
        return false;
    }
}
