using System.Windows;

namespace TcpRouteTray;

internal partial class AdvancedTomlWindow : Window
{
    private readonly TcpRouteController _controller;

    public AdvancedTomlWindow(TcpRouteController controller)
    {
        _controller = controller;
        InitializeComponent();
        var raw = controller.LoadRawEditableConfig();
        ProfileName.Text = raw.ProfileName;
        Editor.Text = raw.Content;
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        try
        {
            _ = EditableTcpRouteConfigCodec.Parse(ProfileName.Text, Editor.Text);
            var fileName = _controller.SaveRawEditableConfig(ProfileName.Text, Editor.Text);
            StatusText.Text = $"已保存 {fileName}";
            DialogResult = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            System.Windows.MessageBox.Show(this, ex.Message, "TcpRoute2", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
