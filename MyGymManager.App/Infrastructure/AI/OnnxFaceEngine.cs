using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MyGymManager.Core.Interfaces;
using MyGymManager.Core.Models;
using OpenCvSharp;

namespace MyGymManager.Infrastructure.AI;

/// <summary>
/// Động cơ nhận diện khuôn mặt ONNX Runtime (InsightFace: SCRFD + ArcFace).
/// Thực thi phát hiện khuôn mặt 5 landmarks và trích xuất vector đặc trưng 512 chiều.
/// Tuân thủ nguyên lý Dependency Inversion, tự giải phóng tài nguyên unmanaged OrtValue/Session.
/// </summary>
public class OnnxFaceEngine : IFaceEngine
{
    private readonly InferenceSession _detSession;
    private readonly InferenceSession _recSession;
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private bool _disposed;

    private const int ScrfdInputSize = 640;
    private const int ArcFaceInputSize = 112;

    private static readonly int[] ScrfdStrides = { 8, 16, 32 };
    private static readonly string[] ScrfdScoreNames = { "448", "471", "494" };
    private static readonly string[] ScrfdBboxNames = { "451", "474", "497" };
    private static readonly string[] ScrfdKpsNames = { "454", "477", "500" };

    /// <summary>
    /// Khởi tạo FaceEngine với đường dẫn tự động tìm kiếm hoặc chỉ định trực tiếp.
    /// </summary>
    public OnnxFaceEngine(string? detModelPath = null, string? recModelPath = null)
    {
        var resolvedDetPath = ResolveModelPath(detModelPath, "det_10g.onnx");
        var resolvedRecPath = ResolveModelPath(recModelPath, "w600k_r50.onnx");

        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };

