// -*- coding: utf-8 -*-
using KTools_App.Core;

namespace KTools_App.Core;

/// <summary>
/// Стилевое решение для строк журнала: какая стандартная ресурсная кисть
/// соответствует уровню логирования. Чистая логика без WinRT-типов,
/// поэтому покрывается обычными headless-тестами.
/// </summary>
public static class LogLevelStyle
{
    /// <summary>
    /// Возвращает ключ стандартной ThemeResource-кисти для уровня.
    /// Debug — приглушённый третичный текст, Info — основной текст,
    /// Warning — штатный предупреждающий, Error/Fatal — штатный критический.
    /// </summary>
    public static string ResolveBrushResourceKey(LogLevel level)
    {
        return level switch
        {
            LogLevel.Debug => "TextFillColorTertiaryBrush",
            LogLevel.Warning => "SystemFillColorCautionBrush",
            LogLevel.Error => "SystemFillColorCriticalBrush",
            LogLevel.Fatal => "SystemFillColorCriticalBrush",
            _ => "TextFillColorPrimaryBrush",
        };
    }

    /// <summary>
    /// Требуется ли уровню дополнительное акцентирование (полужирный).
    /// </summary>
    public static bool IsEmphasized(LogLevel level)
    {
        return level == LogLevel.Fatal;
    }
}
