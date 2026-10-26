// -*- coding: utf-8 -*-
namespace KTools_App.Core;

/// <summary>
/// Цвета сплошного фона окна для режима «Обычный» без эффектов размытия.
/// Тёмная тема использует #080808 вместо чистого чёрного,
/// светлая — стандартный SolidBackgroundFillColorBase (#F3F3F3).
/// </summary>
public static class SolidBackdropTheme
{
    /// <summary>Сплошной фон для тёмной темы.</summary>
    public const string DarkBackgroundHex = "#080808";

    /// <summary>Сплошной фон для светлой темы (стандартная палитра).</summary>
    public const string LightBackgroundHex = "#F3F3F3";

    /// <summary>
    /// Возвращает hex сплошного фона для указанной темы.
    /// </summary>
    /// <param name="isLight">True для светлой темы, false для тёмной.</param>
    public static string ResolveBackgroundHex(bool isLight)
    {
        return isLight ? LightBackgroundHex : DarkBackgroundHex;
    }

    /// <summary>
    /// Определяет светлую ли эффективную тему по строковым значениям.
    /// </summary>
    /// <param name="requestedTheme">Запрошенная тема (Light/Dark/System).</param>
    /// <param name="actualTheme">Фактическая тема элемента (Light/Dark/Default).</param>
    public static bool IsLightEffective(string? requestedTheme, string? actualTheme)
    {
        if (string.Equals(requestedTheme, "Light", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(requestedTheme, "Dark", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(actualTheme, "Light", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(actualTheme, "Dark", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Требуется ли в режиме без эффектов гасить фон контентной области
    /// NavigationView в прозрачный, чтобы окно было единым пятном.
    /// В режимах Mica/Acrylic переопределение не ставится.
    /// </summary>
    /// <param name="backdropType">Тип фона (Mica/Acrylic/None, также принимается Solid).</param>
    public static bool RequiresContentBackgroundOverride(string? backdropType)
    {
        return string.Equals(backdropType, "None", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backdropType, "Solid", StringComparison.OrdinalIgnoreCase);
    }
}
