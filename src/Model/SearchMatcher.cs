using System.IO.Enumeration;

namespace ImageTools.Model;

/// <summary>
/// Parses the search box: <c>.iso</c> matches an extension, <c>*.mp4</c> / <c>log?.txt</c> are
/// wildcards over the whole name, anything else is a case-insensitive substring.
/// </summary>
public sealed class SearchMatcher
{
    private enum Kind { Substring, Extension, Wildcard }

    private readonly Kind _kind;
    private readonly string _pattern;

    private SearchMatcher(Kind kind, string pattern)
    {
        _kind = kind;
        _pattern = pattern;
    }

    public string Text => _pattern;

    public static SearchMatcher? Parse(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        if (text.IndexOfAny(['*', '?']) >= 0)
            return new SearchMatcher(Kind.Wildcard, text);
        if (text.Length > 1 && text[0] == '.' && text.IndexOf(' ') < 0)
            return new SearchMatcher(Kind.Extension, text);
        return new SearchMatcher(Kind.Substring, text);
    }

    public bool IsMatch(string name) => _kind switch
    {
        Kind.Extension => name.EndsWith(_pattern, StringComparison.OrdinalIgnoreCase),
        Kind.Wildcard => FileSystemName.MatchesSimpleExpression(_pattern, name, ignoreCase: true),
        _ => name.Contains(_pattern, StringComparison.OrdinalIgnoreCase),
    };

    /// <summary>Only real files and folders are matched by name; drives and synthetic blocks never are.</summary>
    public bool Matches(FsNode node) => node.Kind is NodeKind.Directory or NodeKind.File && IsMatch(node.Name);

    /// <summary>True when the node or one of its ancestors matches, i.e. its whole box counts as a hit.</summary>
    public static bool IsInsideMatch(FsNode node, SearchMatcher? matcher)
    {
        if (matcher == null)
            return true;
        for (var n = node; n != null; n = n.Parent)
            if (matcher.Matches(n))
                return true;
        return false;
    }
}
