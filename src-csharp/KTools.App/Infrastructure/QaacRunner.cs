// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Core;

namespace KTools_App.Infrastructure;

/// <summary>
/// Синглтон-обертка для запуска кодировщика Apple AAC (qaac64.exe) через конвейер с FFmpeg.
/// Позволяет кодировать любые медиаформаты, поддерживаемые FFmpeg, напрямую в высококачественный AAC/M4A 
/// без создания промежуточных временных WAV-файлов на диске.
/// Все комментарии и логирование выполнены строго на русском языке в соответствии с регламентом.
/// </summary>
public sealed class QaacRunner
{
    private static readonly Lazy<QaacRunner> LazyInstance =
        new(() => new QaacRunner());

    private QaacRunner() { }

    /// <summary>
    /// Возвращает единственный экземпляр класса QaacRunner.
    /// </summary>
    public static QaacRunner Instance => LazyInstance.Value;

    /// <summary>
    /// Запустить кодирование AAC через потоковый конвейер FFmpeg | QAAC64.
    /// </summary>
    /// <param name="inputPath">Абсолютный путь к исходному медиафайлу.</param>
    /// <param name="outputPath">Абсолютный путь к выходному файлу M4A/AAC.</param>
    /// <param name="tvbr">Уровень качества переменного битрейта (True VBR, например, "127").</param>
    /// <param name="adts">Использовать ли контейнер ADTS (расширение .aac) вместо M4A.</param>
    /// <param name="extraArgs">Дополнительные параметры для qaac64.</param>
    /// <param name="cancellationToken">Токен отмены задачи.</param>
    /// <returns>True в случае успеха, иначе false.</returns>
    public async Task<bool> RunAsync(
        string inputPath,
        string outputPath,
        string tvbr = "127",
        bool adts = false,
        List<string>? extraArgs = null,
        CancellationToken cancellationToken = default)
    {
        string qaacPath = PathManager.GetBinaryPath("qaac64");
        string ffmpegPath = PathManager.GetBinaryPath("ffmpeg");

        if (!File.Exists(qaacPath))
        {
            LogService.Instance.Error($"Критическая ошибка: отсутствует кодировщик qaac64.exe по пути: '{qaacPath}'", "QaacRunner");
            return false;
        }

        if (!File.Exists(ffmpegPath))
        {
            LogService.Instance.Error($"Критическая ошибка: отсутствует декодер ffmpeg.exe по пути: '{ffmpegPath}'", "QaacRunner");
            return false;
        }

        LogService.Instance.Info($"Начало кодирования QAAC (TVBR: {tvbr}) для файла: '{Path.GetFileName(inputPath)}'", "QaacRunner");

        // 1. Формируем аргументы для FFmpeg (декодирование в WAV и вывод в stdout)
        string ffmpegArgs = $"-v error -i \"{inputPath}\" -f wav -";

        // 2. Формируем аргументы для QAAC (чтение из stdin и запись в файл)
        var qaacArgsList = new List<string>();
        if (adts)
        {
            qaacArgsList.Add("--adts");
        }
        qaacArgsList.Add("--tvbr");
        qaacArgsList.Add(tvbr);
        qaacArgsList.Add("-"); // Вход из stdin
        qaacArgsList.Add("-o");
        qaacArgsList.Add($"\"{outputPath}\"");

        if (extraArgs != null)
        {
            qaacArgsList.AddRange(extraArgs);
        }

        string qaacArgs = string.Join(" ", qaacArgsList);

        // 3. Подготовка переменных окружения Apple Application Support
        var env = PrepareAppleEnvironment(qaacPath);

        LogService.Instance.DebugLog($"Запуск конвейера: ffmpeg {ffmpegArgs} | qaac64 {qaacArgs}", "QaacRunner");

        // 4. Настройка процессов
        var ffmpegStartInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = ffmpegArgs,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(ffmpegPath) ?? AppContext.BaseDirectory
        };

        var qaacStartInfo = new ProcessStartInfo
        {
            FileName = qaacPath,
            Arguments = qaacArgs,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(qaacPath) ?? AppContext.BaseDirectory
        };

        // Заполняем переменные окружения Apple
        foreach (var pair in env)
        {
            qaacStartInfo.EnvironmentVariables[pair.Key] = pair.Value;
        }

        using var ffmpegProc = new Process { StartInfo = ffmpegStartInfo };
        using var qaacProc = new Process { StartInfo = qaacStartInfo };

