namespace MyGymManager.Core.Entities;

/// <summary>
/// Thực thể Hội viên (Member) tuân thủ Rich Domain Model.
/// Đóng gói nghiệp vụ gia hạn, kiểm tra hạn thẻ và quản lý vector nhận diện khuôn mặt (Multi-Template FaceID).
/// </summary>
public class Member
{
    public Guid Id { get; private set; }
    public string FullName { get; private set; } = string.Empty;
    public string PhoneNumber { get; private set; } = string.Empty;
    public string Gender { get; private set; } = "Nam";
    public string? AvatarPath { get; private set; }
    public Guid? PackageId { get; private set; }
    public Package? Package { get; private set; }

    public DateTime StartDate { get; private set; }
    public DateTime EndDate { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }

    /// <summary>
    /// Danh sách các vector đặc trưng khuôn mặt 512 chiều (ArcFace embedding).
    /// Hỗ trợ Multi-Template (chụp nhiều góc: thẳng, nghiêng trái, nghiêng phải).
    /// </summary>
    public List<float[]> FaceVectors { get; private set; } = new();

    /// <summary>
    /// Mã định danh thẻ từ RFID / NFC (Mifare 13.56MHz UID).
    /// </summary>
    public string? CardUid { get; private set; }

    /// <summary>
    /// Chuỗi mã hóa dữ liệu mẫu vân tay sinh trắc học.
    /// </summary>
    public string? FingerprintData { get; private set; }

    /// <summary>
    /// Mã định danh QR Code thành viên (quét từ app di động).
    /// </summary>
    public string? QrKey { get; private set; }
    // Navigation property lịch sử điểm danh
    public ICollection<CheckInLog> CheckInLogs { get; private set; } = new List<CheckInLog>();

    // Constructor private dành riêng cho EF Core
    private Member() { }

    /// <summary>
    /// Factory Method khởi tạo Hội viên mới.
    /// </summary>
    public static Member Create(
        string fullName,
        string phoneNumber,
        int packageDays,
        float[]? initialFaceVector = null,
        Guid? packageId = null,
        string gender = "Nam",
        string? cardUid = null,
        string? fingerprintData = null,
        string? qrKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName, nameof(fullName));
        ArgumentException.ThrowIfNullOrWhiteSpace(phoneNumber, nameof(phoneNumber));
        if (packageDays <= 0)
            throw new ArgumentOutOfRangeException(nameof(packageDays), "Số ngày gói tập phải lớn hơn 0.");

        if (initialFaceVector != null && initialFaceVector.Length != 512)
            throw new ArgumentException("Vector khuôn mặt ban đầu phải có đúng 512 chiều.", nameof(initialFaceVector));

        var now = DateTime.UtcNow;
        var member = new Member
        {
            Id = Guid.NewGuid(),
            FullName = fullName.Trim(),
            PhoneNumber = phoneNumber.Trim(),
            Gender = string.IsNullOrWhiteSpace(gender) ? "Nam" : gender.Trim(),
            PackageId = packageId,
            CardUid = string.IsNullOrWhiteSpace(cardUid) ? null : cardUid.Trim(),
            FingerprintData = string.IsNullOrWhiteSpace(fingerprintData) ? null : fingerprintData.Trim(),
            QrKey = string.IsNullOrWhiteSpace(qrKey) ? null : qrKey.Trim(),
            StartDate = now,
            EndDate = now.AddDays(packageDays),
            IsActive = true,
            CreatedAt = now
        };

        if (initialFaceVector != null)
        {
            member.FaceVectors.Add(initialFaceVector);
        }

        return member;
    }

    /// <summary>
    /// Gia hạn gói tập.
    /// Nếu thẻ còn hạn thì cộng dồn ngày; nếu đã hết hạn thì tính từ thời điểm gia hạn.
    /// </summary>
    public void Renew(int extraDays)
    {
        if (extraDays <= 0)
            throw new ArgumentOutOfRangeException(nameof(extraDays), "Số ngày gia hạn phải lớn hơn 0.");

        var now = DateTime.UtcNow;
        StartDate = IsExpired() ? now : StartDate;
        EndDate = IsExpired() ? now.AddDays(extraDays) : EndDate.AddDays(extraDays);
        IsActive = true;
    }

    /// <summary>
    /// Kiểm tra trạng thái hết hạn thẻ hội viên.
    /// </summary>
    public bool IsExpired() => DateTime.UtcNow > EndDate;

    /// <summary>
    /// Số ngày còn lại của gói tập (trả về 0 nếu đã hết hạn).
    /// </summary>
    public int RemainingDays()
    {
        if (IsExpired()) return 0;
        return (int)Math.Ceiling((EndDate - DateTime.UtcNow).TotalDays);
    }

    /// <summary>
    /// Bổ sung vector khuôn mặt mới (tối đa 5 templates góc chụp).
    /// </summary>
    public void AddFaceVector(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector, nameof(vector));
        if (vector.Length != 512)
            throw new ArgumentException("Vector khuôn mặt phải có đúng 512 chiều.", nameof(vector));

        if (FaceVectors.Count >= 5)
        {
            // Thay thế template cũ nhất nếu đã đạt giới hạn 5 góc
            FaceVectors.RemoveAt(0);
        }

        FaceVectors.Add(vector);
    }

    /// <summary>
    /// Cập nhật thông tin hồ sơ hội viên.
    /// </summary>
    public void UpdateProfile(string fullName, string phoneNumber, string gender = "Nam")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName, nameof(fullName));
        ArgumentException.ThrowIfNullOrWhiteSpace(phoneNumber, nameof(phoneNumber));

        FullName = fullName.Trim();
        PhoneNumber = phoneNumber.Trim();
        Gender = string.IsNullOrWhiteSpace(gender) ? "Nam" : gender.Trim();
    }

    /// <summary>
    /// Gán gói tập cụ thể cho hội viên.
    /// </summary>
    public void AssignPackage(Package package)
    {
        ArgumentNullException.ThrowIfNull(package, nameof(package));
        PackageId = package.Id;
        Package = package;
    }

    /// <summary>
    /// Khóa / Vô hiệu hóa thẻ hội viên.
    /// </summary>
    public void Deactivate() => IsActive = false;

    /// <summary>
    /// Kích hoạt lại thẻ hội viên.
    /// </summary>
    public void Activate() => IsActive = true;

    public void AssignCardUid(string? cardUid) => CardUid = string.IsNullOrWhiteSpace(cardUid) ? null : cardUid.Trim();
    public void AssignFingerprint(string? fingerprint) => FingerprintData = string.IsNullOrWhiteSpace(fingerprint) ? null : fingerprint.Trim();
    public void AssignQrKey(string? qrKey) => QrKey = string.IsNullOrWhiteSpace(qrKey) ? null : qrKey.Trim();
}
