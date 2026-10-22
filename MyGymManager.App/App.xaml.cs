namespace MyGymManager.App;

using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyGymManager.App.ViewModels;
using MyGymManager.Core.Interfaces;
using MyGymManager.Data;
using MyGymManager.Infrastructure.AI;
using MyGymManager.Infrastructure.Camera;
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

        // 1. Cấu hình DI Container
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();
        Services = _serviceProvider;

        // 2. Khởi tạo Database SQLite và seed gói tập mẫu ban đầu
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await DbInitializer.InitializeAsync(db);
        }

        // 3. Khởi động AttendanceCoordinator và nạp vector hội viên vào RAM
        var coordinator = _serviceProvider.GetRequiredService<AttendanceCoordinator>();
        await coordinator.LoadActiveMembersCacheAsync();
        coordinator.Start();

        // 4. Khởi chạy CameraService (Webcam USB mặc định index 0)
        var camera = _serviceProvider.GetRequiredService<ICameraService>();
        await camera.StartAsync("0");

        // 5. Hiển thị MainWindow với MainViewModel được inject từ DI
        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        var viewModel = _serviceProvider.GetRequiredService<MainViewModel>();
        mainWindow.DataContext = viewModel;
        await viewModel.InitializeServicesAsync();
        mainWindow.Show();
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
