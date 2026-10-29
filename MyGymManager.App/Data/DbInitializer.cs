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

        // Tự động bổ sung các cột mới nếu CSDL SQLite đã tồn tại từ các phiên bản trước (Safe Migration)
        try { await context.Database.ExecuteSqlRawAsync("ALTER TABLE Members ADD COLUMN CardUid TEXT NULL;"); } catch { }
        try { await context.Database.ExecuteSqlRawAsync("ALTER TABLE Members ADD COLUMN FingerprintData TEXT NULL;"); } catch { }
        try { await context.Database.ExecuteSqlRawAsync("ALTER TABLE Members ADD COLUMN QrKey TEXT NULL;"); } catch { }
        try { await context.Database.ExecuteSqlRawAsync("ALTER TABLE Members ADD COLUMN Gender TEXT DEFAULT 'Nam';"); } catch { }
        try { await context.Database.ExecuteSqlRawAsync("ALTER TABLE CheckInLogs ADD COLUMN Method INTEGER NOT NULL DEFAULT 0;"); } catch { }
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
            // Hội viên 1: Còn hạn 90 ngày (Gói Silver) -> Thẻ xanh
            var m1 = Member.Create("Nguyễn Văn An", "0901234567", 90, pSilver.Id, "Nam", cardUid: "RFID_101", qrKey: "QR_AN_0901234567");

            // Hội viên 2: Còn hạn 365 ngày (Gói Gold VIP) -> Thẻ xanh
            var m2 = Member.Create("Trần Thị Bích", "0912345678", 365, pGold.Id, "Nữ", cardUid: "RFID_102", qrKey: "QR_BICH_0912345678");

            // Hội viên 3: Hết hạn thẻ 10 ngày trước -> Thẻ hết hạn
            var m3 = Member.Create("Lê Hoàng Cường", "0987654321", 30, pStandard.Id, "Nam", cardUid: "RFID_103", qrKey: "QR_CUONG_0987654321");
            typeof(Member).GetProperty(nameof(Member.EndDate))?.SetValue(m3, DateTime.UtcNow.AddDays(-10));

            // Hội viên 4: Còn hạn 30 ngày (Gói Standard)
            var m4 = Member.Create("Phạm Minh Dũng", "0934567890", 30, pStandard.Id, "Nam", cardUid: "RFID_104", qrKey: "QR_DUNG_0934567890");

            // Hội viên 5: Còn hạn 90 ngày (Gói Silver)
            var m5 = Member.Create("Võ Thị Kim Em", "0978901234", 90, pSilver.Id, "Nữ", cardUid: "RFID_105", qrKey: "QR_EM_0978901234");
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
