namespace MyGymManager.Core.Interfaces;

/// <summary>
/// Các phương thức xác minh danh tính được hỗ trợ trong hệ thống (Pluggable Multi-Biometrics).
/// </summary>
public enum IdentificationMethod
{
    FaceRecognition = 0, // Nhận diện khuôn mặt (Camera + InsightFace)
    Fingerprint = 1,     // Máy quét vân tay sinh trắc học
    RfidCard = 2,        // Thẻ từ RFID / NFC (Mifare 13.56MHz)
    QrCode = 3,          // Mã QR thành viên trên điện thoại
    Manual = 4           // Điểm danh thủ công qua SĐT
}

/// <summary>
/// DTO chứa kết quả xác minh danh tính từ một thiết bị phần cứng hoặc thuật toán sinh trắc học.
/// </summary>
public record IdentificationResult(
    bool IsSuccess,
    string Identifier,              // MemberId / CardUid / FingerprintId / QrKey / PhoneNumber
    IdentificationMethod Method,    // Phương thức xác minh
    float ConfidenceScore,          // Độ tin cậy (0.0 -> 1.0)
    DateTime Timestamp,
    string? DisplayName = null,
    string? ErrorMessage = null
);

/// <summary>
/// Interface hợp đồng chung cho mọi thiết bị / phương thức xác minh danh tính (Pluggable Provider Pattern).
/// Tách rời hoàn toàn Tầng Quản Lý Gym và Tầng Phần Cứng Điểm Danh.
/// </summary>
public interface IIdentificationProvider : IDisposable
{
    /// <summary>
    /// Định danh duy nhất của provider (ví dụ: "face_camera_1", "rfid_usb_reader", "zk_fingerprint").
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Tên hiển thị thân thiện trên giao diện quản trị.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Phương thức xác minh danh tính mà provider này phụ trách.
    /// </summary>
    IdentificationMethod Method { get; }

    /// <summary>
    /// Trạng thái hoạt động của thiết bị / provider.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Sự kiện bắn ra khi thiết bị thu thập và xác thực được danh tính hội viên.
    /// </summary>
    event EventHandler<IdentificationResult>? IdentificationOccurred;

    /// <summary>
    /// Kích hoạt thiết bị / luồng đọc dữ liệu.
    /// </summary>
    Task StartAsync();

    /// <summary>
    /// Tạm dừng / ngắt kết nối thiết bị.
    /// </summary>
    Task StopAsync();
}
