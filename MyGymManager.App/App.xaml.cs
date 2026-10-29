namespace MyGymManager.App;

using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyGymManager.App.ViewModels;
using MyGymManager.App.Views;
using MyGymManager.Core.Interfaces;
using MyGymManager.Data;
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

            // 3. Khởi động AttendanceCoordinator và nạp cache hội viên vào RAM
            var coordinator = _serviceProvider.GetRequiredService<AttendanceCoordinator>();
            await coordinator.LoadActiveMembersCacheAsync();
            coordinator.RegisterProvider(_serviceProvider.GetRequiredService<RfidCardIdentificationProvider>());
            coordinator.RegisterProvider(_serviceProvider.GetRequiredService<FingerprintIdentificationProvider>());
            coordinator.RegisterProvider(_serviceProvider.GetRequiredService<QrCodeIdentificationProvider>());
            coordinator.Start();

            // 5. Hiển thị MainWindow với MainViewModel được inject từ DI
            var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            var viewModel = _serviceProvider.GetRequiredService<MainViewModel>();
            mainWindow.DataContext = viewModel;
            MainWindow = mainWindow;
            await viewModel.InitializeServicesAsync();
            mainWindow.Show();

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

        // Tầng Core & Infrastructure
        services.AddSingleton<ICameraService, OpenCvCameraService>();
        services.AddSingleton<AttendanceCoordinator>();
        services.AddSingleton<AudioNotificationService>();

        // Providers xác thực đa phương thức ngoài (Pluggable Identity Architecture)
        services.AddSingleton<RfidCardIdentificationProvider>();
        services.AddSingleton<FingerprintIdentificationProvider>();
        services.AddSingleton<QrCodeIdentificationProvider>();
        // ViewModels & Views
        services.AddSingleton<MainViewModel>();
        services.AddTransient<MainWindow>();
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
