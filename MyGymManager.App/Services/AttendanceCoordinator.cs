namespace MyGymManager.Services;

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using MyGymManager.Core.Entities;
using MyGymManager.Core.Events;
using MyGymManager.Core.Interfaces;
using MyGymManager.Core.Models;
using MyGymManager.Data;
using OpenCvSharp;

/// <summary>
/// Bộ điều phối Điểm danh tự động (Attendance Coordinator) tuân thủ Observer Pattern.
/// - Nhận luồng frame từ ICameraService.
/// - Sử dụng chiến lược Drop-frame: Nếu luồng AI đang xử lý frame trước thì hủy frame mới để không nghẽn CPU và chống rò rỉ RAM.
/// - So khớp vector khuôn mặt in-memory với danh sách hội viên đang hoạt động (Cosine Similarity >= 0.65).
/// - Ghi nhận nhật ký CheckInLog vào SQLite (có cơ chế Debounce Cooldown chống ghi đúp).
/// - Phát sự kiện MemberIdentified để UI, âm thanh và hệ thống phần cứng đồng loạt phản ứng.
/// </summary>
public class AttendanceCoordinator : IDisposable
{
    private readonly ICameraService _cameraService;
    private readonly IFaceEngine _faceEngine;
    private readonly AppDbContext _dbContext;
    private readonly SemaphoreSlim _dbLock = new(1, 1);

    // Bộ nhớ đệm RAM lưu hội viên đang hoạt động kèm vector đặc trưng 512 chiều
    private readonly ConcurrentDictionary<Guid, CachedMemberEntry> _cachedMembers = new();

    // Debounce Cooldown: Lưu thời điểm check-in gần nhất của từng hội viên để tránh ghi log hàng loạt trong vài giây
    private readonly ConcurrentDictionary<Guid, DateTime> _lastCheckInTimes = new();

    // Biến cờ nguyên tử kiểm soát trạng thái đang xử lý frame (0: Rảnh, 1: Đang bận inference)
    private int _isProcessingFrame = 0;
    private bool _isSubscribed = false;
    private bool _disposed = false;

    /// <summary>
    /// Ngưỡng tương đồng tối thiểu để xác nhận cùng một người (mặc định 0.65 theo chuẩn ArcFace).
    /// </summary>
    public float SimilarityThreshold { get; set; } = 0.65f;

