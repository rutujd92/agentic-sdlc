using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Orchestrator.Core.Governance;

/// <summary>Path globs: <c>**</c> spans directories, <c>*</c> and <c>?</c> stay within one segment.</summary>
public static class Glob
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    public static bool IsMatch(string pattern, string path) => Cache.GetOrAdd(pattern, Compile).IsMatch(path);

    private static Regex Compile(string pattern)
    {
        var regex = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                var slash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                regex.Append(slash ? "(.*/)?" : ".*");
                i += slash ? 2 : 1;
            }
            else
            {
                regex.Append(c switch
                {
                    '*' => "[^/]*",
                    '?' => "[^/]",
                    _ => Regex.Escape(c.ToString()),
                });
            }
        }

        return new Regex(regex.Append('$').ToString(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}
