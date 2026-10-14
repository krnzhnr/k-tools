// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace KTools_App.Core;

/// <summary>
/// Одна секция списка изменений (например «Добавлено», «Исправлено»).
/// </summary>
public sealed class ReleaseNotesSection
{
    /// <summary>Заголовок секции.</summary>
    public string Title { get; }

    /// <summary>Пункты секции в исходном порядке.</summary>
    public IReadOnlyList<string> Items { get; }

    /// <summary>
    /// Инициализирует новую секцию списка изменений.
    /// </summary>
    public ReleaseNotesSection(string title, IEnumerable<string> items)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Заголовок секции не задан.", nameof(title));
        }

        Title = title;
        Items = new ReadOnlyCollection<string>(
            (items ?? Enumerable.Empty<string>())
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Select(i => i.Trim())
            .ToList());
    }
}

/// <summary>
/// Структурированные данные для окна «Что нового».
/// </summary>
public sealed class ReleaseNotes
{
    /// <summary>Версия релиза (например «2.1.9»).</summary>
    public string Version { get; }

    /// <summary>Заголовок релиза.</summary>
    public string Title { get; }

    /// <summary>Краткая выжимка — главные пункты версии.</summary>
    public IReadOnlyList<string> Highlights { get; }

    /// <summary>Полные секции изменений.</summary>
    public IReadOnlyList<ReleaseNotesSection> Sections { get; }

    /// <summary>Имя ascii.rest-анимации для шапки (например «alpine-dawn»).</summary>
    public string ArtPieceName { get; }

    /// <summary>
    /// Инициализирует данные окна «Что нового».
    /// </summary>
    public ReleaseNotes(
        string version,
        string title,
        IEnumerable<string> highlights,
        IEnumerable<ReleaseNotesSection> sections,
        string artPieceName)
    {
        Version = string.IsNullOrWhiteSpace(version) ? "?" : version.Trim();
        Title = title ?? string.Empty;
        Highlights = new ReadOnlyCollection<string>(
            (highlights ?? Enumerable.Empty<string>())
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim())
            .ToList());
        Sections = new ReadOnlyCollection<ReleaseNotesSection>(
            (sections ?? Enumerable.Empty<ReleaseNotesSection>()).ToList());
        ArtPieceName = string.IsNullOrWhiteSpace(artPieceName)
            ? WhatsNewArtCatalog.DefaultPiece
            : artPieceName.Trim();
    }
}

/// <summary>
/// Полный каталог сцен ascii.rest (категория scenes, 34 штуки) для шапки
/// окна «Что нового». Только сцены: логотипы, спиннеры и виджеты
/// исключены сознательно. Библиотека ascii.rest распространяется под MIT.
/// </summary>
public static class WhatsNewArtCatalog
{
    /// <summary>Запасная анимация при ошибке выбора или загрузки.</summary>
    public const string DefaultPiece = "alpine-dawn";

