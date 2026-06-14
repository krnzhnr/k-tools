// -*- coding: utf-8 -*-
using System;

namespace KTools_App.Core;

/// <summary>
/// Предоставляет статические методы для сравнения версий программного обеспечения в соответствии со спецификацией SemVer.
/// Все описания и XML-комментарии написаны строго на русском языке.
/// </summary>
public static class VersionComparer
{
    /// <summary>
    /// Сравнивает две строки версий по стандарту SemVer.
    /// Игнорирует ведущий символ 'v' (например, "v2.0.0" трактуется как "2.0.0").
    /// </summary>
    /// <param name="versionA">Первая строка версии.</param>
    /// <param name="versionB">Вторая строка версии.</param>
    /// <returns>
    /// Число 1, если версия versionA новее (больше) версии versionB;
    /// Число -1, если версия versionA старее (меньше) версии versionB;
    /// Число 0, если версии абсолютно идентичны.
    /// </returns>
    public static int CompareVersions(string versionA, string versionB)
    {
        if (string.IsNullOrEmpty(versionA) && string.IsNullOrEmpty(versionB)) return 0;
        if (string.IsNullOrEmpty(versionA)) return -1;
        if (string.IsNullOrEmpty(versionB)) return 1;

        // Удаляем префикс 'v', если он присутствует
        versionA = versionA.TrimStart('v');
        versionB = versionB.TrimStart('v');

        string baseA = versionA;
        string suffixA = string.Empty;
        int dashIdxA = versionA.IndexOf('-');
        if (dashIdxA >= 0)
        {
            baseA = versionA.Substring(0, dashIdxA);
            suffixA = versionA.Substring(dashIdxA + 1);
        }

        string baseB = versionB;
        string suffixB = string.Empty;
        int dashIdxB = versionB.IndexOf('-');
        if (dashIdxB >= 0)
        {
            baseB = versionB.Substring(0, dashIdxB);
            suffixB = versionB.Substring(dashIdxB + 1);
        }

        // 1. Сравниваем базовые числовые сегменты (например, 2.0.0)
        var partsA = baseA.Split('.');
        var partsB = baseB.Split('.');
        int maxLen = Math.Max(partsA.Length, partsB.Length);
        for (int i = 0; i < maxLen; i++)
        {
            int numA = i < partsA.Length && int.TryParse(partsA[i], out int valA) ? valA : 0;
            int numB = i < partsB.Length && int.TryParse(partsB[i], out int valB) ? valB : 0;
            if (numA != numB)
            {
                return numA.CompareTo(numB);
            }
        }

        // 2. Если базовые версии абсолютно равны, сравниваем суффиксы пререлиза
        bool hasSuffixA = !string.IsNullOrEmpty(suffixA);
        bool hasSuffixB = !string.IsNullOrEmpty(suffixB);

        // По стандарту SemVer, стабильная версия (без суффикса) ВСЕГДА новее, чем версия с суффиксом.
        if (!hasSuffixA && !hasSuffixB) return 0;
        if (!hasSuffixA && hasSuffixB) return 1;  // A без суффикса (стабильная) новее, чем B с суффиксом
        if (hasSuffixA && !hasSuffixB) return -1; // A с суффиксом старее, чем B без суффикса

        // Оба имеют суффиксы (например, "preview.12" и "preview.15" или "csharp-dev")
        var suffixPartsA = suffixA.Split('.');
        var suffixPartsB = suffixB.Split('.');
        int minSuffixLen = Math.Min(suffixPartsA.Length, suffixPartsB.Length);

        for (int i = 0; i < minSuffixLen; i++)
        {
            string partA = suffixPartsA[i];
            string partB = suffixPartsB[i];

            bool isNumA = int.TryParse(partA, out int valSuffixA);
            bool isNumB = int.TryParse(partB, out int valSuffixB);

            if (isNumA && isNumB)
            {
                if (valSuffixA != valSuffixB)
                    return valSuffixA.CompareTo(valSuffixB);
            }
            else
            {
                int cmp = string.Compare(partA, partB, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;
            }
        }

        // Если общие части суффиксов равны, то длиннее суффикс считается более новой промежуточной сборкой
        return suffixPartsA.Length.CompareTo(suffixPartsB.Length);
    }
}
