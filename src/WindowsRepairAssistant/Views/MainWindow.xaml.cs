using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using WindowsRepairAssistant.ViewModels;

namespace WindowsRepairAssistant.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.CloseRequested += (_, _) => Close();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Screen))
            {
                RootScroll.ScrollToTop();
            }
        };
        viewModel.OutputLines.CollectionChanged += OnOutputChanged;
        Loaded += (_, _) => viewModel.Initialize();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Never abandon a running repair from the UI. We do not kill DISM/SFC either way, but
        // keeping the window open means the user still gets the result and a complete log.
        if (_viewModel.IsRunning)
        {
            e.Cancel = true;
            MessageBox.Show(this,
                "Windows Repair Assistant is still working on your computer.\n\n" +
                "Stopping part-way through could leave a repair or an update unfinished, so this window will stay open until all steps are complete. " +
                "You can minimise it and keep using your computer in the meantime.",
                "Still working", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        base.OnClosing(e);
    }

    private void OnOutputChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DetailsExpander.IsExpanded && OutputList.Items.Count > 0)
        {
            OutputList.ScrollIntoView(OutputList.Items[^1]);
        }
    }
}
