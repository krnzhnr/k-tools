// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Security.Cryptography;

namespace KTools_App.Core;

/// <summary>
/// Проверка целостности скачанных файлов по хешу SHA-256.
/// Сверка выполняется после скачивания и до распаковки или запуска,
/// чтобы подмена файла детектировалась до передачи управления внешнему коду.
/// </summary>
public static class FileIntegrity
{
    /// <summary>
    /// Префикс, допустимый в начале ожидаемого значения хеша.
    /// </summary>
    private const string Sha256Prefix = "sha256:";

    /// <summary>
    /// Вычисляет SHA-256 хеш файла и возвращает его в шестнадцатеричном виде.
    /// </summary>
    /// <param name="filePath">Полный путь к проверяемому файлу.</param>
    /// <returns>Хеш в верхнем регистре без разделителей.</returns>
    /// <exception cref="ArgumentException">Путь к файлу не задан.</exception>
    /// <exception cref="FileNotFoundException">Файл отсутствует на диске.</exception>
    public static string ComputeSha256Hex(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Путь к файлу не задан.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Файл для сверки хеша не найден.", filePath);
        }

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, false);
        using var sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Сверяет SHA-256 хеш файла с ожидаемым значением.
    /// Сравнение нечувствительно к регистру, пробелам по краям и префиксу «sha256:».
    /// </summary>
    /// <param name="filePath">Полный путь к скачанному файлу.</param>
    /// <param name="expectedHex">Ожидаемый хеш в шестнадцатеричном виде.</param>
    /// <exception cref="ArgumentException">Ожидаемый хеш не задан.</exception>
    /// <exception cref="InvalidDataException">Фактический хеш не совпал с ожидаемым: файл повреждён или подменён.</exception>
    public static void VerifySha256(string filePath, string expectedHex)
    {
        if (string.IsNullOrWhiteSpace(expectedHex))
        {
            throw new ArgumentException("Ожидаемый хеш не задан.", nameof(expectedHex));
        }

        string actualHex = ComputeSha256Hex(filePath);
        if (!NormalizeHex(actualHex).Equals(NormalizeHex(expectedHex), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Контрольная сумма скачанного файла не совпала с ожидаемой: файл повреждён или подменён, запуск отменён.");
        }
    }

    /// <summary>
    /// Приводит значение хеша к сопоставимому виду: убирает пробелы и допустимый префикс.
    /// </summary>
    /// <param name="value">Исходное значение хеша.</param>
    /// <returns>Нормализованное значение хеша.</returns>
    private static string NormalizeHex(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[Sha256Prefix.Length..];
        }

        return trimmed;
    }
}
