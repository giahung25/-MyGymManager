namespace MyGymManager.App.Converters;

using System.Globalization;
using System.Windows.Data;

/// <summary>
/// Converter hỗ trợ liên kết RadioButton hai chiều với thuộc tính Gender dạng chuỗi ("Nam", "Nữ").
/// </summary>
public class GenderToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return false;
        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isChecked && isChecked && parameter != null)
        {
            return parameter.ToString() ?? "Nam";
        }
        return Binding.DoNothing;
    }
}
