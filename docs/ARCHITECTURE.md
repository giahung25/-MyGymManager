# KIẾN TRÚC HỆ THỐNG — PHUCGYM DESKTOP (.NET 10 WPF STANDALONE)

## 1. MÔ HÌNH TỔNG THỂ
Ứng dụng vận hành độc lập theo mô hình **Standalone Monolith (All-in-One)** trên 1 máy tính lễ tân duy nhất, loại bỏ hoàn toàn tầng trung gian Web/Server/Mạng.

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                            PHUCGYM DESKTOP (.NET 10)                         │
│                                                                              │
│  ┌───────────────────────┐                    ┌───────────────────────────┐  │
│  │   CameraService       │                    │   FaceRecognitionService  │  │
│  │   (OpenCvSharp4)      │ ──[Frame QImage]─> │   (OnnxRuntime C#)        │  │
│  │   Webcam / RTSP 60fps │                    │   SCRFD Detect + ArcFace  │  │
│  └──────────┬────────────┘                    └─────────────┬─────────────┘  │
│             │                                               │                │
│             │                                               │ Vector 512-d   │
│             ▼                                               ▼                │
│  ┌────────────────────────────────────────────────────────────────────────┐  │
│  │                 AttendanceViewModel (Main UI Coordinator)              │  │
│  │                 - So khớp Cosine Similarity in-memory                  │  │
│  │                 - Kiểm tra thời hạn gói tập                            │  │
│  │                 - Ghi log CheckIn/CheckOut                             │  │
│  └───────────────────────────────────┬────────────────────────────────────┘  │
│                                      │                                       │
│                                      ▼                                       │
│                       ┌─────────────────────────────┐                        │
│                       │   Local SQLite (EF Core 10) │                        │
│                       │   phuc_gym.db               │                        │
│                       └─────────────────────────────┘                        │
└──────────────────────────────────────────────────────────────────────────────┘
```

---

## 2. THIẾT KẾ ĐA LUỒNG (THREADING MODEL - KHÔNG BLOCK UI)

Để đảm bảo camera hiển thị **30-60 FPS mượt mà**, ứng dụng chia thành 3 luồng hoạt động độc lập:

1. **UI Thread (WPF Dispatcher):**
   - Chỉ chịu trách nhiệm render giao diện XAML, các hiệu ứng animation, nút bấm và hiển thị thẻ thông tin hội viên.
2. **Camera Capture Thread (Background Task):**
   - Dùng vòng lặp `while (!token.IsCancellationRequested)` của `VideoCapture` (OpenCvSharp).
   - Đọc frame liên tục, chuyển đổi sang `WriteableBitmap` cập nhật lên màn hình qua `Dispatcher.BeginInvoke`.
3. **AI Inference Worker Thread (Background Queue / Channel):**
   - Không chạy nhận diện trên mọi frame (để tiết kiệm CPU). Chạy theo chu kỳ (ví dụ mỗi 3-5 frame hoặc khi phát hiện có chuyển động).
   - Nạp frame vào `InferenceSession` của ONNX Runtime, trích xuất 512-dim vector và so sánh với danh sách vector đang lưu trong RAM.
   - Khi khớp hội viên: Bắn sự kiện `MemberRecognizedEvent` về UI Thread để hiển thị pop-up và phát âm thanh chào mừng.

---

## 3. THIẾT KẾ CƠ SỞ DỮ LIỆU (SQLITE LOCAL SCHEMA)

```sql
-- 1. Bảng Hội viên
CREATE TABLE Members (
    Id TEXT PRIMARY KEY,               -- GUID
    FullName TEXT NOT NULL,
    PhoneNumber TEXT UNIQUE NOT NULL,
    AvatarPath TEXT,
    FaceVectors TEXT NOT NULL,         -- JSON chứa mảng các vector 512-d (Multi-template)
    PackageId TEXT,
    StartDate TEXT NOT NULL,
    EndDate TEXT NOT NULL,
    IsActive INTEGER DEFAULT 1,
    CreatedAt TEXT NOT NULL
);

-- 2. Bảng Gói tập
CREATE TABLE Packages (
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL,                -- Gói 1 tháng, 3 tháng, 1 năm...
    DurationDays INTEGER NOT NULL,
    Price REAL NOT NULL,
    Description TEXT
);

-- 3. Bảng Lịch sử Điểm danh
CREATE TABLE CheckInLogs (
    Id TEXT PRIMARY KEY,
    MemberId TEXT NOT NULL,
    CheckInTime TEXT NOT NULL,
    CheckOutTime TEXT,
    ConfidenceScore REAL,
    FOREIGN KEY(MemberId) REFERENCES Members(Id)
);
```

---

## 4. CHIẾN LƯỢC SO KHỚP VECTOR IN-MEMORY (<1ms)
Toàn bộ `FaceVectors` của các hội viên đang hoạt động (`IsActive = 1`) được tải lên một mảng `Dictionary<Guid, float[][]>` ngay khi app khởi động.
Khi có vector mới từ camera, hàm so khớp dùng phép nhân vô hướng (Dot Product / Cosine Similarity):
$$\text{Similarity}(A, B) = \frac{A \cdot B}{\|A\| \|B\|}$$
Vì vector của ArcFace đã được chuẩn hóa L2 ($\|A\| = 1$), độ tương đồng chỉ đơn giản là `DotProduct(A, B)`. Với 1.000 hội viên, phép toán này chỉ tốn **dưới 0.2ms** trên CPU .NET 10 nhờ công nghệ SIMD!
