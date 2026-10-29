namespace MyGymManager.Core.Entities;

using MyGymManager.Core.Interfaces;

/// <summary>
/// Thực thể Lịch sử Điểm danh (CheckInLog) tuân thủ Rich Domain Model.
/// Ghi nhận thời điểm vào/ra phòng tập và độ tin cậy nhận diện khuôn mặt (Confidence Score).
/// </summary>
public class CheckInLog
{
    public Guid Id { get; private set; }
    public Guid MemberId { get; private set; }
    public Member? Member { get; private set; }

    public DateTime CheckInTime { get; private set; }
    public DateTime? CheckOutTime { get; private set; }

    /// <summary>
    /// Điểm tin cậy so khớp khuôn mặt Cosine Similarity (thang 0.0 -> 1.0).
    /// </summary>
    public float ConfidenceScore { get; private set; }

    /// <summary>
    /// Phương thức xác thực đã sử dụng (Khuôn mặt, Thẻ từ, Vân tay, Mã QR, Thủ công).
    /// </summary>
    public IdentificationMethod Method { get; private set; } = IdentificationMethod.FaceRecognition;

    // Constructor private dành riêng cho EF Core
    private CheckInLog() { }

    /// <summary>
    /// Factory Method tạo lượt điểm danh mới khi nhận diện thành công.
    /// </summary>
    public static CheckInLog Create(
        Guid memberId,
        float confidenceScore,
        DateTime? checkInTime = null,
        IdentificationMethod method = IdentificationMethod.FaceRecognition)
    {
        if (memberId == Guid.Empty)
            throw new ArgumentException("MemberId không được là Guid rỗng.", nameof(memberId));
        if (confidenceScore < 0f || confidenceScore > 1f)
            throw new ArgumentOutOfRangeException(nameof(confidenceScore), "Độ tin cậy nhận diện phải nằm trong khoảng [0.0, 1.0].");

        return new CheckInLog
        {
            Id = Guid.NewGuid(),
            MemberId = memberId,
            CheckInTime = checkInTime ?? DateTime.UtcNow,
            ConfidenceScore = confidenceScore,
            Method = method
        };
    }

    /// <summary>
    /// Ghi nhận thời điểm ra về (Check-out).
    /// </summary>
    public void CheckOut(DateTime? checkOutTime = null)
    {
        var time = checkOutTime ?? DateTime.UtcNow;
        if (time < CheckInTime)
            throw new InvalidOperationException("Thời điểm check-out không thể trước thời điểm check-in.");

        CheckOutTime = time;
    }
}
