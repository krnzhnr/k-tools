// -*- coding: utf-8 -*-
using System;
using System.IO;

namespace KTools_App.Core;

/// <summary>
/// Представляет информацию о встроенном вложении внутри медиа-файла
/// (например, файл шрифта, обложка или метаданные).
/// Все комментарии выполнены исключительно на русском языке.
/// </summary>
public sealed class MediaAttachment
{
    /// <summary>
    /// Идентификатор вложения в контейнере для последующего извлечения.
    /// </summary>
    public int AttachmentId { get; set; }

    /// <summary>
    /// Оригинальное имя прикрепленного файла (например, "ArialBold.ttf").
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// MIME-тип содержимого вложения (например, "application/x-truetype-font").
    /// </summary>
    public string MimeType { get; set; } = string.Empty;

    /// <summary>
    /// Размер вложенного файла в байтах.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Определяет, является ли вложение шрифтом на основе MIME-типа или расширения.
    /// </summary>
    public bool IsFont
    {
        get
        {
            if (string.IsNullOrEmpty(FileName)) return false;

            // Проверяем MIME-тип
            if (!string.IsNullOrEmpty(MimeType))
            {
                string mimeLower = MimeType.ToLowerInvariant();
                if (mimeLower.Contains("font") || 
                    mimeLower.Contains("truetype") || 
                    mimeLower.Contains("opentype"))
                {
                    return true;
                }
            }

            // Проверяем расширение файла на всякий случай
            string ext = Path.GetExtension(FileName).ToLowerInvariant();
            return ext == ".ttf" || ext == ".otf" || ext == ".woff" || ext == ".woff2";
        }
    }
}
