namespace MyGymManager.App.Converters;

using System.Globalization;
using System.Windows.Data;
using MyGymManager.Core.Interfaces;

/// <summary>
/// Converter hiển thị tên tiếng Việt thân thiện cho enum IdentificationMethod.
/// </summary>
public class MethodToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is IdentificationMethod method)
        {
            return method switch
            {
                IdentificationMethod.FaceRecognition => "Khuôn mặt (Face ID)",
                IdentificationMethod.Fingerprint => "Vân tay sinh trắc học",
                IdentificationMethod.RfidCard => "Thẻ từ RFID / NFC",
                IdentificationMethod.QrCode => "Mã QR thành viên",
                IdentificationMethod.Manual => "Thủ công (SĐT)",
                _ => method.ToString()
            };
        }

        return value?.ToString() ?? "--";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
