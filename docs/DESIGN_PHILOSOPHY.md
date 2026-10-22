# 5 TRIẾT LÝ THIẾT KẾ HƯỚNG ĐỐI TƯỢNG (IN4211) — ÁP DỤNG VÀO MYGYMMANAGER DESKTOP

Tài liệu này chuẩn hóa toàn bộ các nguyên tắc kiến trúc và mẫu thiết kế từ môn học **Phát triển phần mềm hướng đối tượng (IN4211 - ThS. Nguyễn Tuấn Kiệt)** để áp dụng trực tiếp vào dự án **MyGymManager Desktop (.NET 10 WPF Standalone)**.

---

## 🏛️ TRIẾT LÝ 1: RICH DOMAIN MODEL & ENCAPSULATION (CHƯƠNG 1)

### 1.1 Nguyên lý
- Loại bỏ hoàn toàn **Anemic Domain Model** (Model nghèo nàn chỉ chứa thuộc tính `{ get; set; }`).
- Entity phải tự chịu trách nhiệm bảo vệ tính đúng đắn dữ liệu (Invariants) của chính nó thông qua đóng gói (`private set`).
- Khởi tạo thông qua Factory Method tĩnh (`Member.Create(...)`).

### 1.2 Minh họa code C# trong MyGymManager
```csharp
namespace MyGymManager.Core.Entities;

public class Member
{
    public Guid Id { get; private set; }
    public string FullName { get; private set; } = string.Empty;
    public string PhoneNumber { get; private set; } = string.Empty;
    public DateTime StartDate { get; private set; }
    public DateTime EndDate { get; private set; }
    public bool IsActive { get; private set; }
    public List<float[]> FaceVectors { get; private set; } = new();

    private Member() { } // Dành riêng cho EF Core

    public static Member Create(string fullName, string phone, int packageDays, float[] initialFaceVector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName, "Tên hội viên không được để trống");
        ArgumentException.ThrowIfNullOrWhiteSpace(phone, "Số điện thoại không được để trống");
        if (initialFaceVector == null || initialFaceVector.Length != 512)
            throw new ArgumentException("Vector khuôn mặt ban đầu phải chuẩn 512 chiều");

        var member = new Member
        {
            Id = Guid.NewGuid(),
            FullName = fullName.Trim(),
            PhoneNumber = phone.Trim(),
            StartDate = DateTime.UtcNow,
            EndDate = DateTime.UtcNow.AddDays(packageDays),
            IsActive = true
        };
        member.FaceVectors.Add(initialFaceVector);
        return member;
    }

    public void Renew(int extraDays)
    {
        if (extraDays <= 0) throw new ArgumentException("Số ngày gia hạn phải lớn hơn 0");
        // Nếu gói cũ còn hạn thì cộng dồn, nếu đã hết hạn thì tính từ thời điểm gia hạn
        StartDate = IsExpired() ? DateTime.UtcNow : StartDate;
        EndDate = IsExpired() ? DateTime.UtcNow.AddDays(extraDays) : EndDate.AddDays(extraDays);
        IsActive = true;
    }

    public bool IsExpired() => DateTime.UtcNow > EndDate;
}
```

---

## ⚡ TRIẾT LÝ 2: SINGLE RESPONSIBILITY PRINCIPLE - SRP (CHƯƠNG 2)

### 2.1 Nguyên lý
Mỗi class chỉ có một trách nhiệm duy nhất và chỉ có một lý do duy nhất để thay đổi. Tách rời triệt để luồng đọc Camera, luồng suy luận AI và luồng xử lý CSDL.

### 2.2 Phân rã trách nhiệm trong MyGymManager
| Lớp / Dịch vụ | Trách nhiệm duy nhất (Single Responsibility) | Không được làm gì |
|---|---|---|
| `CameraService` | Đọc video 30-60 FPS từ RTSP/USB và bắn ra Frame ảnh thô | Không được gọi AI, không được ghi CSDL |
| `FaceDetectionService` | Dùng model `det_10g` phát hiện vị trí khuôn mặt và 5 điểm mốc | Không biết camera đến từ đâu |
| `FaceRecognitionService`| Dùng model `w600k_r50` trích xuất 512-dim embedding và tính Cosine Similarity | Không đụng đến logic giao diện WPF |
| `AttendanceCoordinator`| Nhận diện kết quả, kiểm tra hạn thẻ và ghi log check-in | Không xử lý việc render hình ảnh |

---

## 🔌 TRIẾT LÝ 3: DEPENDENCY INVERSION & ABSTRACTION (CHƯƠNG 2 & 3)

