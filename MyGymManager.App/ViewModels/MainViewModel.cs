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
using MyGymManager.App.Views;
using MyGymManager.Core.Entities;
using MyGymManager.Core.Events;
using MyGymManager.Core.Interfaces;
using MyGymManager.Data;
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
/// Chế độ lọc danh sách hội viên trên giao diện quản trị.
/// </summary>
public enum MemberFilterMode
{
    All = 0,          // Tất cả hội viên
    Active = 1,       // Đang hoạt động (EndDate >= now && IsActive)
    ExpiringSoon = 2, // Sắp hết hạn (EndDate >= now && EndDate <= now + 7 ngày && IsActive)
    Expired = 3       // Đã hết hạn hoặc bị khóa (EndDate < now || !IsActive)
}

/// <summary>
/// ViewModel chính của màn hình Điểm danh & Quản trị phòng Gym (Phuc Gym Desktop).
/// Quản lý Live Camera Feed, thông tin hội viên nhận diện thời gian thực,
/// danh sách lịch sử điểm danh, danh mục hội viên và gói tập gym.
/// </summary>
public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private int _selectedTabIndex = 0; // Mặc định tab 0: Bảng điều khiển (Dashboard)
    private BitmapSource? _cameraFeed;
    private Member? _currentMember;
    private string _statusMessage = "Hệ thống sẵn sàng đón hội viên";
    private Brush _statusColor = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Xanh lá mặc định

    private string _cameraStatusText = "Camera Đã tắt";
    private Brush _cameraStatusColor = new SolidColorBrush(Color.FromRgb(156, 163, 175));
    private string _memberFullName = "Chờ quẹt thẻ / quét mã QR...";
    private string _memberPhoneNumber = "--";
    private string _memberPackageName = "--";
    private string _memberExpiryText = "Vui lòng quẹt thẻ từ hoặc quét mã QR tại quầy";
    private string _memberStatusBadgeText = "ĐANG CHỜ";
    private Brush _memberStatusBadgeBg = new SolidColorBrush(Color.FromRgb(243, 244, 246));
    private Brush _memberStatusBadgeFg = new SolidColorBrush(Color.FromRgb(107, 114, 128));
    private string _authDeviceBadgeText = "🏷️ Thẻ RFID";
    private string? _memberAvatarPath;
    private string _todayLogsCountText = "0 lượt";
    private string _systemClockText = string.Empty;
    private bool _isCameraRunning = false;
    private bool _isInitialized = false;
    // Cờ nguyên tử chống Dispatcher Queue Flooding (0: Rảnh, 1: Đang bận render UI)
    private int _isRenderingUi = 0;
    private DateTime _lastUnidentifiedBeepTime = DateTime.MinValue;

    // Camera selection
    private CameraDeviceInfo? _selectedCamera;
    private string _currentCameraSource = "1";

    public ObservableCollection<CameraDeviceInfo> AvailableCameras { get; } = new();

    public CameraDeviceInfo? SelectedCamera
    {
        get => _selectedCamera;
        set
        {
            if (SetField(ref _selectedCamera, value) && value != null)
            {
                if (IsCameraRunning)
                {
                    _ = SwitchCameraSourceAsync(value);
                }
            }
        }
    }


    // Manual checkin
    private string _manualPhoneNumber = string.Empty;
    private string _manualCheckInStatus = string.Empty;

    // Search filters
    private string _memberSearchText = string.Empty;
    private string _packageSearchText = string.Empty;

    private readonly DispatcherTimer _clockTimer;
    private readonly ICameraService? _cameraService;
    private readonly AttendanceCoordinator? _attendanceCoordinator;
    private readonly AppDbContext? _dbContext;
    private readonly AudioNotificationService? _audioService;
    private bool _disposed;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Index tab hiện tại được điều khiển bởi Sidebar Menu:
    /// 0 = Bảng điều khiển (Dashboard)
    /// 1 = Cổng điểm danh (Attendance Gate)
    /// 2 = Lịch sử điểm danh (Logs)
    /// 3 = Hội viên (Members)
    /// 4 = Gói tập Gym (Packages)
    /// </summary>
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetField(ref _selectedTabIndex, value);
    }

    /// <summary>
    /// Danh sách nhật ký điểm danh trong ngày (hỗ trợ DataBinding ObservableCollection).
    /// </summary>
    public ObservableCollection<TodayLogItem> TodayLogs { get; } = new();

    /// <summary>
    /// Danh sách toàn bộ hội viên nạp từ SQLite.
    /// </summary>
    public ObservableCollection<Member> MembersList { get; } = new();

    /// <summary>
    /// Danh sách toàn bộ gói tập nạp từ SQLite.
    /// </summary>
    public ObservableCollection<Package> PackagesList { get; } = new();

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

    public string AuthDeviceBadgeText
    {
        get => _authDeviceBadgeText;
        set => SetField(ref _authDeviceBadgeText, value);
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

    public string ManualPhoneNumber
    {
        get => _manualPhoneNumber;
        set => SetField(ref _manualPhoneNumber, value);
    }

    public string ManualCheckInStatus
    {
        get => _manualCheckInStatus;
        set => SetField(ref _manualCheckInStatus, value);
    }

    private MemberFilterMode _currentMemberFilter = MemberFilterMode.All;
    public MemberFilterMode CurrentMemberFilter
    {
        get => _currentMemberFilter;
        set
        {
            if (SetField(ref _currentMemberFilter, value))
            {
                OnPropertyChanged(nameof(IsFilterAll));
                OnPropertyChanged(nameof(IsFilterActive));
                OnPropertyChanged(nameof(IsFilterExpiringSoon));
                OnPropertyChanged(nameof(IsFilterExpired));
                OnPropertyChanged(nameof(IsFilterAllActive));
                OnPropertyChanged(nameof(IsFilterActiveActive));
                OnPropertyChanged(nameof(IsFilterExpiringSoonActive));
                OnPropertyChanged(nameof(IsFilterExpiredActive));
                _ = FilterMembersAsync();
            }
        }
    }

    public bool IsFilterAll => CurrentMemberFilter == MemberFilterMode.All;
    public bool IsFilterActive => CurrentMemberFilter == MemberFilterMode.Active;
    public bool IsFilterExpiringSoon => CurrentMemberFilter == MemberFilterMode.ExpiringSoon;
    public bool IsFilterExpired => CurrentMemberFilter == MemberFilterMode.Expired;

    public bool IsFilterAllActive => CurrentMemberFilter == MemberFilterMode.All;
    public bool IsFilterActiveActive => CurrentMemberFilter == MemberFilterMode.Active;
    public bool IsFilterExpiringSoonActive => CurrentMemberFilter == MemberFilterMode.ExpiringSoon;
    public bool IsFilterExpiredActive => CurrentMemberFilter == MemberFilterMode.Expired;

    /// <summary>
    /// Lịch sử điểm danh của riêng 1 hội viên đang được chọn xem chi tiết.
    /// </summary>
    public ObservableCollection<CheckInLog> SelectedMemberHistory { get; } = new();

    private Member? _selectedHistoryMember;
    public Member? SelectedHistoryMember
    {
        get => _selectedHistoryMember;
        set => SetField(ref _selectedHistoryMember, value);
    }

    public string MemberSearchText
    {
        get => _memberSearchText;
        set
        {
            if (SetField(ref _memberSearchText, value))
            {
                _ = FilterMembersAsync();
            }
        }
    }

    public string PackageSearchText
    {
        get => _packageSearchText;
        set
        {
            if (SetField(ref _packageSearchText, value))
            {
                _ = FilterPackagesAsync();
            }
        }
    }

    public ICommand RegisterNewMemberCommand { get; }
    public ICommand ToggleCameraCommand { get; }
    public ICommand ManualCheckInCommand { get; }
    public ICommand RefreshMembersCommand { get; }
    public ICommand RefreshPackagesCommand { get; }
    public ICommand NavigateTabCommand { get; }
    public ICommand AddPackageCommand { get; }
    public ICommand EditPackageCommand { get; }
    public ICommand DeletePackageCommand { get; }
    public ICommand EditMemberCommand { get; }
    public ICommand DeleteMemberCommand { get; }
    public ICommand RefreshLogsCommand { get; }
    public ICommand RefreshCamerasCommand { get; }
    public ICommand SetMemberFilterCommand { get; }
    public ICommand ViewMemberHistoryCommand { get; }

    /// <summary>
    /// Constructor DI chính nhận các service được inject từ container.
    /// </summary>
    public MainViewModel(
        ICameraService cameraService,
        AttendanceCoordinator attendanceCoordinator,
        AppDbContext dbContext,
        AudioNotificationService audioService)
    {
        _cameraService = cameraService;
        _attendanceCoordinator = attendanceCoordinator;
        _dbContext = dbContext;
        _audioService = audioService;

        RegisterNewMemberCommand = new RelayCommand(OnRegisterNewMember);
        ToggleCameraCommand = new RelayCommand(async () => await ToggleCameraAsync());
        ManualCheckInCommand = new RelayCommand(async () => await OnManualCheckInAsync());
        RefreshMembersCommand = new RelayCommand(async () => await LoadMembersAsync());
        RefreshPackagesCommand = new RelayCommand(async () => await LoadPackagesAsync());
        NavigateTabCommand = new RelayCommand<int>(tabIndex => SelectedTabIndex = tabIndex);
        AddPackageCommand = new RelayCommand(OnAddPackage);
        EditPackageCommand = new RelayCommand<Package>(OnEditPackage);
        DeletePackageCommand = new RelayCommand<Package>(async (pkg) => await OnDeletePackageAsync(pkg));
        EditMemberCommand = new RelayCommand<Member>(OnEditMember);
        DeleteMemberCommand = new RelayCommand<Member>(async (m) => await OnDeleteMemberAsync(m));
        RefreshLogsCommand = new RelayCommand(async () => await LoadTodayLogsAsync());
        RefreshCamerasCommand = new RelayCommand(async () => await LoadAvailableCamerasAsync());
        SetMemberFilterCommand = new RelayCommand<object>(param =>
        {
            if (param is MemberFilterMode mode)
            {
                CurrentMemberFilter = mode;
            }
            else if (param is int intVal && Enum.IsDefined(typeof(MemberFilterMode), intVal))
            {
                CurrentMemberFilter = (MemberFilterMode)intVal;
            }
            else if (param is string strVal)
            {
                if (Enum.TryParse<MemberFilterMode>(strVal, true, out var parsedMode))
                {
                    CurrentMemberFilter = parsedMode;
                }
                else if (int.TryParse(strVal, out var parsedInt) && Enum.IsDefined(typeof(MemberFilterMode), parsedInt))
                {
                    CurrentMemberFilter = (MemberFilterMode)parsedInt;
                }
            }
        });
        ViewMemberHistoryCommand = new RelayCommand<Member>(async m => await OnViewMemberHistoryAsync(m));

        // Lắng nghe sự kiện từ CameraService
        if (_cameraService != null)
        {
            _cameraService.FrameCaptured += OnCameraFrameCaptured;
        }

        // Lắng nghe sự kiện từ AttendanceCoordinator (Observer Pattern)
        if (_attendanceCoordinator != null)
        {
            _attendanceCoordinator.MemberIdentified += OnMemberIdentified;
            _attendanceCoordinator.UnidentifiedDetected += OnUnidentifiedDetected;
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
    public MainViewModel() : this(null!, null!, null!, null!)
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

            // Tải danh sách hội viên và gói tập
            await LoadMembersAsync();
            await LoadPackagesAsync();

            await LoadAvailableCamerasAsync();
            if (_attendanceCoordinator != null)
            {
                StatusMessage = $"Hệ thống sẵn sàng đón hội viên ({_attendanceCoordinator.CachedMembersCount} hội viên trong RAM)";
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainViewModel] Lỗi nạp dữ liệu ban đầu: {ex.Message}");
        }
    }

    /// <summary>
    /// Nhận frame trực tiếp từ camera, chuyển đổi sang BitmapSource và đẩy lên UI Dispatcher.
    /// </summary>
    private void OnCameraFrameCaptured(object? sender, Mat frame)
    {
        if (frame.Empty()) return;

        // Khắc phục Dispatcher Queue Flooding: Drop frame ngay lập tức nếu UI chưa render xong frame trước
        if (Interlocked.CompareExchange(ref _isRenderingUi, 1, 0) != 0)
        {
            return;
        }

        try
        {
            // Chuyển đổi Mat sang BitmapSource chuẩn WPF trên luồng Camera
            var bitmap = BitmapSourceConverter.ToBitmapSource(frame);
            // Freeze giúp BitmapSource bất biến (thread-safe) và loại bỏ hoàn toàn GC pause
            bitmap.Freeze();
            Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
            {
                try
                {
                    CameraFeed = bitmap;
                }
                finally
                {
                    // Mở lại cờ cho phép frame kế tiếp được render
                    Interlocked.Exchange(ref _isRenderingUi, 0);
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainViewModel] Lỗi convert frame: {ex.Message}");
            // Đảm bảo mở lại cờ nếu có ngoại lệ xảy ra trước khi dispatch
            Interlocked.Exchange(ref _isRenderingUi, 0);
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
            AuthDeviceBadgeText = e.Method switch
            {
                MyGymManager.Core.Interfaces.IdentificationMethod.RfidCard => "🏷️ Thẻ RFID",
                MyGymManager.Core.Interfaces.IdentificationMethod.QrCode => "📱 Mã QR",
                MyGymManager.Core.Interfaces.IdentificationMethod.Fingerprint => "🏢 Máy Chấm Công LAN",
                MyGymManager.Core.Interfaces.IdentificationMethod.Manual or MyGymManager.Core.Interfaces.IdentificationMethod.ManualSearch => "⌨️ Nhập SĐT",
                _ => "🏷️ Thẻ RFID"
            };

            bool isExpired = member.IsExpired();
            if (isExpired)
            {
                // Thẻ hết hạn -> Hiển thị cảnh báo màu đỏ
                MemberExpiryText = $"Đã hết hạn ngày {member.EndDate:dd/MM/yyyy}";
                MemberStatusBadgeText = "❌ THẺ HẾT HẠN — CẦN GIA HẠN";
                MemberStatusBadgeBg = new SolidColorBrush(Color.FromRgb(254, 226, 226)); // Đỏ nhạt
                MemberStatusBadgeFg = new SolidColorBrush(Color.FromRgb(185, 28, 28));   // Đỏ đậm
                StatusColor = new SolidColorBrush(Color.FromRgb(239, 68, 68));          // ĐỎ

                // Phát âm thanh cảnh báo khi thẻ đã hết hạn
                if (e.IsNewCheckIn)
                {
                    _audioService?.PlayMembershipExpired(member.FullName);
                }
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

                // Phát âm thanh chuông đôi chào mừng điểm danh thành công
                if (e.IsNewCheckIn)
                {
                    _audioService?.PlayCheckInSuccess(member.FullName);
                }
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
    /// Xử lý sự kiện phát hiện định danh lạ (thẻ từ / mã QR / số điện thoại) chưa được đăng ký trong hệ thống.
    /// </summary>
    private void OnUnidentifiedDetected(object? sender, string identifier)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            CurrentMember = null;
            MemberFullName = "Khách Chưa Đăng Ký";
            MemberPhoneNumber = "Vui lòng đăng ký hội viên mới";
            MemberPackageName = "Chưa có gói tập";
            MemberExpiryText = $"Mã định danh chưa gán: {identifier}";
            MemberStatusBadgeText = "⚠️ CHƯA ĐĂNG KÝ";
            MemberStatusBadgeBg = new SolidColorBrush(Color.FromRgb(254, 243, 199)); // Vàng nhạt
            MemberStatusBadgeFg = new SolidColorBrush(Color.FromRgb(180, 83, 9));    // Vàng cam đậm
            StatusColor = new SolidColorBrush(Color.FromRgb(245, 158, 11));          // VÀNG / CAM

            StatusMessage = $"Phát hiện định danh lạ ('{identifier}'). Nhấn '+ Đăng ký Hội Viên' để tạo hồ sơ.";

            var now = DateTime.UtcNow;
            if ((now - _lastUnidentifiedBeepTime).TotalSeconds >= 4)
            {
                _lastUnidentifiedBeepTime = now;
                _audioService?.PlayAlertSound();
            }
        });
    }



    /// <summary>
    /// Điểm danh thủ công qua Số điện thoại.
    /// </summary>
    private async Task OnManualCheckInAsync()
    {
        if (_dbContext == null || string.IsNullOrWhiteSpace(ManualPhoneNumber))
        {
            ManualCheckInStatus = "Vui lòng nhập số điện thoại.";
            return;
        }

        try
        {
            var phone = ManualPhoneNumber.Trim();
            var member = await _dbContext.Members
                .Include(m => m.Package)
                .FirstOrDefaultAsync(m => m.PhoneNumber == phone);

            if (member == null)
            {
                ManualCheckInStatus = "❌ Không tìm thấy hội viên với số điện thoại này.";
                return;
            }

            bool isExpired = member.IsExpired();
            var log = CheckInLog.Create(member.Id, 1.0f);
            await _dbContext.CheckInLogs.AddAsync(log);
            await _dbContext.SaveChangesAsync();

            // Cập nhật lên UI
            var logItem = new TodayLogItem(
                CheckInTime: DateTime.Now.ToString("HH:mm:ss"),
                FullName: member.FullName,
                PhoneNumber: member.PhoneNumber,
                PackageName: member.Package?.Name ?? "Gói tập",
                Confidence: "100% (Thủ công)",
                Status: isExpired ? "Hết hạn" : "Thành công",
                IsValid: !isExpired);

            TodayLogs.Insert(0, logItem);
            TodayLogsCountText = $"{TodayLogs.Count} lượt";
            ManualCheckInStatus = isExpired 
                ? $"⚠️ Điểm danh thành công nhưng THẺ ĐÃ HẾT HẠN ({member.FullName})"
                : $"✅ Điểm danh thủ công thành công: {member.FullName}";

            CurrentMember = member;
            MemberFullName = member.FullName;
            MemberPhoneNumber = member.PhoneNumber;
            MemberPackageName = member.Package?.Name ?? "Gói tập mặc định";
            AuthDeviceBadgeText = "⌨️ Nhập SĐT";
            ManualPhoneNumber = string.Empty;
        }
        catch (Exception ex)
        {
            ManualCheckInStatus = $"Lỗi: {ex.Message}";
        }
    }

    /// <summary>
    /// Nạp danh sách nhật ký check-in hôm nay từ SQLite.
    /// </summary>
    public async Task LoadTodayLogsAsync()
    {
        if (_dbContext == null) return;

        try
        {
            // Fix lệch múi giờ: Chuyển đầu ngày theo giờ địa phương sang UTC
            var startOfLocalDayUtc = DateTime.Today.ToUniversalTime();
            var logs = await _dbContext.CheckInLogs
                .AsNoTracking()
                .Include(l => l.Member)
                .ThenInclude(m => m!.Package)
                .Where(l => l.CheckInTime >= startOfLocalDayUtc)
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
    /// Nạp danh sách hội viên từ SQLite kết hợp bộ lọc.
    /// </summary>
    public async Task LoadMembersAsync()
    {
        await FilterMembersAsync();
    }

    /// <summary>
    /// Lọc danh sách hội viên theo từ khóa tìm kiếm (MemberSearchText) VÀ bộ lọc trạng thái (CurrentMemberFilter).
    /// - Active: EndDate >= now &amp;&amp; IsActive == true
    /// - ExpiringSoon: EndDate >= now &amp;&amp; EndDate &lt;= now.AddDays(7) &amp;&amp; IsActive == true
    /// - Expired: EndDate &lt; now || IsActive == false
    /// - All: Tất cả hội viên
    /// </summary>
    public async Task FilterMembersAsync()
    {
        if (_dbContext == null) return;

        try
        {
            var query = _dbContext.Members
                .AsNoTracking()
                .Include(m => m.Package)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(MemberSearchText))
            {
                var kw = MemberSearchText.Trim().ToLower();
                query = query.Where(m => m.FullName.ToLower().Contains(kw) || m.PhoneNumber.Contains(kw));
            }

            var now = DateTime.UtcNow;
            var expiringThreshold = now.AddDays(7);

            switch (CurrentMemberFilter)
            {
                case MemberFilterMode.Active:
                    query = query.Where(m => m.EndDate >= now && m.IsActive == true);
                    break;
                case MemberFilterMode.ExpiringSoon:
                    query = query.Where(m => m.EndDate >= now && m.EndDate <= expiringThreshold && m.IsActive == true);
                    break;
                case MemberFilterMode.Expired:
                    query = query.Where(m => m.EndDate < now || m.IsActive == false);
                    break;
                case MemberFilterMode.All:
                default:
                    break;
            }

            var results = await query.OrderByDescending(m => m.CreatedAt).ToListAsync();

            if (Application.Current?.Dispatcher != null)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    MembersList.Clear();
                    foreach (var m in results)
                    {
                        MembersList.Add(m);
                    }
                });
            }
            else
            {
                MembersList.Clear();
                foreach (var m in results)
                {
                    MembersList.Add(m);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainViewModel] Lỗi lọc hội viên: {ex.Message}");
        }
    }

    /// <summary>
    /// Nạp danh sách gói tập từ SQLite.
    /// </summary>
    public async Task LoadPackagesAsync()
    {
        if (_dbContext == null) return;

        try
        {
            var packages = await _dbContext.Packages
                .AsNoTracking()
                .Include(p => p.Members)
                .OrderBy(p => p.DurationDays)
                .ToListAsync()
                .ConfigureAwait(false);

            Application.Current?.Dispatcher.Invoke(() =>
            {
                PackagesList.Clear();
                foreach (var p in packages)
                {
                    PackagesList.Add(p);
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainViewModel] Lỗi load gói tập: {ex.Message}");
        }
    }

    /// <summary>
    /// Lọc gói tập theo từ khóa tìm kiếm.
    /// </summary>
    private async Task FilterPackagesAsync()
    {
        if (_dbContext == null) return;

        try
        {
            var query = _dbContext.Packages.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(PackageSearchText))
            {
                var kw = PackageSearchText.Trim().ToLower();
                query = query.Where(p => p.Name.ToLower().Contains(kw));
            }

            var results = await query.OrderBy(p => p.DurationDays).ToListAsync();

            Application.Current?.Dispatcher.Invoke(() =>
            {
                PackagesList.Clear();
                foreach (var p in results)
                {
                    PackagesList.Add(p);
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainViewModel] Lỗi lọc gói tập: {ex.Message}");
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
            string src = SelectedCamera?.Source ?? _currentCameraSource;
            await _cameraService.StartAsync(src);
            IsCameraRunning = true;
            CameraStatusText = $"Camera Live ({SelectedCamera?.Name ?? "WebCam"} • 30 FPS)";
            CameraStatusColor = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            StatusMessage = "Camera đã hoạt động trở lại.";
        }
    }


    /// <summary>
    /// Quét lại toàn bộ danh sách Camera phần cứng trên máy tính và cập nhật ComboBox.
    /// </summary>
    public async Task LoadAvailableCamerasAsync()
    {
        try
        {
            var cameras = await CameraDeviceService.EnumerateCamerasAsync();

            Application.Current?.Dispatcher.Invoke(() =>
            {
                AvailableCameras.Clear();
                foreach (var cam in cameras)
                {
                    AvailableCameras.Add(cam);
                }

                // Chọn camera ban đầu
                if (SelectedCamera == null && AvailableCameras.Count > 0)
                {
                    var target = AvailableCameras.FirstOrDefault(c => c.Source == _currentCameraSource)
                              ?? AvailableCameras.FirstOrDefault(c => c.Index == 1)
                              ?? AvailableCameras[0];
                    _selectedCamera = target;
                    OnPropertyChanged(nameof(SelectedCamera));
                }

                StatusMessage = $"Đã phát hiện {AvailableCameras.Count} camera trên hệ thống.";
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainViewModel] Lỗi quét camera: {ex.Message}");
        }
    }

    /// <summary>
    /// Chuyển đổi nguồn camera đang stream sang thiết bị mới chọn một cách mượt mà.
    /// </summary>
    public async Task SwitchCameraSourceAsync(CameraDeviceInfo camera)
    {
        if (_cameraService == null || camera == null) return;
        if (_currentCameraSource == camera.Source && _cameraService.IsRunning) return;

        _currentCameraSource = camera.Source;
        StatusMessage = $"Đang chuyển đổi sang {camera.Name}...";

        try
        {
            await _cameraService.StopAsync();
            await _cameraService.StartAsync(camera.Source);
            IsCameraRunning = true;
            CameraStatusText = $"Camera Live ({camera.Name} • 30 FPS)";
            CameraStatusColor = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            StatusMessage = $"Đã chuyển thành công sang {camera.Name}.";
        }
        catch (Exception ex)
        {
            CameraStatusText = "Lỗi kết nối camera";
            CameraStatusColor = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            StatusMessage = $"Không thể mở camera {camera.Name}: {ex.Message}";
        }
    }
    private void OnRegisterNewMember()
    {
        if (_dbContext == null || _attendanceCoordinator == null)
        {
            MessageBox.Show(
                "Các dịch vụ hệ thống chưa sẵn sàng.",
                "Thông báo",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            var registerVm = new RegisterMemberViewModel(_dbContext, _attendanceCoordinator, _audioService);
            var window = new Views.RegisterMemberWindow(registerVm)
            {
                Owner = Application.Current?.MainWindow
            };

            if (window.ShowDialog() == true)
            {
                _ = LoadMembersAsync();
                StatusMessage = "Đã lưu thông tin hội viên mới thành công!";
                StatusColor = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                _audioService?.PlayCheckInSuccess();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Lỗi khi mở giao diện đăng ký: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }


    private void OnAddPackage()
    {
        if (_dbContext == null) return;

        var vm = new PackageEditViewModel(_dbContext);
        var window = new Views.PackageEditWindow(vm) { Owner = Application.Current?.MainWindow };
        if (window.ShowDialog() == true && vm.ResultPackage != null)
        {
            PackagesList.Add(vm.ResultPackage);
            StatusMessage = $"Đã thêm mới gói tập '{vm.ResultPackage.Name}' thành công!";
            StatusColor = new SolidColorBrush(Color.FromRgb(16, 185, 129));
        }
    }

    private void OnEditPackage(Package? package)
    {
        if (_dbContext == null || package == null) return;

        var vm = new PackageEditViewModel(_dbContext, package);
        var window = new Views.PackageEditWindow(vm) { Owner = Application.Current?.MainWindow };
        if (window.ShowDialog() == true)
        {
            _ = LoadPackagesAsync();
            StatusMessage = $"Đã cập nhật thông tin gói tập '{package.Name}' thành công!";
            StatusColor = new SolidColorBrush(Color.FromRgb(16, 185, 129));
        }
    }

    private async Task OnDeletePackageAsync(Package? package)
    {
        if (_dbContext == null || package == null) return;

        var confirm = MessageBox.Show(
            $"Bạn có chắc chắn muốn xóa gói tập '{package.Name}' không?\nCác hội viên đang dùng gói này sẽ chuyển về trạng thái Chưa có gói.",
            "Xác nhận xóa gói tập",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            _dbContext.Packages.Remove(package);
            await _dbContext.SaveChangesAsync();

            PackagesList.Remove(package);
            StatusMessage = $"Đã xóa gói tập '{package.Name}' khỏi hệ thống.";
            StatusColor = new SolidColorBrush(Color.FromRgb(245, 158, 11));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Lỗi khi xóa gói tập: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task OnViewMemberHistoryAsync(Member? member)
    {
        if (_dbContext == null || member == null) return;

        SelectedHistoryMember = member;

        try
        {
            var logs = await _dbContext.CheckInLogs
                .AsNoTracking()
                .Include(l => l.Member)
                .Where(l => l.MemberId == member.Id)
                .OrderByDescending(l => l.CheckInTime)
                .Take(100)
                .ToListAsync();

            if (Application.Current?.Dispatcher != null)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    SelectedMemberHistory.Clear();
                    foreach (var log in logs)
                    {
                        SelectedMemberHistory.Add(log);
                    }
                });

                Application.Current.Dispatcher.Invoke(() =>
                {
                    var historyWindow = new Views.MemberHistoryWindow(member, _dbContext, SelectedMemberHistory)
                    {
                        Owner = Application.Current?.MainWindow
                    };
                    historyWindow.ShowDialog();
                });
            }
            else
            {
                SelectedMemberHistory.Clear();
                foreach (var log in logs)
                {
                    SelectedMemberHistory.Add(log);
                }
            }

            StatusMessage = $"Đã tải {logs.Count} lượt điểm danh của hội viên '{member.FullName}'.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Lỗi hiển thị lịch sử điểm danh: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnEditMember(Member? member)
    {
        if (_dbContext == null || _attendanceCoordinator == null || member == null) return;

        var vm = new MemberEditViewModel(member, _dbContext, _attendanceCoordinator);
        var window = new Views.MemberEditWindow(vm) { Owner = Application.Current?.MainWindow };
        if (window.ShowDialog() == true)
        {
            _ = LoadMembersAsync();
            StatusMessage = $"Đã cập nhật hồ sơ hội viên '{member.FullName}' thành công!";
            StatusColor = new SolidColorBrush(Color.FromRgb(16, 185, 129));
        }
    }

    private async Task OnDeleteMemberAsync(Member? member)
    {
        if (_dbContext == null || _attendanceCoordinator == null || member == null) return;

        var confirm = MessageBox.Show(
            $"Bạn có chắc chắn muốn xóa hội viên '{member.FullName}' (SĐT: {member.PhoneNumber}) không?\nToàn bộ lịch sử điểm danh của hội viên này sẽ bị xóa hoàn toàn.",
            "Xác nhận xóa hội viên",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            _dbContext.Members.Remove(member);
            await _dbContext.SaveChangesAsync();

            // Xóa khỏi RAM Cache của AttendanceCoordinator
            _attendanceCoordinator.RemoveMemberFromCache(member.Id);

            // Cập nhật ObservableCollection trên UI
            MembersList.Remove(member);

            StatusMessage = $"Đã xóa hội viên '{member.FullName}' khỏi hệ thống.";
            StatusColor = new SolidColorBrush(Color.FromRgb(245, 158, 11));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Lỗi khi xóa hội viên: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
            _attendanceCoordinator.UnidentifiedDetected -= OnUnidentifiedDetected;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
