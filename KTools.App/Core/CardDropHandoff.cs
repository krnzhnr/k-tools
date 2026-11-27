// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;

namespace KTools_App.Core;

/// <summary>
/// Разовая передача путей файлов, сброшенных на карточку домашнего экрана, в рабочую панель скрипта.
/// Домашний экран отфильтровывает пути по расширениям скрипта и запоминает их здесь до навигации,
/// WorkPanel однократно забирает их при входе и добавляет в очередь через FileList.AddFiles.
/// Потокобезопасно: доступ сериализуется блокировкой.
/// </summary>
public static class CardDropHandoff
{
    private static readonly object _sync = new();
    private static readonly Dictionary<string, List<string>> _pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Запоминает отфильтрованные пути файлов для скрипта с указанным именем.
    /// Повторный вызов для того же скрипта заменяет предыдущие ожидающие пути.
    /// </summary>
    /// <param name="scriptName">Имя целевого скрипта.</param>
    /// <param name="paths">Отфильтрованные пути файлов.</param>
    public static void Stage(string scriptName, IEnumerable<string> paths)
    {
        if (string.IsNullOrWhiteSpace(scriptName))
        {
            throw new ArgumentException("Имя скрипта не задано.", nameof(scriptName));
        }

        ArgumentNullException.ThrowIfNull(paths);

        var cleaned = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        lock (_sync)
        {
            _pending[scriptName] = cleaned;
        }
    }

    /// <summary>
    /// Однократно забирает ожидающие пути для скрипта и удаляет их из хранилища.
    /// Повторный вызов возвращает null: файлы не добавляются в очередь дважды.
    /// </summary>
    /// <param name="scriptName">Имя целевого скрипта.</param>
    /// <returns>Ожидающие пути или null, если для скрипта ничего не запомнено.</returns>
    public static IReadOnlyList<string>? Take(string scriptName)
    {
        if (string.IsNullOrWhiteSpace(scriptName))
        {
            return null;
        }

        lock (_sync)
        {
            if (!_pending.Remove(scriptName, out var paths))
            {
                return null;
            }

            return paths.ToList();
        }
    }
}
