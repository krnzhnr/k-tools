// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Core;
using KTools_App.Services.Contracts;

namespace KTools_App.Infrastructure;

/// <summary>
/// Синглтон-обертка для запуска кодировщика Apple AAC (qaac64.exe) через конвейер с FFmpeg.
/// Использует изолированный запуск во временной папке для обхода ограничений AppContainer (MSIX),
/// гарантируя чистоту папки бинарных зависимостей bin/.
/// Все комментарии и логирование выполнены строго на русском языке в соответствии с регламентом.
/// </summary>
public sealed class QaacRunner
{
    private static readonly Lazy<QaacRunner> LazyInstance =
        new(() => new QaacRunner(Core.LogService.Instance));

    private readonly ILogService _logService;

    /// <summary>
    /// Инициализирует новый экземпляр QaacRunner с внедрением зависимостей.
    /// </summary>
    /// <param name="logService">Сервис логирования.</param>
    public QaacRunner(ILogService logService)
    {
        _logService = logService;
    }

    /// <summary>
    /// Возвращает единственный экземпляр класса QaacRunner.
    /// </summary>
    public static QaacRunner Instance => LazyInstance.Value;

    /// <summary>
    /// Запустить кодирование AAC через потоковый конвейер FFmpeg | QAAC64.
    /// </summary>
    public async Task<bool> RunAsync(
        string inputPath,
        string outputPath,
        string tvbr = "127",
        bool adts = false,
        List<string>? extraArgs = null,
        double totalDuration = 0.0,
        Action<ProgressInfo>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        string qaacPath = PathManager.GetBinaryPath("qaac64");
        string ffmpegPath = PathManager.GetBinaryPath("ffmpeg");

        if (!File.Exists(qaacPath))
        {
            _logService.Error(
                $"Критическая ошибка: отсутствует кодировщик qaac64.exe по пути: '{qaacPath}'", 
                "QaacRunner");
            return false;
        }

        if (!File.Exists(ffmpegPath))
        {
            _logService.Error(
                $"Критическая ошибка: отсутствует декодер ffmpeg.exe по пути: '{ffmpegPath}'", 
                "QaacRunner");
            return false;
        }

        _logService.Info(
            $"Начало кодирования QAAC (TVBR: {tvbr}) для файла: '{Path.GetFileName(inputPath)}'", 
            "QaacRunner");

        // 1. Создаем изолированную временную папку для обхода ограничений AppContainer (WinUI 3 MSIX)
        string tempDir = Path.Combine(Path.GetTempPath(), "KTools_Qaac_" + Guid.NewGuid().ToString("N"));
        string tempQaacPath = Path.Combine(tempDir, "qaac64.exe");

        try
        {
            Directory.CreateDirectory(tempDir);
            
            // Копируем исполняемый файл во временную директорию
            File.Copy(qaacPath, tempQaacPath, true);

            // Копируем все DLL библиотеки Apple из подпапки QTfiles64 / QTFiles64
            string baseDir = Path.GetDirectoryName(qaacPath) ?? AppContext.BaseDirectory;
            string[] sourceSubfolders = { "QTfiles64", "QTFiles64", "QTFiles", "QTfiles" };
            string? sourceDir = null;

            foreach (var subfolder in sourceSubfolders)
            {
                string path = Path.Combine(baseDir, subfolder);
                if (Directory.Exists(path) && File.Exists(Path.Combine(path, "CoreAudioToolbox.dll")))
                {
                    sourceDir = path;
                    break;
                }
            }

            if (sourceDir != null)
            {
                var dllFiles = Directory.GetFiles(sourceDir, "*.dll");
                foreach (var dllFile in dllFiles)
                {
                    File.Copy(dllFile, Path.Combine(tempDir, Path.GetFileName(dllFile)), true);
                }
                _logService.DebugLog(
                    $"Изолированное окружение QAAC подготовлено во временной папке: '{tempDir}'", 
                    "QaacRunner");
            }
            else
            {
                _logService.Warn(
                    "Не найдена папка QTfiles64 с библиотеками Apple Application Support. Возможен сбой запуска.", 
                    "QaacRunner");
            }
        }
        catch (Exception ex)
        {
            _logService.Exception(
                ex, 
                $"Не удалось создать изолированное временное окружение для QAAC: {ex.Message}", 
                "QaacRunner");
            // Фоллбэк: пробуем запуск из оригинальной папки
            tempQaacPath = qaacPath;
            tempDir = Path.GetDirectoryName(qaacPath) ?? AppContext.BaseDirectory;
        }

        // 2. Формируем аргументы для FFmpeg (декодирование в WAV и вывод в stdout)
        string ffmpegArgs = $"-v error -i \"{inputPath}\" -f wav -";

        // 3. Формируем аргументы для QAAC (чтение из stdin и запись в файл)
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

        _logService.DebugLog(
            $"Запуск конвейера: ffmpeg {ffmpegArgs} | qaac64 {qaacArgs}", 
            "QaacRunner");

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
            FileName = tempQaacPath,
            Arguments = qaacArgs,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = tempDir
        };

