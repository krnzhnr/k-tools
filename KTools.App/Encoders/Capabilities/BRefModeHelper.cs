// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;

using KTools_App.Core;

namespace KTools_App.Encoders.Capabilities;

/// <summary>
/// Общее построение настройки и аргументов опорных B-кадров NVENC (-b_ref_mode).
/// Раньше HEVC и H.264 несли две одинаковые копии (опции, комментарий, поле, аргументы),
/// AV1 отличался только запретом режима each. Ключ настройки не меняется.
/// Все комментарии выполнены исключительно на русском языке.
/// </summary>
public static class BRefModeHelper
{
    /// <summary>
    /// Ключ настройки опорных B-кадров.
    /// </summary>
    public const string SettingKey = "nv_b_ref_mode";

    /// <summary>
    /// Режим по умолчанию (B-pyramid).
    /// </summary>
    public const string DefaultMode = "middle";

    /// <summary>
    /// Комментарий для HEVC и H.264 (поддерживают режим each).
    /// </summary>
    public const string DefaultComment = "Использование B-кадров как опорных (middle - B-pyramid, each - каждый, disabled - выключено)";

    /// <summary>
    /// Комментарий для AV1 (режим each не поддерживается).
    /// </summary>
    public const string Av1Comment = "Использование B-кадров как опорных (middle - B-pyramid, disabled - выключено; режим 'each' не поддерживается в AV1 NVENC)";

    /// <summary>
    /// Возвращает свежий список вариантов: новый экземпляр при каждом вызове,
    /// потому что <see cref="SettingField"/> хранит переданный список по ссылке.
    /// </summary>
    /// <param name="supportEach">Признак поддержки режима each (нет только у AV1).</param>
    public static List<string> GetOptions(bool supportEach)
    {
        return supportEach
            ? new List<string> { "disabled", "each", "middle" }
            : new List<string> { "disabled", "middle" };
    }

    /// <summary>
    /// Строит поле настройки опорных B-кадров с общими группой, колонкой и условием.
    /// </summary>
    /// <param name="options">Варианты режима (см. <see cref="GetOptions"/>).</param>
    /// <param name="comment">Комментарий для интерфейса.</param>
    public static SettingField CreateSettingField(List<string> options, string comment)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new SettingField(
            SettingKey,
            "Опорные B-кадры",
            SettingType.Combo,
            DefaultMode,
            "Видео:Расширенные параметры",
            options: options,
            comment: comment,
            column: 1,
            colSpan: 1,
            disableConditions: new List<SettingDisableCondition>
            {
                new("lossless", "True")
            });
    }

    /// <summary>
    /// Добавляет флаг -b_ref_mode в аргументы. В lossless rate control отключён,
    /// поэтому B-пирамида не влияет на результат и опускается.
    /// </summary>
    /// <param name="settings">Словарь настроек запуска.</param>
    /// <param name="context">Общий контекст энкодера.</param>
    /// <param name="args">Список аргументов командной строки.</param>
    /// <param name="normalizeEachToMiddle">Заменять each на middle (только AV1,
    /// автоматическая защита от ошибки инициализации -542398533).</param>
    public static void AppendArguments(Dictionary<string, object> settings, EncoderSharedContext context, List<string> args, bool normalizeEachToMiddle)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(args);

        if (context.IsLossless)
        {
            return;
        }

        string mode = DefaultMode;
        if (settings.TryGetValue(SettingKey, out object? val) && val != null)
        {
            mode = SettingValue.AsString(val, DefaultMode);
        }
        if (normalizeEachToMiddle && mode.Equals("each", StringComparison.OrdinalIgnoreCase))
        {
            mode = DefaultMode;
        }

        if (!string.IsNullOrEmpty(mode))
        {
            args.AddRange(new[] { "-b_ref_mode", mode });
        }
    }
}
