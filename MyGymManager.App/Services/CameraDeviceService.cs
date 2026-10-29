namespace MyGymManager.Services;

using System.Diagnostics;
using System.Management;
using OpenCvSharp;

/// <summary>
/// Đại diện cho một thiết bị Camera phát hiện được trên máy tính.
/// </summary>
public class CameraDeviceInfo
{
    public int Index { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public bool IsWorking { get; init; } = true;

    public string DisplayName => $"📷 {Name} [Cổng {Index}]";

    public override string ToString() => DisplayName;
}

/// <summary>
/// Dịch vụ tự động quét và quản lý danh sách Camera trên hệ điều hành Windows.
/// Kết hợp truy vấn thiết bị phần cứng PnP (System.Management) và kiểm tra khả năng mở luồng thực tế qua OpenCV.
/// </summary>
public static class CameraDeviceService
{
    /// <summary>
    /// Quét toàn bộ camera đang kết nối bất đồng bộ trên luồng nền (không làm đơ giao diện).
    /// </summary>
    public static async Task<List<CameraDeviceInfo>> EnumerateCamerasAsync()
    {
        return await Task.Run(() =>
        {
            var result = new List<CameraDeviceInfo>();
            var hardwareNames = new List<string>();

            // 1. Quét danh sách camera vật lý từ Device Manager qua WMI (Win32_PnPEntity)
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DeviceID, Status FROM Win32_PnPEntity WHERE PNPClass = 'Camera' OR PNPClass = 'Image'");

                foreach (ManagementObject device in searcher.Get())
                {
                    var name = device["Name"]?.ToString();
                    var status = device["Status"]?.ToString();

                    if (!string.IsNullOrWhiteSpace(name) && status == "OK")
                    {
                        hardwareNames.Add(name.Trim());
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraDeviceService] WMI Query lỗi: {ex.Message}");
            }

            // 2. Thử kiểm tra khả năng mở luồng thực tế trên các index từ 0 -> 3
            // Ưu tiên kiểm tra Index 1 trước rồi đến 0, 2, 3
            var indicesToProbe = new[] { 1, 0, 2, 3 };

            foreach (int idx in indicesToProbe)
            {
                bool opened = false;

                // Thử mở nhanh với Media Foundation (MSMF)
                try
                {
                    using var cap = new VideoCapture(idx, VideoCaptureAPIs.MSMF);
                    if (cap.IsOpened())
                    {
                        opened = true;
                    }
                }
                catch { }

                // Fallback thử với API ANY mặc định
                if (!opened)
                {
                    try
                    {
                        using var cap = new VideoCapture(idx, VideoCaptureAPIs.ANY);
                        if (cap.IsOpened())
                        {
                            opened = true;
                        }
                    }
                    catch { }
                }

                if (opened)
                {
                    // Gán tên hiển thị thân thiện từ Device Manager
                    string friendlyName;
                    if (hardwareNames.Count > 0)
                    {
                        // Nếu có USB WebCam trong tên phần cứng và index là 1
                        var usbCam = hardwareNames.FirstOrDefault(n => n.Contains("USB", StringComparison.OrdinalIgnoreCase));
                        if (idx == 1 && usbCam != null)
                        {
                            friendlyName = usbCam;
                        }
                        else if (idx == 0 && hardwareNames.Count > 1)
                        {
                            friendlyName = hardwareNames[0];
                        }
                        else
                        {
                            friendlyName = hardwareNames[0];
                        }
                    }
                    else
                    {
                        friendlyName = idx == 1 ? "USB2.0 HD UVC WebCam" : $"Camera {idx}";
                    }

                    result.Add(new CameraDeviceInfo
                    {
                        Index = idx,
                        Name = friendlyName,
                        Source = idx.ToString(),
                        IsWorking = true
                    });
                }
            }

            // 3. Nếu không mở được index nào (fallback an toàn)
            if (result.Count == 0)
            {
                string defaultName = hardwareNames.Count > 0 ? hardwareNames[0] : "USB2.0 HD UVC WebCam";
                result.Add(new CameraDeviceInfo { Index = 1, Name = defaultName, Source = "1", IsWorking = true });
                result.Add(new CameraDeviceInfo { Index = 0, Name = "Camera Mặc Định", Source = "0", IsWorking = false });
            }

            return result;
        });
    }
}
