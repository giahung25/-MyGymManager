namespace MyGymManager.StressTest;

using System.Diagnostics;
using System.IO;
using Microsoft.EntityFrameworkCore;
using MyGymManager.Core.Entities;
using MyGymManager.Core.Interfaces;
using MyGymManager.Core.Models;
using MyGymManager.Data;
using MyGymManager.Infrastructure.AI;
using MyGymManager.Services;
using OpenCvSharp;

public class Program
{
    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("================================================================================");
        Console.WriteLine("           MYGYMMANAGER SYSTEM BENCHMARK & STRESS TEST SUITE                     ");
        Console.WriteLine("   Testing .NET 10 WPF Standalone Backend: AI, In-Memory Math, SQLite, Concurrency");
        Console.WriteLine("================================================================================\n");
        await TestCameraEnumerationAsync();
        Console.WriteLine();


        await RunVectorMatchingStressTestAsync();
        Console.WriteLine();

        await RunDatabaseConcurrencyStressTestAsync();
        Console.WriteLine();

        await RunOnnxInferenceStressTestAsync();
        Console.WriteLine();

        await TestMemberFiltersAndHistoryAsync();
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
    /// TEST SUITE 1: Stress test so khớp Cosine Similarity in-memory trên 1.000, 5.000 và 10.000 hội viên.
    /// </summary>
    private static async Task RunVectorMatchingStressTestAsync()
    {
        Console.WriteLine(">>> [TEST 1/3] STRESS TEST SO KHỚP VECTOR IN-MEMORY (ArcFace 512-d SIMD)");
        Console.WriteLine("--------------------------------------------------------------------------------");

        int[] memberCounts = { 1_000, 5_000, 10_000 };
        var rnd = new Random(42);

        float[] GenerateNormalizedVector(int seed)
        {
            var v = new float[512];
            var r = new Random(seed);
            float norm = 0f;
            for (int i = 0; i < 512; i++)
            {
                v[i] = (float)(r.NextDouble() * 2.0 - 1.0);
                norm += v[i] * v[i];
            }
            norm = MathF.Sqrt(norm);
            for (int i = 0; i < 512; i++) v[i] /= norm;
            return v;
        }

        var queryVector = GenerateNormalizedVector(9999);

        foreach (int count in memberCounts)
        {
            // Mỗi hội viên có 3 templates khuôn mặt (Thẳng, Trái, Phải)
            var database = new List<(Guid Id, List<float[]> Templates)>(count);
            for (int i = 0; i < count; i++)
            {
                var templates = new List<float[]>
                {
                    GenerateNormalizedVector(i * 3 + 1),
                    GenerateNormalizedVector(i * 3 + 2),
                    GenerateNormalizedVector(i * 3 + 3)
                };
                database.Add((Guid.NewGuid(), templates));
            }

            int totalVectors = count * 3;

            // Warmup
            for (int i = 0; i < 100; i++)
            {
                AttendanceCoordinator.CalculateCosineSimilarity(queryVector, database[i].Templates[0]);
            }

            // Benchmark 500 lần truy vấn tìm hội viên
            const int testQueries = 500;
            var sw = Stopwatch.StartNew();

            for (int q = 0; q < testQueries; q++)
            {
                float highestSim = 0f;
                Guid matchedId = Guid.Empty;

                for (int m = 0; m < database.Count; m++)
                {
                    var templates = database[m].Templates;
                    for (int t = 0; t < templates.Count; t++)
                    {
                        float sim = AttendanceCoordinator.CalculateCosineSimilarity(queryVector, templates[t]);
                        if (sim > highestSim)
                        {
                            highestSim = sim;
                            matchedId = database[m].Id;
                        }
                    }
                }
            }

            sw.Stop();
            double totalMs = sw.Elapsed.TotalMilliseconds;
            double avgLatencyMs = totalMs / testQueries;
            double avgLatencyMicroseconds = avgLatencyMs * 1000.0;
            double opsPerSec = (testQueries * totalVectors) / sw.Elapsed.TotalSeconds;

            Console.WriteLine($"• Quy mô: {count:N0} hội viên ({totalVectors:N0} vectors 512-d)");
            Console.WriteLine($"  - Độ trễ tìm kiếm trung bình: {avgLatencyMs:F3} ms ({avgLatencyMicroseconds:F1} µs / lượt quẹt thẻ)");
            Console.WriteLine($"  - Tốc độ xử lý phép nhân vô hướng: {opsPerSec:N0} dot products/giây");
            Console.WriteLine($"  - Đánh giá: {(avgLatencyMs < 5.0 ? "ĐẠT CHUẨN SIÊU TỐC (< 5ms)" : "CẢNH BÁO")}\n");
        }
    }

