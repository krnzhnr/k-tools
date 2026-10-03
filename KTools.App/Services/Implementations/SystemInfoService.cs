// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

using KTools_App.Core;
using KTools_App.Diagnostics;
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
            LogOperatingSystem();
            LogRuntimeAndProcess();
            LogProcessor();
            LogMemory();
            LogGraphicsAdapters();
            LogDrives();
            LogCultureAndEncodings();

            _logService.Write(
                "system.snapshot.completed",
                LogLevel.Info,
                LogStatus.Succeeded,
                "Сведения о системе и оборудовании собраны",
                source: LogCategory,
                properties: LogProps.Create("Schema", "system.snapshot.v1"));
        }
        catch (Exception ex)
        {
            _logService.Write(
                "system.snapshot.failed",
                LogLevel.Error,
                LogStatus.Failed,
                "Не удалось собрать сведения о системе и оборудовании",
                ex,
                LogCategory,
                properties: LogProps.Create("ErrorCode", "SYSTEM_SNAPSHOT_FAILED"));
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
                    _logService.Write(
                        "system.os.registry_read_failed",
                        LogLevel.Debug,
                        LogStatus.Skipped,
                        "Сведения о версии Windows из реестра недоступны",
                        regEx,
                        LogCategory,
                        properties: LogProps
                            .Create("Group", "OperatingSystem")
                            .With("ErrorCode", "OS_REGISTRY_UNAVAILABLE"));
                }
            }

            _logService.Write(
                "system.os.detected",
                LogLevel.Info,
                LogStatus.Succeeded,
                $"Параметры ОС определены: {osDescription} ({osArchitecture}), сборка {build}, версия {Present(displayVersion)}, продукт {Present(productName)}",
                source: LogCategory,
                properties: LogProps
                    .Create("Platform", "Windows")
                    .With("Architecture", osArchitecture)
                    .With("Version", LogRedactor.ReadableMachineValue(osDescription)));
        }
        catch (Exception ex)
        {
            _logService.Write(
                "system.os.detect_failed",
                LogLevel.Warning,
                LogStatus.Skipped,
                "Не удалось определить параметры операционной системы",
                ex,
                LogCategory,
                properties: LogProps.Create("ErrorCode", "OS_DETECT_FAILED"));
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

            _logService.Write(
                "system.runtime.detected",
                LogLevel.Info,
                LogStatus.Succeeded,
                $"Среда выполнения определена: {framework}, архитектура процесса {processArch}, 64-битный процесс: {is64BitProcess}, 64-битная ОС: {is64BitOperatingSystem}",
                source: LogCategory,
                properties: LogProps
                    .Create("Version", LogRedactor.ReadableMachineValue(framework))
                    .With("Architecture", processArch)
                    .With("Platform", is64BitOperatingSystem ? "x64-os" : "x86-os"));
        }
        catch (Exception ex)
        {
            _logService.Write(
                "system.runtime.detect_failed",
                LogLevel.Warning,
                LogStatus.Skipped,
                "Не удалось определить параметры среды выполнения",
                ex,
                LogCategory,
                properties: LogProps.Create("ErrorCode", "RUNTIME_DETECT_FAILED"));
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
                    _logService.Write(
                        "system.processor.registry_read_failed",
                        LogLevel.Debug,
                        LogStatus.Skipped,
                        "Наименование процессора из реестра недоступно",
                        regEx,
                        LogCategory,
                        properties: LogProps
                            .Create("Group", "Processor")
                            .With("ErrorCode", "PROCESSOR_REGISTRY_UNAVAILABLE"));
                }
            }

            int logicalCores = Environment.ProcessorCount;

            _logService.Write(
                "system.processor.detected",
                LogLevel.Info,
                LogStatus.Succeeded,
                $"Процессор определён: {Present(cpuName)}, логических ядер: {logicalCores}",
                source: LogCategory,
                properties: LogProps
                    .Create("Count", logicalCores)
                    .With("Version", LogRedactor.ReadableMachineValue(cpuName)));
        }
        catch (Exception ex)
        {
            _logService.Write(
                "system.processor.detect_failed",
                LogLevel.Warning,
                LogStatus.Skipped,
                "Не удалось определить параметры процессора",
                ex,
                LogCategory,
                properties: LogProps.Create("ErrorCode", "PROCESSOR_DETECT_FAILED"));
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
                    _logService.Write(
                        "system.memory.detected",
                        LogLevel.Info,
                        LogStatus.Succeeded,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Оперативная память: всего {0:F1} ГБ, доступно {1:F1} ГБ, загрузка {2}%",
                            totalGb,
                            availGb,
                            memStatus.dwMemoryLoad),
                        source: LogCategory,
                        properties: LogProps
                            .Create("Percent", (int)memStatus.dwMemoryLoad)
                            .With("TotalBytes", (long)memStatus.ullTotalPhys));
                    return;
                }
            }

            long workingSet = Environment.WorkingSet;
            _logService.Write(
                "system.memory.fallback",
                LogLevel.Debug,
                LogStatus.Skipped,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Глобальная статистика памяти недоступна, используется рабочее множество текущего процесса: {0} МБ",
                    workingSet / (1024 * 1024)),
                source: LogCategory,
                properties: LogProps.Create("TotalBytes", workingSet));
        }
        catch (Exception ex)
        {
            _logService.Write(
                "system.memory.detect_failed",
                LogLevel.Warning,
                LogStatus.Skipped,
                "Не удалось определить объём оперативной памяти",
                ex,
                LogCategory,
                properties: LogProps.Create("ErrorCode", "MEMORY_DETECT_FAILED"));
        }
    }

    private void LogGraphicsAdapters()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            List<string> gpus = new();
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
                                    gpus.Add(string.IsNullOrWhiteSpace(driverVer)
                                        ? driverDesc
                                        : $"{driverDesc} (драйвер {driverVer})");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception regEx)
            {
                _logService.Write(
                    "system.graphics.registry_read_failed",
                    LogLevel.Debug,
                    LogStatus.Skipped,
                    "Список видеоадаптеров из реестра недоступен",
                    regEx,
                    LogCategory,
                    properties: LogProps
                        .Create("Group", "Graphics")
                        .With("ErrorCode", "GRAPHICS_REGISTRY_UNAVAILABLE"));
            }

            if (gpus.Count > 0)
            {
                _logService.Write(
                    "system.graphics.detected",
                    LogLevel.Info,
                    LogStatus.Succeeded,
                    $"Видеоадаптеры определены: {gpus.Count}, первый: {gpus[0]}",
                    source: LogCategory,
                    properties: LogProps
                        .Create("Count", gpus.Count)
                        .With("Version", LogRedactor.ReadableMachineValue(gpus[0])));
            }
            else
            {
                _logService.Write(
                    "system.graphics.empty",
                    LogLevel.Info,
                    LogStatus.Skipped,
                    "Видеоадаптеры в реестре не обнаружены",
                    source: LogCategory,
                    properties: LogProps.Create("Count", 0));
            }
        }
        catch (Exception ex)
        {
            _logService.Write(
                "system.graphics.detect_failed",
                LogLevel.Warning,
                LogStatus.Skipped,
                "Не удалось опросить видеоадаптеры",
                ex,
                LogCategory,
                properties: LogProps.Create("ErrorCode", "GRAPHICS_DETECT_FAILED"));
        }
    }

    private void LogDrives()
    {
        try
        {
            DriveInfo[] drives = DriveInfo.GetDrives();
            foreach (DriveInfo drive in drives)
            {
                string root = LogProps.RootName(drive.RootDirectory.FullName);
                try
                {
                    if (!drive.IsReady)
                    {
                        continue;
                    }

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
                            _logService.Write(
                                "system.drive.media_type_failed",
                                LogLevel.Debug,
                                LogStatus.Skipped,
                                $"Физический тип носителя {drive.Name} не определён",
                                mediaEx,
                                LogCategory,
                                properties: LogProps
                                    .Create("FileName", root)
                                    .With("ErrorCode", "MEDIA_TYPE_UNAVAILABLE"));
                        }
                    }

                    _logService.Write(
                        "system.drive.detected",
                        LogLevel.Info,
                        LogStatus.Succeeded,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Накопитель {0} ({1}{2}, {3}): свободно {4:F1} ГБ из {5:F1} ГБ",
                            drive.Name,
                            drive.DriveType,
                            mediaTypeStr,
                            drive.DriveFormat,
                            freeGb,
                            totalGb),
                        source: LogCategory,
                        properties: LogProps
                            .Create("FileName", root)
                            .With("TotalBytes", drive.TotalSize)
                            .With("Container", drive.DriveFormat));
                }
                catch (Exception driveEx)
                {
                    _logService.Write(
                        "system.drive.detail_failed",
                        LogLevel.Debug,
                        LogStatus.Skipped,
                        $"Детальные сведения о накопителе {drive.Name} недоступны",
                        driveEx,
                        LogCategory,
                        properties: LogProps
                            .Create("FileName", root)
                            .With("ErrorCode", "DRIVE_DETAIL_UNAVAILABLE"));
                }
            }
        }
        catch (Exception ex)
        {
            _logService.Write(
                "system.drive.enumeration_failed",
                LogLevel.Warning,
                LogStatus.Skipped,
                "Не удалось опросить дисковые накопители",
                ex,
                LogCategory,
                properties: LogProps.Create("ErrorCode", "DRIVE_ENUMERATION_FAILED"));
        }
    }

    private void LogCultureAndEncodings()
    {
        try
        {
            string currentCulture = CultureInfo.CurrentCulture.Name;
            string currentUiCulture = CultureInfo.CurrentUICulture.Name;
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
                    _logService.Write(
                        "system.encoding.win32_read_failed",
                        LogLevel.Debug,
                        LogStatus.Skipped,
                        "Системные кодовые страницы Win32 недоступны",
                        cpEx,
                        LogCategory,
                        properties: LogProps
                            .Create("Group", "Encoding")
                            .With("ErrorCode", "CODEPAGE_UNAVAILABLE"));
                }
            }

            _logService.Write(
                "system.encoding.detected",
                LogLevel.Info,
                LogStatus.Succeeded,
                $"Региональные настройки определены: культура {currentCulture}, UI-культура {currentUiCulture}, ANSI-кодовая страница {acp}, OEM-кодовая страница {oemcp}",
                source: LogCategory,
                properties: LogProps
                    .Create("Language", LogRedactor.ReadableMachineValue(currentCulture))
                    .With("Platform", LogRedactor.ReadableMachineValue(currentUiCulture))
                    .With("Count", (int)acp)
                    .With("Container", oemcp.ToString(CultureInfo.InvariantCulture)));
        }
        catch (Exception ex)
        {
            _logService.Write(
                "system.encoding.detect_failed",
                LogLevel.Warning,
                LogStatus.Skipped,
                "Не удалось определить региональные настройки и кодовые страницы",
                ex,
                LogCategory,
                properties: LogProps.Create("ErrorCode", "LOCALE_DETECT_FAILED"));
        }
    }

    private static string Present(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "не определено" : value;
}
