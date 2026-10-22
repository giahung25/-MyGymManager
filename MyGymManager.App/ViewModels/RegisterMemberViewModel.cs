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
using MyGymManager.Core.Interfaces;
using MyGymManager.Core.Models;
using MyGymManager.Data;
using MyGymManager.Services;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

/// <summary>
/// Định nghĩa các góc mặt mục tiêu thu thập Face ID.
/// </summary>
public enum FacePose
{
    Center = 0,    // Bước 1: Chính diện (Ratio: 0.42 - 0.58)
    TurnLeft = 1,  // Bước 2: Nghiêng trái (Ratio: < 0.38)
    TurnRight = 2  // Bước 3: Nghiêng phải (Ratio: > 0.62)
}

/// <summary>
/// ViewModel Đăng ký Hội viên mới với BỘ HƯỚNG DẪN XOAY GÓC MẶT (Visual Angle Guide):
/// - Đo góc yaw qua 5 facial landmarks (khoảng cách mũi đến mắt trái và mắt phải).
/// - Quy trình 3 bước chuẩn: Bước 1 (Chính diện) -> Bước 2 (Nghiêng trái) -> Bước 3 (Nghiêng phải).
/// - Chỉ khi xoay đúng góc target thì mới đếm giờ ổn định, phát beep và chụp.
/// - Đồng bộ vị trí con trỏ Pose Indicator Bar real-time (0.0 -> 1.0).
/// </summary>
public class RegisterMemberViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ICameraService _cameraService;
    private readonly IFaceEngine _faceEngine;
    private readonly AppDbContext _dbContext;
    private readonly AttendanceCoordinator _attendanceCoordinator;
    private readonly AudioNotificationService _audioService;

    private string _fullName = string.Empty;
    private string _phoneNumber = string.Empty;
    private Package? _selectedPackage;
    private BitmapSource? _cameraPreview;
    private BitmapSource? _capturedFaceImage;
    private string _statusMessage = "Vui lòng điền thông tin hội viên và bấm 'Bắt đầu quét Face ID'";
    private Brush _statusColor = new SolidColorBrush(Color.FromRgb(107, 114, 128)); // Xám
    private string _errorMessage = string.Empty;
    private bool _isBusy = false;
    private bool _isCapturingActive = false;
    private int _isRenderingUi = 0;
    private int _isProcessingAutoCapture = 0;
    private bool _disposed = false;
    private FaceBox? _lastDetectedFace;
    private DateTime _lastFaceDetectedTime = DateTime.MinValue;

    // Visual Angle Guide & Pose Estimation Properties
    private double _currentPoseRatio = 0.5;
    private double _poseIndicatorPointerLeft = 143;
    private FacePose _currentTargetPose = FacePose.Center;
    private string _currentTargetAngleText = "Góc 1/3: Nhìn thẳng (Chính diện)";
    private bool _isAngleMatched = false;
    private Brush _angleIndicatorColor = new SolidColorBrush(Color.FromRgb(255, 107, 0)); // Cam
    private DateTime _poseMatchStartTime = DateTime.MinValue;

    // Auto-capture progress (0/3 -> 3/3)
    private double _progressValue = 0;
    private string _progressText = "0/3 mẫu";
    private readonly List<float[]> _capturedVectors = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<bool>? RequestClose;

    public ObservableCollection<Package> Packages { get; } = new();

    public string FullName
    {
        get => _fullName;
        set
        {
            if (SetField(ref _fullName, value))
            {
                OnPropertyChanged(nameof(CanSave));
            }
        }
    }

    public string PhoneNumber
    {
        get => _phoneNumber;
        set
        {
            if (SetField(ref _phoneNumber, value))
            {
                OnPropertyChanged(nameof(CanSave));
            }
        }
    }

    public Package? SelectedPackage
    {
        get => _selectedPackage;
        set
        {
            if (SetField(ref _selectedPackage, value))
            {
                OnPropertyChanged(nameof(CanSave));
            }
        }
    }

    public BitmapSource? CameraPreview
    {
        get => _cameraPreview;
        set => SetField(ref _cameraPreview, value);
    }

    public BitmapSource? CapturedFaceImage
    {
        get => _capturedFaceImage;
        set => SetField(ref _capturedFaceImage, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public Brush StatusColor
    {
        get => _statusColor;
        set => SetField(ref _statusColor, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetField(ref _errorMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetField(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanSave));
            }
        }
    }

    public bool IsCapturingActive
    {
        get => _isCapturingActive;
        set
        {
            if (SetField(ref _isCapturingActive, value))
            {
                OnPropertyChanged(nameof(StartCaptureButtonText));
            }
        }
    }

    public string StartCaptureButtonText => IsCapturingActive
        ? "🔄 Đang Quét... (Bấm để quét lại)"
        : "⚡ Bắt Đầu Quét Face ID";

    // Visual Angle Guide Properties
    public double CurrentPoseRatio
    {
        get => _currentPoseRatio;
        set => SetField(ref _currentPoseRatio, value);
    }

    public double PoseIndicatorPointerLeft
    {
        get => _poseIndicatorPointerLeft;
        set => SetField(ref _poseIndicatorPointerLeft, value);
    }

    public FacePose CurrentTargetPose
    {
        get => _currentTargetPose;
        set
        {
            if (SetField(ref _currentTargetPose, value))
            {
                OnPropertyChanged(nameof(IsTargetCenter));
                OnPropertyChanged(nameof(IsTargetLeft));
                OnPropertyChanged(nameof(IsTargetRight));
            }
        }
    }

    public bool IsTargetCenter => CurrentTargetPose == FacePose.Center;
    public bool IsTargetLeft => CurrentTargetPose == FacePose.TurnLeft;
    public bool IsTargetRight => CurrentTargetPose == FacePose.TurnRight;

    public string CurrentTargetAngleText
    {
        get => _currentTargetAngleText;
        set => SetField(ref _currentTargetAngleText, value);
    }

    public bool IsAngleMatched
    {
        get => _isAngleMatched;
        set => SetField(ref _isAngleMatched, value);
    }

    public Brush AngleIndicatorColor
    {
        get => _angleIndicatorColor;
        set => SetField(ref _angleIndicatorColor, value);
    }

    // ProgressBar Properties
    public double ProgressValue
    {
        get => _progressValue;
        set => SetField(ref _progressValue, value);
    }

    public string ProgressText
    {
        get => _progressText;
        set => SetField(ref _progressText, value);
    }

    public int CapturedAngleCount => _capturedVectors.Count;
    public bool HasFaceVector => _capturedVectors.Count > 0;
    public bool IsAutoCaptureComplete => _capturedVectors.Count >= 3;

    public bool CanSave =>
        !string.IsNullOrWhiteSpace(FullName) &&
        !string.IsNullOrWhiteSpace(PhoneNumber) &&
        SelectedPackage != null &&
        IsAutoCaptureComplete &&
        !IsBusy;

    public ICommand StartCaptureCommand { get; }
    public ICommand ResetCaptureCommand { get; }
    public ICommand SaveMemberCommand { get; }
    public ICommand CancelCommand { get; }

    public RegisterMemberViewModel(
        ICameraService cameraService,
        IFaceEngine faceEngine,
        AppDbContext dbContext,
        AttendanceCoordinator attendanceCoordinator,
        AudioNotificationService? audioService = null)
    {
        _cameraService = cameraService ?? throw new ArgumentNullException(nameof(cameraService));
        _faceEngine = faceEngine ?? throw new ArgumentNullException(nameof(faceEngine));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _attendanceCoordinator = attendanceCoordinator ?? throw new ArgumentNullException(nameof(attendanceCoordinator));
        _audioService = audioService ?? new AudioNotificationService();

        StartCaptureCommand = new RelayCommand(async () => await OnStartCaptureAsync());
        ResetCaptureCommand = new RelayCommand(OnResetCapture);
        SaveMemberCommand = new RelayCommand(async () => await OnSaveMemberAsync());
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));

        _cameraService.FrameCaptured += OnFrameCaptured;
        _ = LoadPackagesAsync();
    }

    /// <summary>
    /// Nạp danh sách gói tập từ SQLite để người dùng lựa chọn.
    /// </summary>
    private async Task LoadPackagesAsync()
    {
        try
        {
            var packages = await _dbContext.Packages.AsNoTracking().OrderBy(p => p.DurationDays).ToListAsync();
            Application.Current?.Dispatcher.Invoke(() =>
            {
                Packages.Clear();
                foreach (var p in packages)
                {
                    Packages.Add(p);
                }

                if (Packages.Count > 0)
                {
                    SelectedPackage = Packages[0];
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RegisterMemberViewModel] Lỗi load packages: {ex.Message}");
        }
    }

    /// <summary>
    /// Nhận frame từ camera, render preview kèm vẽ Bounding Box Cam trực tiếp lên Mat.
    /// </summary>
    private void OnFrameCaptured(object? sender, Mat frame)
    {
        if (frame.Empty()) return;

        // 1. Render Video Preview lên UI Thread (chống tràn hàng đợi Dispatcher)
        if (Interlocked.CompareExchange(ref _isRenderingUi, 1, 0) == 0)
        {
            try
            {
                // VẼ TRỰC TIẾP BOUNDING BOX LÊN MAT BẰNG OPENCV:
                if (_lastDetectedFace != null && (DateTime.UtcNow - _lastFaceDetectedTime).TotalMilliseconds < 600)
                {
                    var rect = _lastDetectedFace.ToRect().Intersect(new OpenCvSharp.Rect(0, 0, frame.Width, frame.Height));
                    if (rect.Width > 0 && rect.Height > 0)
                    {
                        var orangeColor = new Scalar(0, 107, 255);
                        Cv2.Rectangle(frame, rect, orangeColor, 2);

                        string label = $"FACE DETECTED ({_lastDetectedFace.Score * 100:F0}%)";
                        var textPos = new OpenCvSharp.Point(rect.X, Math.Max(24, rect.Y - 8));
                        Cv2.PutText(frame, label, textPos, HersheyFonts.HersheySimplex, 0.6, orangeColor, 2, LineTypes.AntiAlias);
                    }
                }

                var bitmap = BitmapSourceConverter.ToBitmapSource(frame);
                bitmap.Freeze();

                Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                {
                    try
                    {
                        CameraPreview = bitmap;
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _isRenderingUi, 0);
                    }
                });
            }
            catch
            {
                Interlocked.Exchange(ref _isRenderingUi, 0);
            }
        }

        // 2. TỰ ĐỘNG THU THẬP FACE ID THEO GÓC MẶT (Visual Angle Guide Engine)
        if (IsCapturingActive && !IsAutoCaptureComplete && Interlocked.CompareExchange(ref _isProcessingAutoCapture, 1, 0) == 0)
        {
            var aiFrame = frame.Clone();
            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessAutoCaptureAsync(aiFrame);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[RegisterMemberViewModel] Lỗi auto capture: {ex.Message}");
                }
                finally
                {
                    aiFrame.Dispose();
                    Interlocked.Exchange(ref _isProcessingAutoCapture, 0);
                }
            });
        }
    }

    /// <summary>
    /// Thuật toán ước lượng góc Yaw qua 5 Landmarks và kiểm tra xoay đúng góc mục tiêu:
    /// - Landmarks[0]: Mắt trái (x0, y0)
    /// - Landmarks[1]: Mắt phải (x1, y1)
    /// - Landmarks[2]: Mũi (x2, y2)
    /// </summary>
    public static float CalculateYawRatio(Point2f[]? landmarks)
    {
        if (landmarks == null || landmarks.Length < 3)
            return 0.5f;

        var leftEye = landmarks[0];
        var rightEye = landmarks[1];
        var nose = landmarks[2];

        float distLeft = Math.Abs(nose.X - leftEye.X);
        float distRight = Math.Abs(nose.X - rightEye.X);
        float total = distLeft + distRight;

        if (float.IsNaN(total) || float.IsInfinity(total) || total <= 0.0001f)
            return 0.5f;

        float ratio = distLeft / total;
        return float.IsNaN(ratio) || float.IsInfinity(ratio) ? 0.5f : Math.Clamp(ratio, 0.0f, 1.0f);
    }

    /// <summary>
    /// Kiểm tra tỉ lệ góc yaw hiện tại có khớp với góc mục tiêu của bước đang quét hay không.
    /// </summary>
    public static bool CheckIfPoseMatchesTarget(float ratio, FacePose targetPose)
    {
        return targetPose switch
        {
            FacePose.Center => ratio >= 0.42f && ratio <= 0.58f,
            FacePose.TurnLeft => ratio < 0.38f,
            FacePose.TurnRight => ratio > 0.62f,
            _ => false
        };
    }

    /// <summary>
    /// Thuật toán kiểm tra góc mặt, đếm thời gian giữ góc và tự động chụp khi đúng góc target.
    /// </summary>
    private async Task ProcessAutoCaptureAsync(Mat frame)
    {
        // 1. Chạy SCRFD phát hiện khuôn mặt và 5 landmarks
        var faces = await _faceEngine.DetectFacesAsync(frame);

        if (faces == null || faces.Count == 0)
        {
            _lastDetectedFace = null;
            _poseMatchStartTime = DateTime.MinValue;

            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (IsCapturingActive && !IsAutoCaptureComplete)
                {
                    StatusMessage = "Đang tìm khuôn mặt... Vui lòng nhìn vào camera.";
                    StatusColor = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                    IsAngleMatched = false;
                    AngleIndicatorColor = new SolidColorBrush(Color.FromRgb(255, 107, 0));
                }
            });
            return;
        }

        // Lấy khuôn mặt lớn nhất gần camera nhất
        var bestFace = faces.OrderByDescending(f => f.Width * f.Height).First();
        _lastDetectedFace = bestFace;
        _lastFaceDetectedTime = DateTime.UtcNow;

        // 2. Tính toán tỉ lệ góc xoay đầu (Yaw Ratio)
        float yawRatio = CalculateYawRatio(bestFace.Landmarks);
        bool isAngleMatched = CheckIfPoseMatchesTarget(yawRatio, CurrentTargetPose);

        // Cập nhật vị trí con trỏ Pose Indicator Bar real-time (thanh bar rộng 300px, pointer rộng 26px -> travel 274px)
        double pointerPos = Math.Clamp(yawRatio * 274.0, 0, 274.0);

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            CurrentPoseRatio = yawRatio;
            PoseIndicatorPointerLeft = pointerPos;
            IsAngleMatched = isAngleMatched;
            AngleIndicatorColor = isAngleMatched
                ? new SolidColorBrush(Color.FromRgb(16, 185, 129)) // Xanh lá #10B981
                : new SolidColorBrush(Color.FromRgb(255, 107, 0)); // Cam #FF6B00
        });

        // 3. Kiểm tra tiêu chuẩn chất lượng (Score > 0.70 & Width > 80px)
        bool isQualified = bestFace.Score > 0.70f && bestFace.Width > 80f && bestFace.Height > 80f;
        if (!isQualified)
        {
            _poseMatchStartTime = DateTime.MinValue;
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                StatusMessage = "Khuôn mặt chưa rõ nét hoặc ở quá xa. Vui lòng tiến lại gần camera hơn.";
                StatusColor = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            });
            return;
        }

        // 4. CHỈ KHI NGƯỜI DÙNG QUAY ĐÚNG GÓC TARGET MỚI ĐẾM GIỜ VÀ CHỤP
        if (!isAngleMatched)
        {
            _poseMatchStartTime = DateTime.MinValue; // reset bộ đếm giữ góc
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                StatusMessage = $"Hãy xoay mặt theo hướng dẫn: {CurrentTargetAngleText}";
                StatusColor = new SolidColorBrush(Color.FromRgb(255, 107, 0));
            });
            return;
        }

        // Đã đúng góc target -> Đếm thời gian giữ góc ổn định (tối thiểu 600ms)
        if (_poseMatchStartTime == DateTime.MinValue)
        {
            _poseMatchStartTime = DateTime.UtcNow;
        }

        var holdDurationMs = (DateTime.UtcNow - _poseMatchStartTime).TotalMilliseconds;
        if (holdDurationMs < 600)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                StatusMessage = $"🎯 ĐÚNG GÓC! Hãy giữ yên khuôn mặt... ({(int)Math.Ceiling((600 - holdDurationMs) / 100.0)})";
                StatusColor = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            });
            return;
        }

        // Đã giữ đúng góc ổn định đủ 600ms -> TIẾN HÀNH TRÍCH XUẤT VÀ CHỤP MẪU!
        _poseMatchStartTime = DateTime.MinValue;

        var rect = bestFace.ToRect().Intersect(new OpenCvSharp.Rect(0, 0, frame.Width, frame.Height));
        if (rect.Width < 40 || rect.Height < 40) return;

        using var faceCrop = new Mat(frame, rect);

        var vector = await _faceEngine.ExtractEmbeddingAsync(faceCrop);
        if (vector == null || vector.Length != 512) return;

        var faceBitmap = BitmapSourceConverter.ToBitmapSource(faceCrop);
        faceBitmap.Freeze();

        _capturedVectors.Add(vector);
        int currentCount = _capturedVectors.Count;

        // Phát âm thanh bip theo thứ tự mẫu 1, 2, 3
        _audioService.PlayCaptureBeep(currentCount);

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            CapturedFaceImage = faceBitmap;
            ProgressValue = (currentCount / 3.0) * 100.0;
            ProgressText = $"{currentCount}/3 mẫu";

            OnPropertyChanged(nameof(CapturedAngleCount));
            OnPropertyChanged(nameof(HasFaceVector));

            if (currentCount == 1)
            {
                CurrentTargetPose = FacePose.TurnLeft;
                CurrentTargetAngleText = "Góc 2/3: Xoay nhẹ sang TRÁI (👈)";
                StatusMessage = "📸 Chụp xong góc Chính diện! Giờ hãy xoay nhẹ mặt sang TRÁI.";
                StatusColor = new SolidColorBrush(Color.FromRgb(255, 107, 0));
            }
            else if (currentCount == 2)
            {
                CurrentTargetPose = FacePose.TurnRight;
                CurrentTargetAngleText = "Góc 3/3: Xoay nhẹ sang PHẢI (👉)";
                StatusMessage = "📸 Chụp xong góc Trái! Giờ hãy xoay nhẹ mặt sang PHẢI.";
                StatusColor = new SolidColorBrush(Color.FromRgb(255, 107, 0));
            }
            else if (currentCount >= 3)
            {
                IsCapturingActive = false; // Tắt chế độ tự động chụp
                CurrentTargetAngleText = "✅ ĐÃ THU THẬP ĐỦ 3 GÓC MẶT!";
                StatusMessage = "✅ ĐÃ THU THẬP ĐỦ 3 GÓC MẶT (Thẳng - Trái - Phải)! Nhấn 'Lưu Hội Viên' để hoàn tất.";
                StatusColor = new SolidColorBrush(Color.FromRgb(16, 185, 129));

                OnPropertyChanged(nameof(IsAutoCaptureComplete));
                OnPropertyChanged(nameof(CanSave));
            }
        });
    }

    /// <summary>
    /// Xóa toàn bộ mẫu đã chụp và quét lại từ đầu.
    /// </summary>
    private void OnResetCapture()
    {
        _capturedVectors.Clear();
        CapturedFaceImage = null;
        ProgressValue = 0;
        ProgressText = "0/3 mẫu";
        _poseMatchStartTime = DateTime.MinValue;
        CurrentTargetPose = FacePose.Center;
        CurrentTargetAngleText = "Góc 1/3: Nhìn thẳng (Chính diện)";
        CurrentPoseRatio = 0.5;
        PoseIndicatorPointerLeft = 143;
        IsAngleMatched = false;
        AngleIndicatorColor = new SolidColorBrush(Color.FromRgb(255, 107, 0));
        StatusMessage = "Đã đặt lại. Bấm 'Bắt đầu quét Face ID' để quét lại...";
        StatusColor = new SolidColorBrush(Color.FromRgb(107, 114, 128));

        OnPropertyChanged(nameof(CapturedAngleCount));
        OnPropertyChanged(nameof(HasFaceVector));
        OnPropertyChanged(nameof(IsAutoCaptureComplete));
        OnPropertyChanged(nameof(CanSave));
    }

    /// <summary>
    /// Kiểm tra tính hợp lệ thông tin hội viên và kích hoạt quy trình quét tự động Face ID.
    /// </summary>
    private async Task OnStartCaptureAsync()
    {
        ErrorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(FullName))
        {
            ErrorMessage = "Vui lòng nhập Họ và Tên hội viên trước khi quét Face ID.";
            return;
        }

        if (string.IsNullOrWhiteSpace(PhoneNumber))
        {
            ErrorMessage = "Vui lòng nhập Số điện thoại hội viên trước khi quét Face ID.";
            return;
        }

        if (SelectedPackage == null)
        {
            ErrorMessage = "Vui lòng chọn Gói tập cho hội viên.";
            return;
        }

        // Kiểm tra số điện thoại có bị trùng không
        var isTaken = await _dbContext.Members.AnyAsync(m => m.PhoneNumber == PhoneNumber.Trim());
        if (isTaken)
        {
            ErrorMessage = "Số điện thoại này đã được đăng ký cho một hội viên khác.";
            return;
        }

        // Đặt lại số mẫu và bắt đầu từ Bước 1: Chính diện
        _capturedVectors.Clear();
        CapturedFaceImage = null;
        ProgressValue = 0;
        ProgressText = "0/3 mẫu";
        _poseMatchStartTime = DateTime.MinValue;
        CurrentTargetPose = FacePose.Center;
        CurrentTargetAngleText = "Góc 1/3: Nhìn thẳng (Chính diện)";
        CurrentPoseRatio = 0.5;
        PoseIndicatorPointerLeft = 143;
        IsAngleMatched = false;
        AngleIndicatorColor = new SolidColorBrush(Color.FromRgb(255, 107, 0));

        IsCapturingActive = true;
        StatusMessage = "BƯỚC 1/3: Hãy nhìn thẳng vào camera để thu thập góc chính diện.";
        StatusColor = new SolidColorBrush(Color.FromRgb(255, 107, 0));

        OnPropertyChanged(nameof(CapturedAngleCount));
        OnPropertyChanged(nameof(HasFaceVector));
        OnPropertyChanged(nameof(IsAutoCaptureComplete));
        OnPropertyChanged(nameof(CanSave));
    }

    /// <summary>
    /// Lưu thông tin hội viên mới vào SQLite và cập nhật ngay vào RAM cache.
    /// </summary>
    private async Task OnSaveMemberAsync()
    {
        if (!CanSave) return;

        var name = FullName.Trim();
        var phone = PhoneNumber.Trim();

        IsBusy = true;
        ErrorMessage = string.Empty;

        try
        {
            var isPhoneTaken = await _dbContext.Members.AnyAsync(m => m.PhoneNumber == phone);
            if (isPhoneTaken)
            {
                ErrorMessage = "Số điện thoại này đã được đăng ký cho một hội viên khác.";
                IsBusy = false;
                return;
            }

            var member = Member.Create(
                fullName: name,
                phoneNumber: phone,
                packageDays: SelectedPackage!.DurationDays,
                initialFaceVector: _capturedVectors[0],
                packageId: SelectedPackage.Id);

            for (int i = 1; i < _capturedVectors.Count; i++)
            {
                member.AddFaceVector(_capturedVectors[i]);
            }

            await _dbContext.Members.AddAsync(member);
            await _dbContext.SaveChangesAsync();

            _attendanceCoordinator.UpdateMemberInCache(member);
            RequestClose?.Invoke(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Lỗi khi lưu hội viên vào CSDL: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
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

        if (_cameraService != null)
        {
            _cameraService.FrameCaptured -= OnFrameCaptured;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
