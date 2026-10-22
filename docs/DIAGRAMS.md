# TỔNG HỢP SƠ ĐỒ THIẾT KẾ — PHUCGYM DESKTOP (.NET 10 STANDALONE)

Tài liệu thiết kế bao gồm: Use Case, User Flow (Điểm danh & Đăng ký), Sơ đồ Lớp OOP (Class Diagram), và Sơ đồ CSDL (ERD).

---

## 1. SƠ ĐỒ USE CASE CƠ BẢN (USE CASE DIAGRAM)

```mermaid
graph LR
    subgraph He_Thong_PhucGym["Hệ Thống PhucGym Desktop"]
        UC1((Điểm danh nhận diện khuôn mặt))
        UC2((Đăng ký hội viên mới & Chụp FaceID))
        UC3((Gia hạn gói tập hội viên))
        UC4((Quản lý danh mục gói tập))
        UC5((Xem lịch sử check-in & Báo cáo))
        UC6((Xem Live Camera quầy lễ tân))
    end

    HoiVien[Hội Viên] --> UC1
    LeTan[Nhân Viên Lễ Tân] --> UC2
    LeTan --> UC3
    LeTan --> UC5
    LeTan --> UC6
    ChuGym[Chủ Phòng Gym] --> UC4
    ChuGym --> UC5
```

---

## 2. USER FLOW (LUỒNG THAO TÁC NGƯỜI DÙNG)

### 2.1 Luồng Điểm danh tự động bằng FaceID (Tốc độ < 70ms)
```mermaid
sequenceDiagram
    autonumber
    actor HV as Hội Viên
    participant CAM as Camera Service (OpenCvSharp)
    participant AI as Face Engine (ONNX Runtime)
    participant APP as Attendance Coordinator
    participant DB as SQLite DB
    participant UI as Giao Diện Lễ Tân (WPF)

    HV->>CAM: Bước vào vùng quét camera
    CAM->>AI: Gửi frame ảnh (60fps)
    AI->>AI: SCRFD detect mặt + ArcFace trích 512-d vector
    AI->>APP: Bắn kết quả vector khuôn mặt
    APP->>APP: So khớp Cosine Similarity với RAM (> 0.65)
    alt Không tìm thấy hội viên
        APP->>UI: Hiển thị khung đỏ "Khách lạ / Chưa đăng ký"
    else Khớp thông tin hội viên
        APP->>DB: Kiểm tra ngày hết hạn gói tập
        alt Gói tập đã hết hạn
            APP->>UI: Hiển thị thẻ ĐỎ cảnh báo "Hết hạn ngày XX/YY"
            APP->>UI: Phát loa cảnh báo: "Thẻ hết hạn, vui lòng gia hạn"
        else Gói tập còn hạn
            APP->>DB: Ghi log CheckInLog (Thời gian, Độ tin cậy)
            APP->>UI: Hiển thị thẻ XANH "Chào mừng [Tên Hội Viên]"
            APP->>UI: Phát loa: "Chào mừng [Tên Hội Viên]"
        end
    end
```

### 2.2 Luồng Đăng ký Hội viên mới & Chụp Multi-Template FaceID
```mermaid
flowchart TD
    Start([Bắt đầu đăng ký]) --> Form[Lễ tân nhập Họ tên, SĐT, Chọn Gói tập]
    Form --> StepCam[Bật chế độ chụp khuôn mặt từ Camera]
    StepCam --> Shot1[Chụp góc 1: Nhìn thẳng]
    Shot1 --> Shot2[Chụp góc 2: Nghiêng nhẹ trái]
    Shot2 --> Shot3[Chụp góc 3: Nghiêng nhẹ phải]
    Shot3 --> Extract[AI trích xuất 3 vector 512 chiều]
    Extract --> Save[Lưu Entity Member vào SQLite DB]
    Save --> SyncRAM[Nạp 3 vector mới vào mảng RAM đang chạy]
    SyncRAM --> Done([Hoàn tất - Hội viên có thể quẹt mặt ngay])
```

