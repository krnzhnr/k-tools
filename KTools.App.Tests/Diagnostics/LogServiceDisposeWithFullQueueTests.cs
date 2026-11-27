// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.Diagnostics;

/// <summary>
/// Тест ишью 140: Dispose с полной очередью не должен усекать сброс.
/// Медленный диск (1500 мс на запись батча) делает суммарный дренаж ~6 с:
/// Flush 5 с не успевает, остаток должен добрать DisposeDrain.
/// Старый код ждал лишь 750 мс и терял хвостовые (краш-)события.
/// </summary>
[TestClass]
public class LogServiceDisposeWithFullQueueTests
{
    [TestMethod]
    public void Dispose_WithFullQueue_DrainsWithoutTruncation()
    {
        using var scope = new TempDirectoryScope();
        LogServiceOptions options = new()
        {
            CustomLogDirectory = Path.Combine(scope.RootPath, "logs"),
            EmergencyDirectory = Path.Combine(scope.RootPath, "emergency"),
            FlushIntervalMilliseconds = 60000,
            FileFormat = LogFileFormat.Jsonl,
            QueueCapacity = 4096
        };

        LogService service = new(options);
        string? path = service.CurrentLogFile;
        path.Should().NotBeNullOrEmpty();

        SlowFlushStream slow = InstallSlowWriter(service, path!, millisecondsPerWrite: 1500);

        const int Count = 1000;
        for (int index = 0; index < Count; index++)
        {
            service.Write(
                "test.dispose.fullqueue",
                LogLevel.Info,
                LogStatus.Running,
                "Нагрузка полного дренажа " + index.ToString("D4"),
                source: "DisposeTest");
        }

        service.Dispose();
        slow.SlowWrites.Should().BeGreaterThan(0, "медленный диск обязан быть задействован");

        string[] lines = ReadPhysicalLines(path!);
        lines.Length.Should().BeGreaterThanOrEqualTo(
            Count,
            "Dispose обязан добрать всю очередь, а не усекать её по таймауту 750 мс");

        string content = File.ReadAllText(path!, Encoding.UTF8);
        content.Should().Contain(
            "Нагрузка полного дренажа 0999",
            "хвостовое (краш-)событие не должно теряться при Dispose с полной очередью");

        service.DisposeErrorCount.Should().Be(
            0,
            "полный дренаж в пределах единого лимита завершения не считается ошибкой");
    }

    private static SlowFlushStream InstallSlowWriter(LogService service, string path, int millisecondsPerWrite)
    {
        SlowFlushStream stream = new(path, millisecondsPerWrite);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(LogService).GetField("_stream", flags)!.SetValue(service, stream);
        typeof(LogService).GetField("_lineBatchCount", flags)!.SetValue(service, 0);
        typeof(LogService).GetField("_writerBytes", flags)!.SetValue(service, 0L);
        typeof(LogService).GetField("_writerPoisoned", flags)!.SetValue(service, 0);
        typeof(LogService).GetField("_writerStopping", flags)!.SetValue(service, 0);
        return stream;
    }

    private static string[] ReadPhysicalLines(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private sealed class SlowFlushStream : Stream
    {
        private readonly FileStream _inner;
        private readonly int _delayMilliseconds;
        private int _slowWrites;

        public SlowFlushStream(string path, int delayMilliseconds)
        {
            _inner = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _delayMilliseconds = delayMilliseconds;
        }

        public int SlowWrites => Volatile.Read(ref _slowWrites);

        public override bool CanRead => false;

        public override bool CanSeek => true;

        public override bool CanWrite => true;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count <= 0)
            {
                return;
            }

            Interlocked.Increment(ref _slowWrites);
            Thread.Sleep(_delayMilliseconds);
            _inner.Write(buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
