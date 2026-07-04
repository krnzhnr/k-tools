// -*- coding: utf-8 -*-
namespace KTools_App.Infrastructure;

/// <summary>
/// Интерфейс парсера файлов субтитров в форматах ASS/SSA и SRT.
/// </summary>
public interface IAssParser
{
    /// <summary>
    /// Распарсить файл субтитров (ASS/SSA или SRT).
    /// </summary>
    AssData Parse(string filePath);

    /// <summary>
    /// Удалить теги форматирования ASS/SRT/VTT из текста.
    /// </summary>
    string StripTags(string text);

    /// <summary>
    /// Удалить слова в верхнем регистре (капслок) из текста.
    /// </summary>
    string StripCaps(string text);

    /// <summary>
    /// Проверить, является ли текст полностью написанным в верхнем регистре.
    /// </summary>
    bool IsFullCaps(string text);

    /// <summary>
    /// Получить минимальный заголовок ASS-файла.
    /// </summary>
    string GetMinimalHeader();

    /// <summary>
    /// Сериализовать объект диалога в строку формата ASS.
    /// </summary>
    string ToAssLine(AssDialogue dialogue);
}
