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
/// ViewModel Đăng ký Hội viên mới với tính năng TỰ ĐỘNG THU THẬP FACE ID (Auto Capture):
/// - Khi đưa mặt vào khung hình, hệ thống tự động phát hiện mặt đạt chuẩn (Confidence > 0.70, Width > 80px).
/// - Tự động trích xuất 3 mẫu vector 512-d ArcFace cách nhau 1 giây (0/3 -> 1/3 -> 2/3 -> 3/3).
/// - Hiển thị Bounding Box màu Cam bám theo mặt kèm nhãn 'FACE DETECTED'.
/// - Thanh ProgressBar tăng tiến 0% -> 33% -> 66% -> 100% kèm âm thanh bíp mỗi lần chụp.
/// - Đủ 3/3 mẫu thì kích hoạt nút Lưu Hội Viên.
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
    private bool _isCapturingActive = false;
    private bool _isBusy = false;
    private int _isRenderingUi = 0;
    private int _isProcessingAutoCapture = 0;
    private bool _disposed = false;
    private FaceBox? _lastDetectedFace;
    private DateTime _lastFaceDetectedTime = DateTime.MinValue;

    // Bounding Box Overlay
    private bool _isFaceDetected = false;
    private double _faceRectLeft = 0;
    private double _faceRectTop = 0;
    private double _faceRectWidth = 0;
    private double _faceRectHeight = 0;
    private string _faceBoxLabel = "FACE DETECTED";

    // Auto-capture progress (0/3 -> 3/3)
    private double _progressValue = 0;
    private string _progressText = "0/3 mẫu";
    private DateTime _lastSampleTime = DateTime.MinValue;
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


    // Bounding Box Properties
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

    public ICommand ResetCaptureCommand { get; }
    public ICommand SaveMemberCommand { get; }
    public ICommand StartCaptureCommand { get; }
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

        ResetCaptureCommand = new RelayCommand(OnResetCapture);
        StartCaptureCommand = new RelayCommand(async () => await OnStartCaptureAsync());
        SaveMemberCommand = new RelayCommand(async () => await OnSaveMemberAsync());
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));

        // Lắng nghe luồng camera
        _cameraService.FrameCaptured += OnFrameCaptured;

        // Tải danh sách gói tập
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
    /// Nhận frame từ camera, render preview và tự động phân tích phát hiện khuôn mặt để Auto Capture.
    /// </summary>
    private void OnFrameCaptured(object? sender, Mat frame)
    {
        if (frame.Empty()) return;

        // 1. Render Video Preview lên UI Thread (chống tràn hàng đợi)
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

        // 2. TỰ ĐỘNG THU THẬP FACE ID (Auto Capture Engine)
        // 2. TỰ ĐỘNG THU THẬP FACE ID (Chỉ chạy khi IsCapturingActive == true và chưa đủ 3 mẫu)
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
    /// Thuật toán kiểm tra tiêu chuẩn khuôn mặt và tự động chụp cách nhau 1 giây.
    /// </summary>
    private async Task ProcessAutoCaptureAsync(Mat frame)
    {
        // 1. Chạy SCRFD phát hiện khuôn mặt
        var faces = await _faceEngine.DetectFacesAsync(frame);

        if (faces == null || faces.Count == 0)
        {
            _lastDetectedFace = null;
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                IsFaceDetected = false;
                if (IsCapturingActive && !IsAutoCaptureComplete)
                {
                    StatusMessage = "Đang tìm khuôn mặt... Vui lòng nhìn vào camera.";
                    StatusColor = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                }
            });
            return;
        }

        // Lấy khuôn mặt lớn nhất gần camera nhất
        var bestFace = faces.OrderByDescending(f => f.Width * f.Height).First();
        _lastDetectedFace = bestFace;
        _lastFaceDetectedTime = DateTime.UtcNow;
        // Cập nhật vị trí Bounding Box màu Cam trên khung preview (Kích thước preview: ~450x260)
        const double containerW = 450.0;
        const double containerH = 260.0;
        double scaleX = containerW / 1280.0;
        double scaleY = containerH / 720.0;

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            FaceRectLeft = Math.Max(0, bestFace.X1 * scaleX);
            FaceRectTop = Math.Max(0, bestFace.Y1 * scaleY);
            FaceRectWidth = Math.Max(20, bestFace.Width * scaleX);
            FaceRectHeight = Math.Max(20, bestFace.Height * scaleY);
            FaceBoxLabel = $"FACE DETECTED ({bestFace.Score * 100:F0}%)";
            IsFaceDetected = true;
        });

        // 2. Kiểm tra tiêu chuẩn chất lượng khuôn mặt (Confidence > 0.70 & Width > 80px)
        bool isQualified = bestFace.Score > 0.70f && bestFace.Width > 80f && bestFace.Height > 80f;
        if (!isQualified)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                StatusMessage = "Khuôn mặt chưa rõ nét hoặc ở quá xa. Vui lòng tiến lại gần camera hơn.";
                StatusColor = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Vàng cam
            });
            return;
        }

        // 3. Kiểm tra khoảng cách thời gian giữa 2 lần chụp (tối thiểu 1 giây = 1000ms)
        var now = DateTime.UtcNow;
        if ((now - _lastSampleTime).TotalMilliseconds >= 1000 && _capturedVectors.Count < 3)
        {
            _lastSampleTime = now;

            // Cắt vùng khuôn mặt an toàn
            var rect = bestFace.ToRect().Intersect(new OpenCvSharp.Rect(0, 0, frame.Width, frame.Height));
            if (rect.Width < 40 || rect.Height < 40) return;

            using var faceCrop = new Mat(frame, rect);

            // Trích xuất vector 512 chiều qua ArcFace
            var vector = await _faceEngine.ExtractEmbeddingAsync(faceCrop);
            if (vector == null || vector.Length != 512) return;

            var faceBitmap = BitmapSourceConverter.ToBitmapSource(faceCrop);
            faceBitmap.Freeze();

            _capturedVectors.Add(vector);
            int currentCount = _capturedVectors.Count;

            // Phát âm thanh bip theo thứ tự mẫu 1, 2, 3
            _audioService.PlayCaptureBeep(currentCount);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                CapturedFaceImage = faceBitmap;
                ProgressValue = (currentCount / 3.0) * 100.0;
                ProgressText = $"{currentCount}/3 mẫu";

                OnPropertyChanged(nameof(CapturedAngleCount));
                OnPropertyChanged(nameof(HasFaceVector));

                if (currentCount < 3)
                {
                    StatusMessage = $"📸 Đã tự động chụp mẫu {currentCount}/3! Hãy giữ yên khuôn mặt hoặc nghiêng nhẹ...";
                    StatusColor = new SolidColorBrush(Color.FromRgb(255, 107, 0)); // Cam
                }
                else
                {
                    IsCapturingActive = false; // TẮT TỰ ĐỘNG CHỤP KHI ĐÃ ĐỦ 3 MẪU
                    StatusMessage = "✅ ĐÃ THU THẬP ĐỦ 3 MẪU FACE ID! Nhấn 'Lưu Hội Viên' để hoàn tất.";
                    StatusColor = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Xanh lá
                    OnPropertyChanged(nameof(IsAutoCaptureComplete));
                    OnPropertyChanged(nameof(CanSave));
                }
            });
        }
        else if (_capturedVectors.Count < 3)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                StatusMessage = $"Đang giữ khuôn mặt ổn định... Đã thu thập {_capturedVectors.Count}/3 mẫu";
            });
        }
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
        _lastSampleTime = DateTime.MinValue;
        StatusMessage = "Đã đặt lại. Đưa mặt vào khung camera để tự động quét lại từ đầu...";
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

        // Đặt lại số mẫu và kích hoạt chế độ tự động chụp
        _capturedVectors.Clear();
        CapturedFaceImage = null;
        ProgressValue = 0;
        ProgressText = "0/3 mẫu";
        _lastSampleTime = DateTime.MinValue;

        IsCapturingActive = true;
        StatusMessage = "Đang quét... Vui lòng nhìn thẳng vào camera để thu thập 3 mẫu Face ID.";
        StatusColor = new SolidColorBrush(Color.FromRgb(255, 107, 0)); // Cam

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
            // Kiểm tra trùng lặp số điện thoại
            var isPhoneTaken = await _dbContext.Members.AnyAsync(m => m.PhoneNumber == phone);
            if (isPhoneTaken)
            {
                ErrorMessage = "Số điện thoại này đã được đăng ký cho một hội viên khác.";
                IsBusy = false;
                return;
            }

            // Tạo thực thể Member theo Rich Domain Model với mẫu mặt đầu tiên
            var member = Member.Create(
                fullName: name,
                phoneNumber: phone,
                packageDays: SelectedPackage!.DurationDays,
                initialFaceVector: _capturedVectors[0],
                packageId: SelectedPackage.Id);

            // Bổ sung các góc mặt phụ (Multi-Template 3 góc)
            for (int i = 1; i < _capturedVectors.Count; i++)
            {
                member.AddFaceVector(_capturedVectors[i]);
            }

            // Ghi vào SQLite
            await _dbContext.Members.AddAsync(member);
            await _dbContext.SaveChangesAsync();

            // Cập nhật ngay lập tức vào RAM Cache để hội viên có thể quẹt mặt điểm danh tức thì
            _attendanceCoordinator.UpdateMemberInCache(member);

            // Đóng cửa sổ và báo thành công
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
