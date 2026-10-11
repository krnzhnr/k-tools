// -*- coding: utf-8 -*-
using System;

namespace KTools_App.Core;

/// <summary>
/// Универсальное соглашение об именовании ключей авторасчёта границ битрейта.
/// Работает для любого кодека без настройки под каждый: контроллер — это ключ
/// <c>auto_bitrate</c> либо <c>&lt;префикс&gt;_auto_bitrate</c> (например,
/// <c>x265_auto_bitrate</c>, <c>x264_auto_bitrate</c>), а зависимые поля образуются
/// тем же префиксом: <c>&lt;префикс&gt;_v_bitrate</c>, <c>&lt;префикс&gt;_min_bitrate</c>,
/// <c>&lt;префикс&gt;_max_bitrate</c>, <c>&lt;префикс&gt;_bufsize</c> (для пустого префикса —
/// без подчёркивания: <c>v_bitrate</c>, <c>min_bitrate</c> и т.д.).
/// Все комментарии выполнены исключительно на русском языке.
/// </summary>
public static class BitrateAutoMode
{
    /// <summary>
    /// Суффикс ключа-контроллера авторасчёта.
    /// </summary>
    public const string AutoBitrateSuffix = "_auto_bitrate";

    /// <summary>
    /// Проверяет, является ли ключ контроллером авторасчёта
    /// (<c>auto_bitrate</c> или <c>&lt;префикс&gt;_auto_bitrate</c>).
    /// </summary>
    public static bool IsAutoBitrateController(string key)
    {
        return key.Equals("auto_bitrate", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith(AutoBitrateSuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Извлекает префикс кодека из ключа поля битрейтной группы.
    /// Возвращает пустую строку для беспрефиксных ключей (NVENC) и null,
    /// если ключ не относится к битрейтной группе.
    /// </summary>
    public static string? TryGetPrefix(string key)
    {
        if (key.Equals("auto_bitrate", StringComparison.OrdinalIgnoreCase)
            || key.Equals("v_bitrate", StringComparison.OrdinalIgnoreCase)
            || key.Equals("min_bitrate", StringComparison.OrdinalIgnoreCase)
            || key.Equals("max_bitrate", StringComparison.OrdinalIgnoreCase)
            || key.Equals("bufsize", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        foreach (var suffix in new[] { "_auto_bitrate", "_v_bitrate", "_min_bitrate", "_max_bitrate", "_bufsize" })
        {
            if (key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return key.Substring(0, key.Length - suffix.Length);
            }
        }

        return null;
    }

    /// <summary>
    /// Образует ключ поля битрейтной группы из префикса кодека и базового имени
    /// (для пустого префикса — без подчёркивания: <c>auto_bitrate</c>, <c>v_bitrate</c> и т.д.).
    /// </summary>
    public static string WithPrefix(string prefix, string baseName)
    {
        return string.IsNullOrEmpty(prefix) ? baseName : prefix + "_" + baseName;
    }

    /// <summary>
    /// Ключ целевого битрейта для префикса.
    /// </summary>
    public static string TargetBitrateKey(string prefix) => WithPrefix(prefix, "v_bitrate");

    /// <summary>
    /// Ключ минимального битрейта для префикса.
    /// </summary>
    public static string MinBitrateKey(string prefix) => WithPrefix(prefix, "min_bitrate");

    /// <summary>
    /// Ключ максимального битрейта для префикса.
    /// </summary>
    public static string MaxBitrateKey(string prefix) => WithPrefix(prefix, "max_bitrate");

    /// <summary>
    /// Ключ размера буфера для префикса.
    /// </summary>
    public static string BufsizeKey(string prefix) => WithPrefix(prefix, "bufsize");

    /// <summary>
    /// Вычисляет границы по целевому битрейту по единому правилу:
    /// минимум равен цели, максимум — удвоенной цели, буфер — удвоенному максимуму.
    /// То же правило продублировано в энкодерах при сборке аргументов FFmpeg.
    /// </summary>
    public static (int MinBitrate, int MaxBitrate, int Bufsize) ComputeBounds(int targetBitrate)
    {
        int min = targetBitrate;
        int max = targetBitrate * 2;
        return (min, max, max * 2);
    }
}
