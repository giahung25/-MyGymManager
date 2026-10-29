namespace MyGymManager.Infrastructure.Providers;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics.Tensors;
using MyGymManager.Core.Entities;
using MyGymManager.Core.Interfaces;
using MyGymManager.Core.Models;
using OpenCvSharp;

/// <summary>
/// Provider xác minh danh tính qua nhận diện khuôn mặt thời gian thực (Pluggable Identity Provider).
/// Kế thừa hợp đồng IIdentificationProvider, tích hợp ICameraService và IFaceEngine (InsightFace: SCRFD + ArcFace).
/// Thực thi so khớp Cosine Similarity in-memory với SIMD AVX2 và bắn sự kiện IdentificationOccurred.
/// </summary>
public class FaceIdentificationProvider : IIdentificationProvider
{
    private readonly ICameraService _cameraService;
    private readonly IFaceEngine _faceEngine;

    // Bộ nhớ đệm lưu các mẫu khuôn mặt (Identifier -> List of 512-d ArcFace vectors)
    private readonly ConcurrentDictionary<string, FaceTemplateEntry> _templates = new();

    // Debounce Cooldown: Chống bắn lặp sự kiện cho cùng một hội viên trong khoảng thời gian nhất định
    private readonly ConcurrentDictionary<string, DateTime> _lastIdentifiedTimes = new();
    private DateTime _lastStrangerTime = DateTime.MinValue;
    private readonly object _strangerLock = new();

    // Điều khiển luồng và drop-frame
    private int _isProcessingFrame = 0;
    private DateTime _lastInferenceTime = DateTime.MinValue;
    private volatile bool _isRunning = false;
    private bool _isSubscribed = false;
    private bool _disposed = false;

    public string ProviderId { get; }
    public string ProviderName { get; }
    public IdentificationMethod Method => IdentificationMethod.FaceRecognition;
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Ngưỡng tương đồng Cosine Similarity tối thiểu để xác nhận khuôn mặt trùng khớp (mặc định 0.65).
    /// </summary>
    public float SimilarityThreshold { get; set; } = 0.55f;

    /// <summary>
    /// Thời gian giãn cách giữa các lần xác thực thành công của cùng một định danh (mặc định 10 giây).
    /// </summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Thời gian giãn cách báo khách lạ để chống spam sự kiện (mặc định 3 giây).
    /// </summary>
    public TimeSpan StrangerCooldown { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Chu kỳ tối thiểu giữa các lần chạy inference AI để tiết kiệm CPU (mặc định 150ms).
    /// </summary>
    public int MinInferenceIntervalMs { get; set; } = 150;

    /// <summary>
    /// Số lượng template danh tính đang được nạp trong bộ nhớ RAM.
    /// </summary>
    public int TemplatesCount => _templates.Count;

    /// <summary>
    /// Sự kiện bắn ra khi provider xác thực được danh tính (thành công hoặc không khớp).
    /// </summary>
    public event EventHandler<IdentificationResult>? IdentificationOccurred;

    /// <summary>
    /// Sự kiện tùy chọn thông báo tọa độ Bounding Box của khuôn mặt nổi bật nhất trong khung hình.
    /// </summary>
    public event EventHandler<FaceBox?>? FaceDetected;

    public FaceIdentificationProvider(
        ICameraService cameraService,
        IFaceEngine faceEngine,
        string? providerId = null,
        string? providerName = null)
    {
        _cameraService = cameraService ?? throw new ArgumentNullException(nameof(cameraService));
        _faceEngine = faceEngine ?? throw new ArgumentNullException(nameof(faceEngine));
        ProviderId = string.IsNullOrWhiteSpace(providerId) ? "face_camera_main" : providerId.Trim();
        ProviderName = string.IsNullOrWhiteSpace(providerName) ? "Camera Nhận Diện Khuôn Mặt (InsightFace)" : providerName.Trim();
    }

    #region Template Management

    /// <summary>
    /// Đăng ký hoặc cập nhật template khuôn mặt cho một mã định danh (MemberId / UserCode).
    /// </summary>
    public void RegisterTemplate(string identifier, List<float[]> vectors, string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier, nameof(identifier));
        ArgumentNullException.ThrowIfNull(vectors, nameof(vectors));

        var validVectors = vectors.Where(v => v != null && v.Length == 512).ToList();
        if (validVectors.Count > 0)
        {
            _templates[identifier] = new FaceTemplateEntry(identifier, validVectors, displayName);
        }
        else
        {
            _templates.TryRemove(identifier, out _);
        }
    }

