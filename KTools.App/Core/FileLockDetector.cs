// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using KTools_App.Services.Contracts;

namespace KTools_App.Core;

/// <summary>
/// Класс для обнаружения процессов в ОС Windows, удерживающих блокировку (дескрипторы) на файлы.
/// Использует системный API Restart Manager (rstrtmgr.dll).
/// Все логи и комментарии выполнены исключительно на русском языке.
/// </summary>
public static class FileLockDetector
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    private enum RM_APP_TYPE
    {
        RmUnknownApp = 0,
        RmMainWindow = 1,
        RmOtherWindow = 2,
        RmService = 3,
        RmExplorer = 4,
        RmConsole = 5,
        RmCritical = 1000
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string strServiceShortName;
        public RM_APP_TYPE ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bGracefulExit;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, uint dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFileNames, uint nApplications, RM_UNIQUE_PROCESS[]? rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo, [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);

    /// <summary>
    /// Возвращает описание процессов, которые удерживают файл, в виде строки.
    /// Если процессов нет или произошла ошибка, возвращает пустую строку.
    /// </summary>
    /// <param name="filePath">Абсолютный путь к файлу.</param>
    /// <returns>Строковое перечисление процессов, блокирующих файл, или пустая строка.</returns>
    public static string GetLockingProcessesInfo(string filePath, ILogService logService)
    {
        if (!OperatingSystem.IsWindows())
        {
            return string.Empty;
        }

        try
        {
            var sessionKey = Guid.NewGuid().ToString();
            int res = RmStartSession(out uint handle, 0, sessionKey);
            if (res != 0)
            {
                logService.Warn($"Не удалось запустить сессию Restart Manager. Код ошибки: {res}", "FileLockDetector");
                return string.Empty;
            }

            try
            {
                string[] resources = { filePath };
                res = RmRegisterResources(handle, (uint)resources.Length, resources, 0, null, 0, null);
                if (res != 0)
                {
                    logService.Warn($"Не удалось зарегистрировать ресурс '{filePath}' в Restart Manager. Код ошибки: {res}", "FileLockDetector");
                    return string.Empty;
                }

                uint pnProcInfoNeeded = 0;
                uint pnProcInfo = 0;
                uint rebootReasons = 0;

                // Запрос количества блокирующих процессов
                res = RmGetList(handle, out pnProcInfoNeeded, ref pnProcInfo, null, ref rebootReasons);
                if (res == 234) // ERROR_MORE_DATA
                {
                    var processInfo = new RM_PROCESS_INFO[pnProcInfoNeeded];
                    pnProcInfo = pnProcInfoNeeded;
                    res = RmGetList(handle, out pnProcInfoNeeded, ref pnProcInfo, processInfo, ref rebootReasons);

                    if (res == 0)
                    {
                        var names = new List<string>();
                        for (int i = 0; i < pnProcInfo; i++)
                        {
                            try
                            {
                                using var proc = Process.GetProcessById(processInfo[i].Process.dwProcessId);
                                names.Add($"'{proc.ProcessName}' (PID: {proc.Id})");
                            }
                            catch (ArgumentException)
                            {
                                // Процесс завершился до вызова GetProcessById, берем имя из структуры
                                if (!string.IsNullOrEmpty(processInfo[i].strAppName))
                                {
                                    names.Add($"'{processInfo[i].strAppName}' (PID: {processInfo[i].Process.dwProcessId})");
                                }
                            }
                        }

                        if (names.Count > 0)
                        {
                            return string.Join(", ", names);
                        }
                    }
                }
            }
            finally
            {
                _ = RmEndSession(handle);
            }
        }
        catch (Exception ex)
        {
            logService.Exception(ex, $"Непредвиденная ошибка при определении блокирующего процесса для '{filePath}': {ex.Message}", "FileLockDetector");
        }

        return string.Empty;
    }
}
