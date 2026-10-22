namespace MyGymManager.App.ViewModels;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using MyGymManager.Core.Entities;
using MyGymManager.Data;
using MyGymManager.Services;

/// <summary>
/// ViewModel cho cửa sổ Chỉnh sửa thông tin hội viên & Gia hạn gói tập.
/// - Cập nhật Họ và Tên, Số điện thoại.
/// - Khóa / Kích hoạt lại thẻ hội viên (IsActive).
/// - Gia hạn thời hạn tập qua nghiệp vụ Rich Domain Model (member.Renew).
/// - Đồng bộ tức thì với RAM Cache của AttendanceCoordinator.
/// </summary>
public class MemberEditViewModel : INotifyPropertyChanged
{
    private readonly Member _member;
    private readonly AppDbContext _dbContext;
    private readonly AttendanceCoordinator _attendanceCoordinator;

    private string _fullName = string.Empty;
    private string _phoneNumber = string.Empty;
    private bool _isActive = true;
    private bool _doRenew = false;
    private int _renewDays = 30;
    private string _errorMessage = string.Empty;
    private bool _isBusy = false;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<bool>? RequestClose;

    public Member Member => _member;

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

    public bool IsActive
    {
        get => _isActive;
        set => SetField(ref _isActive, value);
    }

    public bool DoRenew
    {
        get => _doRenew;
        set => SetField(ref _doRenew, value);
    }

    public int RenewDays
    {
        get => _renewDays;
        set => SetField(ref _renewDays, value);
    }

    public string CurrentPackageName => _member.Package?.Name ?? "Gói tập mặc định";
    public string CurrentEndDateFormatted => $"{_member.EndDate:dd/MM/yyyy}";
    public bool IsExpired => _member.IsExpired();
    public string ExpiryStatusText => IsExpired 
        ? $"❌ Đã hết hạn {_member.EndDate:dd/MM/yyyy}" 
        : $"✅ Còn hạn đến {_member.EndDate:dd/MM/yyyy} ({_member.RemainingDays()} ngày)";

    public ObservableCollection<int> QuickRenewOptions { get; } = new() { 30, 90, 180, 365 };

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
        !IsBusy;

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    public MemberEditViewModel(
        Member memberToEdit,
        AppDbContext dbContext,
        AttendanceCoordinator attendanceCoordinator)
    {
        _member = memberToEdit ?? throw new ArgumentNullException(nameof(memberToEdit));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _attendanceCoordinator = attendanceCoordinator ?? throw new ArgumentNullException(nameof(attendanceCoordinator));

        FullName = _member.FullName;
        PhoneNumber = _member.PhoneNumber;
        IsActive = _member.IsActive;

        SaveCommand = new RelayCommand(async () => await OnSaveAsync());
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
    }

    private async Task OnSaveAsync()
    {
        if (!CanSave) return;

        var name = FullName.Trim();
        var phone = PhoneNumber.Trim();

        IsBusy = true;
        ErrorMessage = string.Empty;

        try
        {
            // Kiểm tra nếu đổi SĐT thì SĐT mới không được trùng với hội viên khác
            if (phone != _member.PhoneNumber)
            {
                var isTaken = await _dbContext.Members.AnyAsync(m => m.PhoneNumber == phone && m.Id != _member.Id);
                if (isTaken)
                {
                    ErrorMessage = "Số điện thoại này đã được sử dụng bởi một hội viên khác.";
                    IsBusy = false;
                    return;
                }
            }

            // 1. Cập nhật hồ sơ hội viên
            _member.UpdateProfile(name, phone);

            // 2. Cập nhật trạng thái kích hoạt / khóa thẻ
            if (IsActive)
            {
                _member.Activate();
            }
            else
            {
                _member.Deactivate();
            }

            // 3. Gia hạn gói tập nếu người dùng chọn gia hạn
            if (DoRenew && RenewDays > 0)
            {
                _member.Renew(RenewDays);
                Debug.WriteLine($"[MemberEditViewModel] Đã gia hạn hội viên {_member.FullName} thêm {RenewDays} ngày.");
            }

            // 4. Lưu thay đổi vào SQLite
            await _dbContext.SaveChangesAsync();

            // 5. Đồng bộ tức thời vào RAM Cache của AttendanceCoordinator
            if (_member.IsActive)
            {
                _attendanceCoordinator.UpdateMemberInCache(_member);
            }
            else
            {
                _attendanceCoordinator.RemoveMemberFromCache(_member.Id);
            }

            RequestClose?.Invoke(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Lỗi khi cập nhật hội viên: {ex.Message}";
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
