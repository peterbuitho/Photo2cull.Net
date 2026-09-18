using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Photo2CullNet.Core;

namespace Photo2CullNet.App;

/// <summary>
/// One photo card in the grid. Wraps a <see cref="PhotoResult"/> plus the
/// bits the UI needs that aren't part of the domain model (the decoded
/// thumbnail bitmap, the badge color, the Overall score as currently
/// weighted). Raises PropertyChanged so the grid updates in place instead
/// of needing a full rebuild on every mode change or weight tweak.
/// </summary>
public sealed class PhotoCardViewModel : INotifyPropertyChanged
{
    private const double SharpnessBlurryMax = 40.0;
    private const double SharpnessSoftMax = 100.0;

    private static readonly IBrush BlurryBrush = new SolidColorBrush(Color.FromRgb(200, 60, 60));
    private static readonly IBrush SoftBrush = new SolidColorBrush(Color.FromRgb(230, 200, 60));
    private static readonly IBrush SharpBrush = new SolidColorBrush(Color.FromRgb(80, 160, 80));
    private static readonly IBrush WhiteText = Brushes.White;
    private static readonly IBrush BlackText = Brushes.Black;

    public PhotoResult Result { get; private set; }
    public string FileName { get; }
    public Bitmap? Thumbnail { get; }

    public PhotoCardViewModel(PhotoResult result, Bitmap? thumbnail)
    {
        Result = result;
        FileName = System.IO.Path.GetFileName(result.Path);
        Thumbnail = thumbnail;
    }

    public string Path => Result.Path;

    public PhotoMode Mode
    {
        get => Result.Mode;
        set
        {
            if (Result.Mode != value)
            {
                Result = Result with { Mode = value };
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Display-only mirror of <see cref="Mode"/> for the card's mode
    /// ComboBox: bound OneWay (view model -&gt; UI only) so picking a new
    /// value doesn't silently change <see cref="Mode"/> before the
    /// recompute it should trigger has actually run -- the code-behind
    /// reads the ComboBox's new selection directly and calls
    /// <see cref="UpdateResult"/> once the recompute finishes.
    /// </summary>
    public string ModeText => Mode.ToString();

    /// <summary>Replaces the underlying result (after a recompute) and refreshes every derived display value.</summary>
    public void UpdateResult(PhotoResult result)
    {
        Result = result;
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(ScoreText));
        OnPropertyChanged(nameof(BadgeBrush));
        OnPropertyChanged(nameof(BadgeForeground));
        RefreshOverall(_lastWeights);
    }

    public string ScoreText => Result.Score.ToString("0.0");

    public IBrush BadgeBrush => Result.Score switch
    {
        <= SharpnessBlurryMax => BlurryBrush,
        <= SharpnessSoftMax => SoftBrush,
        _ => SharpBrush,
    };

    public IBrush BadgeForeground => Result.Score <= SharpnessSoftMax && Result.Score > SharpnessBlurryMax ? BlackText : WhiteText;

    private Weights _lastWeights;
    private string _overallText = "";
    public string OverallText
    {
        get => _overallText;
        private set
        {
            if (_overallText != value)
            {
                _overallText = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Recomputes <see cref="OverallText"/> for the given weights -- called whenever a slider changes.</summary>
    public void RefreshOverall(Weights weights)
    {
        _lastWeights = weights;
        OverallText = Ranking.OverallScore(Result.Metrics, weights).ToString("0.0");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
