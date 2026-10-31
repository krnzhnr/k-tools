// -*- coding: utf-8 -*-
using System;
using System.Globalization;
using System.Text.Json;

namespace KTools_App.Core;

/// <summary>
/// Единая нормализация значений настроек из словарей запуска.
/// Значения, загруженные с диска и ни разу не тронутые в сеансе, лежат в виде
/// <see cref="JsonElement"/>: наивный ToString() даёт строку в кавычках
/// (<c>"ultrafast"</c> вместо <c>ultrafast</c>), а Convert бросает исключение.
/// Все чтения настроек энкодерами обязаны идти через этот хелпер, иначе
/// интерфейс показывает одно значение, а кодировщик применяет другое.
/// Все комментарии выполнены исключительно на русском языке.
/// </summary>
public static class SettingValue
{
    /// <summary>
    /// Приводит значение к строке (для селекторов кодеков, пресетов, режимов).
    /// </summary>
    public static string AsString(object? value, string? defaultValue = null)
    {
        if (value == null)
        {
            return defaultValue ?? string.Empty;
        }
        if (value is string s)
        {
            return s;
        }
        if (value is JsonElement json)
        {
            return json.ValueKind switch
            {
                JsonValueKind.String => json.GetString() ?? (defaultValue ?? string.Empty),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                JsonValueKind.Number => json.GetRawText(),
                JsonValueKind.Null or JsonValueKind.Undefined => defaultValue ?? string.Empty,
                _ => json.GetRawText()
            };
        }
        try
        {
            return value.ToString() ?? (defaultValue ?? string.Empty);
        }
        catch
        {
            return defaultValue ?? string.Empty;
        }
    }

    /// <summary>
    /// Приводит значение к логическому флагу (тумблеры, lossless, авторасчёт).
    /// </summary>
    public static bool AsBool(object? value, bool defaultValue = false)
    {
        if (value == null)
        {
            return defaultValue;
        }
        if (value is bool b)
        {
            return b;
        }
        if (value is string s)
        {
            if (bool.TryParse(s, out bool parsed))
            {
                return parsed;
            }
            return defaultValue;
        }
        if (value is JsonElement json)
        {
            return json.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(json.GetString(), out bool parsed) ? parsed : defaultValue,
                _ => defaultValue
            };
        }
        try
        {
            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return defaultValue;
        }
    }

    /// <summary>
    /// Приводит значение к целому числу (CRF, битрейты, lookahead, b-кадры).
    /// </summary>
    public static int AsInt(object? value, int defaultValue = 0)
    {
        if (value == null)
        {
            return defaultValue;
        }
        if (value is int i)
        {
            return i;
        }
        if (value is JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out int num))
            {
                return num;
            }
            if (json.ValueKind == JsonValueKind.String &&
                int.TryParse(json.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int str))
            {
                return str;
            }
            return defaultValue;
        }
        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return defaultValue;
        }
    }

    /// <summary>
    /// Приводит значение к числу с плавающей точкой (силы AQ, psy).
    /// </summary>
    public static float AsFloat(object? value, float defaultValue = 0f)
    {
        if (value == null)
        {
            return defaultValue;
        }
        if (value is float f)
        {
            return f;
        }
        if (value is double d)
        {
            return (float)d;
        }
        if (value is JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.Number && json.TryGetSingle(out float num))
            {
                return num;
            }
            if (json.ValueKind == JsonValueKind.String &&
                float.TryParse(json.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float str))
            {
                return str;
            }
            return defaultValue;
        }
        try
        {
            return Convert.ToSingle(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return defaultValue;
        }
    }
}
