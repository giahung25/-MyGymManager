namespace MyGymManager.Data;

using Microsoft.EntityFrameworkCore;
using MyGymManager.Core.Entities;

/// <summary>
/// Khởi tạo và nạp dữ liệu mẫu ban đầu cho CSDL SQLite cục bộ (phuc_gym.db).
/// Tự động sinh bảng qua EnsureCreated và seed 3 gói tập + 5 hội viên test.
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
            var pStandard = Package.Create(
                name: "Gói 1 Tháng (Standard)",
                durationDays: 30,
                price: 450_000m,
                description: "Tập luyện không giới hạn mọi khung giờ trong 30 ngày."
            );

            var pSilver = Package.Create(
                name: "Gói 3 Tháng (Silver)",
                durationDays: 90,
                price: 1_200_000m,
                description: "Tiết kiệm 150.000đ, tặng kèm 1 buổi hướng dẫn kỹ thuật cùng Huấn luyện viên."
            );

            var pGold = Package.Create(
                name: "Gói 1 Năm (Gold VIP)",
                durationDays: 365,
                price: 4_200_000m,
                description: "Quyền lợi VIP: Không giới hạn thời gian, tủ đồ locker riêng, khăn tập và nước uống miễn phí."
            );

            await context.Packages.AddRangeAsync(pStandard, pSilver, pGold);
            await context.SaveChangesAsync();
        }

        // 3. Seed 5 hội viên mẫu phục vụ kiểm thử (Task 1.5 & QA Test Cases)
        if (!await context.Members.AnyAsync())
        {
            var allPkgs = await context.Packages.ToListAsync();
            var pStandard = allPkgs.FirstOrDefault(p => p.DurationDays == 30) ?? allPkgs[0];
            var pSilver = allPkgs.FirstOrDefault(p => p.DurationDays == 90) ?? allPkgs[0];
            var pGold = allPkgs.FirstOrDefault(p => p.DurationDays == 365) ?? allPkgs[0];
                // Vector mẫu 512 chiều giả lập chuẩn ArcFace
                float[] CreateSampleVector(int seed)
                {
                    var v = new float[512];
                    var rnd = new Random(seed);
                    float norm = 0f;
                    for (int i = 0; i < 512; i++)
                    {
                        v[i] = (float)(rnd.NextDouble() * 2.0 - 1.0);
                        norm += v[i] * v[i];
                    }
                    norm = MathF.Sqrt(norm);
                    for (int i = 0; i < 512; i++) v[i] /= norm;
                    return v;
                }

                // Hội viên 1: Còn hạn 90 ngày (Gói Silver) -> Test TC04 Thẻ xanh
                var m1 = Member.Create("Nguyễn Văn An", "0901234567", 90, CreateSampleVector(101), pSilver.Id);

                // Hội viên 2: Còn hạn 365 ngày (Gói Gold VIP) -> Test TC04 Thẻ xanh
                var m2 = Member.Create("Trần Thị Bích", "0912345678", 365, CreateSampleVector(102), pGold.Id);

                // Hội viên 3: Hết hạn thẻ 10 ngày trước -> Test TC05 Thẻ đỏ hết hạn
                var m3 = Member.Create("Lê Hoàng Cường", "0987654321", 30, CreateSampleVector(103), pStandard.Id);
                // Đặt ngày hết hạn trong quá khứ để test kịch bản thẻ hết hạn
                typeof(Member).GetProperty(nameof(Member.EndDate))?.SetValue(m3, DateTime.UtcNow.AddDays(-10));

                // Hội viên 4: Còn hạn 30 ngày (Gói Standard)
                var m4 = Member.Create("Phạm Minh Dũng", "0934567890", 30, CreateSampleVector(104), pStandard.Id);

                // Hội viên 5: Còn hạn 90 ngày (Gói Silver)
                var m5 = Member.Create("Võ Thị Kim Em", "0978901234", 90, CreateSampleVector(105), pSilver.Id);

            await context.Members.AddRangeAsync(m1, m2, m3, m4, m5);
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
