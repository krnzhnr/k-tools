// -*- coding: utf-8 -*-
using System;
using System.IO;
using KTools_App.Services.Contracts;

namespace KTools_App.Core;

/// <summary>
/// Поддерживаемые вычислительные бэкенды для запуска whisper.cpp CLI.
/// </summary>
public enum WhisperBackend
{
    /// <summary>Автоматический выбор оптимального доступного бэкенда.</summary>
    Auto,

    /// <summary>Вычисления исключительно на центральном процессоре (AVX2).</summary>
    Cpu,

    /// <summary>Аппаратное ускорение на видеокартах NVIDIA через CUDA.</summary>
    Cuda
}

/// <summary>
/// Статический вспомогательный класс для детектирования доступности видеоадаптеров и драйверов.
/// </summary>
public static class WhisperBackendDetector
{
    /// <summary>
    /// Проверяет наличие системного драйвера NVIDIA CUDA в операционной системе Windows.
    /// </summary>
    public static bool IsCudaDriverAvailable()
    {
        try
        {
            string system32 = Environment.SystemDirectory;
            string nvcuda = Path.Combine(system32, "nvcuda.dll");
            return File.Exists(nvcuda);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Определяет наиболее производительный доступный бэкенд на основе установленных зависимостей и драйверов.
    /// </summary>
    /// <param name="depManager">Менеджер зависимостей приложения.</param>
    /// <returns>Рекомендуемый вычислительный бэкенд.</returns>
    public static WhisperBackend DetectBestBackend(IDependencyManager depManager)
    {
        ArgumentNullException.ThrowIfNull(depManager);

        // 1. Приоритет CUDA: если есть видеокарта NVIDIA и установлен рантайм whisper_cuda
        if (IsCudaDriverAvailable() && depManager.IsInstalled("whisper_cuda"))
        {
            return WhisperBackend.Cuda;
        }

        // 2. Fallback на CPU
        return WhisperBackend.Cpu;
    }

    /// <summary>
    /// Возвращает имя подпапки внутри каталога bin/ для указанного типа бэкенда.
    /// </summary>
    public static string GetSubfolderName(WhisperBackend backend) => backend switch
    {
        WhisperBackend.Cuda => "whisper-cuda",
        _ => "whisper-cpu"
    };

    /// <summary>
    /// Возвращает ключ зависимости в IDependencyManager для указанного типа бэкенда.
    /// </summary>
    public static string GetDependencyKey(WhisperBackend backend) => backend switch
    {
        WhisperBackend.Cuda => "whisper_cuda",
        _ => "whisper_cpu"
    };
}
