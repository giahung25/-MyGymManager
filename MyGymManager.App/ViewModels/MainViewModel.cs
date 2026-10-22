namespace MyGymManager.App.ViewModels;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.EntityFrameworkCore;
using MyGymManager.Core.Entities;
using MyGymManager.Core.Events;
using MyGymManager.Core.Interfaces;
using MyGymManager.Data;
using MyGymManager.Infrastructure.AI;
using MyGymManager.Infrastructure.Camera;
using MyGymManager.Services;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

/// <summary>
/// Dòng nhật ký điểm danh hiển thị trên Dashboard giao diện lễ tân.
/// </summary>
public record TodayLogItem(
    string CheckInTime,
    string FullName,
    string PhoneNumber,
    string PackageName,
    string Confidence,
    string Status,
    bool IsValid);

/// <summary>
/// ViewModel chính của màn hình Điểm danh (Attendance Dashboard).
/// Quản lý Live Camera Feed, thông tin hội viên nhận diện thời gian thực,
/// danh sách lịch sử điểm danh trong ngày và thông báo trạng thái.
/// </summary>
public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private BitmapSource? _cameraFeed;
    private Member? _currentMember;
    private string _statusMessage = "Hệ thống sẵn sàng đón hội viên";
    private Brush _statusColor = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Xanh lá mặc định

    private string _cameraStatusText = "Camera Live (DirectShow • 60 FPS)";
    private Brush _cameraStatusColor = new SolidColorBrush(Color.FromRgb(16, 185, 129));
    private string _memberFullName = "Chờ nhận diện khuôn mặt...";
    private string _memberPhoneNumber = "--";
    private string _memberPackageName = "--";
    private string _memberExpiryText = "Vui lòng hướng mặt về phía camera";
    private string _memberStatusBadgeText = "ĐANG CHỜ";
    private Brush _memberStatusBadgeBg = new SolidColorBrush(Color.FromRgb(243, 244, 246));
    private Brush _memberStatusBadgeFg = new SolidColorBrush(Color.FromRgb(107, 114, 128));
    private string? _memberAvatarPath;
    private string _todayLogsCountText = "0 lượt";
    private string _systemClockText = string.Empty;
    private bool _isCameraRunning = true;
    private bool _isInitialized = false;

    private readonly DispatcherTimer _clockTimer;
    private readonly ICameraService? _cameraService;
    private readonly AttendanceCoordinator? _attendanceCoordinator;
    private readonly AppDbContext? _dbContext;
    private bool _disposed;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Danh sách nhật ký điểm danh trong ngày (hỗ trợ DataBinding ObservableCollection).
    /// </summary>
    public ObservableCollection<TodayLogItem> TodayLogs { get; } = new();

    /// <summary>
    /// Nguồn ảnh Camera trực tiếp cập nhật từ OpenCvSharp Mat qua WriteableBitmap/BitmapSource.
    /// </summary>
    public BitmapSource? CameraFeed
    {
        get => _cameraFeed;
        set => SetField(ref _cameraFeed, value);
    }

    /// <summary>
    /// Hội viên vừa được nhận diện thành công gần nhất.
    /// </summary>
    public Member? CurrentMember
    {
        get => _currentMember;
        set
        {
            if (SetField(ref _currentMember, value))
            {
                OnPropertyChanged(nameof(HasCurrentMember));
            }
        }
    }

    public bool HasCurrentMember => _currentMember != null;

    /// <summary>
    /// Thông báo trạng thái hệ thống.
    /// </summary>
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    /// <summary>
    /// Màu sắc thông báo trạng thái:
    /// - Xanh lá (#10B981): Hợp lệ / Hoạt động tốt
    /// - Đỏ (#EF4444): Thẻ hết hạn / Lỗi
    /// - Vàng / Cam (#F59E0B): Khách lạ / Cảnh báo
    /// </summary>
    public Brush StatusColor
    {
        get => _statusColor;
        set => SetField(ref _statusColor, value);
    }

    public string CameraStatusText
    {
        get => _cameraStatusText;
        set => SetField(ref _cameraStatusText, value);
    }

    public Brush CameraStatusColor
    {
        get => _cameraStatusColor;
        set => SetField(ref _cameraStatusColor, value);
    }

    public string MemberFullName
    {
        get => _memberFullName;
        set => SetField(ref _memberFullName, value);
    }

    public string MemberPhoneNumber
    {
        get => _memberPhoneNumber;
        set => SetField(ref _memberPhoneNumber, value);
    }

    public string MemberPackageName
    {
        get => _memberPackageName;
        set => SetField(ref _memberPackageName, value);
    }

    public string MemberExpiryText
    {
        get => _memberExpiryText;
        set => SetField(ref _memberExpiryText, value);
    }

    public string MemberStatusBadgeText
    {
        get => _memberStatusBadgeText;
        set => SetField(ref _memberStatusBadgeText, value);
    }

    public Brush MemberStatusBadgeBg
    {
        get => _memberStatusBadgeBg;
        set => SetField(ref _memberStatusBadgeBg, value);
    }

    public Brush MemberStatusBadgeFg
    {
        get => _memberStatusBadgeFg;
        set => SetField(ref _memberStatusBadgeFg, value);
    }

    public string? MemberAvatarPath
    {
        get => _memberAvatarPath;
        set => SetField(ref _memberAvatarPath, value);
    }

    public string TodayLogsCountText
    {
        get => _todayLogsCountText;
        set => SetField(ref _todayLogsCountText, value);
    }

    public string SystemClockText
    {
        get => _systemClockText;
        set => SetField(ref _systemClockText, value);
    }

    public bool IsCameraRunning
    {
        get => _isCameraRunning;
        set => SetField(ref _isCameraRunning, value);
    }

    public ICommand RegisterNewMemberCommand { get; }
    public ICommand ToggleCameraCommand { get; }

    /// <summary>
    /// Constructor DI chính nhận các service được inject từ container.
    /// </summary>
    public MainViewModel(
        ICameraService cameraService,
        AttendanceCoordinator attendanceCoordinator,
        AppDbContext dbContext)
    {
        _cameraService = cameraService;
        _attendanceCoordinator = attendanceCoordinator;
        _dbContext = dbContext;

        RegisterNewMemberCommand = new RelayCommand(OnRegisterNewMember);
        ToggleCameraCommand = new RelayCommand(async () => await ToggleCameraAsync());

        // Lắng nghe sự kiện từ CameraService
        if (_cameraService != null)
        {
            _cameraService.FrameCaptured += OnCameraFrameCaptured;
        }

        // Lắng nghe sự kiện từ AttendanceCoordinator (Observer Pattern)
        if (_attendanceCoordinator != null)
        {
            _attendanceCoordinator.MemberIdentified += OnMemberIdentified;
            _attendanceCoordinator.UnidentifiedFaceDetected += OnUnidentifiedFaceDetected;
        }

        // Khởi động đồng hồ hệ thống cập nhật mỗi giây
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (s, e) => UpdateClock();
        _clockTimer.Start();
        UpdateClock();
    }

    /// <summary>
    /// Constructor không tham số dành cho XAML Designer / Fallback.
    /// </summary>
    public MainViewModel() : this(null!, null!, null!)
    {
    }

    /// <summary>
    /// Khởi tạo dữ liệu ban đầu từ CSDL nếu chưa chạy.
    /// </summary>
    public async Task InitializeServicesAsync()
    {
        if (_isInitialized) return;
        _isInitialized = true;

        try
        {
            // Tải nhật ký hôm nay từ SQLite
            await LoadTodayLogsAsync();

            if (_attendanceCoordinator != null)
            {
                StatusMessage = $"Hệ thống sẵn sàng đón hội viên ({_attendanceCoordinator.CachedMembersCount} hội viên trong RAM)";
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainViewModel] Lỗi nạp nhật ký ban đầu: {ex.Message}");
        }
    }

    /// <summary>
    /// Nhận frame trực tiếp từ camera, chuyển đổi sang BitmapSource và đẩy lên UI Dispatcher.
    /// </summary>
    private void OnCameraFrameCaptured(object? sender, Mat frame)
    {
        try
        {
            if (frame.Empty()) return;

            // Chuyển đổi Mat sang BitmapSource chuẩn WPF và Freeze để truy cập đa luồng an toàn
            var bitmap = BitmapSourceConverter.ToBitmapSource(frame);
            bitmap.Freeze();

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                CameraFeed = bitmap;
            }, DispatcherPriority.Render);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainViewModel] Lỗi convert frame: {ex.Message}");
        }
    }

    /// <summary>
    /// Xử lý sự kiện nhận diện thành công hội viên từ AttendanceCoordinator.
    /// </summary>
    private void OnMemberIdentified(object? sender, MemberIdentifiedEventArgs e)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var member = e.Member;
            CurrentMember = member;
            MemberFullName = member.FullName;
            MemberPhoneNumber = string.IsNullOrWhiteSpace(member.PhoneNumber) ? "--" : member.PhoneNumber;
            MemberPackageName = member.Package?.Name ?? "Gói tập mặc định";

            bool isExpired = member.IsExpired();
            if (isExpired)
            {
                // Thẻ hết hạn -> Hiển thị cảnh báo màu đỏ
                MemberExpiryText = $"Đã hết hạn ngày {member.EndDate:dd/MM/yyyy}";
                MemberStatusBadgeText = "❌ THẺ HẾT HẠN — CẦN GIA HẠN";
                MemberStatusBadgeBg = new SolidColorBrush(Color.FromRgb(254, 226, 226)); // Đỏ nhạt
                MemberStatusBadgeFg = new SolidColorBrush(Color.FromRgb(185, 28, 28));   // Đỏ đậm
                StatusColor = new SolidColorBrush(Color.FromRgb(239, 68, 68));          // ĐỎ
            }
            else
            {
                // Thẻ hợp lệ -> Hiển thị thẻ xanh mời vào tập
                int remaining = member.RemainingDays();
                MemberExpiryText = $"Hạn đến: {member.EndDate:dd/MM/yyyy} (Còn {remaining} ngày)";
                MemberStatusBadgeText = "✅ HỢP LỆ — MỜI VÀO TẬP";
                MemberStatusBadgeBg = new SolidColorBrush(Color.FromRgb(209, 250, 229)); // Xanh ngọc nhạt
                MemberStatusBadgeFg = new SolidColorBrush(Color.FromRgb(4, 120, 87));    // Xanh đậm
                StatusColor = new SolidColorBrush(Color.FromRgb(16, 185, 129));         // XANH LÁ
            }

            MemberAvatarPath = member.AvatarPath;

            // Bổ sung vào danh sách TodayLogs nếu là lượt check-in mới
            if (e.IsNewCheckIn)
            {
                var logItem = new TodayLogItem(
                    CheckInTime: DateTime.Now.ToString("HH:mm:ss"),
                    FullName: member.FullName,
                    PhoneNumber: member.PhoneNumber,
                    PackageName: member.Package?.Name ?? "Gói tập",
                    Confidence: $"{e.Confidence * 100:F1}%",
                    Status: isExpired ? "Hết hạn" : "Thành công",
                    IsValid: !isExpired);

                TodayLogs.Insert(0, logItem);
                TodayLogsCountText = $"{TodayLogs.Count} lượt";
            }

            StatusMessage = $"Đã nhận diện: {member.FullName} (Độ tin cậy: {e.Confidence * 100:F1}%)";
        });
    }

    /// <summary>
    /// Xử lý sự kiện phát hiện khuôn mặt lạ không khớp hội viên nào.
    /// </summary>
    private void OnUnidentifiedFaceDetected(object? sender, float score)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            CurrentMember = null;
            MemberFullName = "Khách Chưa Đăng Ký";
            MemberPhoneNumber = "Vui lòng đăng ký hội viên mới";
            MemberPackageName = "Chưa có gói tập";
            MemberExpiryText = "Không tìm thấy hồ sơ khuôn mặt tương ứng";
            MemberStatusBadgeText = "⚠️ KHÔNG NHẬN DIỆN ĐƯỢC";
            MemberStatusBadgeBg = new SolidColorBrush(Color.FromRgb(254, 243, 199)); // Vàng nhạt
            MemberStatusBadgeFg = new SolidColorBrush(Color.FromRgb(180, 83, 9));    // Vàng cam đậm
            StatusColor = new SolidColorBrush(Color.FromRgb(245, 158, 11));          // VÀNG / CAM

            StatusMessage = $"Phát hiện khuôn mặt lạ (Score: {score:F2}). Nhấn 'Đăng ký Hội viên mới' để tạo hồ sơ.";
        });
    }

    /// <summary>
    /// Nạp danh sách nhật ký check-in hôm nay từ SQLite.
    /// </summary>
    public async Task LoadTodayLogsAsync()
    {
        if (_dbContext == null) return;

        try
        {
            var startOfDay = DateTime.UtcNow.Date;
            var logs = await _dbContext.CheckInLogs
                .AsNoTracking()
                .Include(l => l.Member)
                .ThenInclude(m => m!.Package)
                .Where(l => l.CheckInTime >= startOfDay)
                .OrderByDescending(l => l.CheckInTime)
                .Take(50)
                .ToListAsync()
                .ConfigureAwait(false);

            Application.Current?.Dispatcher.Invoke(() =>
            {
                TodayLogs.Clear();
                foreach (var log in logs)
                {
                    bool isExpired = log.Member?.IsExpired() ?? false;
                    TodayLogs.Add(new TodayLogItem(
                        CheckInTime: log.CheckInTime.ToLocalTime().ToString("HH:mm:ss"),
                        FullName: log.Member?.FullName ?? "N/A",
                        PhoneNumber: log.Member?.PhoneNumber ?? "--",
                        PackageName: log.Member?.Package?.Name ?? "Gói tập",
                        Confidence: $"{log.ConfidenceScore * 100:F1}%",
                        Status: isExpired ? "Hết hạn" : "Thành công",
                        IsValid: !isExpired));
                }

                TodayLogsCountText = $"{TodayLogs.Count} lượt";
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainViewModel] Lỗi load nhật ký: {ex.Message}");
        }
    }

    /// <summary>
    /// Bật/Tắt stream camera thủ công.
    /// </summary>
    private async Task ToggleCameraAsync()
    {
        if (_cameraService == null) return;

        if (IsCameraRunning)
        {
            await _cameraService.StopAsync();
            IsCameraRunning = false;
            CameraStatusText = "Camera Đã tạm dừng";
            CameraStatusColor = new SolidColorBrush(Color.FromRgb(156, 163, 175));
            StatusMessage = "Camera đã tạm dừng.";
        }
        else
        {
            await _cameraService.StartAsync("0");
            IsCameraRunning = true;
            CameraStatusText = "Camera Live (DirectShow • 60 FPS)";
            CameraStatusColor = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            StatusMessage = "Camera đã hoạt động trở lại.";
        }
    }

    private void OnRegisterNewMember()
    {
        MessageBox.Show(
            "Tính năng 'Đăng ký Hội viên mới' đang được mở rộng.\nBạn có thể chụp 3 góc mặt (Multi-Template FaceID) để ghi danh.",
            "MyGymManager — Đăng ký Hội viên",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void UpdateClock()
    {
        SystemClockText = DateTime.Now.ToString("HH:mm:ss • dd/MM/yyyy");
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        if (_disposed) return;

        _clockTimer.Stop();

        if (_cameraService != null)
        {
            _cameraService.FrameCaptured -= OnCameraFrameCaptured;
        }

        if (_attendanceCoordinator != null)
        {
            _attendanceCoordinator.MemberIdentified -= OnMemberIdentified;
            _attendanceCoordinator.UnidentifiedFaceDetected -= OnUnidentifiedFaceDetected;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