### 3.1 Nguyên lý
- Lớp cấp cao không phụ thuộc lớp cấp thấp; cả hai đều phụ thuộc vào `Interface`.
- Mọi phụ thuộc được truyền từ ngoài vào qua Constructor (Constructor Injection) thông qua Microsoft DI Container.

### 3.2 Minh họa Interface hoán đổi phần cứng
```csharp
namespace MyGymManager.Core.Interfaces;

public interface ICameraService : IDisposable
{
    event EventHandler<Mat> FrameCaptured;
    Task StartAsync(string source); // "0" cho Webcam, "rtsp://..." cho Camera IP
    Task StopAsync();
    bool IsRunning { get; }
}

public interface IFaceEngine : IDisposable
{
    Task<float[]?> ExtractEmbeddingAsync(Mat faceCrop);
    Task<List<FaceBox>> DetectFacesAsync(Mat frame);
}
```
*Lợi ích:* Hôm nay dùng `UsbCameraService`, ngày mai lễ tân mua Camera IP Dahua chỉ cần viết `RtspCameraService` triển khai `ICameraService`, đăng ký lại vào DI mà không cần sửa 1 dòng code logic nào khác.

---

## 📡 TRIẾT LÝ 4: OBSERVER PATTERN (EVENT-DRIVEN ARCHITECTURE) (CHƯƠNG 2)

### 4.1 Nguyên lý
Cơ chế Publisher - Subscriber giúp loại bỏ sự phụ thuộc chéo (Decoupling). AI Service phát tín hiệu sự kiện, các thành phần giao diện, âm thanh, cổng phần cứng tự động phản ứng.

```
       [ FaceRecognitionWorker ]
                  │
        bắn sự kiện OnMemberIdentified
                  │
  ┌───────────────┼───────────────┐
  ▼               ▼               ▼
[ AttendanceView ] [ AudioService ] [ RelayService ]
Hiển thị thẻ xanh/cam   Phát loa:        Kích hoạt mở
+ Tên + Ngày hết hạn   "Chào bạn Hưng"   cổng xoay barrier
```

### 4.2 Minh họa code C#
```csharp
public class MemberIdentifiedEventArgs : EventArgs
{
    public Member Member { get; init; }
    public float Confidence { get; init; }
    public DateTime Timestamp { get; init; }
}

// AttendanceCoordinator là Publisher
public class AttendanceCoordinator
{
    public event EventHandler<MemberIdentifiedEventArgs>? MemberIdentified;

    public void OnFaceMatched(Member member, float score)
    {
        // Kích hoạt toàn bộ observer đăng ký
        MemberIdentified?.Invoke(this, new MemberIdentifiedEventArgs 
        { 
            Member = member, 
            Confidence = score, 
            Timestamp = DateTime.Now 
        });
    }
}
```

---

## 🛡️ TRIẾT LÝ 5: CLEAN ARCHITECTURE & THE DEPENDENCY RULE (CHƯƠNG 3)

### 5.1 Nguyên lý
Chiếu phụ thuộc luôn hướng vào lõi Domain trung tâm:
`Presentation (WPF Views) ──> Application / Services ──> Domain (Core Entities)`
- Domain không tham chiếu bất kỳ thư viện bên ngoài nào (`OpenCvSharp4`, `Wpf.Ui` hay `Microsoft.EntityFrameworkCore`).
- SQLite và OpenCV chỉ là "Plugin" chi tiết cài đặt ở lớp ngoài.

### 5.2 Sơ đồ phân bố thư mục trong PhucGym.App
```
PhucGym.App/
├── Core/                              # [LÕI DOMAIN]
│   ├── Entities/                      # Member, Package, CheckInLog (DDD)
│   └── Interfaces/                    # ICameraService, IFaceEngine, IRepository
├── Infrastructure/                    # [CHI TIẾT KỸ THUẬT - NGOẠI VI]
│   ├── Camera/                        # OpenCvCameraService (OpenCvSharp4)
│   ├── AI/                            # OnnxFaceEngine (Microsoft.ML.OnnxRuntime)
│   └── Data/                          # MyGymDbContext (SQLite EF Core 10)
├── Services/                          # [USE CASES & COORDINATORS]
│   ├── AttendanceCoordinator.cs       # Điều phối quẹt thẻ & Event Observer
│   └── MemberManagementService.cs     # Đăng ký, gia hạn gói tập
└── UI/                                # [PRESENTATION LAYER]
    ├── ViewModels/                    # MVVM ViewModels
    └── Views/                         # WPF Windows & XAML UserControls (Wpf.Ui)
```

---
*Tài liệu này là kim chỉ nam bắt buộc tuân thủ cho toàn bộ các bước Vibecoding của dự án MyGymManager.*
