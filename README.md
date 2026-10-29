# MyGymManager Desktop — Hardware Attendance Gateway & Gym Management System

Hệ thống Quản lý Phòng Gym & Cổng Điều phối Điểm danh Thiết bị Phần cứng (**Hardware Attendance Gateway & Access Control**) xây dựng theo mô hình **Standalone Desktop (Offline-First, Zero-Server)** trên nền tảng **C# .NET 10** và **WPF**.

Ứng dụng được thiết kế chuyên biệt cho trạm quầy lễ tân và cổng kiểm soát ra vào, tập trung vào tính ổn định tuyệt đối, tốc độ xử lý siêu tốc và khả năng tích hợp linh hoạt với các thiết bị phần cứng chấm công chuyên dụng (Đầu đọc thẻ RFID/NFC, Máy quét mã QR, Máy chấm công khuôn mặt Hikvision/ZKTeco qua mạng LAN).

---

## ⚡ Điểm nhấn công nghệ & Kiến trúc

* **Nền tảng hiện đại:** C# .NET 10 (Target: `net10.0-windows`) tận dụng tối đa hiệu năng biên dịch mới nhất của .NET Runtime.
* **Giao diện Lễ tân Thể thao:** WPF MVVM chuẩn mực kết hợp Fluent Design (bo góc 12px, Card layout, gam màu thể thao Cam `#FF6B00` & Trắng, tối ưu hiển thị full màn hình).
* **Kiến trúc Cổng Định danh Cắm rút (Pluggable Identity Provider Architecture):**
  * 🏷️ **Thẻ từ RFID / NFC:** Tương thích các đầu đọc thẻ USB HID / COM (chuẩn Mifare 13.56MHz).
  * 📱 **Mã QR thành viên:** Tiếp nhận dữ liệu quét từ máy quét Barcode/QR Code 2D USB hoặc ứng dụng hội viên.
  * 🏢 **Máy chấm công LAN:** Sẵn sàng tiếp nhận sự kiện chấm công từ các dòng máy công nghiệp (Hikvision, ZKTeco) qua giao thức TCP/IP.
  * ⌨️ **Quầy lễ tân:** Tra cứu một chạm và điểm danh thủ công theo số điện thoại hoặc mã hội viên.
* **Tra cứu Đa chỉ mục In-Memory Siêu Tốc ($O(1)$ Hash Cache):**
  * Nạp danh sách hội viên vào bộ nhớ đệm đa chỉ mục (Multi-Index) đồng thời theo `Id`, `PhoneNumber`, `CardUid`, `QrKey`.
  * **Độ trễ tra cứu chỉ 0.27 µs** (270 nano giây), thông lượng đạt **hơn 3.500.000 lượt xác thực/giây**, phản hồi tức thì cho cổng barrier / khóa từ.
* **Cơ sở dữ liệu Cục bộ An toàn:** SQLite (`phuc_gym.db`) qua **Entity Framework Core 10**, bảo toàn giao dịch ACID với cơ chế khóa đồng thời (`SemaphoreSlim`), chống rò rỉ và chống khóa file (`Zero Locking Error`).
* **Quy trình Quản lý Tinh gọn:**
  * Đăng ký hội viên mới siêu tốc chỉ với 4 thông tin cốt lõi (Họ tên, Giới tính, SĐT, Gói tập), hoàn tất trong **< 5 giây**.
  * Quản lý trạng thái thông minh: Tự động phân loại 4 nhóm hội viên (**Tất cả, Đang tập, Sắp hết hạn trong 7 ngày, Đã hết hạn**).
  * Quản lý gói tập linh hoạt (1 tháng, 3 tháng, 1 năm...) và ghi nhận lịch sử vào/ra chi tiết.
* **Zero Overhead & Nhẹ tuyệt đối:** Không gánh nặng các mô hình AI cồng kềnh, mức tiêu thụ RAM ổn định chỉ **~150 MB** (khởi động dưới 2 giây), thích hợp vận hành liên tục 24/7 không lo sập ứng dụng.

---

## 📊 Kết quả Benchmark & Stress Test Thực tế

Hệ thống đi kèm công cụ kiểm thử hiệu năng độc lập `MyGymManager.StressTest`:

