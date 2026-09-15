// -*- coding: utf-8 -*-
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using KTools_App.Services.Contracts;

namespace KTools_App.Services.Implementations;

/// <summary>
/// Реализация определения типа накопителя (HDD vs SSD) на базе нативных вызовов Win32 IOCTL (DeviceIoControl).
/// Работает мгновенно (быстрее 2 мс), полностью исключая тяжеловесные вызовы PowerShell и WMI.
/// Все комментарии и логи строго на русском языке в соответствии с правилами проекта.
/// </summary>
public sealed class DiskTypeDetectorService : IDiskTypeDetectorService
{
    private readonly ILogService _logService;
    private readonly ConcurrentDictionary<string, DriveMediaType> _cache = new(StringComparer.OrdinalIgnoreCase);

    public DiskTypeDetectorService(ILogService logService)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
    }

    public DriveMediaType GetDriveTypeForPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return DriveMediaType.Unknown;

        string root = Path.GetPathRoot(filePath) ?? string.Empty;
        if (string.IsNullOrEmpty(root)) return DriveMediaType.Unknown;

        if (_cache.TryGetValue(root, out var cachedType))
        {
            return cachedType;
        }

        DriveMediaType type = DetectDriveTypeWin32(root);
        _cache[root] = type;
        _logService.Info($"Определен тип накопителя для диск-корня '{root}': {type}", "DiskTypeDetectorService");
        return type;
    }

    private DriveMediaType DetectDriveTypeWin32(string driveRoot)
    {
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return DriveMediaType.Unknown;
            }

            string volumeName = @"\\.\" + driveRoot.TrimEnd('\\');
            using var handle = CreateFile(
                volumeName,
                0, // No access to drive required for query
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                return DriveMediaType.Unknown;
            }

            // Запрос STORAGE_PROPERTY_QUERY
            STORAGE_PROPERTY_QUERY query = new()
            {
                PropertyId = StorageDeviceProperty,
                QueryType = PropertyStandardQuery
            };

            int querySize = Marshal.SizeOf(query);
            IntPtr queryPtr = Marshal.AllocHGlobal(querySize);
            Marshal.StructureToPtr(query, queryPtr, false);

            int bufSize = 1024;
            IntPtr bufPtr = Marshal.AllocHGlobal(bufSize);

            try
            {
                bool success = DeviceIoControl(
                    handle.DangerousGetHandle(),
                    IOCTL_STORAGE_QUERY_PROPERTY,
                    queryPtr,
                    querySize,
                    bufPtr,
                    bufSize,
                    out int bytesReturned,
                    IntPtr.Zero);

                if (success && bytesReturned > 0)
                {
                    STORAGE_DEVICE_DESCRIPTOR descriptor = Marshal.PtrToStructure<STORAGE_DEVICE_DESCRIPTOR>(bufPtr);
                    
                    // Запрос Seek Penalty (HDD имеет задержку позиционирования, SSD - 0)
                    STORAGE_PROPERTY_QUERY seekQuery = new()
                    {
                        PropertyId = StorageDeviceSeekPenaltyProperty,
                        QueryType = PropertyStandardQuery
                    };
                    
                    IntPtr seekQueryPtr = Marshal.AllocHGlobal(Marshal.SizeOf(seekQuery));
                    Marshal.StructureToPtr(seekQuery, seekQueryPtr, false);

                    DEVICE_SEEK_PENALTY_DESCRIPTOR seekDescriptor = default;
                    IntPtr seekBufPtr = Marshal.AllocHGlobal(Marshal.SizeOf(seekDescriptor));

                    try
                    {
                        bool seekSuccess = DeviceIoControl(
                            handle.DangerousGetHandle(),
                            IOCTL_STORAGE_QUERY_PROPERTY,
                            seekQueryPtr,
                            Marshal.SizeOf(seekQuery),
                            seekBufPtr,
                            Marshal.SizeOf(seekDescriptor),
                            out int seekBytesReturned,
                            IntPtr.Zero);

                        if (seekSuccess && seekBytesReturned > 0)
                        {
                            seekDescriptor = Marshal.PtrToStructure<DEVICE_SEEK_PENALTY_DESCRIPTOR>(seekBufPtr);
                            return seekDescriptor.IncursSeekPenalty ? DriveMediaType.HDD : DriveMediaType.SSD;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(seekQueryPtr);
                        Marshal.FreeHGlobal(seekBufPtr);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(queryPtr);
                Marshal.FreeHGlobal(bufPtr);
            }
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, $"Ошибка Win32 определения типа диска для '{driveRoot}'", "DiskTypeDetectorService");
        }

        return DriveMediaType.Unknown;
    }

    #region Win32 P/Invoke

    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    private const int StorageDeviceProperty = 0;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int PropertyStandardQuery = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public int PropertyId;
        public int QueryType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public byte[] AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_DEVICE_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        public byte DeviceType;
        public byte DeviceTypeModifier;
        [MarshalAs(UnmanagedType.I1)]
        public bool RemovableMedia;
        [MarshalAs(UnmanagedType.I1)]
        public bool CommandQueueing;
        public uint VendorIdOffset;
        public uint ProductIdOffset;
        public uint ProductRevisionOffset;
        public uint SerialNumberOffset;
        public int BusType;
        public uint RawPropertiesLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_SEEK_PENALTY_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        [MarshalAs(UnmanagedType.I1)]
        public bool IncursSeekPenalty;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    #endregion
}
