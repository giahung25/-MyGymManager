namespace MyGymManager.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using MyGymManager.Core.Entities;
using MyGymManager.Data;

/// <summary>
/// Kết quả sau khi thực hiện nhập dữ liệu từ tệp CSV.
/// </summary>
public record CsvImportResult(
    int TotalProcessed,
    int AddedCount,
    int UpdatedCount,
    int ErrorCount,
    List<string> ErrorMessages);

/// <summary>
/// Dịch vụ xuất & nhập dữ liệu hội viên phòng gym ra/vào tệp Excel (.csv UTF-8 with BOM).
/// Tương thích 100% với Microsoft Excel, Google Sheets, LibreOffice, không bao giờ bị lỗi font tiếng Việt.
/// </summary>
public static class ExcelExportService
{
    /// <summary>
    /// Xuất danh sách hội viên & trạng thái thẻ kèm hộp thoại chọn nơi lưu tệp.
    /// Các cột: STT, Họ và Tên, Số điện thoại, Giới tính, Gói tập, Ngày bắt đầu, Ngày hết hạn, Số ngày còn lại, Trạng thái, Mã Thẻ RFID, Mã QR.
    /// </summary>
    public static bool ExportMembersWithDialog(IEnumerable<Member> members)
    {
        try
        {
            var saveDialog = new SaveFileDialog
            {
                Title = "Xuất Báo Cáo Danh Sách Hội Viên Phòng Gym",
                Filter = "Tệp Excel CSV (*.csv)|*.csv|Tất cả tệp (*.*)|*.*",
                FileName = $"HoiVien_PhucGym_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
                DefaultExt = ".csv",
                AddExtension = true
            };

            if (saveDialog.ShowDialog() != true)
            {
                return false;
            }

            string filePath = saveDialog.FileName;
            ExportMembersToCsv(members, filePath);

            var result = MessageBox.Show(
                $"Đã xuất báo cáo thành công ra tệp:\n{filePath}\n\nBạn có muốn mở tệp Excel này ngay bây giờ không?",
                "Xuất Excel Thành Công",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (result == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                });
            }

            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Lỗi khi xuất tệp Excel: {ex.Message}",
                "Lỗi Xuất Báo Cáo",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>
    /// Ghi danh sách hội viên ra file CSV với Encoding UTF-8 BOM chuẩn Excel.
    /// </summary>
    public static void ExportMembersToCsv(IEnumerable<Member> members, string filePath)
    {
        var sb = new StringBuilder();

        // Tiêu đề các cột chuẩn theo yêu cầu:
        // STT, Họ và Tên, Số điện thoại, Giới tính, Gói tập, Ngày bắt đầu, Ngày hết hạn, Số ngày còn lại, Trạng thái, Mã Thẻ RFID, Mã QR
        sb.AppendLine("STT,Họ và Tên,Số điện thoại,Giới tính,Gói tập,Ngày bắt đầu,Ngày hết hạn,Số ngày còn lại,Trạng thái,Mã Thẻ RFID,Mã QR Code");

        int index = 1;
        var now = DateTime.UtcNow;
        var thresholdExpiring = now.AddDays(7);

        foreach (var m in members)
        {
            string packageName = EscapeCsv(m.Package?.Name ?? "Gói tiêu chuẩn");
            string startDate = m.StartDate.ToLocalTime().ToString("dd/MM/yyyy");
            string endDate = m.EndDate.ToLocalTime().ToString("dd/MM/yyyy");
            int remainingDays = m.RemainingDays();

            string status;
            if (m.EndDate < now)
            {
                status = "Đã hết hạn";
            }
            else if (m.EndDate <= thresholdExpiring)
            {
                status = "Sắp hết hạn";
            }
            else
            {
                status = "Đang tập";
            }

            string cardUid = EscapeCsv(m.CardUid ?? "--");
            string qrKey = EscapeCsv(m.QrKey ?? "--");

            sb.AppendLine($"{index},{EscapeCsv(m.FullName)},{EscapeCsv(m.PhoneNumber)},{EscapeCsv(m.Gender)},{packageName},{startDate},{endDate},{remainingDays},{status},{cardUid},{qrKey}");
            index++;
        }

        // Ghi file với UTF8 BOM để Microsoft Excel nhận diện chính xác tiếng Việt có dấu
        var utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        File.WriteAllText(filePath, sb.ToString(), utf8WithBom);
    }

    /// <summary>
    /// Mở hộp thoại chọn file CSV và nhập/cập nhật danh sách hội viên vào SQLite.
    /// </summary>
    public static async Task<CsvImportResult?> ImportMembersWithDialogAsync(AppDbContext dbContext, AttendanceCoordinator? coordinator = null)
    {
        var openDialog = new OpenFileDialog
        {
            Title = "Chọn tệp Excel CSV để nhập / cập nhật hội viên",
            Filter = "Tệp Excel CSV (*.csv)|*.csv|Tất cả tệp (*.*)|*.*",
            Multiselect = false
        };

        if (openDialog.ShowDialog() != true)
        {
            return null;
        }

        try
        {
            var result = await ImportMembersFromCsvAsync(openDialog.FileName, dbContext, coordinator);

            string message = $"Nhập dữ liệu hoàn tất:\n\n" +
                             $"• Thêm mới thành công: {result.AddedCount} hội viên\n" +
                             $"• Cập nhật thông tin: {result.UpdatedCount} hội viên\n" +
                             $"• Dòng lỗi / Bỏ qua: {result.ErrorCount} dòng\n\n" +
                             $"Dữ liệu đã được lưu an toàn vào CSDL và đồng bộ RAM Cache!";

            if (result.ErrorMessages.Count > 0)
            {
                message += $"\n\nChi tiết lỗi đầu tiên:\n- " + string.Join("\n- ", result.ErrorMessages.Take(3));
            }

            MessageBox.Show(
                message,
                "Nhập Dữ Liệu Excel / CSV Thành Công",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return result;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Lỗi nghiêm trọng khi nhập file CSV: {ex.Message}",
                "Lỗi Nhập Dữ Liệu",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return null;
        }
    }

    /// <summary>
    /// Nhập và cập nhật hàng loạt (Bulk Upsert) hội viên từ tệp CSV vào SQLite.
    /// Khóa định danh chính: Số điện thoại.
    /// - Nếu SĐT đã có trong CSDL: Cập nhật Họ tên, Giới tính, Gói tập, Hạn thẻ, Mã thẻ.
    /// - Nếu SĐT chưa có: Tạo mới hội viên.
    /// </summary>
    public static async Task<CsvImportResult> ImportMembersFromCsvAsync(
        string filePath,
        AppDbContext dbContext,
        AttendanceCoordinator? coordinator = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(dbContext);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Không tìm thấy tệp CSV: {filePath}");
        }

        var lines = File.ReadAllLines(filePath, Encoding.UTF8);
        if (lines.Length <= 1)
        {
            return new CsvImportResult(0, 0, 0, 0, new List<string> { "Tệp CSV rỗng hoặc chỉ có tiêu đề." });
        }

        // 1. Phân tích Header để xác định vị trí các cột linh hoạt
        var headers = ParseCsvLine(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToList();

        int colName = FindColumnIndex(headers, "họ và tên", "họ tên", "tên", "fullname", "name");
        int colPhone = FindColumnIndex(headers, "số điện thoại", "sđt", "sdt", "phone", "phonenumber");
        int colGender = FindColumnIndex(headers, "giới tính", "gioi tinh", "gender");
        int colPackage = FindColumnIndex(headers, "gói tập", "goi tap", "package", "gói");
        int colStartDate = FindColumnIndex(headers, "ngày bắt đầu", "ngay bat dau", "startdate", "bắt đầu");
        int colEndDate = FindColumnIndex(headers, "ngày hết hạn", "ngay het han", "enddate", "hết hạn");
        int colCardUid = FindColumnIndex(headers, "mã thẻ rfid", "mã thẻ", "carduid", "card");
        int colQrKey = FindColumnIndex(headers, "mã qr code", "mã qr", "qrkey", "qr");

        if (colPhone < 0)
        {
            throw new InvalidOperationException("Tệp CSV không có cột 'Số điện thoại' (Bắt buộc để định danh hội viên).");
        }

        // Nạp danh sách gói tập hiện có để liên kết
        var packages = await dbContext.Packages.ToListAsync();
        var defaultPackage = packages.FirstOrDefault(p => p.DurationDays == 30) ?? packages.FirstOrDefault();

        int addedCount = 0;
        int updatedCount = 0;
        int errorCount = 0;
        var errorMessages = new List<string>();
        var affectedMembers = new List<Member>();

        var dateFormats = new[]
        {
            "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy",
            "yyyy-MM-dd", "yyyy/MM/dd", "MM/dd/yyyy", "M/d/yyyy"
        };

        // Bọc toàn bộ quá trình Import trong 1 Transaction SQLite duy nhất để đảm bảo tính toàn vẹn ACID
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        try
        {
            for (int i = 1; i < lines.Length; i++)
            {
                string rawLine = lines[i];
                if (string.IsNullOrWhiteSpace(rawLine)) continue;

                var cols = ParseCsvLine(rawLine);
                if (cols.Count == 0) continue;

                string phone = colPhone < cols.Count ? cols[colPhone].Trim() : string.Empty;
                if (string.IsNullOrWhiteSpace(phone))
                {
                    errorCount++;
                    errorMessages.Add($"Dòng {i + 1}: Thiếu Số điện thoại.");
                    continue;
                }

                string name = colName >= 0 && colName < cols.Count && !string.IsNullOrWhiteSpace(cols[colName])
                    ? cols[colName].Trim()
                    : $"Hội viên {phone}";

                string gender = colGender >= 0 && colGender < cols.Count && !string.IsNullOrWhiteSpace(cols[colGender])
                    ? cols[colGender].Trim()
                    : "Nam";

                string packageName = colPackage >= 0 && colPackage < cols.Count ? cols[colPackage].Trim() : string.Empty;
                var matchedPackage = packages.FirstOrDefault(p => p.Name.Equals(packageName, StringComparison.OrdinalIgnoreCase))
                                  ?? defaultPackage;

                DateTime startDateUtc = DateTime.UtcNow;
                if (colStartDate >= 0 && colStartDate < cols.Count &&
                    DateTime.TryParseExact(cols[colStartDate].Trim(), dateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var sDate))
                {
                    startDateUtc = DateTime.SpecifyKind(sDate, DateTimeKind.Utc);
                }

                DateTime endDateUtc = startDateUtc.AddDays(matchedPackage?.DurationDays ?? 30);
                if (colEndDate >= 0 && colEndDate < cols.Count &&
                    DateTime.TryParseExact(cols[colEndDate].Trim(), dateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var eDate))
                {
                    endDateUtc = DateTime.SpecifyKind(eDate, DateTimeKind.Utc);
                }

                string? cardUid = colCardUid >= 0 && colCardUid < cols.Count && cols[colCardUid].Trim() != "--"
                    ? cols[colCardUid].Trim()
                    : null;

                string? qrKey = colQrKey >= 0 && colQrKey < cols.Count && cols[colQrKey].Trim() != "--"
                    ? cols[colQrKey].Trim()
                    : null;

                // Kiểm tra xem hội viên với Số điện thoại này đã tồn tại chưa (ưu tiên tìm trong ChangeTracker Local trước)
                var existingMember = dbContext.Members.Local.FirstOrDefault(m => m.PhoneNumber == phone)
                                  ?? await dbContext.Members.FirstOrDefaultAsync(m => m.PhoneNumber == phone);

                if (existingMember != null)
                {
                    // Cập nhật thông tin hội viên cũ
                    existingMember.UpdateProfile(name, phone, gender);

                    if (matchedPackage != null && existingMember.PackageId != matchedPackage.Id)
                    {
                        existingMember.AssignPackage(matchedPackage);
                    }

                    typeof(Member).GetProperty(nameof(Member.StartDate))?.SetValue(existingMember, startDateUtc);
                    typeof(Member).GetProperty(nameof(Member.EndDate))?.SetValue(existingMember, endDateUtc);

                    if (!string.IsNullOrWhiteSpace(cardUid))
                        existingMember.AssignCardUid(cardUid);

                    if (!string.IsNullOrWhiteSpace(qrKey))
                        existingMember.AssignQrKey(qrKey);

                    affectedMembers.Add(existingMember);
                    updatedCount++;
                }
                else
                {
                    // Thêm mới hội viên
                    int packageDays = (int)Math.Max(1, (endDateUtc - startDateUtc).TotalDays);
                    var newMember = Member.Create(
                        fullName: name,
                        phoneNumber: phone,
                        packageDays: packageDays,
                        packageId: matchedPackage?.Id,
                        gender: gender,
                        cardUid: cardUid,
                        qrKey: qrKey);

                    typeof(Member).GetProperty(nameof(Member.StartDate))?.SetValue(newMember, startDateUtc);
                    typeof(Member).GetProperty(nameof(Member.EndDate))?.SetValue(newMember, endDateUtc);

                    await dbContext.Members.AddAsync(newMember);
                    affectedMembers.Add(newMember);
                    addedCount++;
                }
            }

            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();

            // Đồng bộ vào RAM cache AttendanceCoordinator CHỈ SAU KHI giao dịch SQLite đã commit thành công
            if (coordinator != null)
            {
                foreach (var member in affectedMembers)
                {
                    coordinator.UpdateMemberInCache(member);
                }
            }

            return new CsvImportResult(
                TotalProcessed: addedCount + updatedCount + errorCount,
                AddedCount: addedCount,
                UpdatedCount: updatedCount,
                ErrorCount: errorCount,
                ErrorMessages: errorMessages);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            throw new InvalidOperationException($"Lỗi giao dịch khi lưu CSDL: {ex.Message}", ex);
        }
    }

    private static int FindColumnIndex(List<string> headers, params string[] patterns)
    {
        for (int i = 0; i < headers.Count; i++)
        {
            string h = headers[i];
            foreach (var p in patterns)
            {
                if (h.Contains(p, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }
        return -1;
    }

    /// <summary>
    /// Phân tích một dòng CSV có xử lý dấu ngoặc kép và dấu phẩy bên trong chuỗi.
    /// </summary>
    public static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(line)) return result;

        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++; // Bỏ qua dấu ngoặc kép thoát
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(sb.ToString().Trim());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        result.Add(sb.ToString().Trim());
        return result;
    }

    private static string EscapeCsv(string field)
    {
        if (string.IsNullOrEmpty(field)) return "\"\"";
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }
        return $"\"{field}\"";
    }
}
