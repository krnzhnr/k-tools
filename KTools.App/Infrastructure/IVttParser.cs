// -*- coding: utf-8 -*-
namespace KTools_App.Infrastructure;

/// <summary>
/// Интерфейс обособленного парсера файлов субтитров формата WebVTT (.vtt).
/// Отвечает за разбор блоков, временных меток, параметров размещения и тегов голоса.
/// </summary>
public interface IVttParser
{
    /// <summary>
    /// Распарсить файл субтитров формата WebVTT (.vtt) в единую модель диалогов AssData.
    /// </summary>
    /// <param name="filePath">Полный путь к файлу субтитров .vtt на диске.</param>
    /// <returns>Разобранные реплики субтитров в модели AssData.</returns>
    AssData Parse(string filePath);

    /// <summary>
    /// Конвертировать таймкод WebVTT (HH:MM:SS.mmm или MM:SS.mmm) в формат таймкода ASS (H:MM:SS.CC).
    /// </summary>
    /// <param name="vttTime">Строка таймкода WebVTT.</param>
    /// <returns>Строка таймкода в формате ASS.</returns>
    string VttTimeToAss(string vttTime);

    /// <summary>
    /// Очистить текст реплики WebVTT от тегов стилей, классов, караоке и декодировать HTML-сущности.
    /// </summary>
    /// <param name="text">Исходный текст реплики WebVTT.</param>
    /// <returns>Очищенный текст без тегов.</returns>
    string StripVttTags(string text);
}
