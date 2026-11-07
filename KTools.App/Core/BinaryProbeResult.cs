// -*- coding: utf-8 -*-
namespace KTools_App.Core;

/// <summary>
/// Результат опроса одной бинарной зависимости по текущему пути.
/// Используется проверкой миграции (ишью 46), чтобы доказать, что версии
/// запрашиваются по новым путям в LocalAppData, а не по остаткам в каталоге установки.
/// </summary>
public sealed class BinaryProbeResult
{
    /// <summary>
    /// Маркер «версия не извлечена», совпадающий с запасным значением обычного опроса версий.
    /// Проверка миграции трактует его как непройденную пробу (для инструментов с ожидаемой версией).
    /// </summary>
    public const string VersionUnknownMarker = "Установлено";

    /// <summary>
    /// Ключ зависимости в реестре (например "ffmpeg").
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Подпапка зависимости внутри каталога bin.
    /// </summary>
    public string Subfolder { get; }

    /// <summary>
    /// Имя исполняемого файла-маркера из реестра.
    /// </summary>
    public string VerifyBinary { get; }

    /// <summary>
    /// Разрешённый путь к исполняемому файлу на момент опроса.
    /// </summary>
    public string ResolvedPath { get; }

    /// <summary>
    /// True, если файл существует по разрешённому пути.
    /// </summary>
    public bool Exists { get; }

    /// <summary>
    /// Строка версии из живого опроса (пусто, если опросить не удалось).
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// True, если для этого инструмента ожидается извлекаемая версия
    /// (инструмент с известным форматом вывода версии).
    /// </summary>
    public bool VersionExpected { get; }

    /// <summary>
    /// True, если разрешённый путь находится внутри текущего каталога bin.
    /// </summary>
    public bool IsUnderBinDirectory { get; }

    /// <summary>
    /// True для записей реестра без исполняемого файла (пропуск, а не сбой).
    /// </summary>
    public bool IsSkipped { get; }

    /// <summary>
    /// Причина пропуска или сбоя опроса (пусто при обычном опросе).
    /// </summary>
    public string Error { get; }

    public BinaryProbeResult(
        string key,
        string subfolder,
        string verifyBinary,
        string resolvedPath,
        bool exists,
        string version,
        bool versionExpected,
        bool isUnderBinDirectory,
        string error,
        bool skipped = false)
    {
        Key = key;
        Subfolder = subfolder;
        VerifyBinary = verifyBinary;
        ResolvedPath = resolvedPath;
        Exists = exists;
        Version = version;
        VersionExpected = versionExpected;
        IsUnderBinDirectory = isUnderBinDirectory;
        Error = error;
        IsSkipped = skipped;
    }

    /// <summary>
    /// Создаёт результат для записи реестра без исполняемого файла
    /// (например, системный DirectShow-фильтр eac3to_decoders).
    /// </summary>
    /// <param name="key">Ключ зависимости.</param>
    /// <param name="reason">Причина пропуска.</param>
    /// <returns>Результат со статусом пропуска.</returns>
    public static BinaryProbeResult Skipped(string key, string reason)
    {
        return new BinaryProbeResult(key, string.Empty, string.Empty, string.Empty, false, string.Empty, false, false, reason, skipped: true);
    }
}
