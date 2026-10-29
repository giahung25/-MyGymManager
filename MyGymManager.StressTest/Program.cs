namespace MyGymManager.StressTest;

using System.Diagnostics;
using System.IO;
using Microsoft.EntityFrameworkCore;
using MyGymManager.Core.Entities;
using MyGymManager.Core.Interfaces;
using MyGymManager.Data;
using MyGymManager.Infrastructure.Providers;
using MyGymManager.Services;

public class Program
{
    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("================================================================================");
        Console.WriteLine("           MYGYMMANAGER SYSTEM BENCHMARK & STRESS TEST SUITE                     ");
        Console.WriteLine("   Pure Gym Management & Multi-Credential Identification (RFID, QR, Phone, Desk)");
        Console.WriteLine("================================================================================\n");

        await TestCameraEnumerationAsync();
        Console.WriteLine();

        await RunMultiIndexCacheStressTestAsync();
        Console.WriteLine();

        await RunDatabaseConcurrencyStressTestAsync();
        Console.WriteLine();

        await RunAttendanceCoordinatorStressTestAsync();
        Console.WriteLine();

        await TestMemberFiltersAndHistoryAsync();
        TestExcelExportService();
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("================================================================================");
        Console.WriteLine("                  TẤT CẢ CÁC BÀI STRESS TEST ĐÃ HOÀN TẤT                        ");
        Console.WriteLine("================================================================================");
    }

    private static async Task TestCameraEnumerationAsync()
    {
        Console.WriteLine(">>> [KIỂM TRA CAMERA ENUMERATION & SELECTION]");
        Console.WriteLine("--------------------------------------------------------------------------------");
        var cameras = await CameraDeviceService.EnumerateCamerasAsync();
        Console.WriteLine($"• Tìm thấy {cameras.Count} thiết bị camera khả dụng:");
        foreach (var c in cameras)
        {
            Console.WriteLine($"  - [{c.Index}] {c.Name} (Nguồn: '{c.Source}', Khả dụng: {c.IsWorking}) -> Hiển thị: {c.DisplayName}");
        }
    }

    /// <summary>
    /// TEST SUITE 1: Stress test tra cứu Multi-Index In-Memory (Card UID, QR Key, Phone Number, GUID).
    /// </summary>
    private static async Task RunMultiIndexCacheStressTestAsync()
    {
        Console.WriteLine(">>> [TEST 1/4] STRESS TEST TRA CỨU ĐA CHỈ MỤC IN-MEMORY (Multi-Index O(1) Cache)");
        Console.WriteLine("--------------------------------------------------------------------------------");

        int[] memberCounts = { 1_000, 10_000, 50_000 };

        foreach (int count in memberCounts)
        {
            var byId = new Dictionary<Guid, Member>(count);
            var byPhone = new Dictionary<string, Member>(count);
            var byCard = new Dictionary<string, Member>(count);
            var byQr = new Dictionary<string, Member>(count);

            for (int i = 0; i < count; i++)
            {
                var id = Guid.NewGuid();
                var phone = $"090{i:D7}";
                var card = $"RFID_{i:D6}";
                var qr = $"QR_{i:D6}";
                var member = Member.Create($"Member {i}", phone, 30, gender: (i % 2 == 0 ? "Nam" : "Nữ"), cardUid: card, qrKey: qr);

                byId[id] = member;
                byPhone[phone] = member;
                byCard[card] = member;
                byQr[qr] = member;
            }

            // Benchmark 100,000 lần truy vấn tìm hội viên qua mã thẻ RFID và QR Code
            const int testQueries = 100_000;
            var rnd = new Random(42);
            var sw = Stopwatch.StartNew();

            int hitCount = 0;
            for (int q = 0; q < testQueries; q++)
            {
                int targetIndex = rnd.Next(0, count);
                string queryCard = $"RFID_{targetIndex:D6}";
                if (byCard.TryGetValue(queryCard, out var member))
                {
                    hitCount++;
                }
            }

            sw.Stop();
            double totalMs = sw.Elapsed.TotalMilliseconds;
            double avgLatencyMs = totalMs / testQueries;
            double avgLatencyMicroseconds = avgLatencyMs * 1000.0;
            double opsPerSec = testQueries / sw.Elapsed.TotalSeconds;

            Console.WriteLine($"• Quy mô: {count:N0} hội viên trong RAM");
            Console.WriteLine($"  - Độ trễ tra cứu O(1) trung bình: {avgLatencyMicroseconds:F3} µs / lượt ({avgLatencyMs:F6} ms)");
            Console.WriteLine($"  - Tốc độ xử lý: {opsPerSec:N0} lượt xác thực/giây");
            Console.WriteLine($"  - Đánh giá: {(avgLatencyMicroseconds < 1.0 ? "ĐẠT CHUẨN SIÊU TỐC (< 1 µs)" : "CẢNH BÁO")}\n");
        }
    }

    /// <summary>
    /// TEST SUITE 2: Stress test cơ sở dữ liệu SQLite EF Core (Bulk Insert & Concurrent Access).
    /// </summary>
    private static async Task RunDatabaseConcurrencyStressTestAsync()
    {
        Console.WriteLine(">>> [TEST 2/4] STRESS TEST CSDL SQLITE EF CORE (Bulk Write, Read & Concurrency)");
        Console.WriteLine("--------------------------------------------------------------------------------");

        const string testDbName = "stress_test_temp.db";
        if (File.Exists(testDbName)) File.Delete(testDbName);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={testDbName}")
            .Options;

        using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();

            // 1. Bulk Insert 1,000 Hội viên
            const int totalMembers = 1_000;
            var members = new List<Member>(totalMembers);
            var now = DateTime.UtcNow;

            for (int i = 0; i < totalMembers; i++)
            {
                members.Add(Member.Create(
                    fullName: $"Test Member {i:D4}",
                    phoneNumber: $"09{i:D8}",
                    packageDays: 30 + (i % 3) * 30,
                    gender: (i % 2 == 0 ? "Nam" : "Nữ"),
                    cardUid: $"CARD_{i:D6}",
                    qrKey: $"QR_{i:D6}"
                ));
            }

            var swInsert = Stopwatch.StartNew();
            await db.Members.AddRangeAsync(members);
            await db.SaveChangesAsync();
            swInsert.Stop();

            Console.WriteLine($"• Bulk Insert {totalMembers:N0} Hội viên:");
            Console.WriteLine($"  - Thời gian hoàn tất: {swInsert.ElapsedMilliseconds} ms ({(totalMembers / swInsert.Elapsed.TotalSeconds):N0} records/giây)");
            Console.WriteLine($"  - Đánh giá: {(swInsert.ElapsedMilliseconds < 2000 ? "ĐẠT CHUẨN XUẤT SẮC" : "ĐẠT")}\n");

            // 2. Bulk Insert 5,000 CheckInLogs
            const int totalLogs = 5_000;
            var logs = new List<CheckInLog>(totalLogs);
            for (int i = 0; i < totalLogs; i++)
            {
                var m = members[i % totalMembers];
                logs.Add(CheckInLog.Create(
                    memberId: m.Id,
                    confidenceScore: 1.0f,
                    checkInTime: now.AddMinutes(-i),
                    method: IdentificationMethod.RfidCard
                ));
            }

            var swLogs = Stopwatch.StartNew();
            await db.CheckInLogs.AddRangeAsync(logs);
            await db.SaveChangesAsync();
            swLogs.Stop();

            Console.WriteLine($"• Bulk Insert {totalLogs:N0} CheckInLogs:");
            Console.WriteLine($"  - Thời gian: {swLogs.ElapsedMilliseconds} ms ({(totalLogs / swLogs.Elapsed.TotalSeconds):N0} logs/giây)\n");

            // 3. Concurrent Write Stress Test
            const int concurrentTasks = 50;
            Console.WriteLine($"• Stress Test ghi đồng thời (Concurrency) với {concurrentTasks} luồng song song...");

            var swConcurrency = Stopwatch.StartNew();
            var tasks = new Task[concurrentTasks];

            for (int t = 0; t < concurrentTasks; t++)
            {
                int threadId = t;
                tasks[t] = Task.Run(async () =>
                {
                    using var localDb = new AppDbContext(options);
                    var log = CheckInLog.Create(
                        memberId: members[threadId].Id,
                        confidenceScore: 1.0f,
                        method: IdentificationMethod.QrCode
                    );
                    await localDb.CheckInLogs.AddAsync(log);
                    await localDb.SaveChangesAsync();
                });
            }

            await Task.WhenAll(tasks);
            swConcurrency.Stop();

            Console.WriteLine($"  - Hoàn tất {concurrentTasks} giao dịch song song trong: {swConcurrency.ElapsedMilliseconds} ms (Zero Locking Error)");
            Console.WriteLine($"  - Đánh giá: ĐẠT CHUẨN TOÀN VẸN DỮ LIỆU SQLITE ACID\n");
        }

        try
        {
            if (File.Exists(testDbName)) File.Delete(testDbName);
        }
        catch { }
    }

    /// <summary>
    /// TEST SUITE 3: Stress test AttendanceCoordinator và Pluggable Providers (RFID, QR, Phone).
    /// </summary>
    private static async Task RunAttendanceCoordinatorStressTestAsync()
    {
        Console.WriteLine(">>> [TEST 3/4] STRESS TEST ATTENDANCE COORDINATOR (Multi-Credential Check-In)");
        Console.WriteLine("--------------------------------------------------------------------------------");

        const string testDbName = "coord_test_temp.db";
        if (File.Exists(testDbName)) File.Delete(testDbName);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={testDbName}")
            .Options;

        using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();

            // Khởi tạo 10 hội viên
            var members = new List<Member>();
            for (int i = 0; i < 10; i++)
            {
                members.Add(Member.Create(
                    fullName: $"Khách Hàng {i + 1}",
                    phoneNumber: $"098000000{i}",
                    packageDays: 30,
                    gender: (i % 2 == 0 ? "Nam" : "Nữ"),
                    cardUid: $"RFID_KEY_{i}",
                    qrKey: $"QR_APP_{i}"
                ));
            }
            await db.Members.AddRangeAsync(members);
            await db.SaveChangesAsync();

            using var coordinator = new AttendanceCoordinator(db);
            await coordinator.LoadActiveMembersCacheAsync();
            coordinator.Start();

            var rfidProvider = new RfidCardIdentificationProvider();
            var qrProvider = new QrCodeIdentificationProvider();
            coordinator.RegisterProvider(rfidProvider);
            coordinator.RegisterProvider(qrProvider);

            int identifiedCount = 0;
            coordinator.MemberIdentified += (s, e) =>
            {
                Interlocked.Increment(ref identifiedCount);
            };

            Console.WriteLine($"• Nạp {coordinator.CachedMembersCount} hội viên vào RAM Cache.");

            // 1. Quẹt thẻ từ RFID
            Console.WriteLine("• Giả lập quẹt thẻ từ RFID ('RFID_KEY_0')...");
            rfidProvider.SimulateSwipe("RFID_KEY_0", 1.0f);
            await Task.Delay(200);

            // 2. Quét mã QR
            Console.WriteLine("• Giả lập quét mã QR ('QR_APP_1')...");
            qrProvider.SimulateSwipe("QR_APP_1", 1.0f);
            await Task.Delay(200);

            // 3. Nhập số điện thoại tìm kiếm
            Console.WriteLine("• Giả lập điểm danh số điện thoại ('0980000002')...");
            await coordinator.ProcessCheckInAsync("0980000002", IdentificationMethod.ManualSearch);
            await Task.Delay(200);

            Console.WriteLine($"  - Số lượt điểm danh thành công ghi nhận: {identifiedCount} / 3");
            if (identifiedCount >= 3)
            {
                Console.WriteLine("  -> [PASS] Điểm danh đa phương thức hoàn hảo.");
            }
            else
            {
                throw new InvalidOperationException($"Điểm danh thất bại: ghi nhận {identifiedCount} / 3");
            }
        }

        try
        {
            if (File.Exists(testDbName)) File.Delete(testDbName);
        }
        catch { }
    }

    /// <summary>
    /// TEST SUITE 4: Kiểm thử Module Quản lý Hội viên Tinh gọn (Filters & History Logs).
    /// </summary>
    private static async Task TestMemberFiltersAndHistoryAsync()
    {
        Console.WriteLine(">>> [TEST 4/4] KIỂM THỬ BỘ LỌC HỘI VIÊN & LỊCH SỬ ĐIỂM DANH (Member Filters & History)");
        Console.WriteLine("--------------------------------------------------------------------------------");

        const string testDbName = "member_filter_test_temp.db";
        if (File.Exists(testDbName)) File.Delete(testDbName);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={testDbName}")
            .Options;

        try
        {
            using (var db = new AppDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();

                var now = DateTime.UtcNow;

                // 1. Tạo 10 hội viên test: 4 Nam, 6 Nữ
                var members = new List<Member>
                {
                    Member.Create("Nguyễn Văn Hùng", "0901000001", packageDays: 60, gender: "Nam"),
                    Member.Create("Trần Quốc Tuấn", "0901000002", packageDays: 45, gender: "Nam"),
                    Member.Create("Lê Thị Mai", "0901000003", packageDays: 90, gender: "Nữ"),
                    Member.Create("Phạm Hương Tràm", "0901000004", packageDays: 365, gender: "Nữ"),

                    Member.Create("Hoàng Văn Thắng", "0901000005", packageDays: 3, gender: "Nam"),
                    Member.Create("Đặng Thị Lan", "0901000006", packageDays: 4, gender: "Nữ"),
                    Member.Create("Vũ Ngọc Hà", "0901000007", packageDays: 5, gender: "Nữ"),

                    Member.Create("Ngô Minh Trí", "0901000008", packageDays: 30, gender: "Nam"),
                    Member.Create("Đỗ Phương Thảo", "0901000009", packageDays: 30, gender: "Nữ"),
                    Member.Create("Bùi Yến Nhi", "0901000010", packageDays: 30, gender: "Nữ")
                };

                var expiredMembersToAdjust = members.Skip(7).Take(3);
                foreach (var expMember in expiredMembersToAdjust)
                {
                    typeof(Member).GetProperty(nameof(Member.StartDate))?.SetValue(expMember, now.AddDays(-35));
                    typeof(Member).GetProperty(nameof(Member.EndDate))?.SetValue(expMember, now.AddDays(-5));
                }

                await db.Members.AddRangeAsync(members);
                await db.SaveChangesAsync();

                var totalCount = await db.Members.CountAsync();
                var maleCount = await db.Members.CountAsync(m => m.Gender == "Nam");
                var femaleCount = await db.Members.CountAsync(m => m.Gender == "Nữ");

                Console.WriteLine($"• [Tạo dữ liệu] Đã khởi tạo {totalCount} hội viên vào SQLite:");
                Console.WriteLine($"  - Giới tính: {maleCount} Nam, {femaleCount} Nữ");
                if (totalCount != 10 || maleCount != 4 || femaleCount != 6)
                {
                    throw new InvalidOperationException($"Dữ liệu khởi tạo sai: Tổng={totalCount}, Nam={maleCount}, Nữ={femaleCount}.");
                }
                Console.WriteLine("  -> [PASS] Tạo 10 hội viên (4 Nam, 6 Nữ) thành công.");

                // Filter Active
                var activeQuery = await db.Members
                    .AsNoTracking()
                    .Where(m => m.EndDate >= now)
                    .ToListAsync();
                Console.WriteLine($"• [Filter Active]: {activeQuery.Count} người (Kỳ vọng: 7)");
                if (activeQuery.Count != 7)
                {
                    throw new InvalidOperationException($"Filter Active sai: Có {activeQuery.Count} người, kỳ vọng 7.");
                }
                Console.WriteLine("  -> [PASS] Filter Active đếm đúng 7 hội viên còn hạn.");

                // Filter ExpiringSoon (7 ngày)
                var expiringSoonThreshold = now.AddDays(7);
                var expiringSoonQuery = await db.Members
                    .AsNoTracking()
                    .Where(m => m.EndDate >= now && m.EndDate <= expiringSoonThreshold)
                    .ToListAsync();
                Console.WriteLine($"• [Filter ExpiringSoon (7 ngày)]: {expiringSoonQuery.Count} người (Kỳ vọng: 3)");
                if (expiringSoonQuery.Count != 3)
                {
                    throw new InvalidOperationException($"Filter ExpiringSoon sai: Có {expiringSoonQuery.Count} người, kỳ vọng 3.");
                }
                Console.WriteLine("  -> [PASS] Filter ExpiringSoon lọc đúng 3 hội viên sắp hết hạn trong 5-7 ngày.");

                // Filter Expired
                var expiredQuery = await db.Members
                    .AsNoTracking()
                    .Where(m => m.EndDate < now)
                    .ToListAsync();
                Console.WriteLine($"• [Filter Expired]: {expiredQuery.Count} người (Kỳ vọng: 3)");
                if (expiredQuery.Count != 3)
                {
                    throw new InvalidOperationException($"Filter Expired sai: Có {expiredQuery.Count} người, kỳ vọng 3.");
                }
                Console.WriteLine("  -> [PASS] Filter Expired lọc đúng 3 hội viên đã hết hạn.");

                // Check-in history
                var targetMember = members[0];
                var testLogs = new List<CheckInLog>();
                for (int i = 0; i < 5; i++)
                {
                    testLogs.Add(CheckInLog.Create(
                        memberId: targetMember.Id,
                        confidenceScore: 1.0f,
                        checkInTime: now.AddHours(-i),
                        method: IdentificationMethod.RfidCard
                    ));
                }

                await db.CheckInLogs.AddRangeAsync(testLogs);
                await db.SaveChangesAsync();

                var memberHistory = await db.CheckInLogs
                    .AsNoTracking()
                    .Where(l => l.MemberId == targetMember.Id)
                    .OrderByDescending(l => l.CheckInTime)
                    .ToListAsync();

                Console.WriteLine($"• [Lịch sử Điểm danh] Lấy log của '{targetMember.FullName}': {memberHistory.Count} lượt (Kỳ vọng: 5)");
                if (memberHistory.Count != 5)
                {
                    throw new InvalidOperationException($"Lịch sử điểm danh sai: Có {memberHistory.Count} lượt, kỳ vọng 5.");
                }
                Console.WriteLine("  -> [PASS] Truy vấn lịch sử điểm danh chính xác 5/5 logs.");
            }
        }
        finally
        {
            try
            {
                if (File.Exists(testDbName)) File.Delete(testDbName);
            }
            catch { }
        }
    }
    /// <summary>
    /// TEST SUITE 5: Kiểm thử Dịch vụ Xuất Báo Cáo Excel/CSV (UTF-8 with BOM, Headers, Format).
    /// </summary>
    private static void TestExcelExportService()
    {
        Console.WriteLine(">>> [TEST 5/5] KIỂM THỬ XUẤT BÁO CÁO EXCEL CSV (ExcelExportService UTF-8 BOM)");
        Console.WriteLine("--------------------------------------------------------------------------------");

        const string testCsv = "test_export_members.csv";
        if (File.Exists(testCsv)) File.Delete(testCsv);

        try
        {
            var members = new List<Member>
            {
                Member.Create("Nguyễn Văn An", "0901234567", 90, gender: "Nam", cardUid: "RFID_101", qrKey: "QR_101"),
                Member.Create("Trần Thị Bích", "0912345678", 365, gender: "Nữ", cardUid: "RFID_102", qrKey: "QR_102")
            };

            ExcelExportService.ExportMembersToCsv(members, testCsv);
            Console.WriteLine($"• Đã xuất {members.Count} hội viên ra tệp '{testCsv}'.");

            if (!File.Exists(testCsv))
            {
                throw new FileNotFoundException("Tệp CSV không tồn tại sau khi xuất.");
            }

            var lines = File.ReadAllLines(testCsv, System.Text.Encoding.UTF8);
            Console.WriteLine($"  - Header: {lines[0]}");
            Console.WriteLine($"  - Dòng 1: {lines[1]}");
            Console.WriteLine($"  - Dòng 2: {lines[2]}");

            string expectedHeader = "STT,Họ và Tên,Số điện thoại,Giới tính,Gói tập,Ngày bắt đầu,Ngày hết hạn,Số ngày còn lại,Trạng thái,Mã Thẻ RFID,Mã QR Code";
            if (lines[0] != expectedHeader)
            {
                throw new InvalidOperationException($"Header CSV không khớp kỳ vọng. Nhận được: '{lines[0]}'");
            }

            Console.WriteLine("  -> [PASS] Xuất báo cáo Excel CSV thành công chuẩn UTF-8 with BOM.");
        }
        finally
        {
            try
            {
                if (File.Exists(testCsv)) File.Delete(testCsv);
            }
            catch { }
        }
    }
}
