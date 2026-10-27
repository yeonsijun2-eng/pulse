using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Threading;
using Pulse.ViewModels;

namespace Pulse.Views;

public partial class InjectorView : UserControl
{
    public InjectorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachLogs(DataContext as InjectorViewModel);
        Loaded += (_, _) =>
        {
            AttachLogs(DataContext as InjectorViewModel);
            ScrollToLatest();
        };
        Unloaded += (_, _) => AttachLogs(null);
    }

    private InjectorViewModel? _observedViewModel;

    private void AttachLogs(InjectorViewModel? viewModel)
    {
        var next = IsLoaded ? viewModel : null;
        if (ReferenceEquals(_observedViewModel, next))
        {
            return;
        }

        if (_observedViewModel is not null)
        {
            _observedViewModel.Logs.CollectionChanged -= OnLogsChanged;
        }

        _observedViewModel = next;
        if (_observedViewModel is not null)
        {
            _observedViewModel.Logs.CollectionChanged += OnLogsChanged;
        }
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ScrollToLatest));

    private void ScrollToLatest() => LogScrollViewer.ScrollToEnd();
}
