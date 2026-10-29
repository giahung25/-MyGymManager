namespace MyGymManager.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using MyGymManager.Core.Entities;

/// <summary>
/// Dịch vụ xuất báo cáo dữ liệu phòng gym ra tệp Excel (.csv UTF-8 with BOM).
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