        // Заполняем переменную PATH во временном процессе
        SetupQaacEnvironment(qaacStartInfo, tempDir);

        using var ffmpegProc = new Process { StartInfo = ffmpegStartInfo };
        using var qaacProc = new Process { StartInfo = qaacStartInfo };

        try
        {
            ffmpegProc.Start();
            qaacProc.Start();
            
            _logService.DebugLog(
                $"Запущены процессы конвейера. FFmpeg PID: {ffmpegProc.Id}, QAAC PID: {qaacProc.Id}", 
                "QaacRunner");
        }
        catch (Exception ex)
        {
            _logService.Exception(
                ex, 
                $"Не удалось запустить процессы конвейера QAAC: {ex.Message}", 
                "QaacRunner");
            try { ffmpegProc.Kill(true); } catch { }
            try { qaacProc.Kill(true); } catch { }
            CleanupTempDir(tempDir);
            return false;
        }

        // Задачи для перенаправления вывода ошибок с поддержкой \r и \n
        var ffmpegStderrLines = new List<string>();
        var qaacStderrLines = new List<string>();

        var ffmpegErrorTask = Task.Run(async () =>
        {
            try
            {
                var buffer = new char[4096];
                var sb = new System.Text.StringBuilder();
                while (true)
                {
                    int read = await ffmpegProc.StandardError.ReadAsync(buffer, 0, buffer.Length);
                    if (read <= 0) break;

                    for (int i = 0; i < read; i++)
                    {
                        char c = buffer[i];
                        if (c == '\r' || c == '\n')
                        {
                            if (sb.Length > 0)
                            {
                                string line = sb.ToString();
                                lock (ffmpegStderrLines)
                                {
                                    ffmpegStderrLines.Add(line);
                                }
                                sb.Clear();
                            }
                        }
                        else
                        {
                            sb.Append(c);
                        }
                    }
                }
                if (sb.Length > 0)
                {
                    string line = sb.ToString();
                    lock (ffmpegStderrLines)
                    {
                        ffmpegStderrLines.Add(line);
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.Exception(ex, "Ошибка чтения stderr FFmpeg в QaacRunner", "QaacRunner");
            }
        });

        var qaacErrorTask = Task.Run(async () =>
        {
            try
            {
                var buffer = new char[4096];
                var sb = new System.Text.StringBuilder();
                while (true)
                {
                    int read = await qaacProc.StandardError.ReadAsync(buffer, 0, buffer.Length);
                    if (read <= 0) break;

                    for (int i = 0; i < read; i++)
                    {
                        char c = buffer[i];
                        if (c == '\r' || c == '\n')
                        {
                            if (sb.Length > 0)
                            {
                                string line = sb.ToString();
                                lock (qaacStderrLines)
                                {
                                    qaacStderrLines.Add(line);
                                }

                                if (onProgress != null)
                                {
                                    var progress = QaacOutputParser.ParseLine(line, totalDuration);
                                    if (progress != null)
                                    {
                                        onProgress(progress);
                                    }
                                }
                                sb.Clear();
                            }
                        }
                        else
                        {
                            sb.Append(c);
                        }
                    }
                }
                if (sb.Length > 0)
                {
                    string line = sb.ToString();
                    lock (qaacStderrLines)
                    {
                        qaacStderrLines.Add(line);
                    }
                    if (onProgress != null)
                    {
                        var progress = QaacOutputParser.ParseLine(line, totalDuration);
                        if (progress != null)
                        {
                            onProgress(progress);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.Exception(ex, "Ошибка чтения stderr QAAC в QaacRunner", "QaacRunner");
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
                _logService.Error(
                    $"Ошибка конвейерной передачи данных FFmpeg -> QAAC: {ex.Message}", 
                    "QaacRunner");
            }
            finally
            {
                try
                {
                    qaacProc.StandardInput.BaseStream.Close();
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
            _logService.Warn(
                "Конвейер QAAC прерван пользователем. Принудительная остановка процессов...", 
                "QaacRunner");
            try { ffmpegProc.Kill(true); } catch { }
            try { qaacProc.Kill(true); } catch { }
            
            // Физически удаляем неполный выходной файл при отмене операции
            if (File.Exists(outputPath))
            {
                try
                {
                    File.Delete(outputPath);
                    _logService.DebugLog($"Удален неполный выходной файл при отмене конвейера QAAC: '{Path.GetFileName(outputPath)}'", "QaacRunner");
                }
                catch (Exception deleteEx)
                {
                    _logService.Exception(deleteEx, $"Не удалось удалить неполный выходной файл '{outputPath}' при отмене конвейера QAAC: {deleteEx.Message}", "QaacRunner");
                }
            }

            CleanupTempDir(tempDir);
            return false;
        }

        // Проверка результатов
        bool success = ffmpegProc.ExitCode == 0 && qaacProc.ExitCode == 0;

        if (!success)
        {
            string ffErr = string.Join(Environment.NewLine, ffmpegStderrLines);
            string qaacErr = string.Join(Environment.NewLine, qaacStderrLines);
            _logService.Error(
                $"Сбой конвейера QAAC.\nFFmpeg Code: {ffmpegProc.ExitCode}, Err: {ffErr}\nQAAC Code: {qaacProc.ExitCode}, Err: {qaacErr}", 
                "QaacRunner");

            // Физически удаляем неудавшийся или поврежденный выходной файл при ошибке
            if (File.Exists(outputPath))
            {
                try
                {
                    File.Delete(outputPath);
                    _logService.DebugLog($"Удален поврежденный выходной файл после ошибки конвейера QAAC: '{Path.GetFileName(outputPath)}'", "QaacRunner");
                }
                catch (Exception deleteEx)
                {
                    _logService.Exception(deleteEx, $"Не удалось удалить поврежденный выходной файл '{outputPath}' после ошибки конвейера QAAC: {deleteEx.Message}", "QaacRunner");
                }
            }
        }
        else
        {
            _logService.Info(
                $"Конвейер QAAC успешно завершил работу: '{Path.GetFileName(outputPath)}'", 
                "QaacRunner");
        }

        // Удаляем временное окружение
        CleanupTempDir(tempDir);
        return success;
    }

    /// <summary>
    /// Настраивает PATH для Apple Application Support в окружении процесса QAAC.
    /// </summary>
    private void SetupQaacEnvironment(ProcessStartInfo qaacStartInfo, string tempDir)
    {
        string pathKey = "PATH";
        string currentPath = string.Empty;

        foreach (var key in qaacStartInfo.Environment.Keys)
        {
            if (key.Equals("PATH", StringComparison.OrdinalIgnoreCase))
            {
                pathKey = key;
                currentPath = qaacStartInfo.Environment[key] ?? string.Empty;
                break;
            }
        }

        if (string.IsNullOrEmpty(currentPath))
        {
            currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        }

        string newPath = tempDir + Path.PathSeparator + currentPath;
        qaacStartInfo.Environment[pathKey] = newPath;
    }

    /// <summary>
    /// Безопасно удаляет временное изолированное окружение QAAC.
    /// </summary>
    private void CleanupTempDir(string tempDir)
    {
        try
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
                _logService.DebugLog(
                    $"Изолированное временное окружение QAAC удалено: '{tempDir}'", 
                    "QaacRunner");
            }
        }
        catch (Exception ex)
        {
            _logService.Warn(
                $"Не удалось удалить временную папку QAAC '{tempDir}': {ex.Message}", 
                "QaacRunner");
        }
    }
}
