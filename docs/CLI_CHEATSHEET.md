# SỔ TAY DOTNET CLI — MYGYMMANAGER DESKTOP

Tất cả các câu lệnh thao tác một chạm trên Terminal PowerShell / Linux SSH.

---

## 1. Build & Run
```powershell
# Chuyển vào thư mục dự án
Set-Location "D:\PhucGymDesktop"

# Khôi phục các gói NuGet
dotnet restore

# Build nhanh (Debug)
dotnet build

# Chạy ứng dụng trực tiếp
dotnet run --project PhucGym.App

# Clean build
dotnet clean
```

---

## 2. Quản lý NuGet Packages
```powershell
# Xem danh sách package đã cài
dotnet list PhucGym.App package

# Cài đặt thêm package mới (Ví dụ âm thanh)
dotnet add PhucGym.App package NAudio
```

---

## 3. Entity Framework Core SQLite Migrations
```powershell
# Cài đặt công cụ ef nếu chưa có (1 lần duy nhất)
dotnet tool install --global dotnet-ef

# Tạo migration mới khi thay đổi model
dotnet ef migrations add InitialCreate --project PhucGym.App

# Cập nhật CSDL mygym_manager.db
dotnet ef database update --project PhucGym.App
```

---

## 4. Xuất bản 1 File .EXE Duy nhất (Release Packaging)
```powershell
# Xuất bản file .exe độc lập hoàn toàn (Self-contained, không cần cài .NET runtime trên máy đích)
dotnet publish PhucGym.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "D:\PhucGymDesktop\dist"
```
File `.exe` thành phẩm sẽ xuất hiện tại `D:\PhucGymDesktop\dist\MyGymManager.App.exe`!
