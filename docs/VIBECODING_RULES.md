# NGUYÊN TẮC VIBECODING — MYGYMMANAGER DESKTOP (.NET 10)

Tài liệu này định hình cách thức làm việc giữa **Bạn (Developer / Product Owner)** và **Hermes AI Agent** trong suốt quá trình phát triển dự án.

---

## 🎯 1. Triết lý Cốt lõi: "Working Code Over Speculation"
- **Không code thừa (YAGNI):** Không tạo abstraction vô ích, không tạo interface chỉ có 1 class triển khai nếu chưa cần đa hình.
- **Thực thi bằng sản phẩm thật:** Mỗi khi xong một task, code phải build thành công (`dotnet build` exit code 0) và có thể kiểm chứng ngay lập tức.
- **Zero-Crash Policy:** Toàn bộ tác vụ liên quan đến Camera (`OpenCvSharp4`) và ONNX Runtime phải được bọc trong `try-catch` chặt chẽ, đảm bảo lỗi camera rớt mạng không làm sập ứng dụng quầy lễ tân.

---

## 🛠️ 2. Quy chuẩn Kỹ thuật Dotnet CLI
1. **Toàn bộ thao tác qua CLI:**
   - Tạo file, class, cài package, build, chạy, publish đều dùng `dotnet` CLI để tự động hóa 100% qua terminal.
2. **Quản lý bộ nhớ C++ unmanaged:**
   - Các đối tượng `Mat`, `VideoCapture`, `InferenceSession`, `OrtValue` là tài nguyên unmanaged (C/C++).
   - Bắt buộc phải dùng mẫu `using var mat = ...` hoặc gọi `.Dispose()` khi không còn dùng để tránh rò rỉ RAM khi ứng dụng chạy suốt 12 tiếng/ngày tại phòng gym.
3. **Đa luồng an toàn (Dispatcher Safety):**
   - Không bao giờ cập nhật thuộc tính giao diện WPF từ luồng Background.
   - Luôn cập nhật qua: `Application.Current.Dispatcher.InvokeAsync(() => { ... });`.

---

## 📝 3. Quy chuẩn Commit (Conventional Commits)
Mỗi tính năng hoàn thành sẽ được commit rõ ràng:
- `feat(camera): implement opencv camera capture with 30fps writeablebitmap`
- `feat(ai): add onnx runtime face detection and arcface embedding extraction`
- `feat(ui): build sporty orange fluent dashboard with wpf-ui`
- `refactor(db): optimize in-memory vector cosine similarity query`
- `fix(memory): fix unmanaged mat disposal leak during continuous streaming`
