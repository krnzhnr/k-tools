// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.Chaos;

/// <summary>
/// Хаос-тесты отмены выполнения скриптов и файловых операций с повторами:
/// Cancel/ResetCancellation, DeleteSourceAsync с блокировкой файла,
/// ReplaceSourceWithResultAsync с конкурентным доступом.
/// </summary>
[TestClass]
public sealed class CancellationTests
{
    private static ExposedScript CreateScript()
    {
        return new ExposedScript(
            MockBuilders.CreateLogServiceMock().Object,
            MockBuilders.CreateSettingsManagerMock().Object,
            MockBuilders.CreatePathManagerMock().Object);
    }

    /// <summary>
    /// Хаос: Cancel устанавливает флаг отмены и активирует токен отмены.
    /// </summary>
    [TestMethod]
    public void Cancel_SetsFlagAndCancelsToken()
    {
        // Arrange
        var script = CreateScript();
        script.IsCancelled.Should().BeFalse();

        // Act
        script.Cancel();

        // Assert
        script.IsCancelled.Should().BeTrue("после Cancel флаг обязан быть установлен");
        script.CancellationToken.IsCancellationRequested.Should().BeTrue(
            "токен отмены должен быть активирован");
    }

    /// <summary>
    /// Хаос: повторный Cancel идемпотентен — не бросает исключений
    /// при уже отменённом токене.
    /// </summary>
    [TestMethod]
    public void Cancel_CalledTwice_Idempotent()
    {
        // Arrange
        var script = CreateScript();
        script.Cancel();

        // Act
        Action second = script.Cancel;

        // Assert
        second.Should().NotThrow("повторная отмена не должна бросать");
        script.IsCancelled.Should().BeTrue();
    }

    /// <summary>
    /// Хаос: ResetCancellation сбрасывает флаг и выдаёт новый рабочий токен.
    /// </summary>
    [TestMethod]
    public void ResetCancellation_AfterCancel_RestoresWorkingState()
    {
        // Arrange
        var script = CreateScript();
        script.Cancel();

        // Act
        script.ResetCancellation();

        // Assert
        script.IsCancelled.Should().BeFalse("сброс обязан вернуть скрипт в рабочее состояние");
        script.CancellationToken.IsCancellationRequested.Should().BeFalse(
            "новый токен должен быть неактивирован");
    }

    /// <summary>
    /// Хаос: двойной ResetCancellation не ломает дальнейшую отмену
    /// (утилизированный CTS заменяется новым).
    /// </summary>
    [TestMethod]
    public void ResetCancellation_CalledTwice_TokenStillFunctional()
    {
        // Arrange
        var script = CreateScript();
        script.ResetCancellation();
        script.ResetCancellation();

        // Act
        script.Cancel();

        // Assert
        script.IsCancelled.Should().BeTrue(
            "после двойного сброса отмена обязана работать");
    }

