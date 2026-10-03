// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace KTools_App.Infrastructure;

/// <summary>
/// Обособленный парсер файлов субтитров стандарта WebVTT (.vtt).
/// Реализует стандарты W3C WebVTT: извлечение таймкодов, игнорирование служебных блоков
/// (NOTE, STYLE, REGION), разбор тегов голоса и очистку параметров позиционирования.
/// </summary>
public sealed class VttParser : IVttParser
{
    // Регулярное выражение для удаления HTML- и WebVTT-тегов (<i>, <b>, <c.yellow>, <v Speaker>, etc.)
    private static readonly Regex HtmlVttTagPattern = new(
        @"</?[a-z][a-z0-9_.]*(?:\s+[^>]*?)?>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Регулярное выражение для удаления караоке-таймкодов WebVTT (<00:19.000>)
    private static readonly Regex VttTimestampTagPattern = new(
        @"<(?:\d{1,2}:)?\d{2}:\d{2}[.,]\d{1,3}>",
        RegexOptions.Compiled);

    // Регулярное выражение для извлечения актёра/говорящего из тега голоса WebVTT
    private static readonly Regex VoiceTagPattern = new(
        @"^<v(?:\.[^>]+)?\s+([^>]+)>(.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Регулярное выражение для разделения содержимого на блоки по пустым строкам
    private static readonly Regex BlockSplitPattern = new(
        @"(?:\r\n|\r|\n)\s*(?:\r\n|\r|\n)+",
        RegexOptions.Compiled);

    /// <summary>
    /// Распарсить файл субтитров формата WebVTT (.vtt) в единую модель диалогов AssData.
    /// </summary>
    /// <param name="filePath">Полный путь к файлу субтитров .vtt на диске.</param>
    /// <returns>Разобранные реплики субтитров в модели AssData.</returns>
    public AssData Parse(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var data = new AssData();
        string content = ReadFileWithFallbackEncoding(filePath);

        string[] blocks = BlockSplitPattern.Split(content);
        foreach (string block in blocks)
        {
            string trimmedBlock = block.Trim();
            if (string.IsNullOrEmpty(trimmedBlock))
            {
                continue;
            }

            string[] lines = trimmedBlock.Split(
                new[] { "\r\n", "\r", "\n" },
                StringSplitOptions.None);

            if (lines.Length == 0)
            {
                continue;
            }

            // Поиск строки с временными метками -->
            int timeLineIndex = -1;
            string startVtt = string.Empty;
            string endVtt = string.Empty;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Contains("-->", StringComparison.Ordinal))
                {
                    string[] timeParts = line.Split(new[] { "-->" }, StringSplitOptions.None);
                    if (timeParts.Length >= 2)
                    {
                        startVtt = timeParts[0].Trim();
                        string rawEnd = timeParts[1].Trim();

                        // Конечный таймкод может содержать параметры размещения (align:start, line:0 и т.д.)
                        int firstSpace = -1;
                        for (int c = 0; c < rawEnd.Length; c++)
                        {
                            if (char.IsWhiteSpace(rawEnd[c]))
                            {
                                firstSpace = c;
                                break;
                            }
                        }

                        endVtt = firstSpace >= 0 ? rawEnd.Substring(0, firstSpace).Trim() : rawEnd;
                        timeLineIndex = i;
                        break;
                    }
                }
            }

            // Если в блоке отсутствует строка тайминга -->, это заголовок (WEBVTT) или служебная секция
            if (timeLineIndex < 0 || string.IsNullOrEmpty(startVtt) || string.IsNullOrEmpty(endVtt))
            {
                continue;
            }

            // Если перед таймингом расположен маркер комментария или стиля, пропускаем блок
            if (timeLineIndex > 0 && (
                lines[0].Trim().StartsWith("NOTE", StringComparison.OrdinalIgnoreCase) ||
                lines[0].Trim().StartsWith("STYLE", StringComparison.OrdinalIgnoreCase) ||
                lines[0].Trim().StartsWith("REGION", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var textLines = new List<string>();
            string actor = string.Empty;

            for (int j = timeLineIndex + 1; j < lines.Length; j++)
            {
                string textLine = lines[j];

                // Распознаём тег голоса/актёра WebVTT: <v Roger Bingham>Текст или <v.loud Speaker>Текст
                var voiceMatch = VoiceTagPattern.Match(textLine);
                if (voiceMatch.Success)
                {
                    if (string.IsNullOrEmpty(actor))
                    {
                        actor = voiceMatch.Groups[1].Value.Trim();
                    }
                    string remaining = voiceMatch.Groups[2].Value;
                    if (remaining.EndsWith("</v>", StringComparison.OrdinalIgnoreCase))
                    {
                        remaining = remaining.Substring(0, remaining.Length - 4);
                    }
                    textLine = remaining;
                }

                textLines.Add(textLine);
            }

            string startAss = VttTimeToAss(startVtt);
            string endAss = VttTimeToAss(endVtt);
            string text = string.Join("\\N", textLines);

            data.Dialogues.Add(new AssDialogue(
                start: startAss,
                end: endAss,
                style: "Default",
                actor: actor,
                effect: string.Empty,
                text: text
            ));
        }

        return data;
    }

    /// <summary>
    /// Конвертировать таймкод WebVTT (HH:MM:SS.mmm или MM:SS.mmm) в формат таймкода ASS (H:MM:SS.CC).
    /// </summary>
    /// <param name="vttTime">Строка таймкода WebVTT.</param>
    /// <returns>Строка таймкода в формате ASS.</returns>
    public string VttTimeToAss(string vttTime)
    {
        if (string.IsNullOrWhiteSpace(vttTime))
        {
            return "0:00:00.00";
        }

        try
        {
            string clean = vttTime.Trim().Replace(',', '.');
            string[] parts = clean.Split('.');
            string timePart = parts[0];
            string msPart = parts.Length > 1 ? parts[1] : "0";

            string[] t = timePart.Split(':');
            int h = 0;
            int m = 0;
            int s = 0;

            if (t.Length >= 3)
            {
                h = int.Parse(t[0], CultureInfo.InvariantCulture);
                m = int.Parse(t[1], CultureInfo.InvariantCulture);
                s = int.Parse(t[2], CultureInfo.InvariantCulture);
            }
            else if (t.Length == 2)
            {
                m = int.Parse(t[0], CultureInfo.InvariantCulture);
                s = int.Parse(t[1], CultureInfo.InvariantCulture);
            }
            else if (t.Length == 1)
            {
                s = int.Parse(t[0], CultureInfo.InvariantCulture);
            }

            if (msPart.Length == 1)
            {
                msPart += "00";
            }
            else if (msPart.Length == 2)
            {
                msPart += "0";
            }
            else if (msPart.Length > 3)
            {
                msPart = msPart.Substring(0, 3);
            }

            int ms = int.Parse(msPart, CultureInfo.InvariantCulture);
            int cs = ms / 10;

            return $"{h}:{m:D2}:{s:D2}.{cs:D2}";
        }
        catch
        {
            return "0:00:00.00";
        }
    }

    /// <summary>
    /// Очистить текст реплики WebVTT от тегов стилей, классов, караоке и декодировать HTML-сущности.
    /// </summary>
    /// <param name="text">Исходный текст реплики WebVTT.</param>
    /// <returns>Очищенный текст без тегов.</returns>
    public string StripVttTags(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // Удаляем караоке-метки WebVTT (<00:19.000>)
        string cleaned = VttTimestampTagPattern.Replace(text, string.Empty);

        // Удаляем HTML- и WebVTT-теги (<i>, <b>, <c.yellow>, <v Speaker>, etc.)
        cleaned = HtmlVttTagPattern.Replace(cleaned, string.Empty);

        // Декодируем стандартные HTML-сущности (&amp; -> &, &lt; -> < и т.д.)
        cleaned = WebUtility.HtmlDecode(cleaned);

        return cleaned;
    }

    /// <summary>
    /// Читает содержимое файла с поддержкой UTF-8, автоматическим снятием BOM и откатом к Windows-1251.
    /// </summary>
    private static string ReadFileWithFallbackEncoding(string filePath)
    {
        var utf8Strict = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);
        byte[] bytes = File.ReadAllBytes(filePath);

        int offset = HasUtfBom(bytes) ? 3 : 0;

        try
        {
            return utf8Strict.GetString(bytes, offset, bytes.Length - offset);
        }
        catch
        {
            var cp1251 = Encoding.GetEncoding("windows-1251");
            return cp1251.GetString(bytes, offset, bytes.Length - offset);
        }
    }

    /// <summary>
    /// Определяет наличие сигнатуры UTF-8 BOM в начале массива байтов.
    /// </summary>
    private static bool HasUtfBom(byte[] bytes)
    {
        return bytes.Length >= 3 &&
               bytes[0] == 0xEF &&
               bytes[1] == 0xBB &&
               bytes[2] == 0xBF;
    }
}
