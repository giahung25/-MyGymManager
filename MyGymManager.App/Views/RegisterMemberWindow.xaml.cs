namespace MyGymManager.App.Views;

using System.Windows;
using MyGymManager.App.ViewModels;

/// <summary>
/// Interaction logic for RegisterMemberWindow.xaml
/// </summary>
public partial class RegisterMemberWindow : Window
{
    public RegisterMemberWindow(RegisterMemberViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Đóng cửa sổ khi ViewModel phát sự kiện hoàn tất hoặc hủy
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

        if (DataContext is RegisterMemberViewModel vm)
        {
            vm.RequestClose -= OnRequestClose;
        }
    }
}
