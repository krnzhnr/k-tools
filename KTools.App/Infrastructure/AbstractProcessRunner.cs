// -*- coding: utf-8 -*-
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Core;
using KTools_App.Services.Contracts;

namespace KTools_App.Infrastructure;

/// <summary>
/// Абстрактный базовый класс для безопасного асинхронного запуска дочерних консольных утилит.
/// Предоставляет общий механизм контроля жизненного цикла процесса, перенаправления
/// стандартных потоков вывода (stdout/stderr) и безопасной принудительной остановки 
/// выполнения при запросе отмены.
/// Все описания, сообщения об ошибках и лог-записи выполнены исключительно на русском языке.
/// </summary>
public abstract class AbstractProcessRunner
{
    /// <summary>
    /// Сервис логирования.
    /// </summary>
    protected ILogService Log { get; }
    protected IPathManager PathManager { get; }

    /// <summary>
    /// Инициализирует новый экземпляр AbstractProcessRunner.
    /// </summary>
    /// <param name="logService">Сервис логирования.</param>
    /// <param name="pathManager">Менеджер путей.</param>
    protected AbstractProcessRunner(ILogService logService, IPathManager pathManager)
    {
        Log = logService ?? throw new ArgumentNullException(nameof(logService));
        PathManager = pathManager ?? throw new ArgumentNullException(nameof(pathManager));
    }

    /// <summary>
    /// Асинхронно выполняет внешний бинарный процесс с перенаправлением вывода.
    /// Обеспечивает защиту от зависаний стандартных буферов за счет параллельного считывания потоков.
    /// </summary>
    /// <param name="binaryName">Имя бинарного файла без расширения (например, "ffmpeg").</param>
    /// <param name="arguments">Командные аргументы для запуска.</param>
    /// <param name="onOutputLine">Делегат для построчного перехвата стандартного вывода (stdout).</param>
    /// <param name="onErrorLine">Делегат для построчного перехвата вывода ошибок (stderr).</param>
    /// <param name="cancellationToken">Токен отмены для принудительного прерывания задачи.</param>
    /// <returns>Объект с результатами работы процесса (успех, код возврата, сообщение).</returns>
    protected async Task<ProcessResult> RunProcessAsync(
        string binaryName,
        string arguments,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        CancellationToken cancellationToken = default,
        string? workingDir = null)
    {
        string binaryPath = PathManager.GetBinaryPath(binaryName);
        if (!File.Exists(binaryPath))
        {
            string errorMsg = $"Критическая ошибка: отсутствует исполняемый файл утилиты '{binaryName}' по ожидаемому пути: '{binaryPath}'";
            Log.Error(errorMsg, GetType().Name);
            return new ProcessResult(false, -1, errorMsg);
        }

        Log.DebugLog($"Инициализация запуска процесса: '{binaryName}' с аргументами: '{arguments}' в рабочей директории: '{workingDir ?? "по умолчанию"}'", GetType().Name);

        var startInfo = new ProcessStartInfo
        {
            FileName = binaryPath,
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = workingDir ?? Path.GetDirectoryName(binaryPath) ?? AppContext.BaseDirectory
        };

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
            ActiveProcessTracker.Register(process);
            Log.Info(
                $"Запущен дочерний процесс '{binaryName}' (PID: {process.Id})",
                GetType().Name);
        }
        catch (Exception ex)
        {
            string failMsg =
                $"Не удалось запустить внешний процесс '{binaryName}': {ex.Message}";
            Log.Exception(ex, failMsg, GetType().Name);
            return new ProcessResult(false, -2, failMsg);
        }

        try
        {
            var readOutTask = Task.Run(async () =>
            {
                try
                {
                    var buffer = new char[4096];
                    var sb = new System.Text.StringBuilder();
                    while (true)
                    {
                        int read = await process.StandardOutput.ReadAsync(buffer, 0, buffer.Length);
                        if (read <= 0) break;

                        for (int i = 0; i < read; i++)
                        {
                            char c = buffer[i];
                            if (c == '\r' || c == '\n')
                            {
                                if (sb.Length > 0)
                                {
                                    string line = sb.ToString();
                                    onOutputLine?.Invoke(line);
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
                        onOutputLine?.Invoke(sb.ToString());
                    }
                }
                catch (Exception ex)
                {
                    Log.Exception(ex,
                        $"Ошибка обработки стандартного вывода '{binaryName}'",
                        GetType().Name);
                }
            });

            var readErrTask = Task.Run(async () =>
            {
                try
                {
                    var buffer = new char[4096];
                    var sb = new System.Text.StringBuilder();
                    while (true)
                    {
                        int read = await process.StandardError.ReadAsync(buffer, 0, buffer.Length);
                        if (read <= 0) break;

                        for (int i = 0; i < read; i++)
                        {
                            char c = buffer[i];
                            if (c == '\r' || c == '\n')
                            {
                                if (sb.Length > 0)
                                {
                                    string line = sb.ToString();
                                    onErrorLine?.Invoke(line);
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
                        onErrorLine?.Invoke(sb.ToString());
                    }
                }
                catch (Exception ex)
                {
                    Log.Exception(ex,
                        $"Ошибка обработки вывода ошибок '{binaryName}'",
                        GetType().Name);
                }
            });

            try
            {
                // Ожидание завершения с поддержкой токена отмены
                await process.WaitForExitAsync(cancellationToken);
                // Дожидаемся завершения считывания всех логов
                await Task.WhenAll(readOutTask, readErrTask);
            }
            catch (OperationCanceledException)
            {
                Log.Warn(
                    $"Получен внешний сигнал отмены. Принудительное прерывание " +
                    $"процесса '{binaryName}' (PID: {process.Id})...",
                    GetType().Name);
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true); // Принудительно завершаем процесс
                        Log.Info(
                            $"Процесс '{binaryName}' (PID: {process.Id}) был " +
                            $"успешно остановлен принудительно",
                            GetType().Name);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(
                        $"Ошибка при попытке принудительного прерывания " +
                        $"процесса '{binaryName}': {ex.Message}",
                        GetType().Name);
                }
                return new ProcessResult(false, -3,
                    "Операция принудительно отменена пользователем");
            }

            int exitCode = process.ExitCode;
            bool success = exitCode == 0;

            Log.Info($"Внешний процесс '{binaryName}' завершил работу с кодом выхода: {exitCode}", GetType().Name);
            return new ProcessResult(success, exitCode, success ? "Выполнено успешно" : $"Процесс завершился с ошибкой. Код возврата: {exitCode}");
        }
        finally
        {
            ActiveProcessTracker.Unregister(process);
        }
    }
}

/// <summary>
/// Структурированный результат выполнения консольного процесса.
/// </summary>
public record ProcessResult(bool IsSuccess, int ExitCode, string Message);
