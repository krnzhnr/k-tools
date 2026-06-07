// -*- coding: utf-8 -*-
using System;
using Microsoft.UI.Xaml.Data;

namespace KTools_App.Converters;

/// <summary>
/// Конвертер инверсии булевого значения.
/// true → false, false → true.
/// Используется для привязки IsEnabled в обратной логике (например, блокировка контролов при обработке).
/// </summary>
public sealed class InverseBoolConverter : IValueConverter
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
            return !boolValue;
        }

        return true;
    }

    /// <inheritdoc />
    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        string language)
    {
        if (value is bool boolValue)
        {
            return !boolValue;
        }

        return false;
    }
}
