using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;

namespace SpecIQ;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";
        RuntimeText.Text = $".NET {RuntimeInformation.FrameworkDescription.Replace(".NET ", "")}";
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