        try
        {
            ffmpegProc.Start();
            qaacProc.Start();
            
            LogService.Instance.DebugLog($"Запущены процессы конвейера. FFmpeg PID: {ffmpegProc.Id}, QAAC PID: {qaacProc.Id}", "QaacRunner");
        }
        catch (Exception ex)
        {
            LogService.Instance.Exception(ex, $"Не удалось запустить процессы конвейера QAAC: {ex.Message}", "QaacRunner");
            try { ffmpegProc.Kill(true); } catch { }
            try { qaacProc.Kill(true); } catch { }
            return false;
        }

        // Задачи для перенаправления вывода ошибок
        var ffmpegStderrLines = new List<string>();
        var qaacStderrLines = new List<string>();

        var ffmpegErrorTask = Task.Run(async () =>
        {
            while (!ffmpegProc.StandardError.EndOfStream)
            {
                string? line = await ffmpegProc.StandardError.ReadLineAsync();
                if (line != null)
                {
                    lock (ffmpegStderrLines)
                    {
                        ffmpegStderrLines.Add(line);
                    }
                }
            }
        });

        var qaacErrorTask = Task.Run(async () =>
        {
            while (!qaacProc.StandardError.EndOfStream)
            {
                string? line = await qaacProc.StandardError.ReadLineAsync();
                if (line != null)
                {
                    lock (qaacStderrLines)
                    {
                        qaacStderrLines.Add(line);
                    }
                }
            }
        });

        // 5. Конвейеризация данных из stdout FFmpeg в stdin QAAC
        var pipeTask = Task.Run(async () =>
        {
            try
            {
                byte[] buffer = new byte[65536]; // 64KB буфер
                using var input = ffmpegProc.StandardOutput.BaseStream;
                using var output = qaacProc.StandardInput.BaseStream;

                int bytesRead;
                while ((bytesRead = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Игнорируем при штатной отмене
            }
            catch (Exception ex)
            {
                LogService.Instance.Error($"Ошибка конвейерной передачи данных FFmpeg -> QAAC: {ex.Message}", "QaacRunner");
            }
            finally
            {
                try
                {
                    qaacProc.StandardInput.BaseStream.Close(); // Закрываем поток, чтобы qaac понял конец файла
                }
                catch { }
            }
        });

        // Ожидание завершения процессов или отмены
        try
        {
            var waitFfmpeg = ffmpegProc.WaitForExitAsync(cancellationToken);
            var waitQaac = qaacProc.WaitForExitAsync(cancellationToken);

            await Task.WhenAll(waitFfmpeg, waitQaac, pipeTask, ffmpegErrorTask, qaacErrorTask);
        }
        catch (OperationCanceledException)
        {
            LogService.Instance.Warn("Конвейер QAAC прерван пользователем. Принудительная остановка процессов...", "QaacRunner");
            try { ffmpegProc.Kill(true); } catch { }
            try { qaacProc.Kill(true); } catch { }
            return false;
        }

        // Проверка результатов
        bool success = ffmpegProc.ExitCode == 0 && qaacProc.ExitCode == 0;

        if (!success)
        {
            string ffErr = string.Join(Environment.NewLine, ffmpegStderrLines);
            string qaacErr = string.Join(Environment.NewLine, qaacStderrLines);
            LogService.Instance.Error($"Сбой конвейера QAAC.\nFFmpeg Code: {ffmpegProc.ExitCode}, Err: {ffErr}\nQAAC Code: {qaacProc.ExitCode}, Err: {qaacErr}", "QaacRunner");
            return false;
        }

        LogService.Instance.Info($"Конвейер QAAC успешно завершил работу: '{Path.GetFileName(outputPath)}'", "QaacRunner");
        return true;
    }

    /// <summary>
    /// Настраивает PATH для Apple Application Support, необходимый для работы библиотек QAAC.
    /// </summary>
    private Dictionary<string, string> PrepareAppleEnvironment(string qaacPath)
    {
        string baseDir = Path.GetDirectoryName(qaacPath) ?? AppContext.BaseDirectory;
        
        // Стандартные пути Apple Application Support в системе
        string pf = Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Common Files\Apple\Apple Application Support");
        string pfx86 = Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Common Files\Apple\Apple Application Support");

        var applePaths = new List<string>
        {
            baseDir,
            Path.Combine(baseDir, "QTFiles64"),
            Path.Combine(baseDir, "QTFiles")
        };

        if (Directory.Exists(pf)) applePaths.Add(pf);
        if (Directory.Exists(pfx86)) applePaths.Add(pfx86);

        // Получаем текущую переменную PATH
        string currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        
        // Объединяем пути через разделитель
        string newPath = string.Join(Path.PathSeparator, applePaths) + Path.PathSeparator + currentPath;

        var env = new Dictionary<string, string>();
        env["PATH"] = newPath;
        return env;
    }
}
