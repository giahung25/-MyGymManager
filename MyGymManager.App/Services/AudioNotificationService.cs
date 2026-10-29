namespace MyGymManager.Services;

using System.Diagnostics;
using System.Media;

/// <summary>
/// Dịch vụ phát âm thanh thông báo tự động (Audio Feedback Service).
/// Phát chuông chào mừng khi điểm danh thành công và chuông cảnh báo khi thẻ hội viên hết hạn.
/// Toàn bộ âm thanh được phát trên Background Thread (Task.Run) để không bao giờ block Camera hay UI.
/// </summary>
public class AudioNotificationService
{
    private static readonly object _audioLock = new();

    /// <summary>
    /// Phát âm thanh chào mừng khi hội viên điểm danh thành công (Thẻ hợp lệ).
    /// Chuỗi âm sắc đôi ngân cao C6 -> E6 (1046Hz -> 1318Hz).
    /// </summary>
    public void PlayCheckInSuccess(string? memberName = null)
    {
        _ = Task.Run(() =>
        {
            lock (_audioLock)
            {
                try
                {
                    // Âm thanh đôi ngân cao vui tươi
                    Console.Beep(1046, 100);
                    Thread.Sleep(30);
                    Console.Beep(1318, 180);
                }
                catch
                {
                    try
                    {
                        // Fallback sang âm thanh hệ thống Windows
                        SystemSounds.Asterisk.Play();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[AudioNotificationService] Lỗi phát âm thanh: {ex.Message}");
                    }
                }
            }
        });
    }

    /// <summary>
    /// Phát âm thanh cảnh báo khi thẻ hội viên đã hết hạn.
    /// Chuỗi âm sắc trầm cảnh báo A4 -> E4 (440Hz -> 330Hz).
    /// </summary>
    public void PlayMembershipExpired(string? memberName = null)
    {
        _ = Task.Run(() =>
        {
            lock (_audioLock)
            {
                try
                {
                    // Âm thanh trầm cảnh báo
                    Console.Beep(440, 180);
                    Thread.Sleep(40);
                    Console.Beep(330, 280);
                }
                catch
                {
                    try
                    {
                        // Fallback sang âm thanh Hand / Alert của Windows
                        SystemSounds.Hand.Play();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[AudioNotificationService] Lỗi phát âm thanh cảnh báo: {ex.Message}");
                    }
                }
            }
        });
    }

    /// <summary>
    /// Phát âm thanh cảnh báo khi phát hiện thẻ, mã QR hoặc định danh lạ chưa đăng ký trong hệ thống.
    /// </summary>
    public void PlayAlertSound()
    {
        _ = Task.Run(() =>
        {
            lock (_audioLock)
            {
                try
                {
                    Console.Beep(659, 100);
                }
                catch
                {
                    try
                    {
                        SystemSounds.Beep.Play();
                    }
                    catch { }
                }
            }
        });
    }

    /// <summary>
    /// Phát âm thanh bip mỗi lần tự động chụp thành công 1 mẫu khuôn mặt (1/3, 2/3, 3/3).
    /// Tần số tăng dần theo từng mẫu: Mẫu 1 (1300Hz), Mẫu 2 (1600Hz), Mẫu 3 (1900Hz).
    /// </summary>
    public void PlayCaptureBeep(int sampleNumber)
    {
        _ = Task.Run(() =>
        {
            lock (_audioLock)
            {
                try
                {
                    int freq = Math.Clamp(1000 + sampleNumber * 300, 1000, 2200);
                    Console.Beep(freq, 130);
                }
                catch
                {
                    try { SystemSounds.Beep.Play(); } catch { }
                }
            }
        });
    }
}