    /// <summary>
    /// Tiện ích đăng ký template trực tiếp từ thực thể Member.
    /// </summary>
    public void RegisterMember(Member member)
    {
        if (member == null) return;

        if (member.IsActive && member.FaceVectors.Count > 0)
        {
            RegisterTemplate(member.Id.ToString(), member.FaceVectors, member.FullName);
        }
        else
        {
            UnregisterTemplate(member.Id.ToString());
        }
    }

    /// <summary>
    /// Xóa template khỏi bộ nhớ RAM.
    /// </summary>
    public void UnregisterTemplate(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return;
        _templates.TryRemove(identifier, out _);
        _lastIdentifiedTimes.TryRemove(identifier, out _);
    }

    /// <summary>
    /// Xóa toàn bộ template khỏi bộ nhớ RAM.
    /// </summary>
    public void ClearTemplates()
    {
        _templates.Clear();
        _lastIdentifiedTimes.Clear();
    }

    #endregion

    #region Lifecycle

    /// <summary>
    /// Khởi động provider và bắt đầu lắng nghe luồng camera.
    /// </summary>
    public Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_isSubscribed)
        {
            _cameraService.FrameCaptured += OnCameraFrameCaptured;
            _isSubscribed = true;
        }

        _isRunning = true;
        Debug.WriteLine($"[FaceIdentificationProvider] Provider '{ProviderId}' đã khởi động.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tạm dừng provider và ngắt kết nối với luồng camera.
    /// </summary>
    public Task StopAsync()
    {
        if (_isSubscribed)
        {
            _cameraService.FrameCaptured -= OnCameraFrameCaptured;
            _isSubscribed = false;
        }

        _isRunning = false;
        FaceDetected?.Invoke(this, null);
        Debug.WriteLine($"[FaceIdentificationProvider] Provider '{ProviderId}' đã tạm dừng.");
        return Task.CompletedTask;
    }

    #endregion

    #region Frame Processing & AI Vector Matching

    private void OnCameraFrameCaptured(object? sender, Mat frame)
    {
        if (!_isRunning || frame.Empty() || _templates.IsEmpty)
            return;

        // Tiết kiệm CPU: Throttling chu kỳ inference
        var now = DateTime.UtcNow;
        if ((now - _lastInferenceTime).TotalMilliseconds < MinInferenceIntervalMs)
        {
            return;
        }

        // Drop-frame cơ chế nguyên tử: Bỏ qua nếu vòng suy luận trước chưa hoàn tất
        if (Interlocked.CompareExchange(ref _isProcessingFrame, 1, 0) != 0)
        {
            return;
        }

        _lastInferenceTime = now;
        var aiFrame = frame.Clone();

        _ = Task.Run(async () =>
        {
            try
            {
                await ProcessFrameCoreAsync(aiFrame).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FaceIdentificationProvider] Lỗi xử lý frame: {ex.Message}");
            }
            finally
            {
                aiFrame.Dispose();
                Interlocked.Exchange(ref _isProcessingFrame, 0);
            }
        });
    }

    private async Task ProcessFrameCoreAsync(Mat frame)
    {
        if (!_isRunning || frame.Empty() || _templates.IsEmpty)
            return;

        // 1. SCRFD phát hiện toàn bộ khuôn mặt trong khung hình
        var detectedFaces = await _faceEngine.DetectFacesAsync(frame).ConfigureAwait(false);
        if (detectedFaces == null || detectedFaces.Count == 0)
        {
            FaceDetected?.Invoke(this, null);
            return;
        }

        var primaryFace = detectedFaces.OrderByDescending(f => f.Width * f.Height).First();
        FaceTemplateEntry? bestMatchedEntry = null;
        float bestMatchSimilarity = 0f;
        FaceBox? matchedFaceBox = null;
        float maxStrangerSimilarity = 0f;

        // 2. Trích xuất vector ArcFace và so khớp với kho template
        foreach (var faceBox in detectedFaces)
        {
            // Trích xuất vector 512-d qua ArcFace kết hợp căn chỉnh Affine 5 landmarks chuẩn InsightFace
            var embedding = await _faceEngine.ExtractEmbeddingAsync(frame, faceBox.Landmarks).ConfigureAwait(false);
            if (embedding == null || embedding.Length != 512)
                continue;

            // So khớp Cosine Similarity với kho vector
            foreach (var (_, entry) in _templates)
            {
                foreach (var templateVector in entry.Vectors)
                {
                    float similarity = TensorPrimitives.CosineSimilarity(embedding, templateVector);
                    if (similarity > bestMatchSimilarity)
                    {
                        bestMatchSimilarity = similarity;
                        bestMatchedEntry = entry;
                        matchedFaceBox = faceBox;
                    }
                }
            }

            if (bestMatchSimilarity < SimilarityThreshold && bestMatchSimilarity > maxStrangerSimilarity)
            {
                maxStrangerSimilarity = bestMatchSimilarity;
            }
        }

        var now = DateTime.UtcNow;

        // 3. Phân định kết quả và bắn sự kiện IdentificationOccurred
        if (bestMatchedEntry != null && bestMatchSimilarity >= SimilarityThreshold)
        {
            FaceDetected?.Invoke(this, matchedFaceBox);

            // Kiểm tra Cooldown chống spam cùng một định danh
            if (!_lastIdentifiedTimes.TryGetValue(bestMatchedEntry.Identifier, out var lastTime) || (now - lastTime) >= Cooldown)
            {
                _lastIdentifiedTimes[bestMatchedEntry.Identifier] = now;

                var result = new IdentificationResult(
                    IsSuccess: true,
                    Identifier: bestMatchedEntry.Identifier,
                    Method: IdentificationMethod.FaceRecognition,
                    ConfidenceScore: bestMatchSimilarity,
                    Timestamp: now,
                    DisplayName: bestMatchedEntry.DisplayName,
                    ErrorMessage: null
                );

                IdentificationOccurred?.Invoke(this, result);
            }
        }
        else
        {
            FaceDetected?.Invoke(this, primaryFace);

            // Kiểm tra StrangerCooldown chống spam khách lạ
            bool shouldReportStranger = false;
            lock (_strangerLock)
            {
                if ((now - _lastStrangerTime) >= StrangerCooldown)
                {
                    _lastStrangerTime = now;
                    shouldReportStranger = true;
                }
            }

            if (shouldReportStranger)
            {
                var strangerResult = new IdentificationResult(
                    IsSuccess: false,
                    Identifier: string.Empty,
                    Method: IdentificationMethod.FaceRecognition,
                    ConfidenceScore: maxStrangerSimilarity,
                    Timestamp: now,
                    DisplayName: "Khách chưa đăng ký",
                    ErrorMessage: "Không tìm thấy khuôn mặt tương ứng trong cơ sở dữ liệu."
                );

                IdentificationOccurred?.Invoke(this, strangerResult);
            }
        }
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;

        StopAsync().GetAwaiter().GetResult();
        ClearTemplates();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private record FaceTemplateEntry(string Identifier, List<float[]> Vectors, string? DisplayName);
}
