# TASK BOARD — TIẾN ĐỘ PHÁT TRIỂN MYGYMMANAGER DESKTOP

Bảng quản lý công việc theo mô hình Agile / Vibecoding từng bước (Phase-by-Phase), đảm bảo mỗi bước đều có artifact chạy được thực tế.

---

## 📌 Phase 1: Nền tảng & CSDL Local (Foundation & Data)
- [x] **Task 1.1:** Khởi tạo Solution .NET 10 WPF Standalone (`MyGymManager.slnx` & `MyGymManager.App`).
- [x] **Task 1.2:** Cài đặt bộ NuGet packages (OpenCvSharp4, OnnxRuntime, EF Core SQLite, Wpf.Ui).
- [x] **Task 1.3:** Sao chép bộ weights ONNX InsightFace vào thư mục `models/buffalo_l/`.
- [x] **Task 1.4:** Thiết lập Entity Framework Core SQLite `AppDbContext` + Entity Models (`Member`, `Package`, `CheckInLog`).
- [x] **Task 1.5:** Viết script seed dữ liệu mẫu các gói tập phổ biến và 5 hội viên test.

---

## 📌 Phase 2: AI & Camera Engine Native (Core Vision)
- [x] **Task 2.1:** Xây dựng `CameraService` (OpenCvSharp4) hỗ trợ mở Webcam USB và RTSP IP Camera.
- [x] **Task 2.2:** Xây dựng `FaceDetectionService` chạy model `det_10g.onnx` (SCRFD) trích xuất bounding box và 5 điểm mốc (landmarks).
- [x] **Task 2.3:** Viết hàm cắt & căn chỉnh khuôn mặt chuẩn 112x112 (Affine Transform chuẩn ArcFace).
- [x] **Task 2.4:** Xây dựng `FaceRecognitionService` chạy model `w600k_r50.onnx` trích xuất 512-dim embedding.
- [x] **Task 2.5:** Viết bộ test kiểm tra so khớp 2 vector khuôn mặt mẫu (Cosine Similarity > 0.65 là cùng một người).

---

## 📌 Phase 3: Giao diện Người dùng Fluent WPF (UI/UX)
- [x] **Task 3.1:** Dựng Shell Navigation chính (WPF-UI) tông màu **Cam thể thao (`#FF6B00`) & Trắng sạch**.
- [x] **Task 3.2:** Dựng màn hình **Điểm danh (AttendanceView / MainWindow)**: Khung camera Live, khung nhận diện khuôn mặt thời gian thực, pop-up/card thông tin hội viên (Ảnh, Tên, Ngày hết hạn, Trạng thái gói, DataGrid TodayLogs).
- [ ] **Task 3.3:** Dựng màn hình **Quản lý Hội viên (MembersView)**: Danh sách hội viên, tìm kiếm số điện thoại, đăng ký hội viên mới với tính năng chụp 3 góc mặt (Multi-Template FaceID).
- [ ] **Task 3.4:** Dựng màn hình **Quản lý Gói tập (PackagesView)**: Thêm/sửa gói tập (1 tháng, 3 tháng, 1 năm).
- [ ] **Task 3.5:** Dựng màn hình **Báo cáo Doanh thu & Thống kê (ReportsView)**: Số lượt tập hôm nay, hội viên sắp hết hạn, biểu đồ doanh thu.

---

## 📌 Phase 4: Tinh chỉnh & Đóng gói Độc lập (Hardening & Release)
- [ ] **Task 4.1:** Bổ sung âm thanh thông báo (Audio/TTS phát loa: "Chào mừng [Tên]", "Thẻ hết hạn").
- [x] **Task 4.2:** Tối ưu hóa bộ nhớ: Đảm bảo app chạy 24/7 không bị rò rỉ RAM (Drop-frame, giải phóng unmanaged Mat/Session).
- [ ] **Task 4.3:** Đóng gói xuất xưởng thành 1 file `.exe` duy nhất (`dotnet publish -r win-x64 -p:PublishSingleFile=true`).