    /// <summary>Папка с GIF-сценами относительно каталога приложения.</summary>
    public static string ArtFolderPath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "WhatsNew", "art");

    private static readonly string[] Pieces =
    {
        "alpine-dawn",
        "amer-fort",
        "aurora-fjord",
        "black-prism",
        "cloud-sea",
        "command-city",
        "deep-reef",
        "desert-night",
        "earthrise",
        "edge-orbit",
        "gangaur-ghat",
        "green-storm",
        "jal-mahal",
        "jungle-falls",
        "kite-festival",
        "kyoto-dusk",
        "lake-pichola",
        "lantern-lake",
        "lava-wall",
        "marine-drive",
        "misty-forest",
        "night-coast",
        "night-highway",
        "ocean-sunset",
        "planet-scale",
        "prism-city",
        "salt-flat",
        "saturn-rise",
        "snow-train",
        "storm-plains",
        "taj-dawn",
        "tokyo-rain",
        "varanasi-ghats",
        "volcano-night",
    };

    /// <summary>Все доступные имена анимаций из каталога.</summary>
    public static IReadOnlyList<string> All => Pieces;

    /// <summary>
    /// Выбирает случайную анимацию, отличную от прошлой.
    /// При единственном кандидате возвращает его.
    /// </summary>
    /// <param name="previous">Имя прошлой анимации (может быть пустым).</param>
    /// <param name="random">Генератор случайных чисел (по умолчанию общий).</param>
    public static string PickRandom(string? previous, Random? random = null)
    {
        if (Pieces.Length == 0)
        {
            return DefaultPiece;
        }

        if (Pieces.Length == 1)
        {
            return Pieces[0];
        }

        Random rng = random ?? Random.Shared;
        string candidate;
        do
        {
            candidate = Pieces[rng.Next(Pieces.Length)];
        }
        while (Pieces.Length > 1
            && !string.IsNullOrEmpty(previous)
            && candidate.Equals(previous, StringComparison.OrdinalIgnoreCase));

        return candidate;
    }

    /// <summary>
    /// Возвращает следующую сцену по кругу списка.
    /// Неизвестное имя даёт начало списка.
    /// </summary>
    /// <param name="current">Текущая сцена (может быть пустой).</param>
    public static string PickNext(string? current)
    {
        if (Pieces.Length == 0)
        {
            return DefaultPiece;
        }

        int index = Array.FindIndex(
            Pieces,
            p => p.Equals(current?.Trim(), StringComparison.OrdinalIgnoreCase));

        return Pieces[(index + 1) % Pieces.Length];
    }

    /// <summary>
    /// Возвращает следующие <paramref name="count"/> сцен по кругу списка,
    /// начиная со следующей после <paramref name="current"/>.
    /// </summary>
    public static System.Collections.Generic.IReadOnlyList<string> PickNextSequence(string? current, int count)
    {
        var result = new System.Collections.Generic.List<string>(Math.Max(0, count));
        string cursor = current ?? string.Empty;
        for (int i = 0; i < count; i++)
        {
            cursor = PickNext(cursor);
            result.Add(cursor);
        }

        return new ReadOnlyCollection<string>(result);
    }

    /// <summary>
    /// Проверяет, входит ли имя в каталог сцен.
    /// </summary>
    public static bool Contains(string? pieceName)
    {
        return !string.IsNullOrWhiteSpace(pieceName)
            && Pieces.Contains(pieceName.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Имя GIF-файла сцены (например «night-coast.gif»).
    /// </summary>
    public static string ArtFileNameFor(string pieceName)
    {
        return (pieceName ?? string.Empty).Trim() + ".gif";
    }

    /// <summary>
    /// Ищет файл сцены в папке артов: сначала «имя.gif»,
    /// затем «ascii-rest-имя.gif». Возвращает полный путь.
    /// </summary>
    public static bool TryResolveArtPath(string? pieceName, out string path)
    {
        path = string.Empty;
        if (!Contains(pieceName))
        {
            return false;
        }

        string clean = pieceName!.Trim();
        string direct = System.IO.Path.Combine(ArtFolderPath, clean + ".gif");
        if (System.IO.File.Exists(direct))
        {
            path = direct;
            return true;
        }

        string prefixed = System.IO.Path.Combine(ArtFolderPath, "ascii-rest-" + clean + ".gif");
        if (System.IO.File.Exists(prefixed))
        {
            path = prefixed;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Оставляет из имеющихся файлов только сцены каталога в порядке круга.
    /// Имена принимает с расширением или без, регистр не важен;
    /// префикс «ascii-rest-» отбрасывается.
    /// </summary>
    /// <param name="presentNames">Имена файлов, найденных в папке артов.</param>
    public static System.Collections.Generic.IReadOnlyList<string> FilterAvailable(
        System.Collections.Generic.IEnumerable<string> presentNames)
    {
        var present = new System.Collections.Generic.HashSet<string>(
            (presentNames ?? Enumerable.Empty<string>()).Select(NormalizeArtName),
            StringComparer.OrdinalIgnoreCase);

        return new ReadOnlyCollection<string>(
            Pieces.Where(present.Contains).ToList());
    }

    private static string NormalizeArtName(string fileName)
    {
        string baseName = System.IO.Path.GetFileNameWithoutExtension((fileName ?? string.Empty).Trim());
        const string prefix = "ascii-rest-";
        if (baseName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            baseName = baseName.Substring(prefix.Length);
        }

        return baseName;
    }

    /// <summary>
    /// Сцены, для которых есть GIF-файлы в папке артов, в порядке круга.
    /// </summary>
    public static System.Collections.Generic.IReadOnlyList<string> ListAvailableArt()
    {
        return FilterAvailable(ListArtFiles());
    }

    /// <summary>
    /// Выбирает случайную сцену из имеющихся на диске GIF-файлов.
    /// Если папка пуста, возвращает запасную сцену.
    /// </summary>
    public static string PickRandomAvailable(Random? random = null)
    {
        System.Collections.Generic.IReadOnlyList<string> available = ListAvailableArt();
        if (available.Count == 0)
        {
            return DefaultPiece;
        }

        return available[(random ?? Random.Shared).Next(available.Count)];
    }

    private static System.Collections.Generic.IEnumerable<string> ListArtFiles()
    {
        try
        {
            if (System.IO.Directory.Exists(ArtFolderPath))
            {
                return System.IO.Directory.GetFiles(ArtFolderPath, "*.gif");
            }
        }
        catch
        {
            // Игнорируется: недоступность папки означает отсутствие сцен.
        }

        return Enumerable.Empty<string>();
    }
}

/// <summary>
/// Читает запечённый на момент сборки снапшот изменений версии
/// (Assets/WhatsNew/release-notes.md) для окна «Что нового».
/// </summary>
public static class ReleaseNotesLoader
{
    /// <summary>Имя файла снапшота рядом с GIF-сценами.</summary>
    public const string BakedFileName = "release-notes.md";

    /// <summary>Путь к снапшоту в каталоге приложения.</summary>
    public static string BakedFilePath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "WhatsNew", BakedFileName);

    /// <summary>
    /// Пробует прочитать запечённые данные версии.
    /// При отсутствии файла возвращает false: окно не показывается.
    /// </summary>
    public static bool TryLoadBakedNotes(out ReleaseNotes? notes)
    {
        return TryLoadBakedNotes(BakedFilePath, out notes);
    }

    /// <summary>
    /// Пробует прочитать данные версии из указанного файла.
    /// Первая строка «# версия» задаёт номер, остальное — тело для парсера.
    /// </summary>
    public static bool TryLoadBakedNotes(string path, out ReleaseNotes? notes)
    {
        notes = null;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                return false;
            }

            string text = System.IO.File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string[] lines = text.Split('\n');
            string version = lines[0].Trim().TrimStart('#').Trim();
            if (string.IsNullOrEmpty(version))
            {
                return false;
            }

            string body = string.Join('\n', lines.Skip(1));
            notes = ReleaseNotesParser.Parse(
                version,
                $"Версия {version}",
                body,
                WhatsNewArtCatalog.PickRandomAvailable());
            return true;
        }
        catch
        {
            notes = null;
            return false;
        }
    }
}

