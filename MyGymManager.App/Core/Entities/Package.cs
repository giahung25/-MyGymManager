namespace MyGymManager.Core.Entities;

/// <summary>
/// Thực thể Gói tập (Package) tuân thủ Rich Domain Model.
/// Đóng gói dữ liệu và bảo toàn tính toàn vẹn (Invariants).
/// </summary>
public class Package
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public int DurationDays { get; private set; }
    public decimal Price { get; private set; }
    public string? Description { get; private set; }

    // Navigation property
    public ICollection<Member> Members { get; private set; } = new List<Member>();

    // Constructor private dành riêng cho EF Core
    private Package() { }

    /// <summary>
    /// Factory Method khởi tạo Gói tập mới với validation chặt chẽ.
    /// </summary>
    public static Package Create(string name, int durationDays, decimal price, string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
        if (durationDays <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationDays), "Thời hạn gói tập phải lớn hơn 0 ngày.");
        if (price < 0)
            throw new ArgumentOutOfRangeException(nameof(price), "Giá gói tập không được âm.");

        return new Package
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            DurationDays = durationDays,
            Price = price,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim()
        };
    }

    /// <summary>
    /// Cập nhật thông tin gói tập.
    /// </summary>
    public void Update(string name, int durationDays, decimal price, string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
        if (durationDays <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationDays), "Thời hạn gói tập phải lớn hơn 0 ngày.");
        if (price < 0)
            throw new ArgumentOutOfRangeException(nameof(price), "Giá gói tập không được âm.");

        Name = name.Trim();
        DurationDays = durationDays;
        Price = price;
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }
}
