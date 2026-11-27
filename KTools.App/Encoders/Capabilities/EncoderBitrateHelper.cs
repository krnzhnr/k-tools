// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;

namespace KTools_App.Encoders.Capabilities;

/// <summary>
/// Общий расчёт границ битрейта и построение флагов -b:v/-minrate/-maxrate/-bufsize.
/// Одинаковая формула раньше жила тремя копиями (NVENC, libx264, libx265):
/// правка в одном месте рассинхронизировала остальные. Ключи настроек не меняются —
/// вызыватели сами читают свои ключи и передают готовые числа.
/// Все комментарии выполнены исключительно на русском языке.
/// </summary>
public static class EncoderBitrateHelper
{
    /// <summary>
    /// Считает границы битрейта в автовычисляемом режиме.
    /// </summary>
    /// <param name="targetBitrateKbps">Целевой битрейт (кбит/с).</param>
    /// <param name="collapseForCbr">Для CBR границы и буфер равны целевому битрейту,
    /// иначе maxrate 2x превращает постоянный битрейт в переменный.</param>
    /// <param name="minBitrateKbps">Минимальный битрейт (кбит/с).</param>
    /// <param name="maxBitrateKbps">Максимальный битрейт (кбит/с).</param>
    /// <param name="bufferSizeKbit">Размер буфера VBV (кбит).</param>
    public static void ResolveAutoBounds(int targetBitrateKbps, bool collapseForCbr, out int minBitrateKbps, out int maxBitrateKbps, out int bufferSizeKbit)
    {
        if (collapseForCbr)
        {
            minBitrateKbps = targetBitrateKbps;
            maxBitrateKbps = targetBitrateKbps;
            bufferSizeKbit = targetBitrateKbps;
            return;
        }

        minBitrateKbps = targetBitrateKbps;
        maxBitrateKbps = targetBitrateKbps * 2;
        bufferSizeKbit = maxBitrateKbps * 2;
    }

    /// <summary>
    /// Добавляет флаги битрейта в аргументы FFmpeg. Нулевые границы опускаются.
    /// </summary>
    /// <param name="args">Список аргументов командной строки.</param>
    /// <param name="targetBitrateKbps">Целевой битрейт (кбит/с).</param>
    /// <param name="minBitrateKbps">Минимальный битрейт (кбит/с).</param>
    /// <param name="maxBitrateKbps">Максимальный битрейт (кбит/с).</param>
    /// <param name="bufferSizeKbit">Размер буфера VBV (кбит).</param>
    public static void AppendBitrateArguments(List<string> args, int targetBitrateKbps, int minBitrateKbps, int maxBitrateKbps, int bufferSizeKbit)
    {
        ArgumentNullException.ThrowIfNull(args);

        args.AddRange(new[] { "-b:v", $"{targetBitrateKbps}k" });
        if (minBitrateKbps > 0)
        {
            args.AddRange(new[] { "-minrate", $"{minBitrateKbps}k" });
        }
        if (maxBitrateKbps > 0)
        {
            args.AddRange(new[] { "-maxrate", $"{maxBitrateKbps}k" });
        }
        if (bufferSizeKbit > 0)
        {
            args.AddRange(new[] { "-bufsize", $"{bufferSizeKbit}k" });
        }
    }
}
