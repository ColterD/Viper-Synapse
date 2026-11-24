using Avalonia;
using Avalonia.Controls.Primitives;

namespace ViperSynapse.App.Controls;

/// <summary>Device cards keep their containers and focus when the dashboard changes column count.</summary>
public sealed class DeviceCardsPanel : UniformGrid
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var cards = Children.Where(child => child.IsVisible).ToArray();
        if (cards.Length == 0)
            return default;
        var columns = Math.Max(1, Columns);
        var columnWidth = ColumnWidth(availableSize.Width, columns);
        double width = 0, height = 0;
        for (int row = 0; row < cards.Length; row += columns)
        {
            double rowHeight = 0;
            for (int i = row; i < Math.Min(row + columns, cards.Length); i++)
            {
                cards[i].Measure(new Size(columnWidth, double.PositiveInfinity));
                width = Math.Max(width, cards[i].DesiredSize.Width);
                rowHeight = Math.Max(rowHeight, cards[i].DesiredSize.Height);
            }
            height += rowHeight + RowSpacing;
        }
        return new Size(width * columns + ColumnSpacing * (columns - 1), height - RowSpacing);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var cards = Children.Where(child => child.IsVisible).ToArray();
        var columns = Math.Max(1, Columns);
        var width = ColumnWidth(finalSize.Width, columns);
        double y = 0;
        for (int row = 0; row < cards.Length; row += columns)
        {
            double rowHeight = 0;
            for (int i = row; i < Math.Min(row + columns, cards.Length); i++)
            {
                var height = cards[i].DesiredSize.Height;
                cards[i].Arrange(new Rect((i - row) * (width + ColumnSpacing), y, width, height));
                rowHeight = Math.Max(rowHeight, height);
            }
            y += rowHeight + RowSpacing;
        }
        return finalSize;
    }

    double ColumnWidth(double width, int columns) => Math.Max(0, (width - ColumnSpacing * (columns - 1)) / columns);
}