---

## 3. SƠ ĐỒ LỚP HƯỚNG ĐỐI TƯỢNG (OOP CLASS DIAGRAM)

```mermaid
classDiagram
    %% Core Entities
    class Member {
        +Guid Id
        +string FullName
        +string PhoneNumber
        +DateTime StartDate
        +DateTime EndDate
        +bool IsActive
        +List~float[]~ FaceVectors
        +Create(name, phone, days, vector)$ Member
        +Renew(extraDays) void
        +IsExpired() bool
    }

    class Package {
        +Guid Id
        +string Name
        +int DurationDays
        +decimal Price
        +string Description
        +Create(name, days, price)$ Package
    }

    class CheckInLog {
        +Guid Id
        +Guid MemberId
        +DateTime CheckInTime
        +DateTime? CheckOutTime
        +float ConfidenceScore
    }

    %% Interfaces
    class ICameraService {
        <<interface>>
        +event EventHandler~Mat~ FrameCaptured
        +StartAsync(source) Task
        +StopAsync() Task
        +IsRunning bool
    }

    class IFaceEngine {
        <<interface>>
        +DetectFace(frame) FaceBox
        +ExtractEmbedding(faceCrop) float[]
    }

    %% Implementations
    class OpenCvCameraService {
        -VideoCapture _capture
        -CancellationTokenSource _cts
    }

    class OnnxFaceEngine {
        -InferenceSession _detectorSession
        -InferenceSession _recognizerSession
    }

    class AttendanceCoordinator {
        -ICameraService _cameraService
        -IFaceEngine _faceEngine
        -Dictionary~Guid, float[][]~ _cachedVectors
        +event EventHandler~MemberIdentifiedEventArgs~ MemberIdentified
        +ProcessFrame(frame) void
    }

    ICameraService <|.. OpenCvCameraService
    IFaceEngine <|.. OnnxFaceEngine
    AttendanceCoordinator o-- ICameraService
    AttendanceCoordinator o-- IFaceEngine
    Member "1" *-- "many" CheckInLog : có lịch sử
    Package "1" -- "many" Member : áp dụng
```

---

## 4. SƠ ĐỒ CƠ SỞ DỮ LIỆU (DATABASE ERD - SQLITE)

```mermaid
erDiagram
    PACKAGES ||--o{ MEMBERS : "gán cho"
    MEMBERS ||--o{ CHECK_IN_LOGS : "ghi nhận"

    PACKAGES {
        TEXT Id PK "GUID"
        TEXT Name "Tên gói tập (Gói 1 tháng, 3 tháng...)"
        INTEGER DurationDays "Số ngày hiệu lực"
        REAL Price "Giá tiền gói"
        TEXT Description "Mô tả chi tiết"
    }

    MEMBERS {
        TEXT Id PK "GUID"
        TEXT FullName "Họ và tên"
        TEXT PhoneNumber UK "Số điện thoại duy nhất"
        TEXT AvatarPath "Đường dẫn ảnh đại diện"
        TEXT FaceVectors "JSON chứa mảng các vector 512-d"
        TEXT PackageId FK "Khóa ngoại tới Packages"
        TEXT StartDate "Ngày bắt đầu gói"
        TEXT EndDate "Ngày hết hạn gói"
        INTEGER IsActive "1: Đang hoạt động, 0: Đã khóa"
        TEXT CreatedAt "Thời điểm đăng ký"
    }

    CHECK_IN_LOGS {
        TEXT Id PK "GUID"
        TEXT MemberId FK "Khóa ngoại tới Members"
        TEXT CheckInTime "Thời điểm vào tập"
        TEXT CheckOutTime "Thời điểm về (nullable)"
        REAL ConfidenceScore "Điểm số nhận diện (VD: 0.82)"
    }
```
