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
using MyGymManager.Data;
using MyGymManager.Services;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

/// <summary>
/// ViewModel cho cửa sổ Đăng ký Hội viên mới & Chụp ảnh Face ID (Multi-Template).
/// - Nhập thông tin: Họ và tên, Số điện thoại, Gói tập.
/// - Chụp ảnh khuôn mặt trực tiếp từ Camera, chạy SCRFD + ArcFace để trích xuất vector 512 chiều.
/// - Lưu thực thể Member vào SQLite (phuc_gym.db) và đồng bộ ngay lập tức vào RAM Cache của AttendanceCoordinator.
/// </summary>
public class RegisterMemberViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ICameraService _cameraService;
    private readonly IFaceEngine _faceEngine;
    private readonly AppDbContext _dbContext;
    private readonly AttendanceCoordinator _attendanceCoordinator;

    private string _fullName = string.Empty;
    private string _phoneNumber = string.Empty;
    private Package? _selectedPackage;
    private BitmapSource? _cameraPreview;
    private BitmapSource? _capturedFaceImage;
    private string _statusMessage = "Hướng mặt về camera và nhấn 'Chụp khuôn mặt'";
    private Brush _statusColor = new SolidColorBrush(Color.FromRgb(107, 114, 128)); // Xám
    private string _errorMessage = string.Empty;
    private bool _isBusy = false;
    private int _isRenderingUi = 0;
    private bool _disposed = false;

    private readonly List<float[]> _capturedVectors = new();
    private Mat? _latestFrame;
    private readonly object _frameLock = new();

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

    public int CapturedAngleCount => _capturedVectors.Count;
    public bool HasFaceVector => _capturedVectors.Count > 0;

    public bool CanSave =>
        !string.IsNullOrWhiteSpace(FullName) &&
        !string.IsNullOrWhiteSpace(PhoneNumber) &&
        SelectedPackage != null &&
        HasFaceVector &&
        !IsBusy;

    public ICommand CaptureFaceCommand { get; }
    public ICommand SaveMemberCommand { get; }
    public ICommand CancelCommand { get; }

    public RegisterMemberViewModel(
        ICameraService cameraService,
        IFaceEngine faceEngine,
        AppDbContext dbContext,
        AttendanceCoordinator attendanceCoordinator)
    {
        _cameraService = cameraService ?? throw new ArgumentNullException(nameof(cameraService));
        _faceEngine = faceEngine ?? throw new ArgumentNullException(nameof(faceEngine));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _attendanceCoordinator = attendanceCoordinator ?? throw new ArgumentNullException(nameof(attendanceCoordinator));

        CaptureFaceCommand = new RelayCommand(async () => await OnCaptureFaceAsync());
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
    /// Nhận frame trực tiếp từ camera và cập nhật khung Preview.
    /// </summary>
    private void OnFrameCaptured(object? sender, Mat frame)
    {
        if (frame.Empty()) return;

        // Lưu bản frame mới nhất cho tác vụ chụp ảnh
        lock (_frameLock)
        {
            _latestFrame?.Dispose();
            _latestFrame = frame.Clone();
        }

        // Chống tràn hàng đợi Dispatcher
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

    /// <summary>
    /// Chụp khuôn mặt từ frame hiện tại và trích xuất vector 512 chiều qua SCRFD + ArcFace.
    /// </summary>
    private async Task OnCaptureFaceAsync()
    {
        Mat? frameToProcess = null;
        lock (_frameLock)
        {
            if (_latestFrame != null && !_latestFrame.Empty())
            {
                frameToProcess = _latestFrame.Clone();
            }
        }

        if (frameToProcess == null)
        {
            StatusMessage = "Chưa nhận được tín hiệu hình ảnh từ camera.";
            StatusColor = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Đỏ
            return;
        }

        IsBusy = true;
        StatusMessage = "Đang nhận diện và trích xuất vector khuôn mặt...";
        StatusColor = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Vàng cam
        ErrorMessage = string.Empty;

        try
        {
            using (frameToProcess)
            {
                // 1. Phát hiện khuôn mặt bằng SCRFD
                var faces = await _faceEngine.DetectFacesAsync(frameToProcess);
                if (faces == null || faces.Count == 0)
                {
                    StatusMessage = "Không phát hiện khuôn mặt! Vui lòng hướng thẳng mặt vào camera.";
                    StatusColor = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                    return;
                }

                // Chọn khuôn mặt có diện tích lớn nhất (gần camera nhất)
                var bestFace = faces.OrderByDescending(f => f.Width * f.Height).First();

                // Cắt vùng khuôn mặt an toàn
                var cropRect = bestFace.ToRect().Intersect(new OpenCvSharp.Rect(0, 0, frameToProcess.Width, frameToProcess.Height));
                if (cropRect.Width < 40 || cropRect.Height < 40)
                {
                    StatusMessage = "Khuôn mặt quá xa hoặc quá nhỏ. Vui lòng tiến lại gần camera hơn.";
                    StatusColor = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                    return;
                }

                using var faceCrop = new Mat(frameToProcess, cropRect);

                // 2. Trích xuất 512-d ArcFace vector
                var vector = await _faceEngine.ExtractEmbeddingAsync(faceCrop);
                if (vector == null || vector.Length != 512)
                {
                    StatusMessage = "Không thể trích xuất vector đặc trưng khuôn mặt.";
                    StatusColor = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                    return;
                }

                // Chuyển đổi ảnh crop hiển thị lên UI
                var faceBitmap = BitmapSourceConverter.ToBitmapSource(faceCrop);
                faceBitmap.Freeze();
                CapturedFaceImage = faceBitmap;

                // Thêm vào danh sách templates (tối đa 5 templates)
                if (_capturedVectors.Count >= 5)
                {
                    _capturedVectors.RemoveAt(0);
                }
                _capturedVectors.Add(vector);

                OnPropertyChanged(nameof(CapturedAngleCount));
                OnPropertyChanged(nameof(HasFaceVector));
                OnPropertyChanged(nameof(CanSave));

                StatusMessage = $"Chụp thành công góc mặt {_capturedVectors.Count} (ArcFace 512-d)! Có thể chụp thêm góc khác hoặc Lưu.";
                StatusColor = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Xanh lá
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi khi trích xuất khuôn mặt: {ex.Message}";
            StatusColor = new SolidColorBrush(Color.FromRgb(239, 68, 68));
        }
        finally
        {
            IsBusy = false;
        }
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

            // Tạo thực thể Member theo Rich Domain Model
            var member = Member.Create(
                fullName: name,
                phoneNumber: phone,
                packageDays: SelectedPackage!.DurationDays,
                initialFaceVector: _capturedVectors[0],
                packageId: SelectedPackage.Id);

            // Bổ sung các góc mặt phụ nếu có chụp thêm (Multi-Template)
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

        lock (_frameLock)
        {
            _latestFrame?.Dispose();
            _latestFrame = null;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
