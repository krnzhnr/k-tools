// -*- coding: utf-8 -*-
using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace KTools_App.Core;

/// <summary>
/// Потокобезопасный реестр для отслеживания запущенных внешних процессов (таких как ffmpeg, yt-dlp, mkvmerge и др.).
/// Обеспечивает гарантированное принудительное завершение всех дочерних процессов при закрытии приложения.
/// Все комментарии и документация выполнены исключительно на русском языке.
/// </summary>
public static class ActiveProcessTracker
{
    private static readonly ConcurrentDictionary<int, Process> _activeProcesses = new();

    /// <summary>
    /// Регистрирует запущенный процесс в реестре отслеживания.
    /// </summary>
    /// <param name="process">Объект процесса для регистрации.</param>
    public static void Register(Process process)
    {
        if (process == null) return;
        try
        {
            _activeProcesses[process.Id] = process;
        }
        catch (Exception)
        {
            // Игнорируем ошибки при обращении к ID (если процесс уже успел завершиться)
        }
    }

    /// <summary>
    /// Удаляет завершенный или остановленный процесс из реестра.
    /// </summary>
    /// <param name="process">Объект процесса для удаления.</param>
    public static void Unregister(Process process)
    {
        if (process == null) return;
        try
        {
            _activeProcesses.TryRemove(process.Id, out _);
        }
        catch (Exception)
        {
            // Игнорируем
        }
    }

    /// <summary>
    /// Принудительно завершает все зарегистрированные и активные дочерние процессы (включая дерево их дочерних процессов).
    /// Вызывается при закрытии главного окна или выходе из приложения.
    /// </summary>
    public static void KillAll()
    {
        foreach (var kvp in _activeProcesses)
        {
            try
            {
                var process = kvp.Value;
                if (!process.HasExited)
                {
                    // Завершаем процесс вместе со всеми его дочерними процессами (дерево процессов)
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
                // Игнорируем ошибки при попытке убить процесс
            }
        }
        _activeProcesses.Clear();
    }
}
