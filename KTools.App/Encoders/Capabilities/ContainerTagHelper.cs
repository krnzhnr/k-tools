// -*- coding: utf-8 -*-
using System;

namespace KTools_App.Encoders.Capabilities;

/// <summary>
/// Общие теги контейнера для кодеков. Тег hvc1 для HEVC в MP4 раньше
/// дублировался в NvencHevcCapabilities и CpuHevcCapabilities.
/// Все комментарии выполнены исключительно на русском языке.
/// </summary>
public static class ContainerTagHelper
{
    /// <summary>
    /// Тег контейнера для HEVC в MP4.
    /// </summary>
    public const string HevcMp4Tag = "hvc1";

    /// <summary>
    /// Возвращает тег контейнера для HEVC: hvc1 для MP4, иначе null.
    /// </summary>
    /// <param name="containerExtension">Расширение контейнера (например, .mp4).</param>
    public static string? GetHevcContainerTag(string containerExtension)
    {
        if (containerExtension.EndsWith("mp4", StringComparison.OrdinalIgnoreCase))
        {
            return HevcMp4Tag;
        }
        return null;
    }
}
