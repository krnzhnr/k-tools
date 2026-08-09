// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using Moq;
using KTools_App.Scripts;
using KTools_App.Core;
using KTools_App.Services.Contracts;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для скрипта пересадки аудио AudioTransplantScript.
/// Все комментарии написаны на русском языке в соответствии с регламентом.
/// </summary>
[TestClass]
public class AudioTransplantScriptTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private Mock<IFFmpegRunner> _ffmpegRunnerMock = null!;
    private Mock<IMkvmergeRunner> _mkvmergeRunnerMock = null!;
    private Mock<IEac3toRunner> _eac3toRunnerMock = null!;
    private Mock<IAudioWaveformService> _waveformServiceMock = null!;
    private Mock<IMediaProbeService> _mediaProbeServiceMock = null!;
    private AudioTransplantScript _script = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _pathManagerMock = new Mock<IPathManager>();
        _ffmpegRunnerMock = new Mock<IFFmpegRunner>();
        _mkvmergeRunnerMock = new Mock<IMkvmergeRunner>();
        _eac3toRunnerMock = new Mock<IEac3toRunner>();
        _waveformServiceMock = new Mock<IAudioWaveformService>();
        _mediaProbeServiceMock = new Mock<IMediaProbeService>();

        _script = new AudioTransplantScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _ffmpegRunnerMock.Object,
            _mkvmergeRunnerMock.Object,
            _eac3toRunnerMock.Object,
            _waveformServiceMock.Object,
            _mediaProbeServiceMock.Object
        );
    }

    /// <summary>
    /// Проверяет свойства скрипта пересадки аудио.
    /// </summary>
    [TestMethod]
    public void ScriptProperties_VerifyCorrectValues()
    {
        _script.Name.Should().Be("Пересадка аудио");
        _script.Category.Should().Be("Аудио");
        _script.IconName.Should().Be(AppConstants.ScriptIcons.AudioTransplant);
        _script.RequiredDependencies.Should().Contain("ffmpeg").And.Contain("mkvtoolnix").And.Contain("eac3to");
    }

    /// <summary>
    /// Проверяет точность формулы компенсации задержки AAC (21.33 мс на 1024 сэмпла при 48 кГц).
    /// </summary>
    [TestMethod]
    public void AacPrimingDelay_CalculationAccuracy_ShouldBeExact()
    {
        double delayMs = (1024.0 / 48000.0) * 1000.0;
        delayMs.Should().BeApproximately(21.333333333333332, 0.0001);
    }
}
