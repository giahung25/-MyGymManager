namespace MyGymManager.App.Views;

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using MyGymManager.Core.Entities;
using MyGymManager.Core.Interfaces;
using MyGymManager.Data;

/// <summary>
/// Cửa sổ hiển thị toàn bộ lịch sử điểm danh của riêng một hội viên cụ thể.
/// </summary>
public partial class MemberHistoryWindow : Window
{
    public record MemberHistoryItem(
        string CheckInTimeFormatted,
        string MethodName,
        string ConfidenceFormatted
    );

    public MemberHistoryWindow(Member member, AppDbContext dbContext, IEnumerable<CheckInLog>? preloadedLogs = null)
    {
        InitializeComponent();

        TxtFullName.Text = member.FullName;
        TxtPhone.Text = string.IsNullOrWhiteSpace(member.PhoneNumber) ? "--" : member.PhoneNumber;
        TxtGender.Text = string.IsNullOrWhiteSpace(member.Gender) ? "Nam" : member.Gender;
        TxtPackage.Text = member.Package?.Name ?? "Gói tập mặc định";
        TxtEndDate.Text = $"{member.EndDate:dd/MM/yyyy}";

        if (preloadedLogs != null)
        {
            var logsList = preloadedLogs.ToList();
            TxtTotalLogs.Text = $"{logsList.Count} lượt";
            var viewItems = logsList.Select(l => new MemberHistoryItem(
                CheckInTimeFormatted: l.CheckInTime.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"),
                MethodName: GetMethodDisplayName(l.Method),
                ConfidenceFormatted: $"{l.ConfidenceScore * 100:F1}%"
            )).ToList();
            GridHistory.ItemsSource = viewItems;
        }

        Loaded += async (s, e) =>
        {
            if (preloadedLogs != null) return;
            try
            {
                var logs = await dbContext.CheckInLogs
                    .AsNoTracking()
                    .Where(l => l.MemberId == member.Id)
                    .OrderByDescending(l => l.CheckInTime)
                    .ToListAsync();

                TxtTotalLogs.Text = $"{logs.Count} lượt";

                var viewItems = logs.Select(l => new MemberHistoryItem(
                    CheckInTimeFormatted: l.CheckInTime.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"),
                    MethodName: GetMethodDisplayName(l.Method),
                    ConfidenceFormatted: $"{l.ConfidenceScore * 100:F1}%"
                )).ToList();

                GridHistory.ItemsSource = viewItems;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi tải lịch sử điểm danh: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
    }

    private static string GetMethodDisplayName(IdentificationMethod method) => method switch
    {
        IdentificationMethod.FaceRecognition => "Nhận diện khuôn mặt (FaceID ArcFace)",
        IdentificationMethod.Fingerprint => "Máy quét vân tay sinh trắc học",
        IdentificationMethod.RfidCard => "Thẻ từ RFID / NFC",
        IdentificationMethod.QrCode => "Mã QR thành viên trên ứng dụng",
        IdentificationMethod.Manual => "Điểm danh thủ công qua SĐT",
        _ => "FaceID"
    };

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
