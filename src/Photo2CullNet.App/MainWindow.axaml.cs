using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Channels;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Photo2CullNet.Core;
using Photo2CullNet.Core.Imaging;

namespace Photo2CullNet.App;

/// <summary>
/// Code-behind (not full MVVM) port of the Blazor App.razor UI onto
/// Avalonia -- see the solution README for why this project exists
/// (Native AOT actually works here, screenshot-verified, unlike
/// Photino.Blazor). Ported feature-for-feature from App.razor: same
/// scan/pipeline/dedupe/preview logic against the same Core library,
/// just against Avalonia controls instead of Razor markup.
/// </summary>
public partial class MainWindow : Window
{
    private enum ViewMode { Flat, Grouped, Pipeline }
    private enum SortBy { Sharpness, Overall }

    private const int PreviewMaxDim = 2400;

    private readonly List<PhotoCardViewModel> _allCards = [];
    private readonly Dictionary<string, PhotoCardViewModel> _cardByPath = [];
    private readonly ObservableCollection<GridSection> _sections = [];
    private List<(string Path, string Error)> _failures = [];

    private bool _scanning;
    private int _foundCount;
    private int _processedCount;
    private CancellationTokenSource? _scanCts;

    private ViewMode _viewMode = ViewMode.Flat;
    private SortBy _sortBy = SortBy.Sharpness;
    private List<List<string>> _duplicateGroups = [];
    private int _groupThreshold = 6;
    private double _technicalThreshold = 32.0;
    private int _shortlistSize = 150;
    private PipelineResultView? _pipelineResult;

    private Weights _weights = Weights.Default;

    private List<PhotoCardViewModel>? _flatOrderCache;
    private bool _flatOrderDirty = true;

    private string? _viewerPath;
    private CancellationTokenSource? _viewerCts;

    /// <summary>
    /// Several controls set their initial value directly in XAML (the
    /// sliders' Value=, the NumericUpDowns' Value=, SortByCombo's
    /// SelectedIndex="0"), which fires their ValueChanged/SelectionChanged
    /// event mid-InitializeComponent -- before the named field for the
    /// control raising the event (or others declared after it) has
    /// actually been assigned yet. Every handler that could plausibly
    /// fire during construction guards on this instead of touching a
    /// possibly-still-null field.
    /// </summary>
    private bool _loaded;

