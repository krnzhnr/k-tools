using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using KTools_App.Core;

namespace KTools_App.Infrastructure;

/// <summary>
/// Структура, описывающая входной медиа-источник для сборки контейнера в mkvmerge.
/// </summary>
public record MkvInputSource(string Path, List<string>? Args = null);

/// <summary>
/// Синглтон-обертка для запуска утилиты mkvmerge (из пакета MKVToolNix).
/// Обеспечивает объединение видео, аудио и субтитров в единый файл матроски (.mkv).
/// Поддерживает гибкую передачу аргументов разметки и захват предупреждений (код 1).
/// Все комментарии и логирование выполнены строго на русском языке.
/// </summary>
public sealed class MkvmergeRunner : AbstractProcessRunner
{
    private static readonly Lazy<MkvmergeRunner> LazyInstance =
        new(() => new MkvmergeRunner());

    private MkvmergeRunner() { }

    /// <summary>
    /// Возвращает единственный экземпляр класса MkvmergeRunner.
    /// </summary>
    public static MkvmergeRunner Instance => LazyInstance.Value;

    /// <summary>
    /// Запустить процесс сборки контейнера MKV через mkvmerge.
    /// </summary>
    /// <param name="outputPath">Абсолютный путь к выходному собираемому MKV-файлу.</param>
    /// <param name="inputs">Список входных медиа-источников с индивидуальными флагами.</param>
    /// <param name="title">Глобальный заголовок (метаданные) собираемого MKV.</param>
    /// <param name="extraArgs">Глобальные дополнительные аргументы для mkvmerge.</param>
    /// <param name="onProgress">Колбек для передачи процентов прогресса (от 0 до 100).</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>True, если процесс завершился успешно (код 0 или 1), иначе false.</returns>
    public async Task<bool> RunAsync(
        string outputPath,
        List<MkvInputSource> inputs,
        string? title = null,
        List<string>? extraArgs = null,
        Action<double>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (inputs == null || inputs.Count == 0)
        {
            LogService.Instance.Error("Не переданы входные файлы для сборки в mkvmerge", "MkvmergeRunner");
            return false;
        }

        // Базовые аргументы: выходной файл
        var argsList = new List<string>
        {
            "--output", $"\"{outputPath}\""
        };

        // Глобальный заголовок контейнера
        if (!string.IsNullOrWhiteSpace(title))
        {
            argsList.Add("--title");
            argsList.Add($"\"{title}\"");
        }

        // Глобальные аргументы
        if (extraArgs != null)
        {
            argsList.AddRange(extraArgs);
        }

        // Индивидуальные аргументы каждого источника и сами файлы
        foreach (var input in inputs)
        {
            if (input.Args != null && input.Args.Count > 0)
            {
                argsList.AddRange(input.Args);
            }
            argsList.Add($"\"{input.Path}\"");
        }

        string arguments = string.Join(" ", argsList);

        // Буферы для сбора вывода
        var stdoutLines = new List<string>();
        var stderrLines = new List<string>();

        LogService.Instance.Info($"Начало сборки MKV: '{Path.GetFileName(outputPath)}'. Количество входов: {inputs.Count}", "MkvmergeRunner");

        var result = await RunProcessAsync(
            "mkvmerge",
            arguments,
            onOutputLine: line =>
            {
                lock (stdoutLines)
                {
                    stdoutLines.Add(line);
                }

                if (onProgress != null)
                {
                    double? percent = MkvmergeOutputParser.ParseLine(line);
                    if (percent.HasValue)
                    {
                        onProgress(percent.Value);
                    }
                }
            },
            onErrorLine: line =>
            {
                lock (stderrLines)
                {
                    stderrLines.Add(line);
                }
            },
            cancellationToken
        );

        string stdoutText = string.Join(Environment.NewLine, stdoutLines);
        string stderrText = string.Join(Environment.NewLine, stderrLines);

        // mkvmerge возвращает: 0 - успех, 1 - завершено с предупреждениями, 2 - ошибка
        if (result.ExitCode == 0)
        {
            LogService.Instance.Info($"mkvmerge успешно завершил сборку файла '{Path.GetFileName(outputPath)}'", "MkvmergeRunner");
            return true;
        }
        else if (result.ExitCode == 1)
        {
            LogService.Instance.Warn($"mkvmerge завершил сборку файла '{Path.GetFileName(outputPath)}' с предупреждениями:\n{stdoutText}", "MkvmergeRunner");
            return true;
        }
        else
        {
            LogService.Instance.Error($"Ошибка выполнения mkvmerge (Код: {result.ExitCode}).\nSTDOUT:\n{stdoutText}\nSTDERR:\n{stderrText}", "MkvmergeRunner");
            return false;
        }
    }

    /// <summary>
    /// Получить техническую информацию о MKV-файле в формате JSON через mkvmerge.
    /// Все комментарии и логирование выполнены на русском языке.
    /// </summary>
    /// <param name="filePath">Абсолютный путь к исследуемому MKV-файлу.</param>
    /// <returns>Документ JsonDocument со свойствами дорожек и вложений, или null.</returns>
    public async Task<JsonDocument?> IdentifyAsync(string filePath)
    {
        string arguments = $"--identify --identification-format json \"{filePath}\"";
        
        var outputLines = new List<string>();
        var errorLines = new List<string>();

        // mkvmerge --identify возвращает 0 при успехе или 1 при наличии предупреждений
        var result = await RunProcessAsync(
            "mkvmerge",
            arguments,
            onOutputLine: line => outputLines.Add(line),
            onErrorLine: line => errorLines.Add(line),
            CancellationToken.None
        );

        if (result.ExitCode > 1)
        {
            string errText = string.Join(" ", errorLines);
            LogService.Instance.Error($"Ошибка вызова mkvmerge --identify для файла '{filePath}': {errText}", "MkvmergeRunner");
            return null;
        }

        string fullOutput = string.Join("", outputLines);
        if (string.IsNullOrWhiteSpace(fullOutput))
        {
            LogService.Instance.Error($"mkvmerge --identify вернул пустой вывод для '{filePath}'", "MkvmergeRunner");
            return null;
        }

        try
        {
            return JsonDocument.Parse(fullOutput);
        }
        catch (JsonException ex)
        {
            LogService.Instance.Exception(ex, $"Ошибка парсинга JSON от mkvmerge для '{filePath}'", "MkvmergeRunner");
            return null;
        }
    }
}
