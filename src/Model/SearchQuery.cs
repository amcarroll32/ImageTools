namespace ImageTools.Model;

/// <param name="MatchAncestors">Folders that contain a search hit (so the map can keep their titles readable).</param>
public sealed record SearchResult(long MatchFiles, long MatchFolders, long MatchBytes, IReadOnlySet<FsNode> MatchAncestors);

/// <summary>
/// Counts search hits in the current view. Runs on a background thread over finished
/// (immutable) subtrees. Hidden app assets don't count.
/// </summary>
public static class SearchQuery
{
    public static SearchResult Run(IReadOnlyList<FsNode> roots, bool rootsLit, SearchMatcher matcher, bool showAssets, CancellationToken ct)
    {
        var stack = new Stack<(FsNode Node, bool Lit)>();
        foreach (var r in roots)
            stack.Push((r, rootsLit));

        long matchFiles = 0, matchFolders = 0, matchBytes = 0;
        var ancestors = new HashSet<FsNode>();
        int visited = 0;

        while (stack.Count > 0)
        {
            if ((++visited & 0xFFF) == 0)
                ct.ThrowIfCancellationRequested();

            var (node, lit) = stack.Pop();
            if (node.VisibleCount(showAssets) == 0 && node.Kind != NodeKind.Root)
                continue;
            bool self = matcher.Matches(node);

            // Count only outermost hits so a matching folder isn't added twice with its contents.
            if (self && !lit)
            {
                matchBytes += node.VisibleSize(showAssets);
                if (node.Kind == NodeKind.File) matchFiles++;
                else matchFolders++;
                for (var a = node.Parent; a != null && ancestors.Add(a); a = a.Parent) { }
            }

            // Inside a hit there's nothing more to count.
            if (node.Children != null && !node.IsScanning && !lit && !self)
                foreach (var c in node.Children)
                    stack.Push((c, false));
        }
        return new SearchResult(matchFiles, matchFolders, matchBytes, ancestors);
    }
}
