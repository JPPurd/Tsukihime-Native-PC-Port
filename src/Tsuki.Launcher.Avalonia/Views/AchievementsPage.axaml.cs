using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Tsuki.Launcher.Avalonia.Views;

/// <summary>
/// The achievements (the PS4 trophies): how many are earned, the four grades' emblems, and one row each. The model changes its rows in place, so the page keeps
/// where it was scrolled to when the game earns one.
/// </summary>
public partial class AchievementsPage : UserControl
{
    /// <summary>
    /// The page height below which the page is short (class Short: no header, a tighter summary; the page's styles say how). The smallest window (800 × 460, a
    /// Steam Deck at 150 %) leaves 326 px, where the header and the summary as drawn in a taller window would push the NOTIFY card under the footer. From this
    /// height on (a window 634 px high) the page as always shows the NOTIFY heading and the first switch's row without scrolling.
    /// </summary>
    internal const double ShortBelow = 500;

    public AchievementsPage()
    {
        InitializeComponent();
        SizeChanged += (_, e) => Classes.Set("Short", e.NewSize.Height < ShortBelow);
    }
}
