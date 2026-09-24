// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using KTools_App.Services.Contracts;
using Microsoft.Win32;

namespace KTools_App.Services.Implementations;

/// <summary>
/// Реализация сервиса диагностики и логирования системных характеристик приложения и операционной системы.
/// Собирает сведения об ОС, архитектуре, процессоре, объеме оперативной памяти, видеоадаптерах, накопителях и кодовой странице.
/// Каждый шаг чтения защищён индивидуальным перехватом ошибок, предотвращая аварийное завершение работы приложения.
/// </summary>
public sealed class SystemInfoService : ISystemInfoService
{
    private readonly ILogService _logService;
    private readonly IDiskTypeDetectorService? _diskTypeDetector;
    private const string LogCategory = "SystemInfo";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public static MEMORYSTATUSEX Create()
        {
            return new MEMORYSTATUSEX
            {
                dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
            };
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetACP();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetOEMCP();

    public SystemInfoService(ILogService logService, IDiskTypeDetectorService? diskTypeDetector = null)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _diskTypeDetector = diskTypeDetector;
    }

    /// <inheritdoc/>
    public void LogSystemCharacteristics()
    {
        try
        {
            _logService.Info("=== Характеристики системы и оборудования ===", LogCategory);

            LogOperatingSystem();
            LogRuntimeAndProcess();
            LogProcessor();
            LogMemory();
            LogGraphicsAdapters();
            LogDrives();
            LogCultureAndEncodings();

            _logService.Info("===============================================", LogCategory);
        }
        catch (Exception ex)
        {
            _logService.Error($"Непредвиденное исключение при общем сборе характеристик системы: {ex.Message}", LogCategory);
        }
    }

    private void LogOperatingSystem()
    {
        try
        {
            string osDescription = RuntimeInformation.OSDescription;
            string osArchitecture = RuntimeInformation.OSArchitecture.ToString();
            string build = Environment.OSVersion.Version.ToString();
            string displayVersion = string.Empty;
            string productName = string.Empty;

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                    if (key != null)
                    {
                        displayVersion = key.GetValue("DisplayVersion")?.ToString() ?? string.Empty;
                        productName = key.GetValue("ProductName")?.ToString() ?? string.Empty;
                    }
                }
                catch (Exception regEx)
                {
                    _logService.DebugLog($"Не удалось прочитать версию Windows из реестра: {regEx.Message}", LogCategory);
                }
            }

            var sb = new StringBuilder();
            sb.Append($"ОС: {osDescription} ({osArchitecture}) [Сборка: {build}");
            if (!string.IsNullOrWhiteSpace(displayVersion))
            {
                sb.Append($", Версия: {displayVersion}");
            }
            if (!string.IsNullOrWhiteSpace(productName))
            {
                sb.Append($", Продукт: {productName}");
            }
            sb.Append(']');

