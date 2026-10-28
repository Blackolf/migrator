using System;
using System.Collections;
using System.Globalization;
using System.Windows.Data;

namespace SystemMigrator.Converters
{
    public class SelectedCountConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is IEnumerable items)
            {
                int count = 0;
                foreach (var item in items)
                {
                    // 使用反射獲取 IsSelected 屬性 (兼容 InstalledApp 和 BackupItem)
                    var prop = item.GetType().GetProperty("IsSelected");
                    if (prop != null && prop.GetValue(item) is bool isSelected && isSelected)
                    {
                        count++;
                    }
                }
                return count;
            }
            return 0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}