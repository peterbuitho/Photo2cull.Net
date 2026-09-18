using System.Collections.ObjectModel;

namespace Photo2CullNet.App;

/// <summary>
/// One section of the grid: an optional heading ("Group (3)") plus the
/// cards under it. Mirrors app.rs's flat-vs-grouped-vs-pipeline view
/// modes and the Blazor port's `Section` record -- kept as its own
/// sections list (rather than one flat card collection) so the Grouped
/// view's per-group headers have somewhere to live.
/// </summary>
public sealed class GridSection(string? heading, IEnumerable<PhotoCardViewModel> cards)
{
    public string? Heading { get; } = heading;
    public bool HasHeading => Heading is not null;
    public ObservableCollection<PhotoCardViewModel> Cards { get; } = new(cards);
}

public sealed record PipelineResultView(int Total, int AfterTechnical, int AfterDedupe, List<PhotoCardViewModel> Shortlist);