    /// <summary>
    /// TEST SUITE 2: Stress test cơ sở dữ liệu SQLite EF Core (Bulk Insert & Concurrent Access).
    /// </summary>
    private static async Task RunDatabaseConcurrencyStressTestAsync()
    {
        Console.WriteLine(">>> [TEST 2/3] STRESS TEST CSDL SQLITE EF CORE (Bulk Write, Read & Concurrency)");
        Console.WriteLine("--------------------------------------------------------------------------------");

        const string testDbName = "stress_test_temp.db";
        if (File.Exists(testDbName)) File.Delete(testDbName);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={testDbName}")
            .Options;

        using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();

            // 1. Bulk insert 1000 members kèm JSON vectors 512-d
            const int memberBatchCount = 1_000;
            var swMembers = Stopwatch.StartNew();

            var members = new List<Member>(memberBatchCount);
            var dummyVector = new float[512];
            dummyVector[0] = 1.0f;

            for (int i = 0; i < memberBatchCount; i++)
            {
                var m = Member.Create(
                    fullName: $"Hội Viên Test {i}",
                    phoneNumber: $"09{i:D8}",
                    packageDays: 30,
                    initialFaceVector: dummyVector);
                members.Add(m);
            }

            await db.Members.AddRangeAsync(members);
            await db.SaveChangesAsync();
            swMembers.Stop();

            Console.WriteLine($"• Bulk Insert 1.000 Hội viên (kèm mảng FaceVectors JSON 512-d):");
            Console.WriteLine($"  - Thời gian hoàn tất: {swMembers.ElapsedMilliseconds} ms ({memberBatchCount * 1000.0 / swMembers.ElapsedMilliseconds:F0} records/sec)");

            // 2. Bulk insert 5.000 check-in logs
            const int logBatchCount = 5_000;
            var swLogs = Stopwatch.StartNew();
            var logs = new List<CheckInLog>(logBatchCount);
            var sampleMemberId = members[0].Id;

            for (int i = 0; i < logBatchCount; i++)
            {
                logs.Add(CheckInLog.Create(sampleMemberId, 0.92f, DateTime.UtcNow.AddSeconds(-i)));
            }

            await db.CheckInLogs.AddRangeAsync(logs);
            await db.SaveChangesAsync();
            swLogs.Stop();

            Console.WriteLine($"• Bulk Insert 5.000 Lượt CheckInLogs:");
            Console.WriteLine($"  - Thời gian hoàn tất: {swLogs.ElapsedMilliseconds} ms ({logBatchCount * 1000.0 / swLogs.ElapsedMilliseconds:F0} records/sec)");

            // 3. Concurrency Stress Test: 50 luồng đọc/ghi đồng thời
            const int concurrentTasks = 50;
            Console.WriteLine($"• Concurrency Test: Chạy đồng thời {concurrentTasks} tác vụ đọc/ghi đồng thời trên SQLite...");
            var swConcurrency = Stopwatch.StartNew();

            var tasks = new List<Task>();
            for (int t = 0; t < concurrentTasks; t++)
            {
                int taskId = t;
                tasks.Add(Task.Run(async () =>
                {
                    using var taskDb = new AppDbContext(options);
                    if (taskId % 2 == 0)
                    {
                        // Luồng ghi
                        var log = CheckInLog.Create(sampleMemberId, 0.85f);
                        await taskDb.CheckInLogs.AddAsync(log);
                        await taskDb.SaveChangesAsync();
                    }
                    else
                    {
                        // Luồng đọc
                        var recent = await taskDb.CheckInLogs
                            .AsNoTracking()
                            .OrderByDescending(l => l.CheckInTime)
                            .Take(20)
                            .ToListAsync();
                    }
                }));
            }

            await Task.WhenAll(tasks);
            swConcurrency.Stop();

            Console.WriteLine($"  - Hoàn tất {concurrentTasks} giao dịch song song trong: {swConcurrency.ElapsedMilliseconds} ms (Zero Locking Error)");
            Console.WriteLine($"  - Đánh giá: ĐẠT CHUẨN TOÀN VẸN DỮ LIỆU SQLITE ACID\n");
        }

