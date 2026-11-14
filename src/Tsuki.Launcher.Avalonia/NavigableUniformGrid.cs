using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace Tsuki.Launcher.Avalonia;

/// <summary>
/// The panel of a segmented choice and of a row of tiles (ListBox.Segmented, ListBox.Tiles): equal cells in one row, as a <see cref="UniformGrid"/>, that the arrow
/// keys and Home / End move through. A list moves its choice with the keys only when its panel says which child lies in a direction, and a UniformGrid does not:
/// without this, the Achievements tabs, the language tiles and the prompt, language and window-size choices could not be changed from the keyboard (a list is
/// one Tab stop, on its chosen item). The cells are taken in their order: Left and Up go to the one before, Right and Down to the one after.
/// </summary>
public sealed class NavigableUniformGrid : UniformGrid, INavigableContainer
{
    /// <inheritdoc/>
    IInputElement? INavigableContainer.GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        int count = Children.Count;
        if (count == 0) { return null; }
        // a cell the focus is not in yet: the one after it is the first, the one before it the last when the list wraps
        int index = from is Control control ? Children.IndexOf(control) : -1;
        int? next = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => count - 1,
            NavigationDirection.Left or NavigationDirection.Up or NavigationDirection.Previous => index < 0 ? -1 : index - 1,
            NavigationDirection.Right or NavigationDirection.Down or NavigationDirection.Next => index + 1,
            _ => null,
        };

        if (next is not { } at) { return null; }
        if (wrap) { at = ((at % count) + count) % count; }
        return at >= 0 && at < count ? Children[at] : null;
    }
}