    public MainWindow()
    {
        InitializeComponent();
        SectionsControl.ItemsSource = _sections;
        RootPathBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                ScanButton.IsEnabled = !_scanning && !string.IsNullOrWhiteSpace(RootPathBox.Text);
            }
        };
        _loaded = true;
        RebuildSections();
    }

    // --- Toolbar ---

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel?.StorageProvider is not { } storage) return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select a folder to scan",
            AllowMultiple = false,
        });
        if (folders.Count > 0)
        {
            RootPathBox.Text = folders[0].TryGetLocalPath() ?? folders[0].Path.LocalPath;
        }
    }

    private ScanMode CurrentScanMode() => ScanModeCombo.SelectedIndex switch
    {
        1 => Photo2CullNet.Core.ScanMode.ForMode(PhotoMode.Landscape),
        2 => Photo2CullNet.Core.ScanMode.ForMode(PhotoMode.Portrait),
        3 => Photo2CullNet.Core.ScanMode.ForMode(PhotoMode.Object),
        _ => Photo2CullNet.Core.ScanMode.Auto,
    };

    private async void OnScanClick(object? sender, RoutedEventArgs e)
    {
        var root = RootPathBox.Text ?? "";
        if (!Directory.Exists(root))
        {
            StatusText.Text = $"'{root}' is not a folder";
            return;
        }

        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;

        _allCards.Clear();
        _cardByPath.Clear();
        _duplicateGroups.Clear();
        _pipelineResult = null;
        _failures = [];
        MoveStatusText.Text = "";
        _foundCount = 0;
        _processedCount = 0;
        _flatOrderDirty = true;
        SetScanning(true);
        UpdateTabs();
        RebuildSections();

        var channel = Channel.CreateUnbounded<ScanEvent>();
        var scanMode = CurrentScanMode();
        _ = Task.Run(() => Scan.RunScanAsync(root, scanMode, channel.Writer, cts.Token));

        // Same reasoning as the Blazor port: rebuilding the visible grid
        // after every single photo turns an n-photo scan into an O(n^2)
        // UI update loop. Batch it.
        var throttle = Stopwatch.StartNew();
        await foreach (var evt in channel.Reader.ReadAllAsync(cts.Token))
        {
            bool refreshNow = false;
            switch (evt)
            {
                case ScanEvent.Found found:
                    _foundCount = found.Count;
                    refreshNow = true;
                    break;
                case ScanEvent.Photo photo:
                    AddCard(photo.Result);
                    _processedCount++;
                    _flatOrderDirty = true;
                    refreshNow = throttle.ElapsedMilliseconds > 150;
                    break;
                case ScanEvent.Failed failed:
                    _failures.Add((failed.Path, failed.Error));
                    _processedCount++;
                    break;
                case ScanEvent.Done:
                    SetScanning(false);
                    refreshNow = true;
                    break;
            }
            if (refreshNow)
            {
                UpdateStatus();
                RebuildSections();
                throttle.Restart();
            }
        }
        UpdateStatus();
        RebuildSections();
    }

    private void AddCard(PhotoResult result)
    {
        Bitmap? bitmap = null;
        try
        {
            // Forced onto the UI thread explicitly (Invoke, not Post --
            // synchronous, and a no-op hop if already there) rather than
            // trusting that whatever context called AddCard is already
            // the UI thread: a decoded Bitmap that's valid but never
            // rendered anything but a flat color was traced to exactly
            // this during testing (UI-automation-driven clicks don't
            // reliably carry the Avalonia dispatcher's synchronization
            // context the way a real mouse click does).
            bitmap = Dispatcher.UIThread.Invoke(
                () => new Bitmap(new MemoryStream(result.EncodeThumbPng())));
        }
        catch
        {
            // Matches the Blazor port: a bad thumbnail shouldn't drop the
            // whole photo from the grid, just show it without a preview.
        }
        var card = new PhotoCardViewModel(result, bitmap);
        card.RefreshOverall(_weights);
        _allCards.Add(card);
        _cardByPath[result.Path] = card;
    }

    private void OnGroupClick(object? sender, RoutedEventArgs e)
    {
        var hashes = _allCards.Select(c => (c.Path, c.Result.PHash)).ToList();
        _duplicateGroups = Dedupe.GroupDuplicates(hashes, _groupThreshold);
        _viewMode = ViewMode.Grouped;
        UpdateTabs();
        RebuildSections();
    }

    /// <summary>
    /// Technical filter (raw sharpness above the cutoff) -&gt; dedupe
    /// (best-of-group by Overall) -&gt; rank by Overall -&gt; top N. Same
    /// funnel as the Rust app's "Rank now" and the Blazor port's
    /// RunPipeline.
    /// </summary>
    private void RunPipeline()
    {
        int total = _allCards.Count;
        var survivors = _allCards.Where(c => c.Result.Score > _technicalThreshold).ToList();
        int afterTechnical = survivors.Count;

        var hashes = survivors.Select(c => (c.Path, c.Result.PHash)).ToList();
        var groups = Dedupe.GroupDuplicates(hashes, _groupThreshold);
        var groupedPaths = new HashSet<string>(groups.SelectMany(g => g));

        var bestOfGroup = new List<PhotoCardViewModel>();
        foreach (var group in groups)
        {
            var candidates = group.Where(_cardByPath.ContainsKey).Select(p => _cardByPath[p]);
            bestOfGroup.Add(candidates.OrderByDescending(c => Ranking.OverallScore(c.Result.Metrics, _weights)).First());
        }
        var ungrouped = survivors.Where(c => !groupedPaths.Contains(c.Path));
        var afterDedupe = bestOfGroup.Concat(ungrouped).ToList();

        var shortlist = afterDedupe
            .OrderByDescending(c => Ranking.OverallScore(c.Result.Metrics, _weights))
            .Take(_shortlistSize)
            .ToList();

        _pipelineResult = new PipelineResultView(total, afterTechnical, afterDedupe.Count, shortlist);
    }

    private void OnRankClick(object? sender, RoutedEventArgs e)
    {
        RunPipeline();
        _viewMode = ViewMode.Pipeline;
        UpdateTabs();
        RebuildSections();
    }

    private void OnMoveClick(object? sender, RoutedEventArgs e)
    {
        var root = RootPathBox.Text ?? "";
        var dir = Path.Combine(root, Scan.DisqualifiedDir);
        Directory.CreateDirectory(dir);

        int moved = 0, failed = 0;
        foreach (var card in _allCards.Where(c => c.Result.Score <= _technicalThreshold).ToList())
        {
            try
            {
                File.Move(card.Path, Path.Combine(dir, Path.GetFileName(card.Path)), overwrite: false);
                _allCards.Remove(card);
                _cardByPath.Remove(card.Path);
                moved++;
            }
            catch (Exception ex)
            {
                _failures.Add((card.Path, ex.Message));
                failed++;
            }
        }

        _flatOrderDirty = true;
        MoveStatusText.Text = failed > 0
            ? $"Moved {moved} to {Scan.DisqualifiedDir}/, {failed} failed"
            : $"Moved {moved} to {Scan.DisqualifiedDir}/";
        UpdateMoveButton();
        RebuildSections();
    }

    // --- Tabs / sort ---

    private void OnFlatTabClick(object? sender, RoutedEventArgs e)
    {
        _viewMode = ViewMode.Flat;
        UpdateTabs();
        RebuildSections();
    }

    private void OnGroupedTabClick(object? sender, RoutedEventArgs e)
    {
        _viewMode = ViewMode.Grouped;
        UpdateTabs();
        RebuildSections();
    }

    private void OnPipelineTabClick(object? sender, RoutedEventArgs e)
    {
        _viewMode = ViewMode.Pipeline;
        UpdateTabs();
        RebuildSections();
    }

    private void OnSortByChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        _sortBy = SortByCombo.SelectedIndex == 1 ? SortBy.Overall : SortBy.Sharpness;
        _flatOrderDirty = true;
        if (_viewMode == ViewMode.Flat) RebuildSections();
    }

    // --- Weight sliders (each mirrors the Blazor port's UpdateWeight) ---

    private void OnSharpnessChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_loaded) return;
        _weights = _weights with { Sharpness = (float)e.NewValue };
        SharpnessValueText.Text = _weights.Sharpness.ToString("0.00");
        OnWeightsChanged();
    }

    private void OnExposureChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_loaded) return;
        _weights = _weights with { Exposure = (float)e.NewValue };
        ExposureValueText.Text = _weights.Exposure.ToString("0.00");
        OnWeightsChanged();
    }

    private void OnContrastChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_loaded) return;
        _weights = _weights with { Contrast = (float)e.NewValue };
        ContrastValueText.Text = _weights.Contrast.ToString("0.00");
        OnWeightsChanged();
    }

    private void OnColorChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_loaded) return;
        _weights = _weights with { Color = (float)e.NewValue };
        ColorValueText.Text = _weights.Color.ToString("0.00");
        OnWeightsChanged();
    }

    private void OnCompositionChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_loaded) return;
        _weights = _weights with { Composition = (float)e.NewValue };
        CompositionValueText.Text = _weights.Composition.ToString("0.00");
        OnWeightsChanged();
    }

    private void OnSubjectChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_loaded) return;
        _weights = _weights with { Subject = (float)e.NewValue };
        SubjectValueText.Text = _weights.Subject.ToString("0.00");
        OnWeightsChanged();
    }

    private void OnWeightsChanged()
    {
        _flatOrderDirty = true;
        foreach (var card in _allCards) card.RefreshOverall(_weights);
        if (_viewMode == ViewMode.Pipeline)
        {
            RunPipeline();
            UpdateTabs();
            RebuildSections();
        }
        else if (_viewMode == ViewMode.Flat && _sortBy == SortBy.Overall)
        {
            RebuildSections();
        }
    }

    private void OnThresholdChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (!_loaded) return;
        _technicalThreshold = (double)(ThresholdNumeric.Value ?? 32);
        UpdateMoveButton();
    }

    private void OnGroupThresholdChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (!_loaded) return;
        _groupThreshold = (int)(GroupThresholdNumeric.Value ?? 6);
    }

    private void OnShortlistSizeChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (!_loaded) return;
        _shortlistSize = (int)(ShortlistNumeric.Value ?? 150);
    }

    // --- Per-card mode change (recompute) ---

    private async void OnCardModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: PhotoCardViewModel card } cb) return;
        if (cb.SelectedItem is not string modeText || !Enum.TryParse<PhotoMode>(modeText, out var mode)) return;
        if (mode == card.Result.Mode) return; // initial binding fires this too; ignore no-op selections

        var channel = Channel.CreateUnbounded<RecomputeEvent>();
        var items = new List<(string Path, PhotoMode Mode)> { (card.Path, mode) };
        _ = Task.Run(() => Scan.RunRecomputeAsync(items, channel.Writer));

        await foreach (var evt in channel.Reader.ReadAllAsync())
        {
            if (evt is RecomputeEvent.Result r && _cardByPath.TryGetValue(r.Value.Path, out var c))
            {
                c.UpdateResult(c.Result with { Mode = mode, Score = r.Value.Score, Metrics = r.Value.Metrics });
                _flatOrderDirty = true;
            }
        }
    }

    // --- Full-size preview ---

    private async void OnThumbnailDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: PhotoCardViewModel card }) return;
        await OpenPreview(card.Path);
    }

    private async Task OpenPreview(string path)
    {
        _viewerCts?.Cancel();
        var cts = new CancellationTokenSource();
        _viewerCts = cts;
        _viewerPath = path;

        ViewerTitleText.Text = Path.GetFileName(path);
        ViewerImage.Source = null;
        ViewerLoadingText.IsVisible = true;
        ViewerOverlay.IsVisible = true;

        Bitmap? bitmap;
        try
        {
            bitmap = await Task.Run(() =>
            {
                try
                {
                    var img = Photo.DecodePhoto(path, PreviewMaxDim);
                    return new Bitmap(new MemoryStream(SkiaImageCodec.EncodePng(img)));
                }
                catch
                {
                    return null;
                }
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cts.IsCancellationRequested || _viewerPath != path) return; // superseded or closed while decoding

        if (bitmap is null)
        {
            // Matches app.rs's PreviewEvent::Failed handling: a failed decode just closes the viewer.
            CloseViewer();
            return;
        }

        ViewerImage.Source = bitmap;
        ViewerLoadingText.IsVisible = false;
    }

    private void CloseViewer()
    {
        _viewerCts?.Cancel();
        _viewerPath = null;
        ViewerOverlay.IsVisible = false;
        ViewerImage.Source = null;
    }

    private void OnCloseViewerClick(object? sender, RoutedEventArgs e) => CloseViewer();

    private void OnViewerOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only close on a click that lands on the dim backdrop itself,
        // not one that bubbled up from the content panel inside it.
        if (ReferenceEquals(e.Source, sender)) CloseViewer();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ViewerOverlay.IsVisible)
        {
            CloseViewer();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    // --- Shared UI refresh helpers ---

    private void SetScanning(bool scanning)
    {
        _scanning = scanning;
        RootPathBox.IsEnabled = !scanning;
        BrowseButton.IsEnabled = !scanning;
        ScanModeCombo.IsEnabled = !scanning;
        ScanButton.IsEnabled = !scanning && !string.IsNullOrWhiteSpace(RootPathBox.Text);
        GroupButton.IsEnabled = !scanning && _allCards.Count > 0;
        RankButton.IsEnabled = !scanning && _allCards.Count > 0;
        UpdateMoveButton();
    }

    private void UpdateMoveButton()
    {
        int flagged = _allCards.Count(c => c.Result.Score <= _technicalThreshold);
        MoveButton.Content = $"Move {flagged} disqualified";
        MoveButton.IsEnabled = !_scanning && flagged > 0;
    }

    private void UpdateStatus()
    {
        StatusText.Text = _scanning
            ? $"Scanning… {_processedCount} / {_foundCount}"
            : _allCards.Count > 0
                ? $"{_allCards.Count} photo(s) scored" + (_failures.Count > 0 ? $" -- {_failures.Count} failed" : "")
                : "Enter a folder and click Scan.";
        UpdateMoveButton();
    }

    private void UpdateTabs()
    {
        FlatTab.IsChecked = _viewMode == ViewMode.Flat;
        GroupedTab.IsChecked = _viewMode == ViewMode.Grouped;
        PipelineTab.IsChecked = _viewMode == ViewMode.Pipeline;
        GroupedTab.Content = $"Duplicate groups ({_duplicateGroups.Count})";
        PipelineTab.Content = _pipelineResult is { } pr ? $"Shortlist ({pr.Shortlist.Count})" : "Shortlist";

        if (_viewMode == ViewMode.Pipeline && _pipelineResult is { } pr2)
        {
            PipelineSummaryText.Text =
                $"{pr2.Total} total -> {pr2.AfterTechnical} passed technical filter -> {pr2.AfterDedupe} after dedupe -> top {pr2.Shortlist.Count} shown";
            PipelineSummaryText.IsVisible = true;
        }
        else
        {
            PipelineSummaryText.IsVisible = false;
        }
    }

    /// <summary>
    /// The Flat view's photo order (by raw sharpness or Overall, best
    /// first), cached and only recomputed when the underlying data or
    /// weights actually change -- same reasoning as the Blazor port's
    /// FlatOrder(): resorting all N cards on every single refresh turned
    /// a 1000+ photo scan into a quadratic-time UI update loop.
    /// </summary>
    private List<PhotoCardViewModel> FlatOrder()
    {
        if (_flatOrderDirty || _flatOrderCache is null)
        {
            _flatOrderCache = _sortBy == SortBy.Overall
                ? _allCards.OrderByDescending(c => Ranking.OverallScore(c.Result.Metrics, _weights)).ToList()
                : _allCards.OrderByDescending(c => c.Result.Score).ToList();
            _flatOrderDirty = false;
        }
        return _flatOrderCache;
    }

    private void RebuildSections()
    {
        _sections.Clear();
        switch (_viewMode)
        {
            case ViewMode.Grouped:
                foreach (var group in _duplicateGroups)
                {
                    var cards = group.Where(_cardByPath.ContainsKey).Select(p => _cardByPath[p]);
                    _sections.Add(new GridSection($"Group ({group.Count})", cards));
                }
                break;
            case ViewMode.Pipeline:
                if (_pipelineResult is { } pr)
                {
                    _sections.Add(new GridSection(null, pr.Shortlist));
                }
                break;
            default:
                _sections.Add(new GridSection(null, FlatOrder()));
                break;
        }
    }
}
