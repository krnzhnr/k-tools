// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace KTools_App.Infrastructure;

/// <summary>
/// Представляет одну строку диалога (реплику) из файла субтитров.
/// </summary>
public sealed class AssDialogue
{
    public AssDialogue(
        string start,
        string end,
        string style,
        string actor,
        string effect,
        string text)
    {
        Start = start;
        End = end;
        Style = style;
        Actor = actor;
        Effect = effect;
        Text = text;
    }

    /// <summary>
    /// Время начала реплики в формате ASS (H:MM:SS.CC).
    /// </summary>
    public string Start { get; }

    /// <summary>
    /// Время окончания реплики в формате ASS (H:MM:SS.CC).
    /// </summary>
    public string End { get; }

    /// <summary>
    /// Стиль отображения субтитра.
    /// </summary>
    public string Style { get; }

    /// <summary>
    /// Имя актера (персонажа), произносящего реплику.
    /// </summary>
    public string Actor { get; }

    /// <summary>
    /// Название спецэффекта реплики.
    /// </summary>
    public string Effect { get; }

    /// <summary>
    /// Текст реплики (может содержать управляющие теги).
    /// </summary>
    public string Text { get; set; }
}

/// <summary>
/// Результат парсинга файла субтитров, содержащий список всех реплик.
/// </summary>
public sealed class AssData
{
    /// <summary>
    /// Список разобранных диалогов.
    /// </summary>
    public List<AssDialogue> Dialogues { get; } = new();

    /// <summary>
    /// Оригинальный заголовок файла (стили, метаданные и т.д.).
    /// </summary>
    public string Header { get; set; } = string.Empty;
}

/// <summary>
/// Парсер файлов субтитров в форматах ASS/SSA и SRT.
/// Извлекает диалоги, актёров и деликатно удаляет теги форматирования.
/// </summary>
public sealed class AssParser : IAssParser
{


    // Регулярное выражение для удаления ASS-тегов форматирования.
    private static readonly Regex TagPattern = new(
        @"\{[^}]*\}",
        RegexOptions.Compiled);

    // Регулярное выражение для поиска слов в верхнем регистре (от 2-х букв).
    private static readonly Regex CapsPattern = new(
        @"\b[A-ZА-ЯЁ]{2,}\b",
        RegexOptions.Compiled);

