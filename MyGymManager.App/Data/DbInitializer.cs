namespace MyGymManager.Data;

using Microsoft.EntityFrameworkCore;
using MyGymManager.Core.Entities;

/// <summary>
/// Khởi tạo và nạp dữ liệu mẫu ban đầu cho CSDL SQLite cục bộ (phuc_gym.db).
/// Tự động sinh bảng qua EnsureCreated và seed 3 gói tập phổ biến.
/// </summary>
public static class DbInitializer
{
    /// <summary>
    /// Khởi tạo CSDL bất đồng bộ (Async).
    /// </summary>
    public static async Task InitializeAsync(AppDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context, nameof(context));

        // 1. Tự động tạo cơ sở dữ liệu và các bảng nếu chưa tồn tại
        await context.Database.EnsureCreatedAsync();

        // 2. Seed 3 gói tập mẫu nếu bảng Packages chưa có dữ liệu
        if (!await context.Packages.AnyAsync())
        {
            var packages = new List<Package>
            {
                Package.Create(
                    name: "Gói 1 Tháng (Standard)",
                    durationDays: 30,
                    price: 450_000m,
                    description: "Tập luyện không giới hạn mọi khung giờ trong 30 ngày."
                ),
                Package.Create(
                    name: "Gói 3 Tháng (Silver)",
                    durationDays: 90,
                    price: 1_200_000m,
                    description: "Tiết kiệm 150.000đ, tặng kèm 1 buổi hướng dẫn kỹ thuật cùng Huấn luyện viên."
                ),
                Package.Create(
                    name: "Gói 1 Năm (Gold VIP)",
                    durationDays: 365,
                    price: 4_200_000m,
                    description: "Quyền lợi VIP: Không giới hạn thời gian, tủ đồ locker riêng, khăn tập và nước uống miễn phí."
                )
            };

            await context.Packages.AddRangeAsync(packages);
            await context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Khởi tạo CSDL đồng bộ (Sync) dành cho Startup hoặc UI test runner.
    /// </summary>
    public static void Initialize(AppDbContext context)
    {
        InitializeAsync(context).GetAwaiter().GetResult();
    }
}
