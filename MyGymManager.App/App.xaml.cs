namespace MyGymManager.App;

using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyGymManager.App.ViewModels;
using MyGymManager.App.Views;
using MyGymManager.Core.Interfaces;
using MyGymManager.Data;
using MyGymManager.Infrastructure.AI;
using MyGymManager.Infrastructure.Camera;
using MyGymManager.Infrastructure.Providers;
using MyGymManager.Services;

/// <summary>
/// Interaction logic for App.xaml
/// Cấu hình DI Container (Microsoft.Extensions.DependencyInjection),
/// khởi tạo Database SQLite và kích hoạt AttendanceCoordinator.
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    public static IServiceProvider? Services { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        try
        {
            // 1. Cấu hình DI Container
            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();
            Services = _serviceProvider;

            // 2. Khởi tạo Database SQLite và tự động nâng cấp schema nếu thiếu cột
            using (var scope = _serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await DbInitializer.InitializeAsync(db);
            }

            // 3. Khởi động AttendanceCoordinator và nạp vector hội viên vào RAM
            var coordinator = _serviceProvider.GetRequiredService<AttendanceCoordinator>();
            await coordinator.LoadActiveMembersCacheAsync();
            coordinator.Start();

            // 4. Khởi chạy CameraService (Webcam USB index 1, tự động dò tìm và fallback)
            var camera = _serviceProvider.GetRequiredService<ICameraService>();
            await camera.StartAsync("1");

            // 5. Hiển thị MainWindow với MainViewModel được inject từ DI
            var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            var viewModel = _serviceProvider.GetRequiredService<MainViewModel>();
            mainWindow.DataContext = viewModel;
            MainWindow = mainWindow;
            await viewModel.InitializeServicesAsync();
            mainWindow.Show();

            // 6. Tự động mở màn hình phụ HDMI nếu phát hiện có màn hình thứ hai kết nối
            try
            {
                if (System.Windows.Forms.Screen.AllScreens.Length > 1)
                {
                    var customerWindow = _serviceProvider.GetRequiredService<CustomerFacingWindow>();
                    customerWindow.Show();
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Lỗi khi khởi động ứng dụng:\n{ex.Message}\n\nChi tiết:\n{ex.StackTrace}",
                "Phuc Gym — Lỗi Khởi Động",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Tầng dữ liệu SQLite (phuc_gym.db)
        services.AddDbContext<AppDbContext>(options =>
        {
            options.UseSqlite($"Data Source={AppDbContext.DefaultDbFile}");
        });

        // Tầng Core & Infrastructure (Singleton để tái sử dụng camera và AI engine)
        services.AddSingleton<ICameraService, OpenCvCameraService>();
        services.AddSingleton<IFaceEngine, OnnxFaceEngine>();
        services.AddSingleton<AttendanceCoordinator>();
        services.AddSingleton<AudioNotificationService>();

        // Providers xác thực đa phương thức (Pluggable Identity Architecture)
        services.AddSingleton<FaceIdentificationProvider>();
        services.AddSingleton<RfidCardIdentificationProvider>();
        services.AddSingleton<FingerprintIdentificationProvider>();
        services.AddSingleton<QrCodeIdentificationProvider>();

        // ViewModels & Views
        services.AddSingleton<MainViewModel>();
        services.AddTransient<MainWindow>();
        services.AddSingleton<CustomerFacingViewModel>();
        services.AddTransient<CustomerFacingWindow>();
    }
    protected override async void OnExit(ExitEventArgs e)
    {
        if (_serviceProvider != null)
        {
            var camera = _serviceProvider.GetService<ICameraService>();
            if (camera != null)
            {
                await camera.StopAsync();
            }

            var coordinator = _serviceProvider.GetService<AttendanceCoordinator>();
            coordinator?.Stop();

            await _serviceProvider.DisposeAsync();
        }

        base.OnExit(e);
    }
}
