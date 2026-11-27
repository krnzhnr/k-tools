// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты сверки целостности скачанных файлов (ишью 84).
/// Подмена файла обязана детектироваться по SHA-256 до распаковки и запуска.
/// Все комментарии и тестовые описания написаны на русском языке.
/// </summary>
[TestClass]
public class FileIntegrityTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KTools_FileIntegrityTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// Проверяет, что совпавший хеш пропускает файл без исключений.
    /// </summary>
    [TestMethod]
    public void VerifySha256_MatchingContent_PassesWithoutThrowing()
    {
        // Arrange
        string path = Path.Combine(_tempDir, "payload.bin");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("достоверное содержимое архива"));
        string expected = FileIntegrity.ComputeSha256Hex(path);

        // Act
        Action act = () => FileIntegrity.VerifySha256(path, expected);

        // Assert
        act.Should().NotThrow("совпавший хеш обязан пропускать файл дальше без исключений");
    }

    /// <summary>
    /// Проверяет, что подменённое содержимое детектируется до запуска.
    /// </summary>
    [TestMethod]
    public void VerifySha256_TamperedContent_ThrowsInvalidDataException()
    {
        // Arrange
        string trustedPath = Path.Combine(_tempDir, "trusted.bin");
        File.WriteAllBytes(trustedPath, Encoding.UTF8.GetBytes("доверенное содержимое"));
        string expectedOfTrusted = FileIntegrity.ComputeSha256Hex(trustedPath);

        string tamperedPath = Path.Combine(_tempDir, "tampered.bin");
        File.WriteAllBytes(tamperedPath, Encoding.UTF8.GetBytes("подменённое содержимое"));

        // Act
        Action act = () => FileIntegrity.VerifySha256(tamperedPath, expectedOfTrusted);

        // Assert
        act.Should().Throw<InvalidDataException>("подмена файла обязана детектироваться до распаковки и запуска");
    }

    /// <summary>
    /// Проверяет быстрый отказ при незаданном ожидаемом хеше.
    /// </summary>
    [TestMethod]
    public void VerifySha256_EmptyExpectedHash_ThrowsArgumentException()
    {
        // Arrange
        string path = Path.Combine(_tempDir, "payload.bin");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("содержимое"));

        // Act
        Action act = () => FileIntegrity.VerifySha256(path, string.Empty);

        // Assert
        act.Should().Throw<ArgumentException>("пустой хеш обязан отклоняться сразу, а не молча пропускать сверку");
    }

    /// <summary>
    /// Проверяет быстрый отказ при отсутствии файла на диске.
    /// </summary>
    [TestMethod]
    public void ComputeSha256Hex_MissingFile_ThrowsFileNotFoundException()
    {
        // Arrange
        string path = Path.Combine(_tempDir, "отсутствует.bin");

        // Act
        Action act = () => FileIntegrity.ComputeSha256Hex(path);

        // Assert
        act.Should().Throw<FileNotFoundException>("сверка отсутствующего файла обязана падать сразу");
    }

    /// <summary>
    /// Проверяет, что DependencyManager прерывает установку при несовпадении хеша:
    /// маркер не появляется, временный файл удалён, в журнале событие mismatch.
    /// </summary>
    [TestMethod]
    public async Task InstallDependencyAsync_TamperedArchive_DetectsMismatchBeforeInstall()
    {
        // Arrange: сервер отдаёт чужие байты, в реестре хеш доверенного содержимого.
        byte[] servedBytes = Encoding.UTF8.GetBytes("подменённый установщик");
        string trustedPath = Path.Combine(_tempDir, "trusted.bin");
        File.WriteAllBytes(trustedPath, Encoding.UTF8.GetBytes("доверенный установщик"));
        string trustedHash = FileIntegrity.ComputeSha256Hex(trustedPath);

        var logServiceMock = new Mock<ILogService>();
        var pathManagerMock = new Mock<IPathManager>();
        string binDir = Path.Combine(_tempDir, "bin");
        Directory.CreateDirectory(binDir);
        pathManagerMock.Setup(pm => pm.GetBinDirectory()).Returns(binDir);
        pathManagerMock.Setup(pm => pm.GetBaseDirectory()).Returns(_tempDir);
        pathManagerMock.Setup(pm => pm.GetBinaryPath(It.IsAny<string>()))
            .Returns<string>(binName => Path.Combine(binDir, binName));

        var httpClientFactoryMock = new Mock<IHttpClientFactory>();
        httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(new ByteArrayHttpMessageHandler(servedBytes)));
        var settingsManagerMock = new Mock<ISettingsManager>();
        var hardwareCacheMock = new Mock<KTools_App.Encoders.IHardwareCapabilityCache>();

        var manager = new DependencyManager(
            logServiceMock.Object,
            pathManagerMock.Object,
            httpClientFactoryMock.Object,
            settingsManagerMock.Object,
            hardwareCacheMock.Object);
        DependencyInfo node = manager.GetRegistry().First(d => d.Key == "node");
        node.Sha256 = trustedHash;

        bool? succeeded = null;
        manager.InstallFinished += (key, ok, error) =>
        {
            if (key == "node")
            {
                succeeded = ok;
            }
        };

        // Act
        await manager.InstallDependencyAsync("node");

        // Assert
        succeeded.Should().BeFalse("установка подменённого архива обязана завершаться отказом");
        File.Exists(Path.Combine(binDir, "node", node.VerifyBinary)).Should().BeFalse("маркер не должен появляться после отбраковки");
        File.Exists(Path.Combine(Path.GetTempPath(), node.ArchiveName)).Should().BeFalse("отбракованный файл обязан удаляться из временной папки");
        logServiceMock.Verify(
            l => l.Write(
                It.Is<string>(eventId => eventId == "dependency.hash.mismatch"),
                It.IsAny<LogLevel>(),
                It.IsAny<LogStatus>(),
                It.IsAny<string>(),
                It.IsAny<Exception?>(),
                It.IsAny<string>(),
                It.IsAny<LogContext?>(),
                It.IsAny<IReadOnlyDictionary<string, object?>?>()),
            Times.Once,
            "несоответствие хеша обязано фиксироваться dedicated-событием журнала");
    }

    /// <summary>
    /// Обработчик HTTP, отдающий фиксированный массив байтов без сетевых вызовов.
    /// </summary>
    private sealed class ByteArrayHttpMessageHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;

        public ByteArrayHttpMessageHandler(byte[] payload)
        {
            _payload = payload;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_payload)
            };
            return Task.FromResult(response);
        }
    }
}
