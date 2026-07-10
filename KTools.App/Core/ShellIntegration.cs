using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace KTools_App.Core;

/// <summary>
/// Класс для интеграции K-Tools с контекстным меню Windows Проводника.
/// Все комментарии и логи выполнены строго на русском языке.
/// </summary>
public static class ShellIntegration
{
    private const string RootKeyPath = @"Software\Classes\*\shell\KTools";
    private const string FolderKeyPath = @"Software\Classes\Directory\shell\KTools";

    private static readonly Dictionary<string, string> ScriptTagMap = new()
    {
        { "Кодирование видео", "video_encoding" },
        { "Конвертация контейнера", "container_conversion" },
        { "Очистка метаданных", "metadata_cleanup" },
        { "Кодирование аудио", "audio_encoding" },
        { "Даунмикс в Stereo", "audio_downmix" },
        { "Изменение скорости аудио", "audio_speed" },
        { "Разделение каналов", "audio_channels" },
        { "Сборка MKV", "mkv_assembly" },
        { "Управление потоками", "stream_management" },
        { "Замена потоков", "stream_replacement" },
        { "Разборка контейнера", "container_demux" },
        { "ASS/SRT → VTT", "subtitles_convert" }
    };

    /// <summary>
    /// Регистрирует K-Tools в контекстном меню проводника.
    /// </summary>
    /// <param name="exePath">Полный путь к исполняемому файлу приложения.</param>
    /// <param name="scriptNames">Список названий скриптов для добавления в меню.</param>
    public static void Register(string exePath, List<string> scriptNames)
    {
        var scripts = new List<(string Name, string Code)>();
        foreach (var name in scriptNames)
        {
            if (ScriptTagMap.TryGetValue(name, out var code))
            {
                scripts.Add((name, code));
            }
            else
            {
                // Резервный вариант, если имя изменилось
                string safeCode = name.Replace(" ", "_").ToLowerInvariant();
                scripts.Add((name, safeCode));
            }
        }

        RegisterForKey(Registry.CurrentUser, RootKeyPath, exePath, scripts);
        RegisterForKey(Registry.CurrentUser, FolderKeyPath, exePath, scripts);
    }

    /// <summary>
    /// Проверяет, требуется ли обновить интеграцию с контекстным меню в реестре.
    /// Возвращает true, если записи отсутствуют или не совпадают с текущими параметрами.
    /// </summary>
    public static bool NeedsUpdate(string exePath, List<string> scriptNames)
    {
        var scripts = new List<(string Name, string Code)>();
        foreach (var name in scriptNames)
        {
            if (ScriptTagMap.TryGetValue(name, out var code))
            {
                scripts.Add((name, code));
            }
            else
            {
                string safeCode = name.Replace(" ", "_").ToLowerInvariant();
                scripts.Add((name, safeCode));
            }
        }

        return NeedsUpdateForKey(Registry.CurrentUser, RootKeyPath, exePath, scripts)
            || NeedsUpdateForKey(Registry.CurrentUser, FolderKeyPath, exePath, scripts);
    }

    private static bool NeedsUpdateForKey(RegistryKey root, string subKeyPath, string exePath, List<(string Name, string Code)> scripts)
    {
        try
        {
            using var mainKey = root.OpenSubKey(subKeyPath, false);
            if (mainKey == null) return true;

            var muiVerb = mainKey.GetValue("MUIVerb") as string;
            if (muiVerb != "Открыть в K-Tools") return true;

            var extendedKey = mainKey.GetValue("ExtendedSubCommandsKey") as string;
            if (extendedKey != subKeyPath.Replace(@"Software\Classes\", "")) return true;

            var icon = mainKey.GetValue("Icon") as string;
            if (icon != $"\"{exePath}\",0") return true;

            using var shellKey = mainKey.OpenSubKey("shell", false);
            if (shellKey == null) return true;

            var subKeys = shellKey.GetSubKeyNames();
            if (subKeys.Length != scripts.Count) return true;

            foreach (var (name, code) in scripts)
            {
                using var scriptKey = shellKey.OpenSubKey(code, false);
                if (scriptKey == null) return true;

                var scriptMui = scriptKey.GetValue("MUIVerb") as string;
                if (scriptMui != name) return true;

                using var commandKey = scriptKey.OpenSubKey("command", false);
                if (commandKey == null) return true;

                var cmdValue = commandKey.GetValue("") as string;
                if (cmdValue != $"\"{exePath}\" --script \"{code}\" \"%1\"") return true;
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Удаляет K-Tools из контекстного меню проводника.
    /// </summary>
    public static void Unregister()
    {
        UnregisterForKey(Registry.CurrentUser, RootKeyPath);
        UnregisterForKey(Registry.CurrentUser, FolderKeyPath);
    }

    private static void RegisterForKey(RegistryKey root, string subKeyPath, string exePath, List<(string Name, string Code)> scripts)
    {
        try
        {
            using var mainKey = root.CreateSubKey(subKeyPath, true);
            if (mainKey == null) return;

            mainKey.SetValue("MUIVerb", "Открыть в K-Tools");
            mainKey.SetValue("ExtendedSubCommandsKey", subKeyPath.Replace(@"Software\Classes\", ""));
            mainKey.SetValue("Icon", $"\"{exePath}\",0");

            using var shellKey = mainKey.CreateSubKey("shell", true);
            if (shellKey == null) return;

            // Очищаем старые записи
            foreach (var name in shellKey.GetSubKeyNames())
            {
                shellKey.DeleteSubKeyTree(name, false);
            }

            // Добавляем новые подразделы
            foreach (var (name, code) in scripts)
            {
                using var scriptKey = shellKey.CreateSubKey(code, true);
                if (scriptKey == null) continue;

                scriptKey.SetValue("MUIVerb", name);

                using var commandKey = scriptKey.CreateSubKey("command", true);
                if (commandKey == null) continue;

                commandKey.SetValue("", $"\"{exePath}\" --script \"{code}\" \"%1\"");
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Ошибка записи разделов контекстного меню в реестр: {ex.Message}", ex);
        }
    }

    private static void UnregisterForKey(RegistryKey root, string subKeyPath)
    {
        try
        {
            root.DeleteSubKeyTree(subKeyPath, false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Ошибка удаления разделов контекстного меню из реестра: {ex.Message}", ex);
        }
    }
}
