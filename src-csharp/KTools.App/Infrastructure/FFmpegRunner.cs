// -*- coding: utf-8 -*-
using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Core;

namespace KTools_App.Infrastructure;

/// <summary>
/// Класс для инфраструктурного запуска утилиты FFmpeg в асинхронных процессах Windows.
/// Реализует паттерн «Асинхронная обертка» над CLI процессами с поддержкой отмены.
/// </summary>
public static class FFmpegRunner
{
    /// <summary>
    /// Асинхронно выполнить команду FFmpeg с перенаправлением потоков вывода и поддержкой отмены.
    /// </summary>
    /// <param name="arguments">Строка аргументов командной строки для FFmpeg.</param>
    /// <param name="cancellationToken">Токен отмены CancellationToken для прерывания процесса.</param>
    /// <returns>Строковый вывод (stdout) утилиты после успешного завершения.</returns>
    /// <exception cref="Exception">Генерируется в случае ненулевого кода возврата процесса.</exception>
    public static async Task<string> RunAsync(string arguments, CancellationToken cancellationToken = default)
    {
        string ffmpegPath = PathManager.GetBinaryPath("ffmpeg");

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        // Регистрация асинхронных обработчиков событий для чтения вывода без блокировки UI
        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                outputBuilder.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                errorBuilder.AppendLine(e.Data);
            }
        };

        // Запуск дочернего процесса
        process.Start();

        // Запуск асинхронного чтения из перенаправленных потоков
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            // Асинхронное ожидание завершения процесса с привязкой к CancellationToken
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Принудительное и безопасное завершение всего дерева процессов при отмене операции пользователем
            process.Kill(entireProcessTree: true);
            throw;
        }

        // Проверка кода возврата процесса
        if (process.ExitCode != 0)
        {
            throw new Exception($"FFmpeg завершился с кодом ошибки {process.ExitCode}.\nДетали: {errorBuilder}");
        }

        return outputBuilder.ToString();
    }
}
