using System.Diagnostics;
using MyGymManager.Core.Interfaces;
using OpenCvSharp;

namespace MyGymManager.Infrastructure.Camera;

/// <summary>
/// Dịch vụ Camera thực thi qua thư viện OpenCvSharp4.
/// Hoạt động trên background Task độc lập với CancellationToken,
/// hỗ trợ cả Webcam USB (DirectShow index) và RTSP IP Camera.
/// Thiết kế theo triết lý Zero-Crash: tự động phục hồi và giải phóng tài nguyên unmanaged.
/// </summary>
public class OpenCvCameraService : ICameraService
{
    private readonly object _syncLock = new();
    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private volatile bool _isRunning;
    private bool _disposed;

    /// <summary>
    /// Sự kiện bắn ra khi có frame hình ảnh mới được chụp thành công.
    /// Mat phát ra là một bản Clone() độc lập, subscriber cần Dispose sau khi hoàn tất xử lý.
    /// </summary>
    public event EventHandler<Mat>? FrameCaptured;

    /// <summary>
    /// Trạng thái đang hoạt động của luồng đọc camera.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Bắt đầu stream hình ảnh từ nguồn chỉ định trên background task độc lập.
    /// </summary>
    /// <param name="source">"0", "1" cho Webcam USB hoặc URL "rtsp://..." cho Camera IP.</param>
    public async Task StartAsync(string source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(source, nameof(source));

        if (_isRunning)
        {
            await StopAsync().ConfigureAwait(false);
        }

        lock (_syncLock)
        {
            _cts = new CancellationTokenSource();
            _isRunning = true;
            var token = _cts.Token;
            _captureTask = Task.Run(() => CaptureLoopAsync(source.Trim(), token), token);
        }
    }

    /// <summary>
    /// Dừng stream camera an toàn và giải phóng tài nguyên.
    /// </summary>
    public async Task StopAsync()
    {
        Task? taskToWait = null;

        lock (_syncLock)
        {
            if (!_isRunning)
                return;

            _isRunning = false;
            _cts?.Cancel();
            taskToWait = _captureTask;
        }

        if (taskToWait != null)
        {
            try
            {
                await taskToWait.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Bỏ qua ngoại lệ hủy tác vụ bình thường
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OpenCvCameraService] Lỗi khi chờ dừng tác vụ: {ex.Message}");
            }
        }

