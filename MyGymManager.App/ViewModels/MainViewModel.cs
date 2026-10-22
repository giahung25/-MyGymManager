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
using MyGymManager.Core.Models;
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
/// ViewModel chính của màn hình Điểm danh & Quản trị phòng Gym (Phuc Gym Desktop).
/// Quản lý Live Camera Feed, thông tin hội viên nhận diện thời gian thực,
/// danh sách lịch sử điểm danh, danh mục hội viên và gói tập gym.
/// </summary>
public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private int _selectedTabIndex = 1; // Mặc định tab 1: Cổng điểm danh
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
    // Cờ nguyên tử chống Dispatcher Queue Flooding (0: Rảnh, 1: Đang bận render UI)
    private int _isRenderingUi = 0;
    private FaceBox? _lastDetectedFace;
    private DateTime _lastFaceDetectedTime = DateTime.MinValue;
    // Bounding Box Face Detection Overlay
    private bool _isFaceDetected = false;
    private double _faceRectLeft = 0;
    private double _faceRectTop = 0;
    private double _faceRectWidth = 0;
    private double _faceRectHeight = 0;
    private string _faceBoxLabel = "FACE DETECTED";

    public bool IsFaceDetected
    {
        get => _isFaceDetected;
        set => SetField(ref _isFaceDetected, value);
    }

    public double FaceRectLeft
    {
        get => _faceRectLeft;
        set => SetField(ref _faceRectLeft, value);
    }

    public double FaceRectTop
    {
        get => _faceRectTop;
        set => SetField(ref _faceRectTop, value);
    }

    public double FaceRectWidth
    {
        get => _faceRectWidth;
        set => SetField(ref _faceRectWidth, value);
    }

    public double FaceRectHeight
    {
        get => _faceRectHeight;
        set => SetField(ref _faceRectHeight, value);
    }

    public string FaceBoxLabel
    {
        get => _faceBoxLabel;
        set => SetField(ref _faceBoxLabel, value);
    }

    // Manual checkin
    private string _manualPhoneNumber = string.Empty;
    private string _manualCheckInStatus = string.Empty;

    // Search filters
    private string _memberSearchText = string.Empty;
    private string _packageSearchText = string.Empty;

    private readonly DispatcherTimer _clockTimer;
    private readonly ICameraService? _cameraService;
    private readonly IFaceEngine? _faceEngine;
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

    /// <summary>
    /// Constructor DI chính nhận các service được inject từ container.
    /// </summary>
    public MainViewModel(
        ICameraService cameraService,
        IFaceEngine faceEngine,
        AttendanceCoordinator attendanceCoordinator,
        AppDbContext dbContext,
        AudioNotificationService audioService)
    {
        _cameraService = cameraService;
        _faceEngine = faceEngine;
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
            _attendanceCoordinator.FaceDetected += OnFaceDetected;
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
    public MainViewModel() : this(null!, null!, null!, null!, null!)
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
            // VẼ TRỰC TIẾP BOUNDING BOX LÊN MAT BẰNG OPENCV:
            // Đảm bảo khung viền dính chính xác 100% vào khuôn mặt trên video, không bao giờ bị lệch tỉ lệ
            if (_lastDetectedFace != null && (DateTime.UtcNow - _lastFaceDetectedTime).TotalMilliseconds < 600)
            {
                var rect = _lastDetectedFace.ToRect().Intersect(new OpenCvSharp.Rect(0, 0, frame.Width, frame.Height));
                if (rect.Width > 0 && rect.Height > 0)
                {
                    // Màu cam thể thao #FF6B00 trong BGR format là Scalar(0, 107, 255)
                    var orangeColor = new Scalar(0, 107, 255);
                    Cv2.Rectangle(frame, rect, orangeColor, 2);

                    // Nhãn FACE DETECTED
                    string label = $"FACE DETECTED ({_lastDetectedFace.Score * 100:F0}%)";
                    var textPos = new OpenCvSharp.Point(rect.X, Math.Max(24, rect.Y - 8));
                    Cv2.PutText(frame, label, textPos, HersheyFonts.HersheySimplex, 0.6, orangeColor, 2, LineTypes.AntiAlias);
                }
            }

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

            StatusMessage = $"Phát hiện khuôn mặt lạ (Score: {score:F2}). Nhấn '+ Đăng ký Face ID mới' để tạo hồ sơ.";
            _audioService?.PlayUnidentifiedFace();
        });
    }

    /// <summary>
    /// Cập nhật vị trí Bounding Box hình chữ nhật màu cam quanh khuôn mặt khi phát hiện.
    /// </summary>
    private void OnFaceDetected(object? sender, FaceBox? face)
    {
        _lastDetectedFace = face;
        _lastFaceDetectedTime = DateTime.UtcNow;
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
    /// Nạp danh sách hội viên từ SQLite.
    /// </summary>
    public async Task LoadMembersAsync()
    {
        if (_dbContext == null) return;

        try
        {
            var members = await _dbContext.Members
                .AsNoTracking()
                .Include(m => m.Package)
                .OrderByDescending(m => m.CreatedAt)
                .ToListAsync()
                .ConfigureAwait(false);

            Application.Current?.Dispatcher.Invoke(() =>
            {
                MembersList.Clear();
                foreach (var m in members)
                {
                    MembersList.Add(m);
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainViewModel] Lỗi load hội viên: {ex.Message}");
        }
    }

    /// <summary>
    /// Lọc danh sách hội viên theo từ khóa tìm kiếm.
    /// </summary>
    private async Task FilterMembersAsync()
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

            var results = await query.OrderByDescending(m => m.CreatedAt).ToListAsync();

            Application.Current?.Dispatcher.Invoke(() =>
            {
                MembersList.Clear();
                foreach (var m in results)
                {
                    MembersList.Add(m);
                }
            });
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
            await _cameraService.StartAsync("0");
            IsCameraRunning = true;
            CameraStatusText = "Camera Live (DirectShow • 60 FPS)";
            CameraStatusColor = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            StatusMessage = "Camera đã hoạt động trở lại.";
        }
    }

    private void OnRegisterNewMember()
    {
        if (_cameraService == null || _faceEngine == null || _dbContext == null || _attendanceCoordinator == null)
        {
            MessageBox.Show(
                "Các dịch vụ hệ thống (Camera / AI / CSDL) chưa sẵn sàng.",
                "Thông báo",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            var registerVm = new RegisterMemberViewModel(_cameraService, _faceEngine, _dbContext, _attendanceCoordinator, _audioService);
            var window = new Views.RegisterMemberWindow(registerVm)
            {
                Owner = Application.Current?.MainWindow
            };

            if (window.ShowDialog() == true)
            {
                _ = LoadMembersAsync();
                StatusMessage = "Đã lưu và kích hoạt Face ID cho hội viên mới thành công!";
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
            $"Bạn có chắc chắn muốn xóa hội viên '{member.FullName}' (SĐT: {member.PhoneNumber}) không?\nToàn bộ lịch sử điểm danh và dữ liệu Face ID của hội viên này sẽ bị xóa hoàn toàn.",
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
            _attendanceCoordinator.UnidentifiedFaceDetected -= OnUnidentifiedFaceDetected;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
