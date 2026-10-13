using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TcpRouteTray;

internal sealed class TextPromptWindow : Window
{
    private readonly System.Windows.Controls.TextBox _input;

    private TextPromptWindow(Window owner, string title, string label)
    {
        Owner = owner;
        Title = title;
        Width = 560;
        Height = 190;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(27, 31, 42));
        Foreground = System.Windows.Media.Brushes.White;

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 9) });
        _input = new System.Windows.Controls.TextBox
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 20, 27)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(57, 65, 83)),
            Padding = new Thickness(9, 7, 9, 7)
        };
        Grid.SetRow(_input, 1);
        root.Children.Add(_input);

        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new System.Windows.Controls.Button { Content = "取消", Width = 86, Padding = new Thickness(8, 6, 8, 6), IsCancel = true };
        var accept = new System.Windows.Controls.Button { Content = "导入", Width = 86, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
        accept.Click += (_, _) => DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(accept);
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);
        Content = root;
    }

    public static string? Show(Window owner, string title, string label)
    {
        var dialog = new TextPromptWindow(owner, title, label);
        return dialog.ShowDialog() == true ? dialog._input.Text.Trim() : null;
    }
}
