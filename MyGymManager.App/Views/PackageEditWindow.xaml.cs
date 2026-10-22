namespace MyGymManager.App.Views;

using System.Windows;
using MyGymManager.App.ViewModels;

/// <summary>
/// Interaction logic for PackageEditWindow.xaml
/// </summary>
public partial class PackageEditWindow : Window
{
    public PackageEditWindow(PackageEditViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.RequestClose += OnRequestClose;
    }

    private void OnRequestClose(bool success)
    {
        DialogResult = success;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        if (DataContext is PackageEditViewModel vm)
        {
            vm.RequestClose -= OnRequestClose;
        }
    }
}
