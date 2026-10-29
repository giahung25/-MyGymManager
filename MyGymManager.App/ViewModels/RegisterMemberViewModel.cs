namespace MyGymManager.App.ViewModels;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using MyGymManager.Core.Entities;
using MyGymManager.Data;
using MyGymManager.Services;

/// <summary>
/// ViewModel Đăng ký Hội viên mới Tinh gọn & Nhanh chóng (Front-Desk Quick Enrollment).
/// Thu thập 4 thông tin thiết yếu: Họ tên, Giới tính (Nam/Nữ), Số điện thoại, Gói tập.
/// Tùy chọn gán nhanh Mã Thẻ từ RFID / NFC hoặc Mã QR thành viên.
/// </summary>
public class RegisterMemberViewModel : INotifyPropertyChanged
{
    private readonly AppDbContext _dbContext;
    private readonly AttendanceCoordinator _attendanceCoordinator;
    private readonly AudioNotificationService? _audioService;

    private string _fullName = string.Empty;
    private string _phoneNumber = string.Empty;
    private string _gender = "Nam";
    private Package? _selectedPackage;
    private string? _cardUid;
    private string? _qrKey;
    private string _errorMessage = string.Empty;
    private bool _isBusy = false;

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

    public string Gender
    {
        get => _gender;
        set
        {
            if (SetField(ref _gender, value))
            {
                OnPropertyChanged(nameof(IsMale));
                OnPropertyChanged(nameof(IsFemale));
            }
        }
    }

    public bool IsMale
    {
        get => Gender == "Nam";
        set { if (value) Gender = "Nam"; }
    }

    public bool IsFemale
    {
        get => Gender == "Nữ";
        set { if (value) Gender = "Nữ"; }
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

    public string? CardUid
    {
        get => _cardUid;
        set => SetField(ref _cardUid, value);
    }

    public string? QrKey
    {
        get => _qrKey;
        set => SetField(ref _qrKey, value);
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

    public bool CanSave =>
        !string.IsNullOrWhiteSpace(FullName) &&
        !string.IsNullOrWhiteSpace(PhoneNumber) &&
        SelectedPackage != null &&
        !IsBusy;

    public ICommand SaveMemberCommand { get; }
    public ICommand CancelCommand { get; }

    public RegisterMemberViewModel(
        AppDbContext dbContext,
        AttendanceCoordinator attendanceCoordinator,
        AudioNotificationService? audioService = null,
        IEnumerable<Package>? initialPackages = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _attendanceCoordinator = attendanceCoordinator ?? throw new ArgumentNullException(nameof(attendanceCoordinator));
        _audioService = audioService;

        SaveMemberCommand = new RelayCommand(async () => await OnSaveMemberAsync());
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));

        if (initialPackages != null && initialPackages.Any())
        {
            var sorted = initialPackages.OrderBy(p => p.DurationDays).ToList();
            foreach (var p in sorted)
            {
                Packages.Add(p);
            }

            var p1Month = Packages.FirstOrDefault(p => p.DurationDays == 30 || p.Name.Contains("1 Tháng", StringComparison.OrdinalIgnoreCase))
                       ?? Packages[0];
            SelectedPackage = p1Month;
        }
        else
        {
            _ = LoadPackagesAsync();
        }
    }

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
                    var p1Month = Packages.FirstOrDefault(p => p.DurationDays == 30 || p.Name.Contains("1 Tháng", StringComparison.OrdinalIgnoreCase))
                               ?? Packages[0];
                    SelectedPackage = p1Month;
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RegisterMemberViewModel] Lỗi load packages: {ex.Message}");
        }
    }

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

            // Tạo thực thể Member tinh gọn
            var member = Member.Create(
                fullName: name,
                phoneNumber: phone,
                packageDays: SelectedPackage!.DurationDays,
                packageId: SelectedPackage.Id,
                gender: Gender,
                cardUid: CardUid,
                qrKey: QrKey);

            await _dbContext.Members.AddAsync(member);
            await _dbContext.SaveChangesAsync();

            // Cập nhật ngay vào RAM Cache của AttendanceCoordinator
            _attendanceCoordinator.UpdateMemberInCache(member);
            _audioService?.PlayCheckInSuccess(member.FullName);

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
}
