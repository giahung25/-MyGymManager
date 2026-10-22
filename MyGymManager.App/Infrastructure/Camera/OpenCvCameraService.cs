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

                    // Phát sự kiện với bản sao Clone() độc lập để subscriber không bị tranh chấp bộ nhớ unmanaged
                    try
                    {
                        FrameCaptured?.Invoke(this, frame.Clone());
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[OpenCvCameraService] Ngoại lệ tại handler FrameCaptured: {ex.Message}");
                    }

                    // Nhường CPU nhẹ nhàng để tránh chiếm dụng toàn bộ tài nguyên
                    await Task.Yield();
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
        if (int.TryParse(source, out int cameraIndex))
        {
            // Thử DirectShow (DSHOW) trước vì độ tương thích và tốc độ tốt nhất trên Windows
            var dshowCapture = new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW);
            if (dshowCapture.IsOpened())
                return dshowCapture;

            dshowCapture.Dispose();

            // Fallback sang API mặc định của hệ thống
            return new VideoCapture(cameraIndex, VideoCaptureAPIs.ANY);
        }

        // Nguồn RTSP, HTTP Stream hoặc đường dẫn video file -> Sử dụng FFMPEG
        var ffmpegCapture = new VideoCapture(source, VideoCaptureAPIs.FFMPEG);
        if (ffmpegCapture.IsOpened())
            return ffmpegCapture;

        ffmpegCapture.Dispose();

        // Fallback sang ANY
        return new VideoCapture(source, VideoCaptureAPIs.ANY);
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