/// <summary>
/// Решает, показывать ли окно «Что нового»: один раз на версию.
/// </summary>
public static class WhatsNewGate
{
    /// <summary>
    /// Возвращает true, если окно стоит показать: автопоказ включён
    /// и запечённая версия отличается от уже показанной.
    /// Пустая запечённая версия означает отсутствие данных: не показываем.
    /// </summary>
    public static bool ShouldShow(string? lastSeenVersion, string? bakedVersion, bool enabled = true)
    {
        if (!enabled)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(bakedVersion))
        {
            return false;
        }

        return !string.Equals(
            lastSeenVersion?.Trim(),
            bakedVersion.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Разбирает Markdown-тело релиза (секции «### Главное», «### Добавлено» и т.д.)
/// в структуру для окна «Что нового».
/// </summary>
public static class ReleaseNotesParser
{
    private static readonly Dictionary<string, string> KnownSections = new(StringComparer.OrdinalIgnoreCase)
    {
        ["главное"] = "Главное",
        ["добавлено"] = "Добавлено",
        ["изменено"] = "Изменено",
        ["исправлено"] = "Исправлено",
    };

    /// <summary>
    /// Разбирает тело релиза в структурированные данные.
    /// Неизвестные секции сохраняются как есть; пункты без секции попадают в «Добавлено».
    /// </summary>
    public static ReleaseNotes Parse(
        string version,
        string title,
        string? markdownBody,
        string? artPieceName = null)
    {
        var highlights = new List<string>();
        var sections = new List<ReleaseNotesSection>();
        string? currentName = null;
        var currentItems = new List<string>();

        void FlushSection()
        {
            if (currentName == null)
            {
                return;
            }

            if (currentName.Equals("Главное", StringComparison.OrdinalIgnoreCase))
            {
                highlights.AddRange(currentItems);
            }
            else
            {
                sections.Add(new ReleaseNotesSection(currentName, currentItems));
            }

            currentItems = new List<string>();
        }

        if (!string.IsNullOrEmpty(markdownBody))
        {
            foreach (string rawLine in markdownBody.Split('\n'))
            {
                string line = rawLine.Trim().TrimEnd('\r');
                if (line.StartsWith("### ", StringComparison.Ordinal))
                {
                    FlushSection();
                    string rawName = line.Substring(4).Trim();
                    currentName = KnownSections.TryGetValue(rawName, out string? known)
                        ? known
                        : rawName;
                    continue;
                }

                string? item = ExtractItem(line);
                if (item == null)
                {
                    continue;
                }

                if (currentName == null)
                {
                    currentName = "Добавлено";
                }

                currentItems.Add(item);
            }

            FlushSection();
        }

        // Если секции «Главное» нет, выжимка собирается из первых пунктов остальных секций.
        if (highlights.Count == 0)
        {
            highlights.AddRange(sections.SelectMany(s => s.Items).Take(5));
        }

        return new ReleaseNotes(version, title, highlights, sections, artPieceName ?? string.Empty);
    }

    private static string? ExtractItem(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        // Нумерованный пункт «1. Текст» или маркер «- Текст» / «* Текст».
        int dot = line.IndexOf(". ", StringComparison.Ordinal);
        if (dot > 0 && dot <= 4 && line.Substring(0, dot).All(char.IsDigit))
        {
            return line.Substring(dot + 2).Trim();
        }

        if ((line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            && line.Length > 2)
        {
            return line.Substring(2).Trim();
        }

        return null;
    }
}
