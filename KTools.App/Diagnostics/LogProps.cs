// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace KTools_App.Diagnostics;

public static class LogProps
{
    public static Dictionary<string, object?> Create() => new(4, StringComparer.Ordinal);

    public static Dictionary<string, object?> Create(string key, object? value) =>
        new(4, StringComparer.Ordinal) { [key] = value };

    public static Dictionary<string, object?> Create(
        string firstKey,
        object? firstValue,
        string secondKey,
        object? secondValue)
    {
        Dictionary<string, object?> result = new(4, StringComparer.Ordinal)
        {
            [firstKey] = firstValue,
            [secondKey] = secondValue
        };
        return result;
    }

    public static Dictionary<string, object?> Create(
        string firstKey,
        object? firstValue,
        string secondKey,
        object? secondValue,
        string thirdKey,
        object? thirdValue)
    {
        Dictionary<string, object?> result = new(4, StringComparer.Ordinal)
        {
            [firstKey] = firstValue,
            [secondKey] = secondValue,
            [thirdKey] = thirdValue
        };
        return result;
    }

    public static Dictionary<string, object?> With(
        this Dictionary<string, object?>? target,
        string key,
        object? value)
    {
        Dictionary<string, object?> result =
            target ?? new Dictionary<string, object?>(4, StringComparer.Ordinal);
        result[key] = value;
        return result;
    }

    /// <summary>
    /// Единственная разрешённая форма пути для диагностических и пользовательских полей
    /// журнала (W-03): сначала отсекается query-строка и фрагмент URL, затем берётся
    /// <see cref="Path.GetFileName(string)"/>. Абсолютный путь, UNC-путь, URL-схема
    /// и подписанные параметры (<c>Expires</c>, <c>Policy</c>, <c>Key-Pair-Id</c>,
    /// <c>AWSAccessKeyId</c>) в журнал не попадают — только имя файла.
    /// </summary>
    public static string FileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return LogRedactor.UnknownIdentifier;
        }

        try
        {
            string trimmed = StripQueryAndFragment(path.Trim());
            string name = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(name) ? LogRedactor.UnknownIdentifier : name;
        }
        catch (Exception)
        {
            return LogRedactor.UnknownIdentifier;
        }
    }

    /// <summary>
    /// Политика «только имя файла» (W-03) в единственной реализации: правило отсечения
    /// query-строки и фрагмента живёт в <see cref="FileName"/>, поэтому две политики
    /// для одного и того же свойства журнала не могут разойтись.
    /// </summary>
    /// <param name="path">Исходный путь, который может оказаться URL или UNC-путём.</param>
    public static string FileNameOnly(string? path) => FileName(path);

    /// <summary>
    /// Отсекает query-строку и фрагмент по первой встретившейся метке, чтобы
    /// подписанные параметры загрузчика не попадали в имя файла.
    /// </summary>
    private static string StripQueryAndFragment(string value)
    {
        int query = value.IndexOf('?');
        int fragment = value.IndexOf('#');
        int cut = query < 0 ? fragment : fragment < 0 ? query : Math.Min(query, fragment);
        return cut >= 0 ? value[..cut] : value;
    }

    public static string RootName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return LogRedactor.UnknownIdentifier;
        }

        try
        {
            string? root = Path.GetPathRoot(StripQueryAndFragment(path.Trim()));
            return string.IsNullOrWhiteSpace(root) ? LogRedactor.UnknownIdentifier : root;
        }
        catch (Exception)
        {
            return LogRedactor.UnknownIdentifier;
        }
    }

    public static string Fingerprint(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return LogRedactor.UnknownIdentifier;
        }

        try
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return "sha256:" + Convert.ToHexString(bytes, 0, 6).ToLowerInvariant();
        }
        catch (Exception)
        {
            return LogRedactor.UnknownIdentifier;
        }
    }

    public static string Milliseconds(double value) =>
        Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);

    public static string MegabitsPerSecond(double bitsPerSecond) =>
        (bitsPerSecond / 1_000_000d).ToString("0.###", CultureInfo.InvariantCulture);

    public static string KilobitsPerSecond(double bitsPerSecond) =>
        (bitsPerSecond / 1_000d).ToString("0.###", CultureInfo.InvariantCulture);
}
