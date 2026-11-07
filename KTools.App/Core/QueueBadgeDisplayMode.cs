// -*- coding: utf-8 -*-
namespace KTools_App.Core;

/// <summary>
/// Режим отображения частоты кадров и типа развёртки (CFR/VFR)
/// в строке элемента очереди файлов.
/// </summary>
public enum QueueBadgeDisplayMode
{
    /// <summary>Информация FPS/CFR-VFR скрыта (поведение по умолчанию).</summary>
    Hidden = 0,

    /// <summary>Отдельные пилюли AccentBadge, видимость каждой задают тумблеры.</summary>
    Badges = 1,

    /// <summary>Спокойная текстовая строка с разделителями рядом с размером файла.</summary>
    Text = 2
}
