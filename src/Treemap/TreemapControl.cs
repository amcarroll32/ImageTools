using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ImageTools.Model;

namespace ImageTools.Treemap;

/// <summary>A drawn rectangle and the rectangles nested inside it, used for hit testing.</summary>
internal sealed class LayoutItem(FsNode node, Rect rect)
{
    public FsNode Node { get; } = node;
    public Rect Rect { get; } = rect;
    public List<LayoutItem>? Children { get; set; }
}

/// <summary>
/// Nested squarified treemap of image space. Folders are drawn as framed boxes with a title
/// strip; images are colored by the current <see cref="ColorMode"/>. Everything is drawn into one DrawingVisual, with
/// hover/selection outlines on a second visual so they redraw without re-laying out.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    private const double HeaderHeight = 16;
    private const double DriveHeaderHeight = 34; // title line + capacity bar
    private const double DriveGap = 3;           // space around each drive in the overview
    private const int MaxLabels = 3000;

    private readonly DrawingVisual _mapVisual = new();
    private readonly DrawingVisual _overlayVisual = new();
    private readonly Dictionary<FsNode, Rect> _rects = new();
    private readonly DispatcherTimer _resizeTimer;
    private readonly Typeface _regular = new("Segoe UI");
    private readonly Typeface _semibold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private readonly Typeface _icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private LayoutItem? _layout;
    private FsNode? _root;
    private bool _showAssets;
    private bool _renderQueued;
    private FsNode? _hover;
    private FsNode? _hoverTarget;
    private FsNode? _selected;
    private double _scale = 1;
    private double _pixelsPerDip = 1;
    private int _labelBudget;
    private ColorMode _colorMode;
    private SearchMatcher? _filter;
    private IReadOnlySet<FsNode>? _filterTrail;
    private long _nowTicks;

    public TreemapControl()
    {
        AddVisualChild(_mapVisual);
        AddVisualChild(_overlayVisual);
        ClipToBounds = true;
        Focusable = true;
        RenderOptions.SetEdgeMode(_mapVisual, EdgeMode.Aliased);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        _resizeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Render, (_, _) =>
        {
            _resizeTimer!.Stop();
            RenderMap();
        }, Dispatcher);
        _resizeTimer.Stop();
    }

    /// <summary>Fires with the deepest node under the mouse (null when leaving).</summary>
    public event EventHandler<FsNode?>? HoverChanged;

    /// <summary>Fires with the deepest node under the mouse on left click.</summary>
    public event EventHandler<FsNode>? NodeClicked;

    /// <summary>Fires with the deepest node under the mouse on right click.</summary>
    public event EventHandler<FsNode>? NodeRightClicked;

    public FsNode? Root
    {
        get => _root;
        set
        {
            _root = value;
            _hover = _hoverTarget = null;
            Invalidate();
        }
    }

    /// <summary>Include app and game images (hidden by default).</summary>
    public bool ShowAssets
    {
        get => _showAssets;
        set
        {
            _showAssets = value;
            Invalidate();
        }
    }

    public ColorMode ColorMode
    {
        get => _colorMode;
        set
        {
            _colorMode = value;
            Invalidate();
        }
    }

    /// <summary>When set, everything that doesn't match (and isn't inside a match) is dimmed.</summary>
    public SearchMatcher? Filter
    {
        get => _filter;
        set
        {
            _filter = value;
            Invalidate();
        }
    }

    /// <summary>Folders containing a search hit; their titles stay readable while everything else dims.</summary>
    public IReadOnlySet<FsNode>? FilterTrail
    {
        get => _filterTrail;
        set
        {
            _filterTrail = value;
            Invalidate();
        }
    }

    public FsNode? Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            RenderOverlay();
        }
    }

    public FsNode? Hovered => _hover;

    public long DisplaySize(FsNode node) =>
        node.Kind == NodeKind.Drive && node.IsScanning ? node.PendingSize : node.VisibleSize(_showAssets);

    /// <summary>The folder a click would zoom into: the deepest folder at that point, below the current root.</summary>
    public FsNode? ClickTarget(FsNode deepest)
    {
        var folder = deepest.IsContainer ? deepest : deepest.Parent;
        return folder == _root ? null : folder;
    }

    public void Invalidate()
    {
        if (_renderQueued)
            return;
        _renderQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, RenderMap);
    }

    // ---- Visual tree plumbing ----

    protected override int VisualChildrenCount => 2;

    protected override Visual GetVisualChild(int index) => index == 0 ? _mapVisual : _overlayVisual;

    protected override HitTestResult HitTestCore(PointHitTestParameters p) => new PointHitTestResult(this, p.HitPoint);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        // The first layout renders right away; live resizing is debounced.
        if (_layout == null)
        {
            Invalidate();
        }
        else
        {
            _resizeTimer.Stop();
            _resizeTimer.Start();
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Invalidate();
    }

    // ---- Mouse ----

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var deepest = HitTest(e.GetPosition(this));
        if (deepest == _hover)
            return;
        _hover = deepest;
        _hoverTarget = deepest == null ? null : ClickTarget(deepest);
        RenderOverlay();
        HoverChanged?.Invoke(this, deepest);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = _hoverTarget = null;
        RenderOverlay();
        HoverChanged?.Invoke(this, null);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var node = HitTest(e.GetPosition(this));
        if (node != null)
            NodeClicked?.Invoke(this, node);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        var node = HitTest(e.GetPosition(this));
        if (node != null)
        {
            NodeRightClicked?.Invoke(this, node);
            e.Handled = true;
        }
    }

    public FsNode? HitTest(Point p)
    {
        var item = _layout;
        if (item == null || !item.Rect.Contains(p))
            return null;

        while (true)
        {
            LayoutItem? next = null;
            if (item.Children != null)
            {
                foreach (var child in item.Children)
                {
                    if (child.Rect.Contains(p))
                    {
                        next = child;
                        break;
                    }
                }
            }
            if (next == null)
                return item.Node;
            item = next;
        }
    }

    // ---- Rendering ----

    private double Px => 1 / _scale;

    private double Snap(double v) => Math.Round(v * _scale) / _scale;

    private Rect Snap(Rect r) => new(new Point(Snap(r.Left), Snap(r.Top)), new Point(Snap(r.Right), Snap(r.Bottom)));

    private static Rect Inset(Rect r, double d) =>
        r.Width > 2 * d && r.Height > 2 * d ? new Rect(r.X + d, r.Y + d, r.Width - 2 * d, r.Height - 2 * d) : Rect.Empty;

    private void RenderMap()
    {
        _renderQueued = false;
        var dpi = VisualTreeHelper.GetDpi(this);
        _scale = dpi.DpiScaleX;
        _pixelsPerDip = dpi.PixelsPerDip;
        _rects.Clear();
        _labelBudget = MaxLabels;
        _nowTicks = DateTime.UtcNow.Ticks;

        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        using (var dc = _mapVisual.RenderOpen())
        {
            dc.DrawRectangle(Theme.SurfaceBrush, null, bounds);

            if (_root == null || bounds.Width < 8 || bounds.Height < 8)
            {
                _layout = null;
            }
            else
            {
                _layout = new LayoutItem(_root, bounds);
                _rects[_root] = bounds;
                if (_root.IsScanning)
                {
                    DrawScanning(dc, _root, bounds, 0);
                }
                else if (!LayoutChildren(dc, _layout, ContentArea(dc, bounds), 0, SearchMatcher.IsInsideMatch(_root, _filter)))
                {
                    string message = _root.Kind == NodeKind.Root
                        ? (_root.Children!.Count == 0 ? "Looking for drives…" : "No images found.")
                        : _root.AccessDenied ? "This folder couldn't be read (access denied)."
                        : !_showAssets && _root.AssetCount > 0 ? "Only app and game images here. Tick App assets to show them."
                        : "No images here.";
                    DrawCentered(dc, message, bounds, Theme.MutedText, 14);
                }
            }
        }

        // Keep hover state consistent with the new layout.
        if (_hover != null && !_rects.ContainsKey(_hover))
        {
            _hover = _hoverTarget = null;
            HoverChanged?.Invoke(this, null);
        }
        RenderOverlay();
    }

    /// <summary>The area the current root's children fill; a drive keeps its title band on top.</summary>
    private Rect ContentArea(DrawingContext dc, Rect bounds)
    {
        if (_root!.Kind != NodeKind.Drive || bounds.Height < 120)
            return Inset(bounds, 2);
        var band = new Rect(bounds.X, bounds.Y, bounds.Width, DriveHeaderHeight + 2);
        dc.DrawRectangle(Theme.DriveBorder, null, new Rect(band.X, band.Bottom - 1, band.Width, 1));
        double bottom = DrawDriveHeader(dc, _root, band, dim: false);
        return new Rect(bounds.X + 2, bottom + 4, bounds.Width - 4, Math.Max(0, bounds.Bottom - bottom - 6));
    }

    private bool LayoutChildren(DrawingContext dc, LayoutItem parent, Rect area, int depth, bool lit)
    {
        var children = parent.Node.Children;
        if (children == null || children.Count == 0 || area.IsEmpty || area.Width < 1 || area.Height < 1)
            return false;

        var visible = new List<FsNode>(Math.Min(children.Count, 512));
        long total = 0;
        foreach (var c in children)
        {
            long size = DisplaySize(c);
            if (size <= 0)
                continue;
            visible.Add(c);
            total += size;
        }
        if (total == 0)
            return false;

        // Hiding app assets changes sizes, so the order can change.
        visible.Sort((a, b) => DisplaySize(b).CompareTo(DisplaySize(a)));

        Squarify(visible, total, area, (node, rect) => DrawNode(dc, parent, node, rect, depth, lit));
        return true;
    }

    /// <summary>Squarified treemap (Bruls, Huizing, van Wijk). Items must be sorted largest first.</summary>
    private void Squarify(List<FsNode> items, long total, Rect area, Action<FsNode, Rect> emit)
    {
        double scale = area.Width * area.Height / total;
        double x = area.X, y = area.Y, w = area.Width, h = area.Height;
        int i = 0, n = items.Count;

        while (i < n)
        {
            if (w < 0.5 || h < 0.5)
                return; // remaining items are smaller than a pixel

            double side = Math.Min(w, h);
            double side2 = side * side;
            int start = i;
            double rowSum = 0, bestWorst = double.MaxValue;
            double maxArea = DisplaySize(items[start]) * scale;

            while (i < n)
            {
                double a = DisplaySize(items[i]) * scale;
                double sum = rowSum + a;
                double sum2 = sum * sum;
                double worst = Math.Max(side2 * maxArea / sum2, sum2 / (side2 * a));
                if (i > start && worst > bestWorst)
                    break;
                bestWorst = worst;
                rowSum = sum;
                i++;
            }

            bool lastRow = i >= n;
            if (w >= h)
            {
                // Column along the left edge.
                double stripWidth = lastRow ? w : rowSum / h;
                double cy = y;
                for (int k = start; k < i; k++)
                {
                    double ih = k == i - 1 ? y + h - cy : DisplaySize(items[k]) * scale / stripWidth;
                    emit(items[k], new Rect(x, cy, stripWidth, Math.Max(0, ih)));
                    cy += ih;
                }
                x += stripWidth;
                w -= stripWidth;
            }
            else
            {
                // Row along the top edge.
                double stripHeight = lastRow ? h : rowSum / w;
                double cx = x;
                for (int k = start; k < i; k++)
                {
                    double iw = k == i - 1 ? x + w - cx : DisplaySize(items[k]) * scale / stripHeight;
                    emit(items[k], new Rect(cx, y, Math.Max(0, iw), stripHeight));
                    cx += iw;
                }
                y += stripHeight;
                h -= stripHeight;
            }
        }
    }

    /// <param name="lit">An ancestor matched the search (or there is no search).</param>
    private void DrawNode(DrawingContext dc, LayoutItem parent, FsNode node, Rect rect, int depth, bool lit)
    {
        // Drives in the overview get breathing room so each reads as its own disk.
        if (node.Kind == NodeKind.Drive && rect.Width > 4 * DriveGap && rect.Height > 4 * DriveGap)
            rect = Inset(rect, DriveGap);

        var r = Snap(rect);
        if (r.Width < Px || r.Height < Px)
            return;

        var item = new LayoutItem(node, r);
        (parent.Children ??= []).Add(item);
        _rects[node] = r;

        bool self = _filter != null && _filter.Matches(node);
        bool nodeLit = lit || self;

        if (!node.IsContainer)
        {
            DrawLeaf(dc, node, r, nodeLit);
        }
        else if (node.Kind == NodeKind.Drive && r.Width >= 90 && r.Height >= 64)
        {
            DrawDrive(dc, item, r, depth, nodeLit);
        }
        else if (node.IsScanning)
        {
            DrawScanning(dc, node, r, depth);
        }
        else
        {
            DrawFolder(dc, item, r, depth, nodeLit);
        }

        // Outline the outermost matching boxes so hits stand out against the dimmed rest.
        if (self && !lit)
        {
            var box = Inset(r, 0.75);
            if (!box.IsEmpty)
                dc.DrawRectangle(null, Theme.MatchPen, box);
        }
    }

    private void DrawFolder(DrawingContext dc, LayoutItem item, Rect r, int depth, bool lit)
    {
        dc.DrawRectangle(Theme.FolderBorder, null, r);
        var inner = Inset(r, Px);
        if (inner.IsEmpty)
            return;
        dc.DrawRectangle(Theme.FolderFill(depth), null, inner);

        Rect content;
        if (r.Width >= 36 && r.Height >= 34)
        {
            bool onTrail = _filterTrail != null && _filterTrail.Contains(item.Node);
            DrawHeader(dc, item.Node, r, dim: _filter != null && !lit && !onTrail);
            content = new Rect(r.X + 2, r.Y + HeaderHeight, r.Width - 4, r.Height - HeaderHeight - 2);
        }
        else
        {
            content = new Rect(r.X + 2, r.Y + 2, Math.Max(0, r.Width - 4), Math.Max(0, r.Height - 4));
        }

        if (content.Width >= 2 && content.Height >= 2)
            LayoutChildren(dc, item, content, depth + 1, lit);
    }

    /// <summary>A drive tile: heavier frame, title band with icon and capacity, then its contents.</summary>
    private void DrawDrive(DrawingContext dc, LayoutItem item, Rect r, int depth, bool lit)
    {
        var node = item.Node;
        dc.DrawRectangle(Theme.DriveBorder, null, r);
        var inner = Inset(r, 2);
        if (inner.IsEmpty)
            return;
        dc.DrawRectangle(Theme.FolderFill(depth), null, inner);

        bool onTrail = _filterTrail != null && _filterTrail.Contains(node);
        double bottom = DrawDriveHeader(dc, node, new Rect(inner.X, inner.Y, inner.Width, DriveHeaderHeight), dim: _filter != null && !lit && !onTrail);
        var content = new Rect(inner.X + 2, bottom + 2, inner.Width - 4, Math.Max(0, inner.Bottom - bottom - 4));

        if (node.IsScanning)
        {
            if (content.Height > 24)
            {
                string status = node.ScanError != null ? "Scan failed" : "Scanning…";
                DrawCentered(dc, status, content, Theme.MutedText, 12);
            }
            return;
        }
        if (content.Width >= 2 && content.Height >= 2)
            LayoutChildren(dc, item, content, depth + 1, lit);
    }

    /// <summary>Draws the drive title band into <paramref name="band"/> and returns its bottom edge.</summary>
    private double DrawDriveHeader(DrawingContext dc, FsNode node, Rect band, bool dim)
    {
        dc.DrawRectangle(Theme.DriveHeaderFill, null, band);
        _labelBudget--;

        // Health strip down the left edge: green, yellow or red (none when the disk doesn't report health).
        var health = node.Hardware?.Health ?? DiskHealth.Unknown;
        var healthBrush = Theme.HealthBrush(health);
        if (healthBrush != null)
            dc.DrawRectangle(healthBrush, null, new Rect(band.X, band.Y, 4, band.Height));

        var ink = dim ? Theme.MutedText : Theme.PrimaryText;
        double x = band.X + 11;
        dc.DrawText(MakeIcon(Theme.DriveGlyph(node), 15, ink), new Point(x, band.Y + 5));
        x += 23;

        long capacity = node.Capacity;
        long used = Math.Max(0, capacity - node.FreeSize);
        double usedFraction = capacity > 0 ? (double)used / capacity : 0;

        bool contentOnly = node.IsContentOnlyDrive;
        long images = node.VisibleCount(_showAssets);
        string stats = node.IsScanning
            ? node.ScanError != null ? "scan failed" : contentOnly ? "scanning…" : $"scanning… {node.ScanProgress:P0}"
            : $"{Format.Count(images, "image", "images")}  ·  {Format.Bytes(DisplaySize(node))}"
              + (contentOnly ? "" : $"  ·  {Format.Bytes(node.FreeSize)} free");
        if (!node.IsScanning && node.Hardware != null && band.Width >= 520)
            stats += "  ·  " + node.Hardware.KindLabel;
        var statsText = MakeText(stats, Theme.SecondaryText, 11, false, Math.Max(1, band.Width * 0.55));
        bool showStats = band.Width >= 280;

        // Warning / Unhealthy get an exclamation icon and a label right after the name.
        FormattedText? alertIcon = null, alertText = null;
        if (healthBrush != null && health != DiskHealth.Healthy)
        {
            alertIcon = MakeIcon(Theme.AlertGlyph, 13, healthBrush);
            alertText = MakeText(node.Hardware!.HealthLabel, ink, 12, true, 120);
        }
        // A camera card says whether it has new images for its archive.
        FormattedText? badgeIcon = null, badgeText = null;
        if (node.ImportBadge is { } badge && !node.IsScanning)
        {
            var badgeBrush = node.ImportBadgeGood ? Theme.StatusGood : Theme.AccentText;
            badgeIcon = MakeIcon(node.ImportBadgeGood ? "" : "", 13, badgeBrush);
            badgeText = MakeText(badge, node.ImportBadgeGood ? ink : Theme.AccentText, 12, true, 200);
        }
        double alertWidth = (alertIcon != null ? alertIcon.Width + 5 + alertText!.Width + 12 : 0)
                            + (badgeIcon != null ? badgeIcon.Width + 5 + badgeText!.Width + 12 : 0);

        // The name and health alert matter most: if they don't fit, shorten the stats to just the
        // free space, then drop them (the capacity bar still shows how full the drive is).
        double nameWidth = MakeText(node.DisplayName, ink, 13, true, 10_000).WidthIncludingTrailingWhitespace;
        double Room(FormattedText? stats) => band.Right - 8 - x - alertWidth - (stats != null ? stats.Width + 14 : 0);
        if (showStats && Room(statsText) < nameWidth && !node.IsScanning)
        {
            string shortStats = $"{Format.Count(images, "image", "images")}";
            statsText = MakeText(shortStats, Theme.SecondaryText, 11, false, Math.Max(1, band.Width * 0.55));
            if (Room(statsText) < nameWidth)
                showStats = false;
        }

        double titleWidth = Room(showStats ? statsText : null);
        if (titleWidth >= 10)
        {
            var title = MakeText(node.DisplayName, ink, 13, true, titleWidth);
            dc.DrawText(title, new Point(x, band.Y + 4));
            double ax = x + title.WidthIncludingTrailingWhitespace + 12;
            if (alertIcon != null)
            {
                dc.DrawText(alertIcon, new Point(ax, band.Y + 6));
                dc.DrawText(alertText!, new Point(ax + alertIcon.Width + 5, band.Y + 5));
                ax += alertIcon.Width + 5 + alertText!.Width + 12;
            }
            if (badgeIcon != null)
            {
                dc.DrawText(badgeIcon, new Point(ax, band.Y + 6));
                dc.DrawText(badgeText!, new Point(ax + badgeIcon.Width + 5, band.Y + 5));
            }
        }
        if (showStats)
            dc.DrawText(statsText, new Point(band.Right - 8 - statsText.Width, band.Y + 6));

        // Network and cloud drives have no meaningful capacity (or scan progress) to show.
        if (contentOnly)
            return band.Y + DriveHeaderHeight;

        // Capacity bar (or scan progress while scanning); red when the drive is over 90% full.
        var bar = new Rect(band.X + 8, band.Y + DriveHeaderHeight - 10, Math.Max(0, band.Width - 16), 4);
        double fraction = node.IsScanning ? node.ScanProgress : usedFraction;
        var fill = !node.IsScanning && usedFraction > 0.9 ? Theme.CapacityCritical : Theme.Accent;
        dc.DrawRoundedRectangle(Theme.ProgressTrack, null, bar, 2, 2);
        if (fraction > 0)
            dc.DrawRoundedRectangle(fill, null, new Rect(bar.X, bar.Y, bar.Width * Math.Clamp(fraction, 0, 1), bar.Height), 2, 2);

        return band.Y + DriveHeaderHeight;
    }

    private void DrawLeaf(DrawingContext dc, FsNode node, Rect r, bool lit)
    {
        // Leave a one-pixel seam on the right/bottom so neighbours stay distinguishable.
        var fill = new Rect(r.X, r.Y, Math.Max(Px, r.Width - Px), Math.Max(Px, r.Height - Px));
        dc.DrawRectangle(lit ? Theme.Fill(node, _colorMode, _nowTicks) : Theme.DimFill, null, fill);

        if (!lit || fill.Width < 44 || fill.Height < 15 || _labelBudget <= 0)
            return;
        _labelBudget--;

        var ink = Theme.LabelBrush(node, _colorMode, _nowTicks);
        DrawText(dc, node.Name, fill.X + 3, fill.Y + 1, fill.Width - 6, ink, 11, node.Kind == NodeKind.File);
        if (fill.Height >= 30)
            DrawText(dc, Format.Bytes(node.Size), fill.X + 3, fill.Y + 15, fill.Width - 6, ink, 10.5);
    }

    private void DrawScanning(DrawingContext dc, FsNode node, Rect r, int depth)
    {
        dc.DrawRectangle(Theme.FolderBorder, null, r);
        var inner = Inset(r, Px);
        if (inner.IsEmpty)
            return;
        dc.DrawRectangle(Theme.FolderFill(depth), null, inner);
        if (r.Width >= 36 && r.Height >= 34)
            DrawHeader(dc, node, r, dim: false);

        if (r.Width < 60 || r.Height < 50)
            return;

        string status = node.ScanError != null ? "Scan failed" : $"Scanning… {node.ScanProgress:P0}";
        var center = new Point(r.X + r.Width / 2, r.Y + r.Height / 2 + HeaderHeight / 2);
        var text = MakeText(status, Theme.SecondaryText, 12, false, r.Width - 16);
        dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - 18));

        double barWidth = Math.Min(220, r.Width - 24);
        var track = new Rect(Snap(center.X - barWidth / 2), Snap(center.Y + 2), Snap(barWidth), 4);
        dc.DrawRoundedRectangle(Theme.ProgressTrack, null, track, 2, 2);
        double progress = Math.Clamp(node.ScanProgress, 0, 1);
        if (progress > 0)
            dc.DrawRoundedRectangle(Theme.Accent, null, new Rect(track.X, track.Y, track.Width * progress, track.Height), 2, 2);
    }

    private void DrawHeader(DrawingContext dc, FsNode node, Rect r, bool dim)
    {
        if (_labelBudget <= 0)
            return;
        _labelBudget--;

        string name = node.DisplayName;
        string size = Format.Bytes(DisplaySize(node));
        var text = MakeText(name + "  " + size, dim ? Theme.MutedText : Theme.PrimaryText, 11, false, r.Width - 8);
        text.SetFontWeight(FontWeights.SemiBold, 0, name.Length);
        text.SetForegroundBrush(Theme.MutedText, name.Length, size.Length + 2);
        dc.DrawText(text, new Point(r.X + 4, r.Y + 1));
    }

    private void DrawText(DrawingContext dc, string s, double x, double y, double maxWidth, Brush brush, double size, bool semibold = false)
    {
        if (maxWidth < 10)
            return;
        dc.DrawText(MakeText(s, brush, size, semibold, maxWidth), new Point(x, y));
    }

    private void DrawCentered(DrawingContext dc, string s, Rect r, Brush brush, double size)
    {
        var text = MakeText(s, brush, size, false, Math.Max(10, r.Width - 20));
        dc.DrawText(text, new Point(r.X + (r.Width - text.Width) / 2, r.Y + (r.Height - text.Height) / 2));
    }

    private FormattedText MakeIcon(string glyph, double size, Brush brush) =>
        new(glyph, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _icons, size, brush, _pixelsPerDip);

    private FormattedText MakeText(string s, Brush brush, double size, bool semibold, double maxWidth) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, semibold ? _semibold : _regular, size, brush, _pixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };

    private void RenderOverlay()
    {
        using var dc = _overlayVisual.RenderOpen();

        if (_selected != null && _rects.TryGetValue(_selected, out var sr))
        {
            var box = Inset(sr, 1);
            if (!box.IsEmpty)
                dc.DrawRectangle(Theme.SelectWash, Theme.SelectPen, box);
        }
        if (_hoverTarget != null && _rects.TryGetValue(_hoverTarget, out var tr))
        {
            var box = Inset(tr, 1);
            if (!box.IsEmpty)
                dc.DrawRectangle(null, Theme.TargetPen, box);
        }
        if (_hover != null && _hover != _hoverTarget && !_hover.IsContainer && _rects.TryGetValue(_hover, out var hr))
        {
            var box = Inset(hr, 1);
            if (!box.IsEmpty)
                dc.DrawRectangle(null, Theme.HoverPen, box);
        }

        // A duplicate's other copies light up wherever they're on screen.
        var copiesOf = _hover?.Group ?? _selected?.Group;
        if (copiesOf != null)
        {
            foreach (var copy in copiesOf.Members)
            {
                if (copy == _hover || copy == _selected || !_rects.TryGetValue(copy, out var cr))
                    continue;
                var box = Inset(cr, 1);
                if (!box.IsEmpty)
                    dc.DrawRectangle(Theme.SelectWash, Theme.TargetPen, box);
            }
        }
    }
}