    // Регэкс для удаления HTML-подобных тегов SRT/VTT (<i>, <b>, <font>, etc.)
    // Исключает случайное повреждение декоративных << >> и кириллицы.
    private static readonly Regex HtmlTagPattern = new(
        @"</?[a-z][a-z0-9]*(?:\s+[^>]*?)?>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Регэкс для разделения текста по \N и \n с захватом разделителей.
    private static readonly Regex NewlineSplitPattern = new(
        @"(\\N|\\n)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Регэкс для удаления повторных переносов строк.
    private static readonly Regex DuplicateNewlinePattern = new(
        @"(\\N|\\n){2,}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const string DialoguePrefix = "Dialogue:";

    public AssParser() { }



    /// <summary>
    /// Распарсить файл субтитров (ASS/SSA или SRT).
    /// </summary>
    public AssData Parse(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext == ".srt")
        {
            return ParseSrt(filePath);
        }
        return ParseAss(filePath);
    }

    /// <summary>
    /// Распарсить ASS-файл и извлечь все строки диалогов.
    /// </summary>
    public AssData ParseAss(string filePath)
    {
        var data = new AssData();
        bool inEvents = false;
        bool metFirstDialogue = false;
        var formatFields = new List<string>();
        var headerBuilder = new System.Text.StringBuilder();

        string content = ReadFileWithFallbackEncoding(filePath);

        foreach (string line in content.Split(
            new[] { "\r\n", "\r", "\n" },
            StringSplitOptions.None))
        {
            string stripped = line.Trim();

            if (stripped.StartsWith(DialoguePrefix, StringComparison.OrdinalIgnoreCase))
            {
                metFirstDialogue = true;
                var dialogue = ParseDialogueLine(stripped, formatFields);
                if (dialogue != null)
                {
                    data.Dialogues.Add(dialogue);
                }
            }
            else
            {
                if (!metFirstDialogue)
                {
                    headerBuilder.AppendLine(line);
                }

                // Определение начала секции [Events]
                if (stripped.Equals("[events]", StringComparison.OrdinalIgnoreCase))
                {
                    inEvents = true;
                    continue;
                }

                // Выход из секции [Events] при начале новой секции
                if (stripped.StartsWith("[") && stripped.EndsWith("]"))
                {
                    if (inEvents)
                    {
                        break;
                    }
                    continue;
                }

                if (!inEvents)
                {
                    continue;
                }

                // Парсинг строки Format
                if (stripped.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                {
                    string[] rawFields = stripped.Substring("Format:".Length).Split(',');
                    formatFields = rawFields
                        .Select(f => f.Trim().ToLowerInvariant())
                        .ToList();
                    continue;
                }
            }
        }

        data.Header = headerBuilder.ToString();
        return data;
    }

    /// <summary>
    /// Распарсить SRT-файл и преобразовать в AssDialogue.
    /// </summary>
    public AssData ParseSrt(string filePath)
    {
        var data = new AssData();
        string content = ReadFileWithFallbackEncoding(filePath);

        // Разделяем на блоки по пустым строкам
        string[] blocks = Regex.Split(content.Trim(), @"\r?\n\s*\r?\n");
        foreach (string block in blocks)
        {
            string[] lines = block.Trim().Split(
                new[] { "\r\n", "\r", "\n" },
                StringSplitOptions.None);
            if (lines.Length < 2)
            {
                continue;
            }

            string timeLine = "";
            var textLines = new List<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("-->"))
                {
                    timeLine = lines[i];
                    for (int j = i + 1; j < lines.Length; j++)
                    {
                        textLines.Add(lines[j]);
                    }
                    break;
                }
            }

            if (string.IsNullOrEmpty(timeLine))
            {
                continue;
            }

            try
            {
                string[] timeParts = timeLine.Split(
                    new[] { "-->" },
                    StringSplitOptions.None);
                string startSrt = timeParts[0].Trim();
                string endSrt = timeParts[1].Trim();

                string startAss = SrtTimeToAss(startSrt);
                string endAss = SrtTimeToAss(endSrt);
                string text = string.Join("\\N", textLines);

                data.Dialogues.Add(new AssDialogue(
                    start: startAss,
                    end: endAss,
                    style: "Default",
                    actor: "",
                    effect: "",
                    text: text
                ));
            }
            catch
            {
                // Пропускаем некорректный блок субтитра
            }
        }

        return data;
    }

    private AssDialogue? ParseDialogueLine(
        string line,
        List<string> formatFields)
    {
        string afterPrefix = line.Substring(DialoguePrefix.Length).Trim();
        int numFields = formatFields.Count > 0 ? formatFields.Count : 10;

        string[] parts = afterPrefix.Split(',', numFields);

        if (parts.Length < numFields)
        {
            return null;
        }

        var fieldMap = new Dictionary<string, string>();
        if (formatFields.Count > 0)
        {
            for (int i = 0; i < formatFields.Count; i++)
            {
                fieldMap[formatFields[i]] = parts[i].Trim();
            }
        }
        else
        {
            fieldMap["start"] = parts[1].Trim();
            fieldMap["end"] = parts[2].Trim();
            fieldMap["style"] = parts[3].Trim();
            fieldMap["name"] = parts[4].Trim();
            fieldMap["text"] = parts[9].Trim();
        }

        return new AssDialogue(
            start: fieldMap.GetValueOrDefault("start", "0:00:00.00"),
            end: fieldMap.GetValueOrDefault("end", "0:00:00.00"),
            style: fieldMap.GetValueOrDefault("style", "Default"),
            actor: fieldMap.GetValueOrDefault("name", fieldMap.GetValueOrDefault("actor", "")),
            effect: fieldMap.GetValueOrDefault("effect", ""),
            text: fieldMap.GetValueOrDefault("text", "")
        );
    }

    /// <summary>
    /// Очистить текст субтитров от всех тегов (ASS и HTML).
    /// </summary>
    public string StripTags(string text)
    {
        // Удаляем ASS override-блоки
        string cleaned = TagPattern.Replace(text, "");
        // Удаляем HTML-теги SRT
        cleaned = HtmlTagPattern.Replace(cleaned, "");
        // Конвертируем переносы и спецсимволы
        cleaned = cleaned
            .Replace("\\N", "\n")
            .Replace("\\n", "\n")
            .Replace("\\h", " ");
        return cleaned.Trim();
    }

    /// <summary>
    /// Проверить, является ли текст полным капсом.
    /// Текст считается капсом, если в нем > 1 буквы и все они заглавные.
    /// Игнорируются строки короче 2-х символов для защиты коротких реакций.
    /// </summary>
    public bool IsFullCaps(string text)
    {
        string clean = StripTags(text).Trim();
        if (clean.Length < 2)
        {
            return false;
        }

        // Оставляем только буквы для проверки регистра
        var lettersBuilder = new System.Text.StringBuilder();
        foreach (char c in clean)
        {
            if (char.IsLetter(c))
            {
                lettersBuilder.Append(c);
            }
        }
        string letters = lettersBuilder.ToString();

        // Фраза считается капсом, если в ней > 1 буквы и все они заглавные.
        return letters.Length > 1 && letters.All(char.IsUpper);
    }

    /// <summary>
    /// Удалить из текста части, состоящие из капса.
    /// </summary>
    public string StripCaps(string text)
    {
        // Разделяем по \N и \n с захватом разделителей (используем скомпилированный паттерн)
        string[] parts = NewlineSplitPattern.Split(text);
        var resultParts = new List<string>();

        for (int i = 0; i < parts.Length; i += 2)
        {
            string part = parts[i];
            if (!IsFullCaps(part))
            {
                resultParts.Add(part);
                if (i + 1 < parts.Length)
                {
                    resultParts.Add(parts[i + 1]);
                }
            }
        }

        string res = string.Concat(resultParts);
        
        // Убираем повторные \N\N и висящие края (используем скомпилированный паттерн)
        res = DuplicateNewlinePattern.Replace(res, "$1");
        
        // Обрезаем висящие слеши
        res = TrimSlashN(res);
        return res;
    }

    private string TrimSlashN(string text)
    {
        string current = text;
        while (current.StartsWith("\\N", StringComparison.OrdinalIgnoreCase) || 
               current.StartsWith("\\n", StringComparison.OrdinalIgnoreCase))
        {
            current = current.Substring(2);
        }
        while (current.EndsWith("\\N", StringComparison.OrdinalIgnoreCase) || 
               current.EndsWith("\\n", StringComparison.OrdinalIgnoreCase))
        {
            current = current.Substring(0, current.Length - 2);
        }
        return current;
    }

    /// <summary>
    /// Конвертировать таймкод ASS (H:MM:SS.CC) в WebVTT (HH:MM:SS.mmm).
    /// </summary>
    public static string AssTimeToVtt(string assTime)
    {
        try
        {
            string[] parts = assTime.Split(':');
            string hStr = parts[0];
            string mStr = parts[1];
            string[] sParts = parts[2].Split('.');
            string sStr = sParts[0];
            string csStr = sParts[1];

            int h = int.Parse(hStr);
            int m = int.Parse(mStr);
            int s = int.Parse(sStr);
            int cs = int.Parse(csStr);

            double totalSeconds = h * 3600 + m * 60 + s + cs / 100.0;
            int ms = (int)Math.Round((totalSeconds % 1) * 1000);
            int totalInt = (int)totalSeconds;

            s = totalInt % 60;
            m = (totalInt / 60) % 60;
            h = totalInt / 3600;

            if (ms == 1000)
            {
                ms = 0;
                s += 1;
                if (s == 60)
                {
                    s = 0;
                    m += 1;
                    if (m == 60)
                    {
                        m = 0;
                        h += 1;
                    }
                }
            }

            return $"{h:D2}:{m:D2}:{s:D2}.{ms:D3}";
        }
        catch
        {
            return "00:00:00.000";
        }
    }

    /// <summary>
    /// Конвертировать таймкод SRT (HH:MM:SS,mmm) в ASS (H:MM:SS.CC).
    /// </summary>
    public static string SrtTimeToAss(string srtTime)
    {
        try
        {
            string cleanSrt = srtTime.Replace(',', '.');
            string[] parts = cleanSrt.Split('.');
            string timePart = parts[0];
            string msPart = parts[1];

            string[] t = timePart.Split(':');
            int h = int.Parse(t[0]);
            int m = int.Parse(t[1]);
            int s = int.Parse(t[2]);
            int ms = int.Parse(msPart);
            int cs = ms / 10;

            return $"{h}:{m:D2}:{s:D2}.{cs:D2}";
        }
        catch
        {
            return "0:00:00.00";
        }
    }

    /// <summary>
    /// Собрать объект диалога обратно в строку формата ASS диалога.
    /// </summary>
    public string ToAssLine(AssDialogue dialogue)
    {
        string text = dialogue.Text.Replace("\n", "\\N");
        return $"Dialogue: 0,{dialogue.Start},{dialogue.End}," +
               $"{dialogue.Style},{dialogue.Actor},0,0,0," +
               $"{dialogue.Effect},{text}";
    }

    /// <summary>
    /// Получить минимальный заголовок ASS-файла.
    /// </summary>
    public string GetMinimalHeader()
    {
        return "[Script Info]\n" +
               "ScriptType: v4.00+\n\n" +
               "[Events]\n" +
               "Format: Layer, Start, End, Style, Name, " +
               "MarginL, MarginR, MarginV, Effect, Text\n";
    }

    private string ReadFileWithFallbackEncoding(string filePath)
    {
        var utf8Strict = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);
        try
        {
            return File.ReadAllText(filePath, utf8Strict);
        }
        catch
        {
            var cp1251 = Encoding.GetEncoding("windows-1251");
            return File.ReadAllText(filePath, cp1251);
        }
    }
}
