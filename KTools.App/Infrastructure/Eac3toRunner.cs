// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Core;
using KTools_App.Services.Contracts;

namespace KTools_App.Infrastructure;

/// <summary>
/// Синглтон-обертка для запуска утилиты eac3to.
/// Используется для изменения скорости и тона аудио (PAL ↔ NTSC), демуксинга и обработки DTS/AC3.
/// Поддерживает автоматическую очистку создаваемых eac3to лог-файлов на диске.
/// Все комментарии и логирование выполнены строго на русском языке в соответствии с регламентом.
/// </summary>
public sealed class Eac3toRunner : AbstractProcessRunner, IEac3toRunner
{
    private static readonly Lazy<Eac3toRunner> LazyInstance =
        new(() => new Eac3toRunner());

    private Eac3toRunner() { }

    /// <summary>
    /// Возвращает единственный экземпляр класса Eac3toRunner.
    /// </summary>
    public static Eac3toRunner Instance => LazyInstance.Value;

    /// <summary>
    /// Запустить eac3to асинхронно с переданными аргументами командной строки.
    /// </summary>
    /// <param name="args">Список аргументов для запуска.</param>
    /// <param name="workingDir">Рабочая папка для запуска (если null, используется папка бинарника).</param>
    /// <param name="onProgress">Колбек для передачи прогресса (процентов от 0 до 100).</param>
    /// <param name="cancellationToken">Токен отмены задачи.</param>
    /// <returns>True при успешном завершении (код 0), иначе false.</returns>
    public async Task<bool> RunAsync(
        List<string> args,
        string? workingDir = null,
        Action<double>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        string arguments = string.Join(" ", args);
        string workingCwd = workingDir ?? Path.GetDirectoryName(PathManager.GetBinaryPath("eac3to")) ?? AppContext.BaseDirectory;

        // Буферы для stdout и stderr
        var stdoutLines = new List<string>();
        var stderrLines = new List<string>();

        var result = await RunProcessAsync(
            "eac3to",
            arguments,
            onOutputLine: line =>
            {
                lock (stdoutLines)
                {
                    stdoutLines.Add(line);
                }

                if (onProgress != null)
                {
                    double? percent = Eac3toOutputParser.ParseLine(line);
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
            cancellationToken,
            workingDir: workingCwd
        );

        // Очищаем созданные eac3to файлы логов log*.txt
        CleanupLogs(workingCwd);

        string stdoutText = string.Join(Environment.NewLine, stdoutLines);
        string stderrText = string.Join(Environment.NewLine, stderrLines);

        if (!result.IsSuccess)
        {
            LogService.Instance.Error($"Ошибка выполнения eac3to (Код: {result.ExitCode}).\nSTDOUT:\n{stdoutText}\nSTDERR:\n{stderrText}", "Eac3toRunner");
            return false;
        }

        LogService.Instance.DebugLog($"Вывод eac3to:\n{stdoutText}", "Eac3toRunner");
        return true;
    }

    /// <summary>
    /// Удаляет временные файлы логов, автоматически создаваемые eac3to в процессе работы.
    /// Безопасно удаляет файлы log*.txt, если их содержимое начинается с сигнатуры утилиты.
    /// </summary>
    private void CleanupLogs(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return;

            string[] logFiles = Directory.GetFiles(directory, "log*.txt");
            foreach (string file in logFiles)
            {
                try
                {
                    // Безопасная проверка: читаем первую строку файла
                    using (var reader = new StreamReader(file))
                    {
                        string? firstLine = reader.ReadLine();
                        if (firstLine == null || !firstLine.StartsWith("eac3to v", StringComparison.OrdinalIgnoreCase))
                        {
                            continue; // Пропускаем чужие файлы
                        }
                    }

                    File.Delete(file);
                    LogService.Instance.DebugLog($"🗑 Удален лог eac3to: '{Path.GetFileName(file)}'", "Eac3toRunner");
                }
                catch (Exception ex)
                {
                    LogService.Instance.Warn($"Не удалось удалить лог eac3to '{Path.GetFileName(file)}': {ex.Message}", "Eac3toRunner");
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Ошибка при очистке логов eac3to в папке '{directory}': {ex.Message}", "Eac3toRunner");
        }
    }
}