| Bài kiểm thử (Test Suite) | Quy mô kiểm tra | Kết quả đo đạc | Đánh giá |
| :--- | :--- | :--- | :--- |
| **Tra cứu In-Memory $O(1)$** | 1.000 hội viên trong RAM | **0.267 µs / lượt** (3.749.911 lượt/giây) | 🟢 **Đạt chuẩn Siêu Tốc (< 1 µs)** |
| **Tra cứu In-Memory $O(1)$** | 10.000 hội viên trong RAM | **0.295 µs / lượt** (3.395.263 lượt/giây) | 🟢 **Đạt chuẩn Siêu Tốc (< 1 µs)** |
| **Tra cứu In-Memory $O(1)$** | 50.000 hội viên trong RAM | **0.492 µs / lượt** (2.032.190 lượt/giây) | 🟢 **Đạt chuẩn Siêu Tốc (< 1 µs)** |
| **SQLite Bulk Insert** | 1.000 Hội viên | **251 ms** (3.981 records/giây) | 🟢 **Đạt chuẩn xuất sắc** |
| **SQLite Bulk Insert** | 5.000 Nhật ký điểm danh | **400 ms** (12.495 logs/giây) | 🟢 **Đạt chuẩn xuất sắc** |
| **SQLite Concurrency** | 50 luồng ghi đồng thời | **2.203 ms** (Zero Locking Error) | 🟢 **Đạt chuẩn toàn vẹn ACID** |
| **Xác thực Đa phương thức** | RFID + QR + SĐT | **3/3 thành công** (100%) | 🟢 **Đạt chuẩn** |
| **Bộ lọc Hội viên & Lịch sử**| 10 hội viên (4 Nam, 6 Nữ) | **100% khớp kỳ vọng bộ lọc** | 🟢 **Đạt chuẩn** |

---

## 📂 Cấu trúc dự án

```
D:\PhucGymDesktop\
├── MyGymManager.slnx                  # Solution file .NET 10
├── MyGymManager.App/                  # Project WPF Desktop chính (.NET 10.0-windows)
│   ├── Core/
│   │   ├── Entities/                  # Member (Hội viên), Package (Gói tập), CheckInLog (Nhật ký)
│   │   ├── Events/                    # MemberIdentifiedEventArgs (Sự kiện nhận diện)
│   │   ├── Interfaces/                # IIdentificationProvider, ICameraService
│   │   └── Models/                    # CameraDeviceInfo
│   ├── Data/
│   │   ├── AppDbContext.cs            # Cấu hình EF Core SQLite & Index
│   │   └── DbInitializer.cs           # Khởi tạo CSDL & Seed dữ liệu mẫu ban đầu
│   ├── Infrastructure/
│   │   ├── Camera/                    # OpenCvCameraService (Giám sát quầy lễ tân)
│   │   └── Providers/                 # RfidCardProvider, QrCodeProvider
│   ├── Services/
│   │   ├── AttendanceCoordinator.cs   # Trạm điều phối điểm danh & Cache RAM O(1)
│   │   └── AudioNotificationService.cs # Âm thanh chuông báo (Beep / Chào mừng)
│   ├── ViewModels/                    # MVVM: MainViewModel, RegisterMemberViewModel, MemberEditViewModel...
│   ├── Views/                         # MainWindow, RegisterMemberWindow, MemberEditWindow, PackageEditWindow
│   └── App.xaml / MainWindow.xaml
│
├── MyGymManager.StressTest/           # Công cụ Benchmark & Stress Test hiệu năng backend
│   └── Program.cs
│
└── docs/                              # Tài liệu kỹ thuật dự án
    ├── ARCHITECTURE.md                # Thiết kế kiến trúc & luồng dữ liệu
    ├── TASK_BOARD.md                  # Bảng tiến độ các Sprint
    └── CLI_CHEATSHEET.md              # Sổ tay lệnh dotnet CLI
```

---

## 🚀 Thao tác nhanh với dotnet CLI

### 1. Khôi phục & Biên dịch
```bash
# Di chuyển vào thư mục dự án
cd D:\PhucGymDesktop

# Biên dịch toàn bộ Solution
dotnet build MyGymManager.slnx
```

### 2. Chạy ứng dụng
```bash
# Khởi chạy giao diện chính Desktop (WPF)
dotnet run --project MyGymManager.App

# Khởi chạy bộ kiểm thử Stress Test & Benchmark
dotnet run --project MyGymManager.StressTest
```

### 3. Đóng gói 1 file `.exe` duy nhất (Release Deployment)
```bash
dotnet publish MyGymManager.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "D:\PhucGymDesktop\dist"
```
File thực thi thành phẩm độc lập hoàn toàn sẽ xuất hiện tại `dist\MyGymManager.App.exe` mà không cần cài đặt thêm .NET Runtime trên máy khách!

---

## 📜 Giấy phép & Tác giả
Dự án được phát triển phục vụ công tác quản lý và vận hành trung tâm thể thao / Fitness & Gym Management.
Mã nguồn mở và phân phối theo giấy phép MIT License.
