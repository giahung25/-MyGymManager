namespace MyGymManager.App.Views;

using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MyGymManager.App.ViewModels;
using MyGymManager.Core.Entities;
using MyGymManager.Core.Interfaces;
using Screen = System.Windows.Forms.Screen;

/// <summary>
/// Interaction logic for CustomerFacingWindow.xaml
/// Màn hình phụ HDMI hiển thị toàn màn hình (Kiosk Mode) đối diện Khách Hàng / Hội viên.
/// Tự động dò tìm và di chuyển sang màn hình phụ thứ hai (Screen.AllScreens) nếu có kết nối cáp HDMI / cổng ngoài.
/// </summary>
public partial class CustomerFacingWindow : Window
{
    private bool _isDisplayEventSubscribed = false;

    public CustomerFacingViewModel ViewModel => (CustomerFacingViewModel)DataContext;

    public CustomerFacingWindow() : this(new CustomerFacingViewModel())
    {
    }

    public CustomerFacingWindow(CustomerFacingViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? new CustomerFacingViewModel();

        Loaded += OnLoaded;
        KeyDown += OnWindowKeyDown;

        // Đăng ký sự kiện cắm/rút cáp màn hình HDMI từ Windows OS
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _isDisplayEventSubscribed = true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Tự động tìm và mở sang màn hình phụ HDMI
        AutoPositionToSecondaryScreen();
    }

    /// <summary>
    /// Tự động di chuyển cửa sổ sang màn hình phụ HDMI (Screen.AllScreens) và tối đa hóa toàn màn hình (Kiosk Mode).
    /// </summary>
    public void AutoPositionToSecondaryScreen()
    {
        try
        {
            var screens = Screen.AllScreens;
            Screen targetScreen;

            if (screens.Length > 1)
            {
                // Tìm màn hình phụ (không phải màn hình chính của Lễ Tân)
                targetScreen = screens.FirstOrDefault(s => !s.Primary) ?? screens[1];
                ViewModel.DisplayScreenInfo = $"Màn hình phụ HDMI: {targetScreen.DeviceName} ({targetScreen.Bounds.Width}x{targetScreen.Bounds.Height})";
            }
            else
            {
                // Nếu chỉ có 1 màn hình duy nhất
                targetScreen = Screen.PrimaryScreen ?? screens[0];
                ViewModel.DisplayScreenInfo = $"Màn hình chính: {targetScreen.DeviceName} ({targetScreen.Bounds.Width}x{targetScreen.Bounds.Height})";
            }

            // Đặt vị trí cửa sổ vào tọa độ của màn hình phụ
            WindowState = WindowState.Normal;
            WindowStartupLocation = WindowStartupLocation.Manual;

            var bounds = targetScreen.Bounds;
            Left = bounds.Left;
            Top = bounds.Top;
            Width = bounds.Width;
            Height = bounds.Height;

            // Kích hoạt Kiosk Mode toàn màn hình
            WindowState = WindowState.Maximized;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CustomerFacingWindow] Lỗi định vị màn hình phụ: {ex.Message}");
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowState = WindowState.Maximized;
            ViewModel.DisplayScreenInfo = "Màn hình chuẩn (Fallback CenterScreen)";
        }
    }

    /// <summary>
    /// Chuyển đổi cửa sổ qua lại giữa các màn hình đang kết nối trên máy tính (F12 hoặc nút bấm).
    /// </summary>
    public void SwitchToNextScreen()
    {
        try
        {
            var screens = Screen.AllScreens;
            if (screens.Length <= 1)
            {
                ViewModel.DisplayScreenInfo = "Hệ thống chỉ có 1 màn hình";
                return;
            }

            var currentHandle = new WindowInteropHelper(this).Handle;
            var currentScreen = Screen.FromHandle(currentHandle);

            int currentIndex = Array.FindIndex(screens, s => s.DeviceName == currentScreen.DeviceName);
            if (currentIndex < 0) currentIndex = 0;

            int nextIndex = (currentIndex + 1) % screens.Length;
            var nextScreen = screens[nextIndex];

            WindowState = WindowState.Normal;
            WindowStartupLocation = WindowStartupLocation.Manual;

            var bounds = nextScreen.Bounds;
            Left = bounds.Left;
            Top = bounds.Top;
            Width = bounds.Width;
            Height = bounds.Height;

            WindowState = WindowState.Maximized;

            string screenType = nextScreen.Primary ? "Màn hình chính" : "Màn hình phụ HDMI";
            ViewModel.DisplayScreenInfo = $"{screenType}: {nextScreen.DeviceName} ({bounds.Width}x{bounds.Height})";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CustomerFacingWindow] Lỗi chuyển đổi màn hình: {ex.Message}");
        }
    }

    /// <summary>
    /// Bắt sự kiện khi có thiết bị màn hình HDMI mới được cắm vào hoặc tháo ra.
    /// </summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            Debug.WriteLine("[CustomerFacingWindow] Nhận diện thay đổi thiết bị màn hình (DisplaySettingsChanged)");
            AutoPositionToSecondaryScreen();
        });
    }

    /// <summary>
    /// Phím tắt điều khiển chế độ Kiosk Mode.
    /// </summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Nhấn Esc để thu nhỏ hoặc đóng Kiosk Mode
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
            }
            else
            {
                Close();
            }
            e.Handled = true;
        }
        else if (e.Key == Key.F11)
        {
            // F11 bật/tắt toàn màn hình
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
        }
        else if (e.Key == Key.F12)
        {
            // F12 chuyển đổi màn hình kế tiếp
            SwitchToNextScreen();
            e.Handled = true;
        }
    }

    private void OnSwitchScreenClick(object sender, RoutedEventArgs e)
    {
        SwitchToNextScreen();
    }

    private void OnCloseKioskClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // ==========================================
    // PUBLIC API HỖ TRỢ ĐIỀU KHIỂN TỪ BÊN NGOÀI
    // ==========================================

    /// <summary>
    /// Cập nhật kết quả điểm danh hội viên từ bên ngoài (MainViewModel hoặc Hardware Coordinator).
    /// </summary>
    public void ShowMemberCheckIn(Member member, IdentificationMethod method = IdentificationMethod.FaceRecognition, float confidence = 0.98f)
    {
        ViewModel.HandleMemberIdentified(member, confidence, method);
    }

    /// <summary>
    /// Cập nhật kết quả định danh từ một IIdentificationProvider (Thẻ RFID, Vân tay, QR Code, SĐT).
    /// </summary>
    public void ShowIdentificationResult(IdentificationResult result, Member? member = null)
    {
        ViewModel.HandleIdentificationResult(result, member);
    }

    /// <summary>
    /// Đưa màn hình về lại trạng thái chờ.
    /// </summary>
    public void ResetToStandby()
    {
        ViewModel.ResetToIdle();
    }

    /// <summary>
    /// Cập nhật trực tiếp nguồn ảnh camera stream.
    /// </summary>
    public void SetCameraFeed(BitmapSource? feed)
    {
        ViewModel.CameraFeed = feed;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_isDisplayEventSubscribed)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _isDisplayEventSubscribed = false;
        }

        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnClosed(e);
    }
}