    /// <summary>
    /// Хаос: отмена и сброс в быстрой последовательности из разных потоков
    /// не приводят к исключениям доступа к утилизированному CTS.
    /// </summary>
    [TestMethod]
    public void CancelAndReset_FromMultipleThreads_NoCriticalFailure()
    {
        // Arrange
        var script = CreateScript();
        var rnd = new Random(42);
        var tasks = new List<Task>();

        // Act — 50 итераций конкурентных Cancel/Reset
        for (int i = 0; i < 50; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                if (rnd.Next(2) == 0)
                {
                    script.Cancel();
                }
                else
                {
                    script.ResetCancellation();
                }
            }));
        }

        // Assert — завершается без unobserved-исключений
        bool completed = Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(30));
        completed.Should().BeTrue("конкурентные Cancel/Reset не должны зависать");

        script.ResetCancellation();
        script.IsCancelled.Should().BeFalse(
            "финальный сброс должен вернуть рабочее состояние");
    }

    /// <summary>
    /// Хаос: DeleteSourceAsync удаляет существующий файл и добавляет
    /// сообщение результата с маркером удаления.
    /// </summary>
    [TestMethod]
    public async Task DeleteSourceAsync_ExistingFile_DeletedAndReported()
    {
        // Arrange
        using var tempDir = new TempDirectoryScope();
        string file = tempDir.CreateFile("source.mkv", "data");
        var script = CreateScript();
        var results = new List<string>();

        // Act
        await script.CallDeleteSourceAsync(file, results);

        // Assert
        File.Exists(file).Should().BeFalse("исходный файл обязан быть удалён");
        results.Should().ContainSingle()
            .Which.Should().StartWith("🗑",
                "результат удаления должен содержать маркер удалённого исходника");
    }

    /// <summary>
    /// Хаос (characterization, НАЙДЕННЫЙ БАГ): DeleteSourceAsync для
    /// несуществующего файла НЕ завершается молча. Цикл попыток не имеет
    /// early-return при File.Exists == false (AbstractScript.cs:630-656),
    /// метод доходит до финального сообщения и добавляет в результаты
    /// "⚠ Не удалось удалить ... после 5 попыток" для файла, которого
    /// никогда не существовало. Ожидаемое поведение — молчаливый выход.
    /// </summary>
    [TestMethod]
    public async Task DeleteSourceAsync_MissingFile_Characterization_Bug()
    {
        // Arrange
        var script = CreateScript();
        var results = new List<string>();

        // Act
        await script.CallDeleteSourceAsync("C:\\nonexistent\\file.mkv", results);

        // Assert — баг зафиксирован: ложный отчёт о неудачном удалении
        results.Should().ContainSingle()
            .Which.Should().StartWith("⚠").And.Contain("5 попыток",
                "БАГ: несуществующий файл ошибочно репортится как неудалённый после 5 попыток");
    }

    /// <summary>
    /// Хаос: DeleteSourceAsync для файла, заблокированного другим процессом,
    /// завершает все попытки и сообщает о неудаче с маркером ⚠.
    /// </summary>
    [TestMethod]
    public async Task DeleteSourceAsync_LockedFile_ReportsFailureAfterRetries()
    {
        // Arrange
        using var tempDir = new TempDirectoryScope();
        string file = tempDir.CreateFile("locked.mkv", "data");
        var script = CreateScript();
        var results = new List<string>();

        // Act — держим файл открытым с монопольным доступом всё время теста
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Механика повторов: 5 попыток × 500 мс — но при активной блокировке
            // все попытки обречены; проверяем итоговое сообщение
            await script.CallDeleteSourceAsync(file, results);
        }

        // Assert
        File.Exists(file).Should().BeTrue("заблокированный файл не мог быть удалён");
        results.Should().ContainSingle()
            .Which.Should().StartWith("⚠",
                "неудалённый файл должен получить маркер предупреждения");
    }

    /// <summary>
    /// Хаос: ReplaceSourceWithResultAsync заменяет исходник результатом —
    /// содержимое результата оказывается по пути источника.
    /// </summary>
    [TestMethod]
    public async Task ReplaceSourceWithResultAsync_ValidFiles_Replaced()
    {
        // Arrange
        using var tempDir = new TempDirectoryScope();
        string source = tempDir.CreateFile("original.mkv", "оригинал");
        string result = tempDir.GetFullPath("result.mkv");
        File.WriteAllText(result, "новое содержимое");
        var script = CreateScript();
        var results = new List<string>();

        // Act
        bool replaced = await script.CallReplaceSourceWithResultAsync(source, result, results);

        // Assert
        replaced.Should().BeTrue("успешная подмена обязана возвращать true");
        File.Exists(result).Should().BeFalse("файл результата перемещён");
        File.ReadAllText(source).Should().Be("новое содержимое",
            "содержимое результата должно оказаться по пути источника");
        results.Should().ContainSingle()
            .Which.Should().StartWith("🔄",
                "подмена оригинала должна журналироваться маркером");
    }

    /// <summary>
    /// Хаос: ReplaceSourceWithResultAsync при отсутствии файла результата
    /// НЕ удаляет исходник: порядок операций в AbstractScript.ReplaceSourceWithResultAsync
    /// сначала проверяет наличие результата, затем подменяет исходник
    /// (File.Replace либо «резервная копия → перенос результата» с восстановлением),
    /// поэтому неудачная подмена оставляет оригинал нетронутым.
    /// </summary>
    [TestMethod]
    public async Task ReplaceSourceWithResultAsync_MissingResultFile_ReportsFailureAndKeepsSource()
    {
        // Arrange
        using var tempDir = new TempDirectoryScope();
        string source = tempDir.CreateFile("original.mkv", "оригинал");
        string missingResult = tempDir.GetFullPath("ghost.mkv");
        var script = CreateScript();
        var results = new List<string>();

        // Act
        bool replaced = await script.CallReplaceSourceWithResultAsync(source, missingResult, results);

        // Assert
        replaced.Should().BeFalse("подмена несуществующего результата обязана провалиться");
        File.Exists(source).Should().BeTrue(
            "исходный файл обязан остаться: он не удаляется до гарантированной подмены");
        File.ReadAllText(source).Should().Be("оригинал",
            "содержимое исходника обязано сохраниться без изменений");
        results.Should().ContainSingle()
            .Which.Should().StartWith("❌")
                .And.Contain("исходник сохранён",
            "неудачная подмена обязана явно сообщать о сохранении исходника");
    }

    /// <summary>
    /// Хаос: неудачная подмена не оставляет временных резервных копий рядом с исходником.
    /// </summary>
    [TestMethod]
    public async Task ReplaceSourceWithResultAsync_Failure_LeavesNoBackupArtifacts()
    {
        // Arrange
        using var tempDir = new TempDirectoryScope();
        string source = tempDir.CreateFile("original.mkv", "оригинал");
        string missingResult = tempDir.GetFullPath("ghost.mkv");
        var script = CreateScript();
        var results = new List<string>();

        // Act
        bool replaced = await script.CallReplaceSourceWithResultAsync(source, missingResult, results);

        // Assert
        replaced.Should().BeFalse();
        Directory.GetFiles(tempDir.RootPath, "*.bak")
            .Should().BeEmpty("временные резервные копии не должны оставаться на диске");
        Directory.GetFiles(tempDir.RootPath, "*")
            .Should().ContainSingle("остаётся только исходный файл");
    }

    /// <summary>
    /// Хаос: успешная подмена удаляет резервную копию и не оставляет мусора на диске.
    /// </summary>
    [TestMethod]
    public async Task ReplaceSourceWithResultAsync_Success_LeavesNoBackupArtifacts()
    {
        // Arrange
        using var tempDir = new TempDirectoryScope();
        string source = tempDir.CreateFile("original.mkv", "оригинал");
        string result = tempDir.GetFullPath("result.mkv");
        File.WriteAllText(result, "новое содержимое");
        var script = CreateScript();
        var results = new List<string>();

        // Act
        bool replaced = await script.CallReplaceSourceWithResultAsync(source, result, results);

        // Assert
        replaced.Should().BeTrue();
        Directory.GetFiles(tempDir.RootPath, "*.bak")
            .Should().BeEmpty("резервная копия обязана удаляться после успешной подмены");
    }
}
