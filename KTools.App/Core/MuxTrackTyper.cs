// -*- coding: utf-8 -*-
using System;

namespace KTools_App.Core;

/// <summary>
/// Роль дорожки субтитров, определённая по суффиксу имени файла.
/// </summary>
public enum MuxSubsRole
{
    /// <summary>Полные субтитры (основная дорожка, включая точное совпадение имени).</summary>
    Full,

    /// <summary>Надписи и песни (signs/songs).</summary>
    Signs,

    /// <summary>Прочие субтитры (языковые суффиксы, forced, sdh и т.п.).</summary>
    Other
}

/// <summary>
/// Определяет роль сопутствующего файла по суффиксу перед расширением
/// (например, "video.signs.ass" — надписи, "video.full.ass" — полные).
/// Имя файла остаётся главным источником правды; явный пин в UI имеет приоритет.
/// Все комментарии на русском языке.
/// </summary>
public static class MuxTrackTyper
{
    private static readonly char[] TokenSeparators = ['.', '_', '-', ' ', '(', ')', '[', ']', '{', '}'];

    private static readonly string[] SignsTokens =
        ["sign", "signs", "sings", "songs", "song", "надписи", "надпись", "титры"];

    private static readonly string[] FullTokens =
        ["full", "полные", "полный", "complete", "main"];

    /// <summary>
    /// Определяет роль субтитров по остатку имени после базового имени видео.
    /// Точное совпадение (без остатка) считается полными субтитрами.
    /// </summary>
    public static MuxSubsRole GetSubsRole(string videoStem, string companionStem)
    {
        if (string.IsNullOrEmpty(videoStem) || string.IsNullOrEmpty(companionStem))
        {
            return MuxSubsRole.Other;
        }

        string remainder;
        if (companionStem.Equals(videoStem, StringComparison.OrdinalIgnoreCase))
        {
            remainder = string.Empty;
        }
        else if (companionStem.Length > videoStem.Length
            && companionStem.StartsWith(videoStem, StringComparison.OrdinalIgnoreCase))
        {
            remainder = companionStem.Substring(videoStem.Length);
        }
        else
        {
            return MuxSubsRole.Other;
        }

        string[] tokens = remainder.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return MuxSubsRole.Full;
        }

        foreach (string token in tokens)
        {
            foreach (string signs in SignsTokens)
            {
                if (token.Equals(signs, StringComparison.OrdinalIgnoreCase))
                {
                    return MuxSubsRole.Signs;
                }
            }
        }

        foreach (string token in tokens)
        {
            foreach (string full in FullTokens)
            {
                if (token.Equals(full, StringComparison.OrdinalIgnoreCase))
                {
                    return MuxSubsRole.Full;
                }
            }
        }

        return MuxSubsRole.Other;
    }

    /// <summary>
    /// Возвращает итоговую роль субтитров: ручной выбор имеет приоритет над автоопределением по суффиксу.
    /// </summary>
    public static MuxSubsRole ResolveSubsRole(string videoStem, string companionStem, MuxSubsRole? manualOverride)
    {
        return manualOverride ?? GetSubsRole(videoStem, companionStem);
    }

    /// <summary>
    /// Возвращает русскую подпись роли для отображения в UI.
    /// </summary>
    public static string GetRoleLabel(MuxSubsRole role)
    {
        return role switch
        {
            MuxSubsRole.Full => "Полные",
            MuxSubsRole.Signs => "Надписи",
            _ => "Субтитры"
        };
    }

    /// <summary>
    /// Возвращает порядок сортировки дорожек при муксинге: надписи, полные, прочие.
    /// Надписи идут первыми в своём списке и получают флаги default/forced.
    /// </summary>
    public static int GetRoleOrder(MuxSubsRole role)
    {
        return role switch
        {
            MuxSubsRole.Signs => 0,
            MuxSubsRole.Full => 1,
            _ => 2
        };
    }

    private static readonly Dictionary<string, string> AudioLangMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "en", "eng" }, { "eng", "eng" },
        { "ru", "rus" }, { "rus", "rus" },
        { "de", "deu" }, { "ger", "deu" }, { "deu", "deu" },
        { "fr", "fra" }, { "fre", "fra" }, { "fra", "fra" },
        { "es", "spa" }, { "spa", "spa" },
        { "it", "ita" }, { "ita", "ita" },
        { "ja", "jpn" }, { "jpn", "jpn" },
        { "uk", "ukr" }, { "ukr", "ukr" },
        { "pl", "pol" }, { "pol", "pol" },
        { "pt", "por" }, { "por", "por" },
        { "zh", "zho" }, { "chi", "zho" }, { "zho", "zho" },
        { "ko", "kor" }, { "kor", "kor" },
        { "tr", "tur" }, { "tur", "tur" },
        { "ar", "ara" }, { "ara", "ara" },
        { "hi", "hin" }, { "hin", "hin" },
        { "cs", "ces" }, { "cze", "ces" }, { "ces", "ces" },
        { "hu", "hun" }, { "hun", "hun" },
        { "bg", "bul" }, { "bul", "bul" },
        { "sr", "srp" }, { "srp", "srp" },
        { "he", "heb" }, { "heb", "heb" },
        { "nl", "nld" }, { "dut", "nld" }, { "nld", "nld" },
        { "sv", "swe" }, { "swe", "swe" }
    };

    private static string[] GetRemainderTokens(string videoStem, string companionStem)
    {
        if (string.IsNullOrEmpty(videoStem) || string.IsNullOrEmpty(companionStem))
        {
            return [];
        }

        string remainder;
        if (companionStem.Equals(videoStem, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        if (companionStem.Length > videoStem.Length
            && companionStem.StartsWith(videoStem, StringComparison.OrdinalIgnoreCase))
        {
            remainder = companionStem.Substring(videoStem.Length);
        }
        else
        {
            return [];
        }

        return remainder.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Выводит заголовок дорожки из различающейся части имени
    /// ("s01e01.LostFilm" → "LostFilm"). Суффиксы только из языка заголовка не дают.
    /// </summary>
    public static string InferTrackTitle(string videoStem, string companionStem)
    {
        string[] tokens = GetRemainderTokens(videoStem, companionStem);
        if (tokens.Length == 0)
        {
            return string.Empty;
        }

        bool allLanguage = true;
        foreach (string token in tokens)
        {
            if (!AudioLangMap.ContainsKey(token))
            {
                allLanguage = false;
                break;
            }
        }

        if (allLanguage)
        {
            return string.Empty;
        }

        return string.Join(' ', tokens);
    }
}
