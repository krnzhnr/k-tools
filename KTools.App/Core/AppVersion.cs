// -*- coding: utf-8 -*-
using System;
using System.Reflection;

namespace KTools_App.Core;

/// <summary>
/// Единый источник версии приложения для UI и журнала.
/// Читает AssemblyInformationalVersion текущей сборки и отрезает
/// суффикс метаданных сборки после '+' (хэш коммита).
/// </summary>
public static class AppVersion
{
    /// <summary>
    /// Возвращает нормализованную версию текущей сборки.
    /// </summary>
    /// <returns>Версия без суффикса сборки или "0.0.0" при недоступности.</returns>
    public static string Resolve()
    {
        try
        {
            Assembly assembly = typeof(AppVersion).Assembly;
            string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(informational))
            {
                int plus = informational.IndexOf('+', StringComparison.Ordinal);
                return plus > 0 ? informational[..plus] : informational;
            }

            return assembly.GetName().Version?.ToString() ?? "0.0.0";
        }
        catch (Exception)
        {
            return "0.0.0";
        }
    }

    /// <summary>
    /// Возвращает компактный сабтайтл для TitleBar («v2.2.0»).
    /// </summary>
    public static string ToTitleSubtitle()
    {
        return $"v{Resolve()}";
    }
}
