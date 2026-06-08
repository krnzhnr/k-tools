// -*- coding: utf-8 -*-
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Core;

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
        CancellationToken cancellationToken = default)
    {
        string binaryPath = PathManager.GetBinaryPath(binaryName);
        if (!File.Exists(binaryPath))
        {
            string errorMsg = $"Критическая ошибка: отсутствует исполняемый файл утилиты '{binaryName}' по ожидаемому пути: '{binaryPath}'";
            LogService.Instance.Error(errorMsg, GetType().Name);
            return new ProcessResult(false, -1, errorMsg);
        }

        LogService.Instance.DebugLog($"Инициализация запуска процесса: '{binaryName}' с аргументами: '{arguments}'", GetType().Name);

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
            WorkingDirectory = Path.GetDirectoryName(binaryPath) ?? AppContext.BaseDirectory
        };

        using var process = new Process { StartInfo = startInfo };

        var tcsOut = new TaskCompletionSource<bool>();
        var tcsErr = new TaskCompletionSource<bool>();

        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data == null)
            {
                tcsOut.TrySetResult(true);
            }
            else
            {
                try
                {
                    onOutputLine?.Invoke(e.Data);
                }
                catch (Exception ex)
                {
                    LogService.Instance.Exception(ex,
                        $"Ошибка обработки стандартного вывода '{binaryName}'",
                        GetType().Name);
                }
            }
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data == null)
            {
                tcsErr.TrySetResult(true);
            }
            else
            {
                try
                {
                    onErrorLine?.Invoke(e.Data);
                }
                catch (Exception ex)
                {
                    LogService.Instance.Exception(ex,
                        $"Ошибка обработки вывода ошибок '{binaryName}'",
                        GetType().Name);
                }
            }
        };

        try
        {
            process.Start();
            LogService.Instance.Info(
                $"Запущен дочерний процесс '{binaryName}' (PID: {process.Id})",
                GetType().Name);

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            string failMsg =
                $"Не удалось запустить внешний процесс '{binaryName}': {ex.Message}";
            LogService.Instance.Exception(ex, failMsg, GetType().Name);
            return new ProcessResult(false, -2, failMsg);
        }

        try
        {
            // Ожидание завершения с поддержкой токена отмены
            await process.WaitForExitAsync(cancellationToken);
            // Дожидаемся завершения считывания всех логов
            await Task.WhenAll(tcsOut.Task, tcsErr.Task);
        }
        catch (OperationCanceledException)
        {
            LogService.Instance.Warn(
                $"Получен внешний сигнал отмены. Принудительное прерывание " +
                $"процесса '{binaryName}' (PID: {process.Id})...",
                GetType().Name);
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true); // Принудительно завершаем процесс
                    LogService.Instance.Info(
                        $"Процесс '{binaryName}' (PID: {process.Id}) был " +
                        $"успешно остановлен принудительно",
                        GetType().Name);
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.Error(
                    $"Ошибка при попытке принудительного прерывания " +
                    $"процесса '{binaryName}': {ex.Message}",
                    GetType().Name);
            }
            return new ProcessResult(false, -3,
                "Операция принудительно отменена пользователем");
        }

        int exitCode = process.ExitCode;
        bool success = exitCode == 0;

        LogService.Instance.Info($"Внешний процесс '{binaryName}' завершил работу с кодом выхода: {exitCode}", GetType().Name);
        return new ProcessResult(success, exitCode, success ? "Выполнено успешно" : $"Процесс завершился с ошибкой. Код возврата: {exitCode}");
    }
}

/// <summary>
/// Структурированный результат выполнения консольного процесса.
/// </summary>
public record ProcessResult(bool IsSuccess, int ExitCode, string Message);