            _logService.Info(sb.ToString(), LogCategory);
        }
        catch (Exception ex)
        {
            _logService.Warn($"Не удалось определить параметры операционной системы: {ex.Message}", LogCategory);
        }
    }

    private void LogRuntimeAndProcess()
    {
        try
        {
            string processArch = RuntimeInformation.ProcessArchitecture.ToString();
            string framework = RuntimeInformation.FrameworkDescription;
            bool is64BitProcess = Environment.Is64BitProcess;
            bool is64BitOperatingSystem = Environment.Is64BitOperatingSystem;

            _logService.Info(
                $"Среда выполнения: {framework} | Архитектура процесса: {processArch} (64-бит: {is64BitProcess}) | 64-бит ОС: {is64BitOperatingSystem}",
                LogCategory);
        }
        catch (Exception ex)
        {
            _logService.Warn($"Не удалось определить параметры среды выполнения: {ex.Message}", LogCategory);
        }
    }

    private void LogProcessor()
    {
        try
        {
            string cpuName = string.Empty;
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                    if (key != null)
                    {
                        cpuName = key.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? string.Empty;
                    }
                }
                catch (Exception regEx)
                {
                    _logService.DebugLog($"Не удалось прочитать наименование ЦП из реестра: {regEx.Message}", LogCategory);
                }
            }

            int logicalCores = Environment.ProcessorCount;
            string cpuDisplay = string.IsNullOrWhiteSpace(cpuName) ? "Неизвестный процессор" : cpuName;
            _logService.Info($"Процессор: {cpuDisplay} | Логических ядер/потоков: {logicalCores}", LogCategory);
        }
        catch (Exception ex)
        {
            _logService.Warn($"Не удалось определить параметры процессора: {ex.Message}", LogCategory);
        }
    }

    private void LogMemory()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var memStatus = MEMORYSTATUSEX.Create();
                if (GlobalMemoryStatusEx(ref memStatus))
                {
                    double totalGb = memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
                    double availGb = memStatus.ullAvailPhys / (1024.0 * 1024.0 * 1024.0);
                    _logService.Info(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "ОЗУ: Всего {0:F1} ГБ | Доступно {1:F1} ГБ | Загрузка памяти: {2}%",
                            totalGb,
                            availGb,
                            memStatus.dwMemoryLoad),
                        LogCategory);
                    return;
                }
            }

            _logService.Info($"ОЗУ: Выделено памяти текущим процессором: {Environment.WorkingSet / (1024 * 1024)} МБ", LogCategory);
        }
        catch (Exception ex)
        {
            _logService.Warn($"Не удалось определить объем оперативной памяти: {ex.Message}", LogCategory);
        }
    }

    private void LogGraphicsAdapters()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return;

            var gpus = new List<string>();
            try
            {
                const string videoClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
                using var baseKey = Registry.LocalMachine.OpenSubKey(videoClassKey);
                if (baseKey != null)
                {
                    foreach (string subKeyName in baseKey.GetSubKeyNames())
                    {
                        if (subKeyName.Length == 4 && char.IsDigit(subKeyName[0]))
                        {
                            using var subKey = baseKey.OpenSubKey(subKeyName);
                            if (subKey != null)
                            {
                                string driverDesc = subKey.GetValue("DriverDesc")?.ToString() ?? string.Empty;
                                if (!string.IsNullOrWhiteSpace(driverDesc))
                                {
                                    string driverVer = subKey.GetValue("DriverVersion")?.ToString() ?? string.Empty;
                                    string info = string.IsNullOrWhiteSpace(driverVer)
                                        ? driverDesc
                                        : $"{driverDesc} (Драйвер: {driverVer})";
                                    gpus.Add(info);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception regEx)
            {
                _logService.DebugLog($"Не удалось прочитать видеоадаптеры из реестра: {regEx.Message}", LogCategory);
            }

            if (gpus.Count > 0)
            {
                for (int i = 0; i < gpus.Count; i++)
                {
                    _logService.Info($"Видеоадаптер [{i + 1}]: {gpus[i]}", LogCategory);
                }
            }
            else
            {
                _logService.Info("Видеоадаптер: Не удалось обнаружить графические адаптеры в реестре", LogCategory);
            }
        }
        catch (Exception ex)
        {
            _logService.Warn($"Не удалось опросить видеоадаптеры: {ex.Message}", LogCategory);
        }
    }

    private void LogDrives()
    {
        try
        {
            DriveInfo[] drives = DriveInfo.GetDrives();
            foreach (var drive in drives)
            {
                try
                {
                    if (!drive.IsReady) continue;

                    double totalGb = drive.TotalSize / (1024.0 * 1024.0 * 1024.0);
                    double freeGb = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);

                    string mediaTypeStr = string.Empty;
                    if (_diskTypeDetector != null)
                    {
                        try
                        {
                            var mediaType = _diskTypeDetector.GetDriveTypeForPath(drive.RootDirectory.FullName);
                            if (mediaType != DriveMediaType.Unknown)
                            {
                                mediaTypeStr = $", {mediaType}";
                            }
                        }
                        catch (Exception mediaEx)
                        {
                            _logService.DebugLog($"Не удалось определить физический тип носителя для '{drive.Name}': {mediaEx.Message}", LogCategory);
                        }
                    }

                    _logService.Info(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Диск {0} ({1}{2}, {3}): Свободно {4:F1} ГБ из {5:F1} ГБ",
                            drive.Name,
                            drive.DriveType,
                            mediaTypeStr,
                            drive.DriveFormat,
                            freeGb,
                            totalGb),
                        LogCategory);
                }
                catch (Exception driveEx)
                {
                    _logService.DebugLog($"Не удалось получить детальную информацию для диска '{drive.Name}': {driveEx.Message}", LogCategory);
                }
            }
        }
        catch (Exception ex)
        {
            _logService.Warn($"Не удалось опросить дисковые накопители: {ex.Message}", LogCategory);
        }
    }

    private void LogCultureAndEncodings()
    {
        try
        {
            var currentCulture = CultureInfo.CurrentCulture.Name;
            var currentUiCulture = CultureInfo.CurrentUICulture.Name;
            uint acp = 0;
            uint oemcp = 0;

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    acp = GetACP();
                    oemcp = GetOEMCP();
                }
                catch (Exception cpEx)
                {
                    _logService.DebugLog($"Не удалось получить системные кодовые страницы Win32: {cpEx.Message}", LogCategory);
                }
            }

            _logService.Info(
                $"Региональные настройки: Культура: {currentCulture} | UI-культура: {currentUiCulture} | ANSI кодовая страница (ACP): {acp} | OEM кодовая страница: {oemcp}",
                LogCategory);
        }
        catch (Exception ex)
        {
            _logService.Warn($"Не удалось определить региональные настройки и кодовые страницы: {ex.Message}", LogCategory);
        }
    }
}
