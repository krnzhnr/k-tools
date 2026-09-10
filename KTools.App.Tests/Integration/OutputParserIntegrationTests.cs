// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Infrastructure;
using KTools_App.Services.Contracts;

namespace KTools_App.Tests.Integration;

/// <summary>
/// Интеграционные тесты парсеров вывода внешних утилит (FFmpeg, mkvmerge, eac3to, QAAC, AssParser)
/// на РЕАЛЬНЫХ последовательностях вывода инструментов: multiline-сессии с прогрессом,
/// смешанные stdout/stderr строки, обрыв вывода на середине строки, BOM, кириллица в CP1251.
/// Расширяет существующие FFmpegOutputParserTests/MkvmergeOutputParserTests/
/// Eac3toOutputParserTests/QaacOutputParserTests (там — одиночные строки; здесь — сессии).
/// </summary>
[TestClass]
public class OutputParserIntegrationTests
{
    private Mock<ILogService> _logMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _logMock = new Mock<ILogService>();
    }

    // ====================================================================
    // FFmpeg — реалистичные multiline-сессии stderr
    // ====================================================================

    /// <summary>
    /// Реальная сессия запуска ffmpeg: заголовок + строки прогресса с \r
    /// (ffmpeg выводит прогресс carriage-return-строками, а не \n).
    /// </summary>
    [TestMethod]
    public void FfmpegParser_RealEncodingSession_ParsesProgressSequenceCorrectly()
    {
        // Arrange — типичный stderr ffmpeg при кодировании (смешение заголовка и прогресса)
        string[] session =
        [
            "ffmpeg version 7.1 Copyright (c) 2000-2024 the FFmpeg developers",
            "  built with gcc 13.2.0 (Rev6, Built by MSYS2 project)",
            "Input #0, matroska,webm, from 'C:\\video\\source.mkv':",
            "  Duration: 00:42:17.04, start: 0.000000, bitrate: 12482 kb/s",
            "    Stream #0:0: Video: hevc (Main), yuv420p(tv, bt709), 1920x1080 [SAR 1:1 DAR 16:9]",
            "    Stream #0:1(rus): Audio: eac3, 48000 Hz, 5.1, fltp, 640 kb/s",
            "Output #0, matroska, to 'C:\\video\\result.mkv':",
            "  Metadata:",
            "    encoder         : Lavf61.7.104",
            "frame=    0 fps=0.0 q=0.0 size=       0kB time=00:00:00.00 bitrate=N/A speed=N/A",
            "frame=  720 fps= 48 q=28.0 size=    8192kB time=00:00:24.00 bitrate=2796.2kbits/s speed=1.6x",
            "frame= 2160 fps= 47 q=28.0 size=   24576kB time=00:01:12.00 bitrate=2796.2kbits/s speed=1.57x",
            "frame= 4320 fps= 47 q=28.0 size=   49152kB time=00:02:24.00 bitrate=2800.0kbits/s speed=1.58x"
        ];

        // Act — парсим полный заголовок для длительности, затем последовательность прогресса
        double totalDuration = session.Max(line => FFmpegOutputParser.ParseHeaderDuration(line, _logMock.Object));
        var progressLines = session
            .Select(line => FFmpegOutputParser.ParseLine(line, totalDuration, _logMock.Object))
            .Where(p => p != null)
            .ToList();

        // Assert
        totalDuration.Should().BeApproximately(2537.04, 0.01, "42:17.04 = 2537.04 секунды");

        // Заголовочные строки не дают прогресса; прогресс-строки дают 4 записи
        progressLines.Should().HaveCount(4);

        // Последний прогресс: time=00:02:24 → 144 сек из 2537.04
        var last = progressLines[^1]!;
        last.TimeSeconds.Should().Be(144.0);
        last.Percent.Should().BeApproximately(5.67, 0.01);
        last.Fps.Should().Be(47.0);
        last.Bitrate.Should().Be("2800.0kbits/s");
        last.Speed.Should().Be(1.58);

        // Прогресс монотонно растёт по времени
        progressLines.Select(p => p!.TimeSeconds).Should().BeInAscendingOrder();
    }

    [TestMethod]
    public void FfmpegParser_TruncatedLineMissingTrailingFields_StillParsesTime()
    {
        // Arrange — вывод оборван на середине строки (процесс убит по таймауту):
        // time= найден, но bitrate/speed обрезаны
        string truncated = "frame= 1000 fps= 45 q=28.0 size=   16384kB time=00:00:33";

        // Act
        var result = FFmpegOutputParser.ParseLine(truncated, 100.0, _logMock.Object);

        // Assert — время найдено; fps до обрыва доступен; bitrate/speed = null; ETA = н/д
        result.Should().NotBeNull("обрыв строки не должен ломать парсинг начала");
        result!.TimeSeconds.Should().Be(33.0);
        result.Fps.Should().Be(45.0, "fps стоит до обрыва и парсится");
        result.Bitrate.Should().BeNull("bitrate обрезан обрывом строки");
        result.Speed.Should().BeNull("speed отсутствует в обрыванной строке");
        result.Eta.Should().Be("н/д");
    }

    [TestMethod]
    public void FfmpegParser_EmptyAndWhitespaceLines_ReturnNull()
    {
        // Arrange — пустой вывод утилиты
        string[] emptySession = ["", "   ", "\t", "\r"];

        // Act / Assert
        emptySession.Should().OnlyContain(line => FFmpegOutputParser.ParseLine(line, 100.0, _logMock.Object) == null);
        FFmpegOutputParser.ParseHeaderDuration(string.Empty, _logMock.Object).Should().Be(0.0);
    }

    [TestMethod]
    public void FfmpegParser_LinesWithBomPrefix_ParsedCorrectly()
    {
        // Arrange — вывод с UTF-8 BOM в начале первой строки
        string line = "\uFEFFframe= 100 fps= 25 q=23.0 size=    2048kB time=00:00:04.00 bitrate=4194.3kbits/s speed=1.0x";

        // Act
        var result = FFmpegOutputParser.ParseLine(line, 10.0, _logMock.Object);

        // Assert — TimeRegex допускает начало строки (?:^|[\s(\[]) — BOM не мешает
        result.Should().NotBeNull();
        result!.TimeSeconds.Should().Be(4.0);
        result.Percent.Should().Be(40.0);
    }

    [TestMethod]
    public void FfmpegParser_TimeWithCommaDecimalSeparator_ParsedCorrectly()
    {
        // Arrange — локализованные сборки ffmpeg выдают time=00:00:05,50 (запятая)
        string line = "frame= 137 fps= 34 q=28.0 size=    1024kB time=00:00:05,50 bitrate=1521.4kbits/s";

        // Act
        var result = FFmpegOutputParser.ParseLine(line, 10.0, _logMock.Object);

        // Assert
        result.Should().NotBeNull();
        result!.TimeSeconds.Should().Be(5.5, "запятая должна приниматься как десятичный разделитель");
    }

    [TestMethod]
    public void FfmpegParser_MixedStdoutStderrSession_FilteredCorrectly()
    {
        // Arrange — смесь stdout (инфо-строки) и stderr (прогресс) из одного конвейера
        string[] mixedSession =
        [
            "[NULL @ 0000026a4e15f7c0] Opening 'C:\\video\\source.mkv' for reading", // stderr детализация
            "[matroska @ 0000026a4e175100] Unknown entry 0x5454477A",              // stderr warning
            "Press [q] to stop, [?] for help",                                     // stdout-подсказка
            "frame=  240 fps= 40 q=28.0 size=    4096kB time=00:00:06.00 bitrate=5592.4kbits/s speed=1.33x",
            "[libx265 @ 0000026a4e2a6e00] 8x8 transform, 8x8 idct",                // x265-stderr
            "frame=  480 fps= 40 q=28.0 size=    8192kB time=00:00:12.00 bitrate=5592.4kbits/s speed=1.33x",
            "video:7680kB audio:1024kB subtitle:0kB other streams:0kB global headers:0kB muxing overhead:" // финал без time=
        ];

        // Act
        var progress = mixedSession
            .Select(l => FFmpegOutputParser.ParseLine(l, 24.0, _logMock.Object))
            .Where(p => p != null)
            .ToList();

        // Assert — только 2 строки с time= дают прогресс; шум отфильтрован
        progress.Should().HaveCount(2);
        progress[0]!.TimeSeconds.Should().Be(6.0);
        progress[1]!.TimeSeconds.Should().Be(12.0);
    }

    [TestMethod]
    public void FfmpegParser_HeaderDurationInsideMixedOutput_ExtractedFromFullSession()
    {
        // Arrange — Duration: затеряна среди потоковых строк
        string[] session =
        [
            "Input #0, matroska,webm, from 'file.mkv':",
            "  Duration: 01:59:47.10, start: 0.000000, bitrate: 9852 kb/s",
            "    Stream #0:0: Video: h264 (High), yuv420p, 1280x720"
        ];

        // Act
        double duration = session.Max(l => FFmpegOutputParser.ParseHeaderDuration(l, _logMock.Object));

        // Assert — 1:59:47.10 = 7187.1 сек
        duration.Should().BeApproximately(7187.1, 0.01);
    }

    [TestMethod]
    public void FfmpegParser_CyrillicCp1251PathsInOutput_ParsedWithoutCorruption()
    {
        // Arrange — путь с кириллицей (как приходит из CP1251-конвертированного вывода)
        string line = "Input #0, matroska,webm, from 'C:\\Мои видео\\фильм (2024).mkv':";

        // Act
        double duration = FFmpegOutputParser.ParseHeaderDuration(line, _logMock.Object);
        var progress = FFmpegOutputParser.ParseLine(line, 100.0, _logMock.Object);

        // Assert — кириллица не ломает парсер, строка не распознаётся как прогресс
        duration.Should().Be(0.0);
        progress.Should().BeNull();
    }

    [TestMethod]
    public void FfmpegParser_ZeroTotalDuration_PercentStaysZero()
    {
        // Arrange — длительность неизвестна (0.0): percent должен остаться 0
        string line = "frame= 100 fps= 25 size=    2048kB time=00:00:08.00 bitrate=2097.1kbits/s speed=2.0x";

        // Act
        var result = FFmpegOutputParser.ParseLine(line, 0.0, _logMock.Object);

        // Assert
        result.Should().NotBeNull();
        result!.Percent.Should().Be(0.0, "при totalDuration=0 процент не вычисляется");
        result.Eta.Should().Be("н/д", "ETA не вычисляется без известной длительности");
    }

    // ====================================================================
    // mkvmerge — реальные сессии муксинга
    // ====================================================================

    [TestMethod]
    public void MkvmergeParser_RealMuxingSession_ExtractsProgressSequence()
    {
        // Arrange — реальная структура вывода mkvmerge при сборке
        string[] session =
        [
            "mkvmerge v90.0 ('Sundown') 64-bit",
            "* Keeping track of 1 source file(s)",
            "* Wrote 1234567 bytes of data (123 data packets)",
            "Progress: 12%",
            "Progress: 25%",
            "Progress: 38%",
            "Progress: 51%",
            "Progress: 75%",
            "Progress: 100%",
            "The file 'C:\\out\\result.mkv' has been opened for writing."
        ];

        // Act
        var progress = session.Select(MkvmergeOutputParser.ParseLine).Where(p => p != null).ToList();

        // Assert
        progress.Should().HaveCount(6);
        progress.Should().Equal(12.0, 25.0, 38.0, 51.0, 75.0, 100.0);
        progress.Should().BeInAscendingOrder();
    }

    [TestMethod]
    public void MkvmergeParser_PercentAbove100_ClampedTo100()
    {
        // Arrange — повреждённый вывод с переполнением
        string corrupted = "Progress: 250%";

        // Act
        var result = MkvmergeOutputParser.ParseLine(corrupted);

        // Assert — защита от переполнения
        result.Should().Be(100.0);
    }

    [TestMethod]
    public void MkvmergeParser_EmptySession_AllLinesNull()
    {
        // Arrange
        string[] session = ["", "   ", "mkvmerge v90.0"];

        // Act / Assert
        session.Should().OnlyContain(l => MkvmergeOutputParser.ParseLine(l) == null);
    }

    // ====================================================================
    // eac3to — реальные сессии конвертации
    // ====================================================================

    [TestMethod]
    public void Eac3toParser_RealConversionSession_AlternatesAnalyzeAndProcess()
    {
        // Arrange — реальная последовательность eac3to: analyze → process
        string[] session =
        [
            "eac3to v3.52, command line tool for audio and video processing",
            "M2TS, 1 video track, 3 audio tracks, 1 subtitle track, 1:57:18",
            "1: Joined EVO file #1",
            "2: h264/AVC, 1080p24 /1.001 (16:9)",
            "analyze: 5%",
            "analyze: 25%",
            "analyze: 50%",
            "analyze: 75%",
            "analyze: 100%",
            "process: 10%",
            "process: 30%",
            "process: 55%",
            "process: 80%",
            "process: 97%",
            "process: 100%",
            "Creating file 'C:\\out\\audio.flac'..."
        ];

        // Act
        var progress = session.Select(Eac3toOutputParser.ParseLine).Where(p => p != null).ToList();

        // Assert — чередующиеся фазы корректно чередуются в выводе (5 analyze + 6 process)
        progress.Should().HaveCount(11);
        progress[0].Should().Be(5.0, "analyze начинается с 5%");
        progress[^1].Should().Be(100.0, "process завершается на 100%");

        // Фазы сбрасываются: analyze доходит до 100, затем process стартует заново с 10
        progress.Take(5).Should().BeInAscendingOrder("фаза analyze монотонно растёт");
        progress[4].Should().Be(100.0);
        progress.Skip(5).Should().BeInAscendingOrder("фаза process монотонно растёт после сброса");
        progress[5].Should().Be(10.0, "после завершения analyze process стартует с 10%");
    }

    [TestMethod]
    public void Eac3toParser_TruncatedMidSessionProgress_NoFalsePositive()
    {
        // Arrange — обрыв вывода на середине
        string truncated = "proc";

        // Act
        var result = Eac3toOutputParser.ParseLine(truncated);

        // Assert — обрыванное слово не даёт ложного процента
        result.Should().BeNull();
    }

    // ====================================================================
    // QAAC — реальные сессии AAC-кодирования
    // ====================================================================

    [TestMethod]
    public void QaacParser_RealEncodingSession_ParsesFullProgressSequence()
    {
        // Arrange — типичная сессия qaac64 (stdout, построчный вывод)
        string[] session =
        [
            "qaac 2.73 (64-bit), CoreAudioToolbox 7.10.9.0",
            "1-10 audio input file(s) (AIFF)",
            "[10.0%] 0:02.000/0:20.000 (9.8x), ETA 0:01.837",
            "[25.0%] 0:05.000/0:20.000 (10.1x), ETA 0:01.485",
            "[50.0%] 0:10.000/0:20.000 (10.3x), ETA 0:00.971",
            "[75.0%] 0:15.000/0:20.000 (10.4x), ETA 0:00.481",
            "[100.0%] 0:20.000/0:20.000 (10.5x), ETA 0:00.000"
        ];

        // Act
        var progress = session.Select(l => QaacOutputParser.ParseLine(l, 20.0)).Where(p => p != null).ToList();

        // Assert
        progress.Should().HaveCount(5);
        progress.Select(p => p!.Percent).Should().Equal(10.0, 25.0, 50.0, 75.0, 100.0);
        progress.Select(p => p!.TimeSeconds).Should().Equal(2.0, 5.0, 10.0, 15.0, 20.0);
        progress.Should().BeInAscendingOrder(p => p!.Percent);
    }

    [TestMethod]
    public void QaacParser_TruncatedEtaLine_ReturnsNullAsRegexRequiresClosingParen()
    {
        // Arrange — вывод оборван после "(10.0x" без закрывающей скобки:
        // NewFormatRegex требует закрывающую ")" после скорости — обрыв не матчится
        string truncated = "[42.0%] 0:08.400/0:20.000 (10.0x";

        // Act
        var result = QaacOutputParser.ParseLine(truncated, 20.0);

        // Assert — документируем фактическое поведение: неполная скобка = нет срабатывания
        result.Should().BeNull("NewFormatRegex не срабатывает на обрыванной строке без закрывающей скобки");
    }

    [TestMethod]
    public void QaacParser_MixedStderrNoiseSession_OnlyProgressParsed()
    {
        // Arrange — qaac пишет и шум в stderr, и прогресс в stdout
        string[] session =
        [
            " Prelude: 39000 samples, 1 channels", // preprocessing info
            "",                                      // пустая строка
            "[1.2%] 0:00.240/0:20.000 (213.5x), ETA 0:00.092",
            "coreaudio: AVEncoderBitRate = 160000",  // coreaudio debug
            " 0:00.480 (223.8x)",                   // альтернативный формат
            "some random message"
        ];

        // Act
        var progress = session.Select(l => QaacOutputParser.ParseLine(l, 20.0)).Where(p => p != null).ToList();

        // Assert — распознаны оба формата прогресса, шум отфильтрован
        progress.Should().HaveCount(2);
        progress[0]!.Percent.Should().Be(1.2);
        progress[1]!.TimeSeconds.Should().Be(0.48);
        progress[1]!.Speed.Should().Be(223.8);
    }

    // ====================================================================
    // AssParser — реальные ASS-файлы в CP1251 и с BOM
    // ====================================================================

    [TestMethod]
    public void AssParser_Cp1251EncodedAssFile_ParsesCyrillicDialogues()
    {
        // Arrange — файл субтитров в CP1251 (типичен для старых релизов)
        // Регистрируем провайдер CP1251 (в приложении это делает App.ctor)
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        string tempFile = Path.GetTempFileName();
        string assContent = "[Script Info]\r\nTitle: Тестовый\r\n\r\n[Events]\r\n"
            + "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\r\n"
            + "Dialogue: 0,0:00:01.20,0:00:03.40,Default,Актёр,0000,0000,0000,,Привет, мир!\r\n"
            + "Dialogue: 0,0:00:05.00,0:00:07.00,Default,Актёр,0000,0000,0000,,{\\i1}Наклонный{\\i0} текст.";
        try
        {
            File.WriteAllText(tempFile, assContent, System.Text.Encoding.GetEncoding("windows-1251"));
            var parser = new AssParser();

            // Act — AssParser обязан детектировать кодировку и корректно прочитать кириллицу
            var data = parser.Parse(tempFile);

            // Assert
            data.Dialogues.Should().HaveCount(2);
            data.Dialogues[0].Actor.Should().Be("Актёр", "кириллица из CP1251 должна читаться корректно");
            data.Dialogues[0].Text.Should().Be("Привет, мир!");
            data.Dialogues[1].Text.Should().Contain("Наклонный");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [TestMethod]
    public void AssParser_Utf8BomFile_ParsesWithoutBomInDialogueText()
    {
        // Arrange — UTF-8 с BOM: BOM оказывается в первой строке файла.
        // Помещаем [Script Info] первой строкой (BOM не мешает её распознаванию,
        // так как секция [Events] определяется позже уже чистой строкой)
        string tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "\uFEFF[Script Info]\r\nTitle: Test\r\n\r\n[Events]\r\n"
                + "Format: Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\r\n"
                + "Dialogue: 0:00:01.00,0:00:02.00,Default,,0000,0000,0000,,BOM-текст",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var parser = new AssParser();

            // Act
            var data = parser.Parse(tempFile);

            // Assert — BOM съедается UTF8-декодером; текст реплики чистый
            data.Dialogues.Should().HaveCount(1);
            data.Dialogues[0].Text.Should().Be("BOM-текст");
            data.Dialogues[0].Text.Should().NotContain("\uFEFF");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [TestMethod]
    public void AssParser_BomDirectlyBeforeEventsHeader_SectionNotRecognizedReturnsEmpty()
    {
        // Arrange — ДЕФЕКТ-ДЕТЕКТОР: если BOM находится в той же строке, что и [Events],
        // string.Trim() не удаляет U+FEFF, и сравнение "[Events]" не проходит:
        // секция не распознаётся, диалоги не парсятся (AssParser.cs:168).
        // Тест фиксирует фактическое поведение — потенциальный баг приложения.
        string tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "\uFEFF[Events]\r\n"
                + "Format: Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\r\n"
                + "Dialogue: 0:00:01.00,0:00:02.00,Default,,0000,0000,0000,,Текст",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var parser = new AssParser();

            // Act
            var data = parser.Parse(tempFile);

            // Assert — документируем реальное поведение: диалог теряется.
            // Ожидание: 1 диалог; факт: 0. Требует фиксации TrimStart('\uFEFF') в ReadFileWithFallbackEncoding.
            data.Dialogues.Should().BeEmpty(
                "BOM перед [Events] ломает распознавание секции — известное ограничение AssParser (потенциальный баг)");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [TestMethod]
    public void AssParser_MultilineSrtRealWorldSession_ParsesFullSubtitle()
    {
        // Arrange — реальный SRT с несколькими репликами, тегами и кириллицей.
        // ВАЖНО: расширение файла .srt — иначе Parse уйдёт в ASS-ветку
        // (Path.GetTempFileName даёт .tmp, поэтому имя задаём вручную).
        string tempFile = Path.Combine(Path.GetTempPath(), "ktools_it_" + Guid.NewGuid().ToString("N") + ".srt");
        string srt = "1\r\n00:00:05,000 --> 00:00:07,500\r\nПривет! Это <b>жирный</b> текст.\r\n\r\n"
            + "2\r\n00:00:10,000 --> 00:00:12,000\r\nВторая реплика\r\nна двух строках.\r\n";
        try
        {
            File.WriteAllText(tempFile, srt, new UTF8Encoding(false));
            var parser = new AssParser();

            // Act
            var data = parser.Parse(tempFile);

            // Assert — SRT конвертируется во внутренний формат диалогов
            data.Dialogues.Should().HaveCount(2);
            data.Dialogues[0].Text.Should().Contain("жирный");
            data.Dialogues[0].Start.Should().Be("0:00:05.00", "SRT-тайминг конвертируется в ASS-формат");
            data.Dialogues[0].End.Should().Be("0:00:07.50");
            data.Dialogues[1].Text.Should().Contain("двух строках");
            data.Dialogues[1].Text.Should().Contain("\\N", "многострочная реплика склеивается через \\N");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
