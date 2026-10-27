using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Pulse.Services;
using Pulse.ViewModels;

namespace Pulse;

public partial class MainWindow : Window, IUserDialogs
{
    public MainWindow()
    {
        InitializeComponent();
        var shell = new ShellViewModel { Dialogs = this };
        DataContext = shell;
        Loaded += async (_, _) =>
        {
            try
            {
                await shell.StartAsync(VisualTreeHelperDpi());
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "PULSE", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        Closed += (_, _) => shell.Dispose();
    }

    public string? PickExecutable()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select VALORANT executable",
            Filter = "VALORANT (*.exe)|VALORANT.exe;VALORANT-Win64-Shipping.exe;RiotClientServices.exe|Executable (*.exe)|*.exe",
            CheckFileExists = true
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Maximize_Click(sender, e);
            return;
        }

        DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private double VisualTreeHelperDpi()
    {
        try
        {
            var source = PresentationSource.FromVisual(this);
            return source?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        }
        catch
        {
            return 1;
        }
    }
}
