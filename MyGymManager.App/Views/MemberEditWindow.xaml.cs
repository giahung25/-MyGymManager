namespace MyGymManager.App.Views;

using System.Windows;
using MyGymManager.App.ViewModels;

/// <summary>
/// Interaction logic for MemberEditWindow.xaml
/// </summary>
public partial class MemberEditWindow : Window
{
    public MemberEditWindow(MemberEditViewModel viewModel)
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

        if (DataContext is MemberEditViewModel vm)
        {
            vm.RequestClose -= OnRequestClose;
        }
    }
}
