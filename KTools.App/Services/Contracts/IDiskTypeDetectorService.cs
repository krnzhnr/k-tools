// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace KTools_App.Services.Contracts;

public enum DriveMediaType
{
    Unknown,
    HDD,
    SSD
}

public interface IDiskTypeDetectorService
{
    /// <summary>
    /// Мгновенно вычисляет тип физического диска (HDD / SSD) через нативные вызовы Win32 API IOCTL.
    /// </summary>
    DriveMediaType GetDriveTypeForPath(string filePath);
}
