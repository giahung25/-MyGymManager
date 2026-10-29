namespace MyGymManager.App.ViewModels;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using MyGymManager.Core.Entities;
using MyGymManager.Core.Events;
using MyGymManager.Core.Interfaces;
using MyGymManager.Core.Models;
using MyGymManager.Services;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

/// <summary>
/// ViewModel quản lý dữ liệu và trạng thái hiển thị cho Màn hình phụ HDMI dành riêng cho Khách Hàng (CustomerFacingWindow).
/// Hỗ trợ kiến trúc đa phương thức xác minh (Pluggable Multi-Biometrics) và Kiosk Mode.
/// </summary>
public class CustomerFacingViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ICameraService? _cameraService;
    private readonly AttendanceCoordinator? _coordinator;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _autoResetTimer;

    private BitmapSource? _cameraFeed;
    private bool _isFaceInFrame;
    private bool _isMemberIdentified;
    private Member? _currentMember;

    private string _memberName = string.Empty;
    private string _memberPhone = string.Empty;
    private string? _memberAvatarPath;
    private string _packageName = string.Empty;
    private int _remainingDays;
    private string _remainingDaysText = string.Empty;
    private string _expiryDateText = string.Empty;
    private bool _isExpired;

    private string _statusBadgeText = "HỆ THỐNG SẴN SÀNG";
    private Brush _statusBadgeBg = new SolidColorBrush(Color.FromRgb(30, 41, 59));
    private Brush _statusBadgeFg = new SolidColorBrush(Color.FromRgb(255, 107, 0));

    private IdentificationMethod _activeMethod = IdentificationMethod.FaceRecognition;
    private string _activeMethodName = "Nhận diện khuôn mặt (FaceID ArcFace)";
    private float _confidenceScore = 0.0f;
    private string _confidenceText = "--";
    private string _checkInTimeText = "--";

    private string _currentTimeText = DateTime.Now.ToString("HH:mm:ss");
    private string _currentDateText = DateTime.Now.ToString("dddd, dd/MM/yyyy", new CultureInfo("vi-VN"));
    private int _countdownSeconds = 5;
    private double _countdownProgress = 100;

    private string _displayScreenInfo = "Đang kiểm tra màn hình HDMI...";
    private int _isRenderingUi = 0;
    private bool _disposed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public CustomerFacingViewModel(
        ICameraService? cameraService = null,
        AttendanceCoordinator? coordinator = null)
    {
        _cameraService = cameraService ?? App.Services?.GetService<ICameraService>();
        _coordinator = coordinator ?? App.Services?.GetService<AttendanceCoordinator>();

        // Đồng hồ thời gian thực
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (s, e) =>
        {
            CurrentTimeText = DateTime.Now.ToString("HH:mm:ss");
            CurrentDateText = DateTime.Now.ToString("dddd, dd/MM/yyyy", new CultureInfo("vi-VN"));
        };
        _clockTimer.Start();

        // Timer đếm ngược tự động chuyển về trạng thái chờ sau khi điểm danh
        _autoResetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _autoResetTimer.Tick += OnAutoResetTick;

        // Đăng ký nhận sự kiện Camera & Attendance
        if (_cameraService != null)
        {
            _cameraService.FrameCaptured += OnCameraFrameCaptured;
        }

        if (_coordinator != null)
        {
            _coordinator.MemberIdentified += OnMemberIdentified;
            _coordinator.UnidentifiedDetected += OnUnidentifiedDetected;
        }
    }

    // ==========================================
    // PROPERTIES BINDING XAML
    // ==========================================

    public BitmapSource? CameraFeed
    {
        get => _cameraFeed;
        set => SetField(ref _cameraFeed, value);
    }

    public bool IsFaceInFrame
    {
        get => _isFaceInFrame;
        set => SetField(ref _isFaceInFrame, value);
    }

    public bool IsMemberIdentified
    {
        get => _isMemberIdentified;
        set
        {
            if (SetField(ref _isMemberIdentified, value))
            {
                OnPropertyChanged(nameof(IsIdleState));
            }
        }
    }

    public bool IsIdleState => !_isMemberIdentified;

    public Member? CurrentMember
    {
        get => _currentMember;
        set => SetField(ref _currentMember, value);
    }

    public string MemberName
    {
        get => _memberName;
        set => SetField(ref _memberName, value);
    }

    public string MemberPhone
    {
        get => _memberPhone;
        set => SetField(ref _memberPhone, value);
    }

    public string? MemberAvatarPath
    {
        get => _memberAvatarPath;
        set => SetField(ref _memberAvatarPath, value);
    }

    public string PackageName
    {
        get => _packageName;
        set => SetField(ref _packageName, value);
    }

    public int RemainingDays
    {
        get => _remainingDays;
        set => SetField(ref _remainingDays, value);
    }

    public string RemainingDaysText
    {
        get => _remainingDaysText;
        set => SetField(ref _remainingDaysText, value);
    }

    public string ExpiryDateText
    {
        get => _expiryDateText;
        set => SetField(ref _expiryDateText, value);
    }

    public bool IsExpired
    {
        get => _isExpired;
        set => SetField(ref _isExpired, value);
    }

    public string StatusBadgeText
    {
        get => _statusBadgeText;
        set => SetField(ref _statusBadgeText, value);
    }

    public Brush StatusBadgeBg
    {
        get => _statusBadgeBg;
        set => SetField(ref _statusBadgeBg, value);
    }

    public Brush StatusBadgeFg
    {
        get => _statusBadgeFg;
        set => SetField(ref _statusBadgeFg, value);
    }

    public IdentificationMethod ActiveMethod
    {
        get => _activeMethod;
        set
        {
            if (SetField(ref _activeMethod, value))
            {
                ActiveMethodName = GetMethodDisplayName(value);
                OnPropertyChanged(nameof(IsFaceMethodActive));
                OnPropertyChanged(nameof(IsFingerprintMethodActive));
                OnPropertyChanged(nameof(IsRfidMethodActive));
                OnPropertyChanged(nameof(IsQrMethodActive));
                OnPropertyChanged(nameof(IsManualMethodActive));
            }
        }
    }

    public string ActiveMethodName
    {
        get => _activeMethodName;
        set => SetField(ref _activeMethodName, value);
    }

    public bool IsFaceMethodActive => ActiveMethod == IdentificationMethod.FaceRecognition;
    public bool IsFingerprintMethodActive => ActiveMethod == IdentificationMethod.Fingerprint;
    public bool IsRfidMethodActive => ActiveMethod == IdentificationMethod.RfidCard;
    public bool IsQrMethodActive => ActiveMethod == IdentificationMethod.QrCode;
    public bool IsManualMethodActive => ActiveMethod == IdentificationMethod.Manual;

    public float ConfidenceScore
    {
        get => _confidenceScore;
        set => SetField(ref _confidenceScore, value);
    }

    public string ConfidenceText
    {
        get => _confidenceText;
        set => SetField(ref _confidenceText, value);
    }

    public string CheckInTimeText
    {
        get => _checkInTimeText;
        set => SetField(ref _checkInTimeText, value);
    }

    public string CurrentTimeText
    {
        get => _currentTimeText;
        set => SetField(ref _currentTimeText, value);
    }

    public string CurrentDateText
    {
        get => _currentDateText;
        set => SetField(ref _currentDateText, value);
    }

    public int CountdownSeconds
    {
        get => _countdownSeconds;
        set => SetField(ref _countdownSeconds, value);
    }

    public double CountdownProgress
    {
        get => _countdownProgress;
        set => SetField(ref _countdownProgress, value);
    }

    public string DisplayScreenInfo
    {
        get => _displayScreenInfo;
        set => SetField(ref _displayScreenInfo, value);
    }

    // ==========================================
    // EVENT HANDLERS & LOGIC
    // ==========================================

    private void OnCameraFrameCaptured(object? sender, Mat frame)
    {
        if (frame.Empty()) return;

        if (Interlocked.CompareExchange(ref _isRenderingUi, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var bitmap = BitmapSourceConverter.ToBitmapSource(frame);
            bitmap.Freeze();

            Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
            {
                try
                {
                    CameraFeed = bitmap;
                }
                finally
                {
                    Interlocked.Exchange(ref _isRenderingUi, 0);
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CustomerFacingViewModel] Frame render error: {ex.Message}");
            Interlocked.Exchange(ref _isRenderingUi, 0);
        }
    }
    private void OnUnidentifiedDetected(object? sender, string identifier)
    {
        if (!IsMemberIdentified)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                MemberName = $"Mã '{identifier}' chưa đăng ký";
                StatusBadgeText = "⚠️ ĐỊNH DANH CHƯA ĐĂNG KÝ";
                StatusBadgeBg = new SolidColorBrush(Color.FromRgb(254, 243, 199));
                StatusBadgeFg = new SolidColorBrush(Color.FromRgb(180, 83, 9));
            });
        }
    }

    private void OnMemberIdentified(object? sender, MemberIdentifiedEventArgs e)
    {
        HandleMemberIdentified(e.Member, e.Confidence, e.Method);
    }

    /// <summary>
    /// Xử lý hiển thị khi hội viên được nhận diện thành công qua bất kỳ phương thức nào.
    /// </summary>
    public void HandleMemberIdentified(Member member, float confidence, IdentificationMethod method = IdentificationMethod.FaceRecognition)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            CurrentMember = member;
            MemberName = member.FullName;
            MemberPhone = string.IsNullOrWhiteSpace(member.PhoneNumber) ? "--" : member.PhoneNumber;
            MemberAvatarPath = member.AvatarPath;
            PackageName = member.Package?.Name ?? "Gói tập hội viên tiêu chuẩn";

            bool expired = member.IsExpired();
            IsExpired = expired;

            int remaining = member.RemainingDays();
            RemainingDays = remaining;
            RemainingDaysText = expired ? "Đã hết hạn" : $"Còn {remaining} ngày tập";
            ExpiryDateText = $"Hạn sử dụng: {member.EndDate:dd/MM/yyyy}";

            ActiveMethod = method;
            ConfidenceScore = confidence;
            ConfidenceText = $"{confidence * 100:F1}%";
            CheckInTimeText = DateTime.Now.ToString("HH:mm:ss • dd/MM/yyyy");

            if (expired)
            {
                StatusBadgeText = "❌ THẺ ĐÃ HẾT HẠN — VUI LÒNG LIÊN HỆ LỄ TÂN GIA HẠN";
                StatusBadgeBg = new SolidColorBrush(Color.FromRgb(127, 29, 29)); // #7F1D1D
                StatusBadgeFg = new SolidColorBrush(Color.FromRgb(254, 202, 202));
            }
            else
            {
                StatusBadgeText = "✅ ĐIỂM DANH HỢP LỆ — CHÀO MỪNG BẠN ĐẾN VỚI PHÚC GYM!";
                StatusBadgeBg = new SolidColorBrush(Color.FromRgb(6, 78, 59));   // #064E3B
                StatusBadgeFg = new SolidColorBrush(Color.FromRgb(167, 243, 208));
            }

            IsMemberIdentified = true;
            CountdownSeconds = 5;
            CountdownProgress = 100;
            _autoResetTimer.Stop();
            _autoResetTimer.Start();
        });
    }

    /// <summary>
    /// Xử lý kết quả nhận diện từ một IIdentificationProvider (Thẻ RFID, Vân tay, QR, SĐT).
    /// </summary>
    public void HandleIdentificationResult(IdentificationResult result, Member? member = null)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            ActiveMethod = result.Method;
            ConfidenceScore = result.ConfidenceScore;
            ConfidenceText = $"{result.ConfidenceScore * 100:F1}%";
            CheckInTimeText = result.Timestamp.ToLocalTime().ToString("HH:mm:ss • dd/MM/yyyy");

            if (result.IsSuccess)
            {
                if (member != null)
                {
                    HandleMemberIdentified(member, result.ConfidenceScore, result.Method);
                }
                else
                {
                    MemberName = result.DisplayName ?? $"Hội viên #{result.Identifier}";
                    MemberPhone = result.Identifier;
                    PackageName = "Gói tập đang hoạt động";
                    RemainingDaysText = "Hợp lệ";
                    ExpiryDateText = "Đã xác thực thành công";
                    IsExpired = false;

                    StatusBadgeText = $"✅ XÁC THỰC THÀNH CÔNG ({GetMethodDisplayName(result.Method)})";
                    StatusBadgeBg = new SolidColorBrush(Color.FromRgb(6, 78, 59));
                    StatusBadgeFg = new SolidColorBrush(Color.FromRgb(167, 243, 208));

                    IsMemberIdentified = true;
                    CountdownSeconds = 5;
                    CountdownProgress = 100;
                    _autoResetTimer.Stop();
                    _autoResetTimer.Start();
                }
            }
            else
            {
                MemberName = "Không thể nhận diện";
                MemberPhone = "--";
                StatusBadgeText = $"⚠️ {result.ErrorMessage ?? "Xác thực không hợp lệ"}";
                StatusBadgeBg = new SolidColorBrush(Color.FromRgb(127, 29, 29));
                StatusBadgeFg = new SolidColorBrush(Color.FromRgb(254, 202, 202));

                IsMemberIdentified = true;
                CountdownSeconds = 4;
                CountdownProgress = 100;
                _autoResetTimer.Stop();
                _autoResetTimer.Start();
            }
        });
    }

    private void OnAutoResetTick(object? sender, EventArgs e)
    {
        if (CountdownSeconds > 1)
        {
            CountdownSeconds--;
            CountdownProgress = (CountdownSeconds / 5.0) * 100.0;
        }
        else
        {
            _autoResetTimer.Stop();
            ResetToIdle();
        }
    }

    /// <summary>
    /// Đưa màn hình về trạng thái chờ tiếp nhận hội viên kế tiếp.
    /// </summary>
    public void ResetToIdle()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            IsMemberIdentified = false;
            CurrentMember = null;
            MemberName = string.Empty;
            MemberPhone = string.Empty;
            MemberAvatarPath = null;
            PackageName = string.Empty;
            RemainingDays = 0;
            RemainingDaysText = string.Empty;
            ExpiryDateText = string.Empty;
            IsExpired = false;
            ConfidenceText = "--";

            StatusBadgeText = "HỆ THỐNG SẴN SÀNG";
            StatusBadgeBg = new SolidColorBrush(Color.FromRgb(30, 41, 59));
            StatusBadgeFg = new SolidColorBrush(Color.FromRgb(255, 107, 0));
        });
    }

    public static string GetMethodDisplayName(IdentificationMethod method) => method switch
    {
        IdentificationMethod.FaceRecognition => "Nhận diện khuôn mặt (FaceID ArcFace)",
        IdentificationMethod.Fingerprint => "Máy quét vân tay sinh trắc học",
        IdentificationMethod.RfidCard => "Thẻ từ RFID / NFC (Mifare 13.56MHz)",
        IdentificationMethod.QrCode => "Mã QR thành viên trên ứng dụng",
        IdentificationMethod.Manual => "Điểm danh thủ công qua SĐT",
        _ => "Không xác định"
    };

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
        _disposed = true;

        _clockTimer.Stop();
        _autoResetTimer.Stop();

        if (_cameraService != null)
        {
            _cameraService.FrameCaptured -= OnCameraFrameCaptured;
        }

        if (_coordinator != null)
        {
            _coordinator.MemberIdentified -= OnMemberIdentified;
            _coordinator.UnidentifiedDetected -= OnUnidentifiedDetected;
        }
    }
}
