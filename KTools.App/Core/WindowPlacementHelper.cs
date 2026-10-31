using System;
using Microsoft.UI.Windowing;

namespace KTools_App.Core;

/// <summary>
/// Вспомогательный класс для валидации, ограничения и управления геометрией окна.
/// Обеспечивает безопасное позиционирование окна внутри видимой рабочей области монитора
/// и сохранение нормального состояния при сворачивании и разворачивании.
/// Все комментарии и описание выполнены строго на русском языке.
/// </summary>
public static class WindowPlacementHelper
{
    /// <summary>
    /// Константа неопределённой координаты окна (значение по умолчанию до первого сохранения).
    /// </summary>
    public const int UnspecifiedPosition = int.MinValue;

    /// <summary>
    /// Ширина окна по умолчанию в логических пикселях (DIP).
    /// </summary>
    public const double DefaultWidth = 800.0;

    /// <summary>
    /// Высота окна по умолчанию в логических пикселях (DIP).
    /// </summary>
    public const double DefaultHeight = 740.0;

    /// <summary>
    /// Минимальная ширина окна в логических пикселях (DIP).
    /// </summary>
    public const int MinWidthDip = 800;

    /// <summary>
    /// Минимальная высота окна в логических пикселях (DIP).
    /// </summary>
    public const int MinHeightDip = 620;

    /// <summary>
    /// Проверяет, заданы ли валидные сохраненные экранные координаты окна.
    /// </summary>
    /// <param name="x">Координата X.</param>
    /// <param name="y">Координата Y.</param>
    /// <returns>True, если обе координаты определены; иначе false.</returns>
    public static bool HasValidPosition(int x, int y)
    {
        return x != UnspecifiedPosition && y != UnspecifiedPosition;
    }

    /// <summary>
    /// Ограничивает координаты и размеры окна видимой рабочей областью целевого монитора,
    /// предотвращая отображение окна за пределами экрана при отключении монитора или смене разрешения.
    /// </summary>
    /// <param name="targetX">Целевая координата X верхнего левого угла окна.</param>
    /// <param name="targetY">Целевая координата Y верхнего левого угла окна.</param>
    /// <param name="targetWidth">Целевая ширина окна в пикселях.</param>
    /// <param name="targetHeight">Целевая высота окна в пикселях.</param>
    /// <param name="workAreaX">Координата X рабочей области экрана.</param>
    /// <param name="workAreaY">Координата Y рабочей области экрана.</param>
    /// <param name="workAreaWidth">Ширина рабочей области экрана.</param>
    /// <param name="workAreaHeight">Высота рабочей области экрана.</param>
    /// <returns>Скорректированные координаты и размеры (X, Y, Width, Height).</returns>
    public static (int X, int Y, int Width, int Height) ClampToWorkArea(
        int targetX,
        int targetY,
        int targetWidth,
        int targetHeight,
        int workAreaX,
        int workAreaY,
        int workAreaWidth,
        int workAreaHeight)
    {
        if (workAreaWidth <= 0 || workAreaHeight <= 0)
        {
            return (targetX, targetY, targetWidth, targetHeight);
        }

        // Окно не должно превышать габариты доступной рабочей области экрана
        int clampedWidth = Math.Max(100, Math.Min(targetWidth, workAreaWidth));
        int clampedHeight = Math.Max(100, Math.Min(targetHeight, workAreaHeight));

        // Максимальные координаты для удержания окна целиком внутри рабочей области
        int maxX = workAreaX + workAreaWidth - clampedWidth;
        int maxY = workAreaY + workAreaHeight - clampedHeight;

        int clampedX = Math.Clamp(targetX, workAreaX, Math.Max(workAreaX, maxX));
        int clampedY = Math.Clamp(targetY, workAreaY, Math.Max(workAreaY, maxY));

        return (clampedX, clampedY, clampedWidth, clampedHeight);
    }

    /// <summary>
    /// Определяет, следует ли обновлять нормальные (Restored) размеры и координаты окна.
    /// Нормальные габариты обновляются только когда окно находится в обычном режиме,
    /// предотвращая затирание нормальных размеров экранными габаритами в развернутом
    /// или свернутом состоянии.
    /// </summary>
    /// <param name="state">Текущее состояние представления окна.</param>
    /// <returns>True, если состояние окна является Restored; иначе false.</returns>
    public static bool ShouldUpdateNormalBounds(OverlappedPresenterState state)
    {
        return state == OverlappedPresenterState.Restored;
    }

    /// <summary>
    /// Определяет сохраняемое состояние развернутости окна с учетом возможного нахождения
    /// окна в свернутом виде (Minimized), сохраняя предыдущее состояние до сворачивания.
    /// </summary>
    /// <param name="state">Текущее состояние представления окна.</param>
    /// <param name="previousIsMaximized">Предыдущее зафиксированное значение развернутости окна.</param>
    /// <returns>Итоговое логическое значение развернутости окна.</returns>
    public static bool ResolveIsMaximized(OverlappedPresenterState state, bool previousIsMaximized)
    {
        return state switch
        {
            OverlappedPresenterState.Maximized => true,
            OverlappedPresenterState.Restored => false,
            OverlappedPresenterState.Minimized => previousIsMaximized,
            _ => previousIsMaximized
        };
    }
}
