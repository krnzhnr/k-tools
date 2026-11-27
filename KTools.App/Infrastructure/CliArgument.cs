// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Text;

namespace KTools_App.Infrastructure;

/// <summary>
/// Единый механизм квотирования аргументов командной строки для внешних утилит
/// (mkvmerge, ffmpeg и другие). Заменяет точечные <c>Replace</c> и ручные кавычки
/// в скриптах: скрипты передают сырые значения, а квотирование выполняется здесь.
/// Правила соответствуют разбору CommandLineToArgvW: значение оборачивается
/// в двойные кавычки, внутренние кавычки и предшествующие им обратные слеши
/// экранируются, поэтому название вида 'Комментарий "режиссёра"' остаётся
/// одним аргументом и не внедряет чужие флаги.
/// Все комментарии выполнены исключительно на русском языке.
/// </summary>
public static class CliArgument
{
    /// <summary>
    /// Экранировать одно значение как один аргумент командной строки.
    /// Пустое значение даёт пару кавычек.
    /// </summary>
    /// <param name="value">Сырое значение без ручных кавычек.</param>
    /// <returns>Безопасный токен в двойных кавычках.</returns>
    public static string Quote(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');

        int backslashes = 0;
        foreach (char current in value)
        {
            if (current == '\\')
            {
                backslashes++;
                continue;
            }

            if (current == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
            }
            else
            {
                builder.Append('\\', backslashes);
                builder.Append(current);
            }

            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    /// <summary>
    /// Собрать командную строку из отдельных токенов: флаги остаются как есть,
    /// значения с пробелами, кавычками или пустые квотируются через <see cref="Quote"/>.
    /// Один слой устаревших ручных кавычек вокруг токена снимается, чтобы
    /// переход на единый механизм не дублировал квотирование.
    /// </summary>
    /// <param name="args">Токены в порядке следования (флаги и сырые значения).</param>
    /// <returns>Готовая строка для <c>ProcessStartInfo.Arguments</c>.</returns>
    public static string Join(IEnumerable<string?> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var parts = new List<string>();
        foreach (string? raw in args)
        {
            string token = StripLegacyQuotes(raw ?? string.Empty);
            parts.Add(NeedsQuoting(token) ? Quote(token) : token);
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Проверяет, требует ли токен квотирования.
    /// </summary>
    private static bool NeedsQuoting(string token)
    {
        if (token.Length == 0)
        {
            return true;
        }

        foreach (char current in token)
        {
            if (char.IsWhiteSpace(current) || current == '"')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Снимает один слой устаревших ручных кавычек вокруг токена.
    /// </summary>
    private static string StripLegacyQuotes(string token)
    {
        if (token.Length >= 2 && token[0] == '"' && token[token.Length - 1] == '"')
        {
            return token.Substring(1, token.Length - 2);
        }

        return token;
    }
}
