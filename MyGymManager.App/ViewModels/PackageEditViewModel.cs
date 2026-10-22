namespace MyGymManager.App.ViewModels;

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MyGymManager.Core.Entities;
using MyGymManager.Data;

/// <summary>
/// ViewModel cho cửa sổ Thêm / Chỉnh sửa Gói tập Gym.
/// </summary>
public class PackageEditViewModel : INotifyPropertyChanged
{
    private readonly AppDbContext _dbContext;
    private readonly Package? _package;

    private string _name = string.Empty;
    private int _durationDays = 30;
    private decimal _price = 500_000m;
    private string? _description;
    private string _errorMessage = string.Empty;
    private bool _isBusy = false;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<bool>? RequestClose;

    public bool IsEditMode => _package != null;
    public string WindowTitle => IsEditMode ? "Chỉnh Sửa Gói Tập" : "Tạo Gói Tập Mới";
    public Package? ResultPackage { get; private set; }

    public string Name
    {
        get => _name;
        set
        {
            if (SetField(ref _name, value))
            {
                OnPropertyChanged(nameof(CanSave));
            }
        }
    }

    public int DurationDays
    {
        get => _durationDays;
        set
        {
            if (SetField(ref _durationDays, value))
            {
                OnPropertyChanged(nameof(CanSave));
            }
        }
    }

    public decimal Price
    {
        get => _price;
        set
        {
            if (SetField(ref _price, value))
            {
                OnPropertyChanged(nameof(CanSave));
            }
        }
    }

    public string? Description
    {
        get => _description;
        set => SetField(ref _description, value);
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
        !string.IsNullOrWhiteSpace(Name) &&
        DurationDays > 0 &&
        Price >= 0 &&
        !IsBusy;

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    /// <summary>
    /// Constructor cho Thêm mới hoặc Chỉnh sửa gói tập.
    /// </summary>
    public PackageEditViewModel(AppDbContext dbContext, Package? packageToEdit = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _package = packageToEdit;

        if (_package != null)
        {
            Name = _package.Name;
            DurationDays = _package.DurationDays;
            Price = _package.Price;
            Description = _package.Description;
        }

        SaveCommand = new RelayCommand(async () => await OnSaveAsync());
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
    }

    private async Task OnSaveAsync()
    {
        if (!CanSave) return;

        IsBusy = true;
        ErrorMessage = string.Empty;

        try
        {
            if (IsEditMode && _package != null)
            {
                // Cập nhật thực thể hiện có theo Rich Domain Model
                _package.Update(Name.Trim(), DurationDays, Price, Description);
                ResultPackage = _package;
            }
            else
            {
                // Tạo mới thực thể Package
                ResultPackage = Package.Create(Name.Trim(), DurationDays, Price, Description);
                await _dbContext.Packages.AddAsync(ResultPackage);
            }

            await _dbContext.SaveChangesAsync();
            RequestClose?.Invoke(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Lỗi khi lưu gói tập: {ex.Message}";
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
