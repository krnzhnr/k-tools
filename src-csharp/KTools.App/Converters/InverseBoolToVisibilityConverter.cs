// -*- coding: utf-8 -*-
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace KTools_App.Converters;

/// <summary>
/// Инверсный конвертер булевого значения в Visibility.
/// true → Collapsed, false → Visible.
/// Используется для скрытия элементов при активном состоянии (например, во время обработки).
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        string language)
    {
        if (value is bool boolValue)
        {
            return boolValue
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        return Visibility.Visible;
    }

    /// <inheritdoc />
    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        string language)
    {
        if (value is Visibility visibility)
        {
            return visibility != Visibility.Visible;
        }

        return true;
    }
}