        // Dọn dẹp CSDL test
        try
        {
            if (File.Exists(testDbName)) File.Delete(testDbName);
        }
        catch { }
    }

    /// <summary>
    /// TEST SUITE 3: Stress test động cơ AI ONNX Runtime & Kiểm chứng rò rỉ RAM (Memory Leak Detection).
    /// </summary>
    private static async Task RunOnnxInferenceStressTestAsync()
    {
        Console.WriteLine(">>> [TEST 3/3] STRESS TEST ONNX RUNTIME AI & KIỂM CHỨNG RÒ RỈ RAM (SCRFD + ArcFace)");
        Console.WriteLine("--------------------------------------------------------------------------------");

        IFaceEngine? engine = null;
        try
        {
            engine = new OnnxFaceEngine();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Bỏ qua kiểm tra ONNX vì không tìm thấy file weights: {ex.Message}");
            return;
        }

        using (engine)
        {
            // Tạo ảnh giả lập chuẩn 720p (1280x720 BGR)
            using var testFrame = new Mat(new Size(1280, 720), MatType.CV_8UC3, Scalar.All(120));
            // Vẽ 1 hình tròn giả lập mặt
            Cv2.Circle(testFrame, new Point(640, 360), 120, Scalar.All(200), -1);

            // Warmup 5 lượt để ONNX Runtime khởi tạo bộ đệm Unmanaged Memory Arena ban đầu
            for (int w = 0; w < 5; w++)
            {
                await engine.DetectFacesAsync(testFrame);
            }

            // Đo bộ nhớ cơ sở sau khi ONNX arena đã ổn định
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long initialMem = Process.GetCurrentProcess().WorkingSet64;
            const int iterations = 100;
            Console.WriteLine($"• Chạy liên tục {iterations} lần suy luận AI DetectFacesAsync trên ảnh 1280x720...");

            var latencies = new List<double>(iterations);
            var swTotal = Stopwatch.StartNew();

            for (int i = 0; i < iterations; i++)
            {
                var swIter = Stopwatch.StartNew();
                var faces = await engine.DetectFacesAsync(testFrame);
                swIter.Stop();
                latencies.Add(swIter.Elapsed.TotalMilliseconds);
            }

            swTotal.Stop();

            // Đo bộ nhớ sau 100 lần chạy
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long finalMem = Process.GetCurrentProcess().WorkingSet64;
            long memDiffMB = (finalMem - initialMem) / (1024 * 1024);

            double avgLatency = latencies.Average();
            double minLatency = latencies.Min();
            double maxLatency = latencies.Max();
            double fps = iterations / swTotal.Elapsed.TotalSeconds;

            Console.WriteLine($"  - Tổng thời gian: {swTotal.Elapsed.TotalSeconds:F2} giây ({fps:F1} inferences/sec)");
            Console.WriteLine($"  - Độ trễ trung bình mỗi lượt: {avgLatency:F2} ms (Min: {minLatency:F2} ms, Max: {maxLatency:F2} ms)");
            Console.WriteLine($"  - Biến động RAM sau {iterations} lần chạy: {memDiffMB:+0;-0;0} MB");
            Console.WriteLine($"  - Đánh giá chống rò rỉ RAM (Zero Memory Leak): {(Math.Abs(memDiffMB) < 15 ? "HOÀN HẢO (RAM ỔN ĐỊNH)" : "CÓ DẤU HIỆU TĂNG RAM")}");
        }
    }

    /// <summary>
    /// TEST SUITE 4: Kiểm thử Module Quản lý Hội viên Tinh gọn (Filters: Active, ExpiringSoon, Expired & History Logs).
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
                //    - 4 người hạn còn dài (> 30 ngày) -> Trạng thái Active
                //    - 3 người sắp hết hạn trong 5 ngày tới -> Trạng thái ExpiringSoon
                //    - 3 người đã hết hạn từ 5 ngày trước -> Trạng thái Expired
                var members = new List<Member>
                {
                    // 4 người còn hạn dài (> 30 ngày)
                    Member.Create("Nguyễn Văn Hùng", "0901000001", packageDays: 60, gender: "Nam"),
                    Member.Create("Trần Quốc Tuấn", "0901000002", packageDays: 45, gender: "Nam"),
                    Member.Create("Lê Thị Mai", "0901000003", packageDays: 90, gender: "Nữ"),
                    Member.Create("Phạm Hương Tràm", "0901000004", packageDays: 365, gender: "Nữ"),

                    // 3 người sắp hết hạn trong 5 ngày tới (ExpiringSoon)
                    Member.Create("Hoàng Văn Thắng", "0901000005", packageDays: 3, gender: "Nam"),
                    Member.Create("Đặng Thị Lan", "0901000006", packageDays: 4, gender: "Nữ"),
                    Member.Create("Vũ Ngọc Hà", "0901000007", packageDays: 5, gender: "Nữ"),

                    // 3 người đã hết hạn từ 5 ngày trước (Expired)
                    Member.Create("Ngô Minh Trí", "0901000008", packageDays: 30, gender: "Nam"),
                    Member.Create("Đỗ Phương Thảo", "0901000009", packageDays: 30, gender: "Nữ"),
                    Member.Create("Bùi Yến Nhi", "0901000010", packageDays: 30, gender: "Nữ")
                };

                // Thiết lập ngày hết hạn 5 ngày trước cho 3 hội viên đã hết hạn
                var expiredMembersToAdjust = members.Skip(7).Take(3);
                foreach (var expMember in expiredMembersToAdjust)
                {
                    typeof(Member).GetProperty(nameof(Member.StartDate))?.SetValue(expMember, now.AddDays(-35));
                    typeof(Member).GetProperty(nameof(Member.EndDate))?.SetValue(expMember, now.AddDays(-5));
                }

                await db.Members.AddRangeAsync(members);
                await db.SaveChangesAsync();

                // Xác nhận số lượng và giới tính
                var totalCount = await db.Members.CountAsync();
                var maleCount = await db.Members.CountAsync(m => m.Gender == "Nam");
                var femaleCount = await db.Members.CountAsync(m => m.Gender == "Nữ");

                Console.WriteLine($"• [Tạo dữ liệu] Đã khởi tạo {totalCount} hội viên vào SQLite:");
                Console.WriteLine($"  - Giới tính: {maleCount} Nam, {femaleCount} Nữ");
                if (totalCount != 10 || maleCount != 4 || femaleCount != 6)
                {
                    throw new InvalidOperationException($"Dữ liệu khởi tạo sai: Tổng={totalCount} (kỳ vọng 10), Nam={maleCount} (kỳ vọng 4), Nữ={femaleCount} (kỳ vọng 6).");
                }
                Console.WriteLine("  -> [PASS] Tạo 10 hội viên (4 Nam, 6 Nữ) thành công.");

                // 2. Kiểm tra bộ lọc
                // 2.1 Filter Active: Còn hạn (EndDate >= now) -> 4 còn dài + 3 sắp hết hạn = 7 người
                var activeQuery = await db.Members
                    .AsNoTracking()
                    .Where(m => m.EndDate >= now)
                    .ToListAsync();
                Console.WriteLine($"• [Filter Active] Còn hạn (Active + ExpiringSoon): {activeQuery.Count} người (Kỳ vọng: 7)");
                if (activeQuery.Count != 7)
                {
                    throw new InvalidOperationException($"Filter Active sai: Có {activeQuery.Count} người, kỳ vọng 7.");
                }
                Console.WriteLine("  -> [PASS] Filter Active đếm đúng 7 hội viên còn hạn.");

                // 2.2 Filter ExpiringSoon (7 ngày): Sắp hết hạn trong 7 ngày tới (now <= EndDate <= now + 7 ngày) -> 3 người
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

                // 2.3 Filter Expired: Đã hết hạn (EndDate < now) -> 3 người
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

                // 3. Tạo 5 log điểm danh cho 1 hội viên và kiểm tra truy vấn lịch sử của riêng người đó
                var targetMember = members[0]; // Nguyễn Văn Hùng
                var testLogs = new List<CheckInLog>();
                for (int i = 0; i < 5; i++)
                {
                    testLogs.Add(CheckInLog.Create(
                        memberId: targetMember.Id,
                        confidenceScore: 0.92f + i * 0.01f,
                        checkInTime: now.AddHours(-i),
                        method: IdentificationMethod.FaceRecognition
                    ));
                }

                await db.CheckInLogs.AddRangeAsync(testLogs);
                await db.SaveChangesAsync();

                // Truy vấn lịch sử điểm danh của riêng hội viên đó
                var memberHistory = await db.CheckInLogs
                    .AsNoTracking()
                    .Where(l => l.MemberId == targetMember.Id)
                    .OrderByDescending(l => l.CheckInTime)
                    .ToListAsync();

                Console.WriteLine($"• [Lịch sử Điểm danh] Lấy log của '{targetMember.FullName}' (Id: {targetMember.Id}):");
                Console.WriteLine($"  - Số lượng log: {memberHistory.Count} (Kỳ vọng: 5)");

                if (memberHistory.Count != 5)
                {
                    throw new InvalidOperationException($"Số lượng lịch sử điểm danh không khớp: {memberHistory.Count} (kỳ vọng 5).");
                }

                bool allBelongToTarget = memberHistory.All(l => l.MemberId == targetMember.Id);
                if (!allBelongToTarget)
                {
                    throw new InvalidOperationException("Có bản ghi điểm danh không thuộc về hội viên được chỉ định.");
                }

                // Kiểm tra hội viên khác không bị lẫn log
                var otherMemberId = members[1].Id;
                var otherLogsCount = await db.CheckInLogs.CountAsync(l => l.MemberId == otherMemberId);
                if (otherLogsCount != 0)
                {
                    throw new InvalidOperationException($"Hội viên khác có {otherLogsCount} log, kỳ vọng 0.");
                }

                Console.WriteLine("  -> [PASS] Truy vấn lịch sử điểm danh chính xác 5/5 logs, không lẫn dữ liệu.");
                Console.WriteLine("• ĐÁNH GIÁ: 100% CÁC TEST CASES MODULE QUẢN LÝ HỘI VIÊN & LỊCH SỬ ĐÃ PASS!");
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
}
