# PhucGym Desktop — Standalone Face Recognition Check-in System

Ứng dụng điểm danh nhận diện khuôn mặt tự động dành riêng cho phòng tập **Phuc Gym**, xây dựng theo mô hình **Standalone Desktop (All-in-One, No Server, Offline-First)** trên nền tảng **C# .NET 10** và **WPF**.

---

## ⚡ Điểm nhấn công nghệ
- **Nền tảng:** C# .NET 10 (Target: `net10.0-windows`).
- **Giao diện:** WPF + **WPF-UI** (Fluent Design Windows 11, bo góc mềm mại, tông màu thể thao Cam `#FF6B00` & Trắng).
- **Thị giác máy tính (Live Camera):** `OpenCvSharp4` stream RTSP / Webcam USB 30-60 FPS mượt mà.
- **Lõi AI nhận diện khuôn mặt:** `Microsoft.ML.OnnxRuntime` chạy trực tiếp model InsightFace ONNX (SCRFD + ArcFace 512-d vector) trên CPU với tập lệnh AVX2, độ trễ < 70ms.
- **Cơ sở dữ liệu:** SQLite cục bộ (`phuc_gym.db`) qua Entity Framework Core 10.
- **Đóng gói:** Xuất 1 file thực thi `.exe` duy nhất (`PublishSingleFile`), không phụ thuộc môi trường cài đặt bên ngoài.

---

## 📂 Cấu trúc dự án
```
D:\PhucGymDesktop\
├── PhucGym.slnx                   # Solution file .NET 10
├── models/buffalo_l/              # Model weights ONNX (det_10g, w600k_r50, ...)
├── docs/                          # Bộ tài liệu quản lý Vibecoding
│   ├── ARCHITECTURE.md            # Thiết kế kiến trúc & luồng đa luồng
│   ├── TASK_BOARD.md              # Bảng theo dõi tiến độ phát triển
│   ├── VIBECODING_RULES.md        # Bộ quy tắc Vibecoding hiệu quả cao
│   └── CLI_CHEATSHEET.md          # Sổ tay lệnh dotnet CLI thao tác nhanh
└── PhucGym.App/                   # Project mã nguồn chính
    ├── Core/                      # Models AI, Helpers, Constants
    ├── Data/                      # DbContext, Entities, Migrations SQLite
    ├── Services/                  # CameraService, FaceRecognitionService, MemberService
    ├── ViewModels/                # MVVM ViewModels
    ├── Views/                     # XAML Windows & UserControls
    └── App.xaml / MainWindow.xaml
```

---

## 🚀 Thao tác nhanh với dotnet CLI
```bash
# Build dự án
dotnet build

# Chạy ứng dụng
dotnet run --project PhucGym.App

# Xuất bản 1 file .exe duy nhất
dotnet publish PhucGym.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```