        try
        {
            _detSession = new InferenceSession(resolvedDetPath, sessionOptions);
            _recSession = new InferenceSession(resolvedRecPath, sessionOptions);
        }
        catch (Exception ex)
        {
            sessionOptions.Dispose();
            throw new InvalidOperationException($"Không thể khởi tạo ONNX InferenceSession từ đường dẫn model: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Phát hiện các khuôn mặt trong frame ảnh bằng mô hình SCRFD (det_10g.onnx).
    /// </summary>
    /// <param name="frame">Frame ảnh gốc từ camera.</param>
    /// <returns>Danh sách FaceBox kèm Bounding Box, 5 landmarks và Score.</returns>
    public async Task<List<FaceBox>> DetectFacesAsync(Mat frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame, nameof(frame));

        if (frame.Empty() || frame.Width <= 0 || frame.Height <= 0)
            return new List<FaceBox>();

        return await Task.Run(() =>
        {
            _inferenceLock.Wait();
            try
            {
                return DetectFacesInternal(frame, scoreThreshold: 0.5f, nmsThreshold: 0.4f);
            }
            finally
            {
                _inferenceLock.Release();
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Trích xuất vector đặc trưng 512 chiều từ ảnh mặt crop (112x112) bằng ArcFace (w600k_r50.onnx).
    /// </summary>
    /// <param name="faceCrop">Ảnh khuôn mặt đã căn chỉnh hoặc cắt vuông.</param>
    /// <returns>Vector float[512] đã chuẩn hóa L2 norm = 1.0.</returns>
    public async Task<float[]?> ExtractEmbeddingAsync(Mat faceCrop)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(faceCrop, nameof(faceCrop));

        if (faceCrop.Empty() || faceCrop.Width <= 0 || faceCrop.Height <= 0)
            return null;

        return await Task.Run(() =>
        {
            _inferenceLock.Wait();
            try
            {
                return ExtractEmbeddingInternal(faceCrop);
            }
            finally
            {
                _inferenceLock.Release();
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Tính độ tương đồng Cosine giữa 2 vector khuôn mặt (thang điểm 0.0 -> 1.0).
    /// Giá trị > 0.65 thường biểu thị cùng một người trong InsightFace ArcFace.
    /// </summary>
    public static float CosineSimilarity(float[]? a, float[]? b)
    {
        if (a == null || b == null || a.Length == 0 || a.Length != b.Length)
            return 0f;

        float dot = 0f;
        float normA = 0f;
        float normB = 0f;

        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        float denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        if (denom <= 0f) return 0f;

        float similarity = dot / denom;
        return Math.Clamp(similarity, -1f, 1f);
    }

    #region Internal Processing & SCRFD Decoding

    private List<FaceBox> DetectFacesInternal(Mat frame, float scoreThreshold, float nmsThreshold)
    {
        using var resized = new Mat();
        Cv2.Resize(frame, resized, new Size(ScrfdInputSize, ScrfdInputSize));

        using var rgb = new Mat();
        Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGR2RGB);

        float scaleX = (float)frame.Width / ScrfdInputSize;
        float scaleY = (float)frame.Height / ScrfdInputSize;

        var inputTensor = new DenseTensor<float>(new[] { 1, 3, ScrfdInputSize, ScrfdInputSize });
        byte[] buffer = new byte[ScrfdInputSize * ScrfdInputSize * 3];
        Marshal.Copy(rgb.Data, buffer, 0, buffer.Length);

        for (int y = 0; y < ScrfdInputSize; y++)
        {
            int rowOffset = y * ScrfdInputSize * 3;
            for (int x = 0; x < ScrfdInputSize; x++)
            {
                int px = rowOffset + x * 3;
                inputTensor[0, 0, y, x] = (buffer[px] - 127.5f) / 128.0f;     // R
                inputTensor[0, 1, y, x] = (buffer[px + 1] - 127.5f) / 128.0f; // G
                inputTensor[0, 2, y, x] = (buffer[px + 2] - 127.5f) / 128.0f; // B
            }
        }

        var inputs = new[] { NamedOnnxValue.CreateFromTensor("input.1", inputTensor) };
        using var results = _detSession.Run(inputs);
        var outputDict = results.ToDictionary(r => r.Name, r => r.AsTensor<float>());

        var candidates = new List<FaceBox>();

        for (int i = 0; i < ScrfdStrides.Length; i++)
        {
            int stride = ScrfdStrides[i];
            var scoreTensor = outputDict[ScrfdScoreNames[i]];
            var bboxTensor = outputDict[ScrfdBboxNames[i]];
            var kpsTensor = outputDict[ScrfdKpsNames[i]];

            int featW = ScrfdInputSize / stride;
            const int numAnchors = 2;

            for (int anchorIdx = 0; anchorIdx < scoreTensor.Dimensions[0]; anchorIdx++)
            {
                float score = scoreTensor[anchorIdx, 0];
                if (score < scoreThreshold) continue;

                int gridIdx = anchorIdx / numAnchors;
                int cy = (gridIdx / featW) * stride;
                int cx = (gridIdx % featW) * stride;

                float x1 = (cx - bboxTensor[anchorIdx, 0] * stride) * scaleX;
                float y1 = (cy - bboxTensor[anchorIdx, 1] * stride) * scaleY;
                float x2 = (cx + bboxTensor[anchorIdx, 2] * stride) * scaleX;
                float y2 = (cy + bboxTensor[anchorIdx, 3] * stride) * scaleY;

                var landmarks = new Point2f[5];
                for (int k = 0; k < 5; k++)
                {
                    float lx = (cx + kpsTensor[anchorIdx, 2 * k] * stride) * scaleX;
                    float ly = (cy + kpsTensor[anchorIdx, 2 * k + 1] * stride) * scaleY;
                    landmarks[k] = new Point2f(lx, ly);
                }

                candidates.Add(new FaceBox
                {
                    X1 = Math.Clamp(x1, 0, frame.Width),
                    Y1 = Math.Clamp(y1, 0, frame.Height),
                    X2 = Math.Clamp(x2, 0, frame.Width),
                    Y2 = Math.Clamp(y2, 0, frame.Height),
                    Score = score,
                    Landmarks = landmarks
                });
            }
        }

        return ApplyNms(candidates, nmsThreshold);
    }

    private float[]? ExtractEmbeddingInternal(Mat faceCrop)
    {
        using var resized = new Mat();
        if (faceCrop.Width != ArcFaceInputSize || faceCrop.Height != ArcFaceInputSize)
        {
            Cv2.Resize(faceCrop, resized, new Size(ArcFaceInputSize, ArcFaceInputSize));
        }
        else
        {
            faceCrop.CopyTo(resized);
        }

        using var rgb = new Mat();
        Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGR2RGB);

        var inputTensor = new DenseTensor<float>(new[] { 1, 3, ArcFaceInputSize, ArcFaceInputSize });
        byte[] buffer = new byte[ArcFaceInputSize * ArcFaceInputSize * 3];
        Marshal.Copy(rgb.Data, buffer, 0, buffer.Length);

        for (int y = 0; y < ArcFaceInputSize; y++)
        {
            int rowOffset = y * ArcFaceInputSize * 3;
            for (int x = 0; x < ArcFaceInputSize; x++)
            {
                int px = rowOffset + x * 3;
                inputTensor[0, 0, y, x] = (buffer[px] - 127.5f) / 127.5f;     // R
                inputTensor[0, 1, y, x] = (buffer[px + 1] - 127.5f) / 127.5f; // G
                inputTensor[0, 2, y, x] = (buffer[px + 2] - 127.5f) / 127.5f; // B
            }
        }

        var inputs = new[] { NamedOnnxValue.CreateFromTensor("input.1", inputTensor) };
        using var results = _recSession.Run(inputs);

        var output = results.FirstOrDefault(r => r.Name == "683") ?? results.First();
        var rawEmbedding = output.AsEnumerable<float>().ToArray();

        if (rawEmbedding.Length != 512)
        {
            Debug.WriteLine($"[OnnxFaceEngine] Warning: Embedding dimension mismatch: {rawEmbedding.Length} (expected 512).");
            return null;
        }

        // Chuẩn hóa L2 norm
        float norm = 0f;
        foreach (var v in rawEmbedding) norm += v * v;
        norm = MathF.Sqrt(norm);

        if (norm > 0f)
        {
            for (int i = 0; i < rawEmbedding.Length; i++)
            {
                rawEmbedding[i] /= norm;
            }
        }

        return rawEmbedding;
    }

    private static List<FaceBox> ApplyNms(List<FaceBox> boxes, float nmsThreshold)
    {
        boxes.Sort((a, b) => b.Score.CompareTo(a.Score));
        var result = new List<FaceBox>();

        while (boxes.Count > 0)
        {
            var best = boxes[0];
            result.Add(best);
            boxes.RemoveAt(0);

            boxes.RemoveAll(c => CalculateIoU(best, c) > nmsThreshold);
        }

        return result;
    }

    private static float CalculateIoU(FaceBox a, FaceBox b)
    {
        float x1 = Math.Max(a.X1, b.X1);
        float y1 = Math.Max(a.Y1, b.Y1);
        float x2 = Math.Min(a.X2, b.X2);
        float y2 = Math.Min(a.Y2, b.Y2);

        float interArea = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        float areaA = a.Width * a.Height;
        float areaB = b.Width * b.Height;
        float unionArea = areaA + areaB - interArea;

        return unionArea > 0 ? interArea / unionArea : 0f;
    }

    private static string ResolveModelPath(string? customPath, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
            return customPath;

        var searchLocations = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models", "buffalo_l", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "models", "buffalo_l", fileName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "models", "buffalo_l", fileName),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName)
        };

        foreach (var loc in searchLocations)
        {
            var fullPath = Path.GetFullPath(loc);
            if (File.Exists(fullPath))
                return fullPath;
        }

        throw new FileNotFoundException($"Không tìm thấy file model ONNX '{fileName}'. Vui lòng kiểm tra thư mục 'models/buffalo_l/'.");
    }

    #endregion

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _detSession?.Dispose();
            _recSession?.Dispose();
            _inferenceLock?.Dispose();
        }

        _disposed = true;
    }
}
