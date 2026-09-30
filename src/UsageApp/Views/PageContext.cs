using Microsoft.UI.Xaml;
using UsageApp.State;

namespace UsageApp.Views;

public enum PageKind
{
    Overview,
    Projects,
    Project,
    Activity,
}

public sealed record Route(PageKind Kind, string? ProjectId = null);

/// <summary>What every page needs from the shell.</summary>
public sealed class PageContext
{
    public required AppState State { get; init; }
    public required LogoStore Logos { get; init; }
    public required ProjectColorStore Colors { get; init; }
    public required Action<Route> Navigate { get; init; }

    /// <summary>Rebuilds the current page in place, keeping the scroll position.</summary>
    public required Action Redraw { get; init; }
    public required Window Window { get; init; }
}

/// <summary>A page builds its whole tree from the current report, and rebuilds on change.</summary>
public interface IPage
{
    UIElement Build();
}
