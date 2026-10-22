namespace MyGymManager.Core.Interfaces;

using OpenCvSharp;

/// <summary>
/// Interface dịch vụ Camera (OpenCvSharp4) tuân thủ Dependency Inversion Principle.
/// Cho phép hoán đổi linh hoạt giữa Webcam USB và Camera IP RTSP mà không ảnh hưởng tầng nghiệp vụ.
/// </summary>
public interface ICameraService : IDisposable
{
    /// <summary>
    /// Sự kiện bắn ra khi có frame hình ảnh mới được chụp thành công từ camera (30-60 FPS).
    /// </summary>
    event EventHandler<Mat>? FrameCaptured;

    /// <summary>
    /// Bắt đầu stream hình ảnh từ nguồn chỉ định.
    /// </summary>
    /// <param name="source">"0" hoặc "1" cho Webcam USB; "rtsp://..." hoặc "http://..." cho IP Camera.</param>
    Task StartAsync(string source);

    /// <summary>
    /// Dừng stream và giải phóng tài nguyên phần cứng.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Trạng thái hoạt động của camera stream.
    /// </summary>
    bool IsRunning { get; }
}