    /// <summary>
    /// Thời gian giãn cách giữa 2 lần ghi log liên tiếp của cùng 1 hội viên (mặc định 20 giây).
    /// </summary>
    public TimeSpan CheckInCooldown { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Số lượng hội viên hợp lệ đang được cache trong RAM.
    /// </summary>
    public int CachedMembersCount => _cachedMembers.Count;

    /// <summary>
    /// Sự kiện bắn ra khi nhận diện thành công một hội viên (Observer Pattern).
    /// </summary>
    public event EventHandler<MemberIdentifiedEventArgs>? MemberIdentified;

    /// <summary>
    /// Sự kiện phát ra khi phát hiện khuôn mặt nhưng không khớp hội viên nào (Khách lạ / Chưa đăng ký).
    /// </summary>
    public event EventHandler<float>? UnidentifiedFaceDetected;

    /// <summary>
    /// Sự kiện bắn ra vị trí Bounding Box của khuôn mặt nổi bật nhất trong khung hình (để UI vẽ bounding box trực tiếp).
    /// Trả về null nếu không có khuôn mặt trong khung hình.
    /// </summary>
    public event EventHandler<FaceBox?>? FaceDetected;
    public AttendanceCoordinator(
        ICameraService cameraService,
        IFaceEngine faceEngine,
        AppDbContext dbContext)
    {
        _cameraService = cameraService ?? throw new ArgumentNullException(nameof(cameraService));
        _faceEngine = faceEngine ?? throw new ArgumentNullException(nameof(faceEngine));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    /// <summary>
    /// Nạp toàn bộ hội viên đang hoạt động (IsActive = true) và có vector khuôn mặt từ SQLite vào RAM.
    /// </summary>
    public async Task LoadActiveMembersCacheAsync()
    {
        await _dbLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var activeMembers = await _dbContext.Members
                .AsNoTracking()
                .Include(m => m.Package)
                .Where(m => m.IsActive)
                .ToListAsync()
                .ConfigureAwait(false);

            _cachedMembers.Clear();

            foreach (var member in activeMembers)
            {
                if (member.FaceVectors.Count > 0)
                {
                    _cachedMembers[member.Id] = new CachedMemberEntry(member, member.FaceVectors);
                }
            }

            Debug.WriteLine($"[AttendanceCoordinator] Đã nạp {_cachedMembers.Count} hội viên vào RAM Cache.");
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>
    /// Cập nhật hoặc bổ sung một hội viên cụ thể vào RAM cache (dùng sau khi đăng ký mới hoặc chỉnh sửa).
    /// </summary>
    public void UpdateMemberInCache(Member member)
    {
        if (member == null) return;

        if (member.IsActive && member.FaceVectors.Count > 0)
        {
            _cachedMembers[member.Id] = new CachedMemberEntry(member, member.FaceVectors);
        }
        else
        {
            _cachedMembers.TryRemove(member.Id, out _);
        }
    }

    /// <summary>
    /// Bắt đầu lắng nghe luồng frame từ CameraService.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_isSubscribed)
        {
            _cameraService.FrameCaptured += OnFrameCaptured;
            _isSubscribed = true;
        }
    }

    /// <summary>
    /// Tạm dừng lắng nghe luồng camera.
    /// </summary>
    public void Stop()
    {
        if (_isSubscribed)
        {
            _cameraService.FrameCaptured -= OnFrameCaptured;
            _isSubscribed = false;
        }
    }

    /// <summary>
    /// Xử lý sự kiện FrameCaptured với cơ chế Drop-frame Zero-Allocation.
    /// </summary>
    private void OnFrameCaptured(object? sender, Mat frame)
    {
        if (frame.Empty()) return;

        // Nếu vòng lặp suy luận AI trước đó chưa hoàn tất -> Bỏ qua frame này ngay lập tức
        if (Interlocked.CompareExchange(ref _isProcessingFrame, 1, 0) != 0)
        {
            return;
        }

        // Clone một bản Mat riêng cho Background AI Worker để không tranh chấp với UI Thread
        var aiFrame = frame.Clone();

        _ = Task.Run(async () =>
        {
            try
            {
                await ProcessFrameCoreAsync(aiFrame).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AttendanceCoordinator] Lỗi xử lý frame: {ex.Message}");
            }
            finally
            {
                aiFrame.Dispose();
                Interlocked.Exchange(ref _isProcessingFrame, 0);
            }
        });
    }

    /// <summary>
    /// Lõi phân tích thị giác máy tính: Phát hiện mặt -> Trích vector -> So khớp Cosine -> Ghi log SQLite.
    /// </summary>
    private async Task ProcessFrameCoreAsync(Mat frame)
    {
        if (frame.Empty() || _cachedMembers.IsEmpty)
            return;
        // 1. Phát hiện toàn bộ khuôn mặt trong frame
        var detectedFaces = await _faceEngine.DetectFacesAsync(frame).ConfigureAwait(false);
        if (detectedFaces == null || detectedFaces.Count == 0)
        {
            FaceDetected?.Invoke(this, null);
            return;
        }

        // Bắn sự kiện vị trí khuôn mặt nổi bật nhất để UI vẽ Bounding Box
        var primaryFace = detectedFaces.OrderByDescending(f => f.Width * f.Height).First();
        FaceDetected?.Invoke(this, primaryFace);
        foreach (var faceBox in detectedFaces)
        {
            // Cắt vùng khuôn mặt an toàn (tránh tràn viền ảnh gốc)
            var rect = faceBox.ToRect().Intersect(new Rect(0, 0, frame.Width, frame.Height));
            if (rect.Width < 20 || rect.Height < 20)
                continue;

            using var faceCrop = new Mat(frame, rect);

            // 2. Trích xuất vector đặc trưng 512 chiều qua ArcFace
            var embedding = await _faceEngine.ExtractEmbeddingAsync(faceCrop).ConfigureAwait(false);
            if (embedding == null || embedding.Length != 512)
                continue;

            // 3. So khớp Cosine Similarity với kho vector trong RAM
            Member? bestMatchMember = null;
            float highestSimilarity = 0f;

            foreach (var (_, entry) in _cachedMembers)
            {
                foreach (var templateVector in entry.Vectors)
                {
                    float similarity = CalculateCosineSimilarity(embedding, templateVector);
                    if (similarity > highestSimilarity)
                    {
                        highestSimilarity = similarity;
                        bestMatchMember = entry.Member;
                    }
                }
            }

            // 4. Xử lý kết quả nhận diện
            if (highestSimilarity >= SimilarityThreshold && bestMatchMember != null)
            {
                bool isNewCheckIn = false;
                var now = DateTime.UtcNow;

                // Kiểm tra Debounce Cooldown: Không ghi log dồn dập trong khoảng thời gian CheckInCooldown
                if (!_lastCheckInTimes.TryGetValue(bestMatchMember.Id, out var lastTime) || (now - lastTime) >= CheckInCooldown)
                {
                    _lastCheckInTimes[bestMatchMember.Id] = now;
                    isNewCheckIn = true;

                    // Ghi nhận nhật ký CheckInLog vào SQLite
                    await RecordCheckInLogAsync(bestMatchMember.Id, highestSimilarity).ConfigureAwait(false);
                }

                // Phát sự kiện Observer về UI và các tầng ngoại vi
                MemberIdentified?.Invoke(this, new MemberIdentifiedEventArgs(bestMatchMember, highestSimilarity, isNewCheckIn));
            }
            else
            {
                // Phát hiện khuôn mặt nhưng không khớp hội viên nào (Khách vãng lai / chưa đăng ký)
                UnidentifiedFaceDetected?.Invoke(this, highestSimilarity);
            }
        }
    }

    /// <summary>
    /// Ghi nhận CheckInLog vào SQLite an toàn đa luồng.
    /// </summary>
    private async Task RecordCheckInLogAsync(Guid memberId, float confidenceScore)
    {
        await _dbLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var log = CheckInLog.Create(memberId, confidenceScore);
            await _dbContext.CheckInLogs.AddAsync(log).ConfigureAwait(false);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);

            Debug.WriteLine($"[AttendanceCoordinator] Đã lưu CheckInLog: Member {memberId}, Score: {confidenceScore:F2}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AttendanceCoordinator] Lỗi khi ghi CheckInLog vào SQLite: {ex.Message}");
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>
    /// Tính độ tương đồng Cosine giữa 2 vector ArcFace 512 chiều.
    /// Với ArcFace, các vector đã được chuẩn hóa L2 (norm = 1), do đó Cosine = Dot Product.
    /// Tốc độ thực thi: < 0.001ms trên CPU với tập lệnh AVX2.
    /// </summary>
    public static float CalculateCosineSimilarity(float[] vectorA, float[] vectorB)
    {
        if (vectorA.Length != 512 || vectorB.Length != 512)
            return 0f;

        float dot = 0f;
        float normA = 0f;
        float normB = 0f;

        for (int i = 0; i < 512; i++)
        {
            float a = vectorA[i];
            float b = vectorB[i];
            dot += a * b;
            normA += a * a;
            normB += b * b;
        }

        if (normA <= 0f || normB <= 0f)
            return 0f;

        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }

    public void Dispose()
    {
        if (_disposed) return;

        Stop();
        _dbLock.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private record CachedMemberEntry(Member Member, List<float[]> Vectors);
}
