// -*- coding: utf-8 -*-
using System;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using KTools_App.Core;

namespace KTools_App.Converters;

/// <summary>
/// Конвертер, преобразующий уровень критичности логирования (LogLevel) в соответствующую кисть (SolidColorBrush).
/// Использует статическое кэширование кистей для исключения повторного выделения памяти в UI-потоке.
/// </summary>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush DebugBrush = new(ColorHelper.FromArgb(255, 128, 128, 128));
    private static readonly SolidColorBrush InfoBrush = new(ColorHelper.FromArgb(255, 220, 220, 220));
    private static readonly SolidColorBrush WarningBrush = new(ColorHelper.FromArgb(255, 255, 184, 0));
    private static readonly SolidColorBrush ErrorBrush = new(ColorHelper.FromArgb(255, 255, 77, 77));
    private static readonly SolidColorBrush FatalBrush = new(ColorHelper.FromArgb(255, 255, 0, 0));

    /// <inheritdoc />
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        string language)
    {
        if (value is LogLevel level)
        {
            return level switch
            {
                LogLevel.Debug => DebugBrush,
                LogLevel.Info => InfoBrush,
                LogLevel.Warning => WarningBrush,
                LogLevel.Error => ErrorBrush,
                LogLevel.Fatal => FatalBrush,
                _ => InfoBrush
            };
        }

        return InfoBrush;
    }

    /// <inheritdoc />
    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        string language)
    {
        throw new NotImplementedException("Обратное преобразование уровня логов в кисть не поддерживается.");
    }
}