        lock (_syncLock)
        {
            _cts?.Dispose();
            _cts = null;
            _captureTask = null;
        }
    }

    /// <summary>
    /// Vòng lặp đọc frame liên tục chạy trên luồng nền (Zero-Crash).
    /// </summary>
    private async Task CaptureLoopAsync(string source, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            VideoCapture? capture = null;

            try
            {
                capture = CreateVideoCapture(source);

                if (!capture.IsOpened())
                {
                    Debug.WriteLine($"[OpenCvCameraService] Không thể mở nguồn camera: '{source}'. Thử lại sau 2 giây...");
                    capture.Dispose();
                    capture = null;

                    await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // Cấu hình giảm tối đa độ trễ đệm (latency) cho RTSP / Webcam
                try
                {
                    capture.Set(VideoCaptureProperties.BufferSize, 1);
                }
                catch
                {
                    // Một số backend driver không hỗ trợ BufferSize, bỏ qua an toàn
                }

                using var frame = new Mat();
                var frameTimer = Stopwatch.StartNew();

                while (!cancellationToken.IsCancellationRequested)
                {
                    bool success = false;

                    try
                    {
                        success = capture.Read(frame);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[OpenCvCameraService] Ngoại lệ khi capture.Read: {ex.Message}");
                        break;
                    }

                    if (!success || frame.Empty())
                    {
                        Debug.WriteLine("[OpenCvCameraService] Frame rỗng hoặc mất tín hiệu kết nối camera.");
                        await Task.Delay(200, cancellationToken).ConfigureAwait(false);

                        if (!capture.IsOpened())
                        {
                            break; // Thoát vòng lặp trong để reconnect
                        }

                        continue;
                    }

                    // Phát sự kiện với bản sao Clone() độc lập, tự giải phóng sau khi các subscriber đồng bộ hoàn tất
                    try
                    {
                        using var broadcastFrame = frame.Clone();
                        FrameCaptured?.Invoke(this, broadcastFrame);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[OpenCvCameraService] Ngoại lệ tại handler FrameCaptured: {ex.Message}");
                    }

                    long elapsedMs = frameTimer.ElapsedMilliseconds;
                    if (elapsedMs < 30)
                    {
                        await Task.Delay((int)(30 - elapsedMs), cancellationToken).ConfigureAwait(false);
                    }
                    frameTimer.Restart();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OpenCvCameraService] Lỗi không mong muốn trong CaptureLoop: {ex.Message}. Tự phục hồi sau 2 giây...");
                try
                {
                    await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                if (capture != null)
                {
                    try
                    {
                        capture.Release();
                        capture.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[OpenCvCameraService] Lỗi khi giải phóng VideoCapture: {ex.Message}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Khởi tạo VideoCapture tối ưu cho từng loại nguồn (Webcam USB DirectShow hoặc RTSP FFMPEG).
    /// </summary>
    private static VideoCapture CreateVideoCapture(string source)
    {
        if (int.TryParse(source, out int preferredIndex))
        {
            // Tự động thử index ưu tiên trước, sau đó thử các index khác (1, 0, 2, 3)
            var indicesToTry = new List<int> { preferredIndex };
            if (!indicesToTry.Contains(1)) indicesToTry.Add(1);
            if (!indicesToTry.Contains(0)) indicesToTry.Add(0);
            if (!indicesToTry.Contains(2)) indicesToTry.Add(2);

            foreach (int idx in indicesToTry)
            {
                // 1. Ưu tiên Microsoft Media Foundation (MSMF) - Tốc độ cao nhất, không bị block trên Windows 11
                try
                {
                    var msmfCap = new VideoCapture(idx, VideoCaptureAPIs.MSMF);
                    if (msmfCap.IsOpened())
                    {
                        ConfigureUsbWebcam(msmfCap);
                        Debug.WriteLine($"[OpenCvCameraService] Đã mở thành công Camera Index {idx} qua MSMF.");
                        return msmfCap;
                    }
                    msmfCap.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[OpenCvCameraService] MSMF index {idx} lỗi: {ex.Message}");
                }

                // 2. Thử API mặc định hệ thống (ANY)
                try
                {
                    var anyCap = new VideoCapture(idx, VideoCaptureAPIs.ANY);
                    if (anyCap.IsOpened())
                    {
                        ConfigureUsbWebcam(anyCap);
                        Debug.WriteLine($"[OpenCvCameraService] Đã mở thành công Camera Index {idx} qua ANY.");
                        return anyCap;
                    }
                    anyCap.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[OpenCvCameraService] ANY index {idx} lỗi: {ex.Message}");
                }
            }

            return new VideoCapture(preferredIndex, VideoCaptureAPIs.ANY);
        }

        // Nguồn RTSP, HTTP Stream hoặc đường dẫn video file -> Sử dụng FFMPEG
        var ffmpegCapture = new VideoCapture(source, VideoCaptureAPIs.FFMPEG);
        if (ffmpegCapture.IsOpened())
        {
            try { ffmpegCapture.Set(VideoCaptureProperties.BufferSize, 1); } catch { }
            return ffmpegCapture;
        }

        ffmpegCapture.Dispose();

        // Fallback sang ANY
        var anySourceCapture = new VideoCapture(source, VideoCaptureAPIs.ANY);
        try { anySourceCapture.Set(VideoCaptureProperties.BufferSize, 1); } catch { }
        return anySourceCapture;
    }

    /// <summary>
    /// Cấu hình độ phân giải 1280x720 và 30 FPS cho Webcam USB.
    /// </summary>
    private static void ConfigureUsbWebcam(VideoCapture capture)
    {
        try
        {
            capture.Set(VideoCaptureProperties.FrameWidth, 1280);
            capture.Set(VideoCaptureProperties.FrameHeight, 720);
            capture.Set(VideoCaptureProperties.Fps, 30);
            capture.Set(VideoCaptureProperties.BufferSize, 1);
            Debug.WriteLine("[OpenCvCameraService] Đã cấu hình USB Webcam: 1280x720 @ 30 FPS (BufferSize=1)");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OpenCvCameraService] Không thể thiết lập thông số tối ưu webcam: {ex.Message}");
        }
    }
    /// <summary>
    /// Giải phóng toàn bộ tài nguyên unmanaged và kết thúc tác vụ nền.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            try
            {
                StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OpenCvCameraService] Lỗi khi Dispose: {ex.Message}");
            }
        }

        _disposed = true;
    }
}
