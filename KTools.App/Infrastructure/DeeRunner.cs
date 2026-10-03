// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;

namespace KTools_App.Infrastructure;

/// <summary>
/// Синглтон-обертка для прямого запуска Dolby Encoding Engine (dee.exe) без использования Python-модуля deew.
/// Выполняет подготовку промежуточного аудио через FFmpeg, генерацию XML-конфигурации для DEE и запуск кодировщика.
/// Все комментарии и логирование выполнены строго на русском языке в соответствии с регламентом.
/// </summary>
public sealed class DeeRunner : AbstractProcessRunner
{
    private const string SourceName = nameof(DeeRunner);

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);

    /// <summary>
    /// Возвращает короткий путь в формате 8.3 для операционной системы Windows.
    /// Это необходимо, так как Dolby Encoding Engine не поддерживает пробелы и кириллицу в путях.
    /// </summary>
    /// <param name="path">Исходный длинный путь.</param>
    /// <returns>Короткий путь или исходный путь при невозможности конвертации.</returns>
    private static string GetShortPath(string path, ILogService logService)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        if (!OperatingSystem.IsWindows())
        {
            return path;
        }

        try
        {
            var sb = new StringBuilder(1024);
            uint result = GetShortPathName(path, sb, (uint)sb.Capacity);
            if (result > 0)
            {
                return sb.ToString();
            }

            if (result > sb.Capacity)
            {
                sb.EnsureCapacity((int)result);
                result = GetShortPathName(path, sb, result);
                if (result > 0)
                {
                    return sb.ToString();
                }
            }

            logService.Write(
                ProcessEventIds.PostconditionFailed,
                LogLevel.Debug,
                LogStatus.PartiallySucceeded,
                "Не удалось получить короткий путь 8.3 для временного файла DEE",
                null,
                "DeeRunner",
                context: ProcessExecutionContext.NewOperation("dee").ToLogContext(),
                properties: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = "short_path",
                    ["ErrorCode"] = Marshal.GetLastWin32Error().ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
        }
        catch (Exception ex)
        {
            logService.Write(
                ProcessEventIds.PostconditionFailed,
                LogLevel.Warning,
                LogStatus.PartiallySucceeded,
                "Исключение при получении короткого пути 8.3 для временного файла DEE",
                ex,
                "DeeRunner",
                context: ProcessExecutionContext.NewOperation("dee").ToLogContext(),
                properties: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = "short_path"
                });
        }

        return path;
    }



    private readonly IFFmpegRunner _ffmpegRunner;

    /// <summary>
    /// Инициализирует новый экземпляр DeeRunner с внедрением зависимостей.
    /// </summary>
    /// <param name="logService">Сервис логирования.</param>
    /// <param name="ffmpegRunner">Исполнитель FFmpeg.</param>
    public DeeRunner(ILogService logService, IFFmpegRunner ffmpegRunner, IPathManager pathManager)
        : base(logService, pathManager)
    {
        _ffmpegRunner = ffmpegRunner;
    }



    /// <summary>
    /// Запустить кодирование Dolby Digital (DD) или Dolby Digital Plus (DDP) через dee.exe.
    /// </summary>
    /// <param name="inputPath">Абсолютный путь к исходному аудиофайлу.</param>
    /// <param name="outputPath">Абсолютный путь к выходному сжатому файлу (.ac3/.ec3).</param>
    /// <param name="bitrate">Битрейт кодирования в kbps (например, "448").</param>
    /// <param name="outputFormat">Формат кодирования: "dd" или "ddp".</param>
    /// <param name="downmixChannels">Целевое количество выходных каналов (1, 2, 6, 8).</param>
    /// <param name="drcProfile">Профиль сжатия динамического диапазона (например, "film_standard").</param>
    /// <param name="dialnorm">Значение нормализации диалогов (по умолчанию -31).</param>
    /// <param name="cancellationToken">Токен отмены задачи.</param>
    /// <returns>True при успешном завершении, иначе false.</returns>
    public async Task<ProcessResult> RunAsync(
        string inputPath,
        string outputPath,
        string bitrate,
        string outputFormat = "ddp",
        int downmixChannels = 2,
        string drcProfile = "film_standard",
        int dialnorm = -31,
        Action<double>? onProgress = null,
        CancellationToken cancellationToken = default,
        ProcessExecutionContext? context = null)
    {
        string inputName = Path.GetFileName(inputPath);
        ProcessExecutionContext executionContext = (context ?? ProcessExecutionContext.NewOperation("dee"))
            .WithExpectedArtifact(outputPath);

        Log.Write(ProcessEventIds.Started, LogLevel.Info, LogStatus.Running, $"Запущено кодирование Dolby в формате {outputFormat.ToUpperInvariant()} для файла '{LogProps.FileName(inputName)}'", source: SourceName, context: executionContext.ToLogContext(), properties: LogProps.Create("Tool", "DEE").With("InputName", LogProps.FileName(inputName)).With("Container", outputFormat.ToUpperInvariant()));

        // Создаем изолированную временную директорию для работы
        string tempDir = Path.Combine(PathManager.GetSettingsDirectory(), "temp_dee_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
        }
        catch (Exception ex)
        {
            Log.Write(
                "dee.temp_directory_failed",
                LogLevel.Error,
                LogStatus.Failed,
                "Не удалось создать временную директорию для DEE",
                ex,
                "DeeRunner",
                executionContext.ToLogContext(),
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["ErrorCode"] = "temp-directory-failed"
                });
            return ProcessResult.NotStarted(
                executionContext,
                "temp-directory-failed",
                "Не удалось создать временную директорию для DEE",
                ex);
        }

        string tempWavPath = Path.Combine(tempDir, "input.wav");
        string tempXmlPath = Path.Combine(tempDir, "job.xml");

        try
        {
            // Шаг 1. Конвертация исходного аудио во временный PCM WAV с помощью FFmpeg
            int inputChannels = await GetInputChannelsAsync(inputPath, executionContext);

            // Если выходное число каналов не задано, наследуем количество входных
            int targetChannels = downmixChannels > 0 ? downmixChannels : inputChannels;

            // Защита от выхода за рамки ограничений Dolby
            if (targetChannels != 1 && targetChannels != 2 && targetChannels != 6 && targetChannels != 8)
            {
                targetChannels = 2; // По умолчанию стерео
            }

            Log.Write(
                ProcessEventIds.Launched,
                LogLevel.Debug,
                LogStatus.Running,
                $"Входной файл раскодируется во временный WAV, целевых каналов: {targetChannels}",
                source: SourceName,
                context: executionContext.ToLogContext(),
                properties: LogProps
                    .Create("Tool", "DEE")
                    .With("AudioChannels", targetChannels));

            var ffmpegArgs = new List<string>();
            if (targetChannels == 2)
            {
                // Даунмикс в стерео с коэффициентами HandBrake (обход rematrix_maxval)
                ffmpegArgs.AddRange(new[] { "-af", AppConstants.FFmpegAudio.StereoDownmixPanFilter, "-y" });
            }
            else
            {
                ffmpegArgs.AddRange(new[] { "-ac", targetChannels.ToString(), "-y" });
            }

            Action<ProgressInfo>? decodeProgressCallback = null;
            if (onProgress != null)
            {
                decodeProgressCallback = pInfo =>
                {
                    // Этап раскодирования FFmpeg в WAV занимает 0%..30% общего прогресса DEE
                    double val = (pInfo.Percent / 100.0) * 30.0;
                    onProgress(val);
                };
            }

            ProcessResult decodeResult = await _ffmpegRunner.RunAsync(
                inputPath,
                tempWavPath,
                extraArgs: ffmpegArgs,
                overwrite: true,
                onProgress: decodeProgressCallback,
                cancellationToken: cancellationToken,
                context: executionContext);

            if (!decodeResult.IsSuccess || !File.Exists(tempWavPath))
            {
                Dictionary<string, object?> decodeProperties = decodeResult.ToLogProperties();
                Log.Write(
                    "dee.decode_failed",
                    LogLevel.Error,
                    LogStatus.Failed,
                    "Не удалось раскодировать исходный файл во временный WAV",
                    null,
                    "DeeRunner",
                    decodeResult.Context.ToLogContext().WithProcess(decodeResult.ProcessId),
                    decodeProperties.With("ErrorCode", decodeResult.ErrorCode ?? "DEE_DECODE_FAILED"));
                return decodeResult.IsCancelled
                    ? ProcessResult.Cancelled(
                        executionContext,
                        ProcessResult.MessageCancelled,
                        decodeResult.ProcessId,
                        decodeResult.Pid,
                        decodeResult.ExitCode,
                        decodeResult.DurationMs,
                        terminationVerified: decodeResult.TerminationVerified)
                    : ProcessResult.Failed(
                        executionContext,
                        ProcessResult.ErrorArtifactMissing,
                        "Не удалось раскодировать исходный файл во временный WAV",
                        decodeResult.ProcessId,
                        decodeResult.Pid,
                        decodeResult.ExitCode,
                        decodeResult.DurationMs,
                        false,
                        decodeResult.OutputTail,
                        decodeResult.OutputTruncated,
                        decodeResult.WarningCount,
                        decodeResult.ErrorLineCount);
            }

            // Шаг 2. Генерация XML-конфигурации для Dolby Encoding Engine
            string encoderMode = outputFormat.ToLowerInvariant() == "ddp" ? "ddp" : "dd";
            if (outputFormat.ToLowerInvariant() == "ddp" && targetChannels == 8)
            {
                encoderMode = "ddp71"; // 7.1 кодирование
            }

            string xmlContent = GenerateXmlConfig(
                tempWavPath,
                outputPath,
                encoderMode,
                bitrate,
                targetChannels,
                drcProfile,
                dialnorm,
                tempDir
            );

            await File.WriteAllTextAsync(tempXmlPath, xmlContent, cancellationToken);
            Log.Write(
                "dee.config.generated",
                LogLevel.Debug,
                LogStatus.Succeeded,
                "XML-конфигурация Dolby Encoding Engine сформирована",
                source: SourceName,
                context: executionContext.ToLogContext(),
                properties: LogProps
                    .Create("Tool", "DEE")
                    .With("Container", "xml"));

            // Шаг 3. Запуск dee.exe с сгенерированным XML
            string shortXmlPath = GetShortPath(tempXmlPath, Log);
            string arguments = $"--xml \"{shortXmlPath}\"";

            string currentStep = "init";

            var result = await RunProcessAsync(
                "dee",
                arguments,
                onOutputLine: line =>
                {
                    if (line.Contains("Step: measuring", StringComparison.Ordinal))
                    {
                        currentStep = "measuring";
                    }
                    else if (line.Contains("Step: encoding", StringComparison.Ordinal))
                    {
                        currentStep = "encoding";
                    }

                    if (onProgress is null || !line.Contains("Stage progress:", StringComparison.Ordinal))
                    {
                        return;
                    }

                    try
                    {
                        int idx = line.IndexOf("Stage progress:", StringComparison.Ordinal);
                        string part = line.Substring(idx + "Stage progress:".Length).Trim();
                        int commaIdx = part.IndexOf(',');
                        if (commaIdx != -1)
                        {
                            part = part.Substring(0, commaIdx).Trim();
                        }

                        if (double.TryParse(part, System.Globalization.CultureInfo.InvariantCulture, out double stageProgress))
                        {
                            double progressVal;
                            if (currentStep == "measuring")
                            {
                                double norm = (stageProgress - 25.0) / 75.0;
                                if (norm < 0) norm = 0;
                                if (norm > 1) norm = 1;
                                progressVal = 30.0 + (norm * 10.0);
                            }
                            else if (currentStep == "encoding")
                            {
                                double norm = (stageProgress - 25.0) / 75.0;
                                if (norm < 0) norm = 0;
                                if (norm > 1) norm = 1;
                                progressVal = 40.0 + (norm * 60.0);
                            }
                            else
                            {
                                progressVal = 30.0 + (stageProgress * 0.05); // В начале от 30% до 31.25%
                            }

                            onProgress(progressVal);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Write(
                            "dee.progress_failed",
                            LogLevel.Debug,
                            LogStatus.Failed,
                            "Ошибка расчёта прогресса DEE по строке вывода",
                            ex,
                            "DeeRunner",
                            executionContext.ToLogContext(),
                            new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["Stage"] = "progress"
                            });
                    }
                },
                onErrorLine: null,
                cancellationToken: cancellationToken,
                context: executionContext);

            if (!result.IsSuccess)
            {
                if (!result.IsCancelled)
                {
                    Dictionary<string, object?> encodeProperties = result.ToLogProperties();
                    Log.Write(
                        "dee.encode_failed",
                        LogLevel.Error,
                        LogStatus.Failed,
                        $"Ошибка при работе Dolby Encoding Engine (код: {result.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "н/д"})",
                        null,
                        "DeeRunner",
                        result.Context.ToLogContext().WithProcess(result.ProcessId),
                        encodeProperties.With("ErrorCode", result.ErrorCode ?? "DEE_ENCODE_FAILED"));
                }

                return result;
            }

            // Dolby Encoding Engine генерирует файл в выходную папку с оригинальным именем WAV и расширением .ac3/.ec3
            // Нам нужно переименовать и перенести его по целевому пути outputPath
            string expectedExt = outputFormat.ToLowerInvariant() == "dd" ? ".ac3" : ".ec3";
            string generatedFile = Path.Combine(Path.GetDirectoryName(outputPath) ?? tempDir, Path.GetFileNameWithoutExtension(tempWavPath) + expectedExt);

            // Если .ec3 не найден, проверяем альтернативное расширение .eac3
            if (!File.Exists(generatedFile) && expectedExt == ".ec3")
            {
                generatedFile = Path.Combine(Path.GetDirectoryName(outputPath) ?? tempDir, Path.GetFileNameWithoutExtension(tempWavPath) + ".eac3");
            }

            if (File.Exists(generatedFile))
            {
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }

                File.Move(generatedFile, outputPath);
                Log.Write(
                ProcessEventIds.Exit,
                LogLevel.Info,
                LogStatus.Succeeded,
                $"Кодирование Dolby завершено, результат: '{LogProps.FileName(outputPath)}'",
                source: SourceName,
                context: executionContext.ToLogContext(),
                properties: LogProps
                    .Create("Tool", "DEE")
                    .With("OutputName", LogProps.FileName(outputPath))
                    .With("ArtifactVerified", true));

                return result with
                {
                    OutputExists = true
                };
            }

            Log.Write(
                ProcessEventIds.ArtifactMissing,
                LogLevel.Error,
                LogStatus.Failed,
                "Завершено без ошибок, но выходной файл не был найден на диске",
                null,
                "DeeRunner",
                result.Context.ToLogContext().WithProcess(result.ProcessId),
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["ErrorCode"] = ProcessResult.ErrorArtifactMissing,
                    ["OutputExists"] = false
                });

            return result with
            {
                Status = KTools_App.Diagnostics.LogStatus.Failed,
                OutputExists = false,
                ErrorCode = ProcessResult.ErrorArtifactMissing
            };
        }
        catch (OperationCanceledException)
        {
            return ProcessResult.Cancelled(
                executionContext,
                ProcessResult.MessageCancelled,
                terminationVerified: false);
        }
        catch (Exception ex)
        {
            Log.Write(
                "dee.critical_failure",
                LogLevel.Error,
                LogStatus.Failed,
                "Критический сбой при обработке Dolby аудио",
                ex,
                "DeeRunner",
                executionContext.ToLogContext(),
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["ErrorCode"] = "dee-critical-failure"
                });
            return ProcessResult.Failed(
                executionContext,
                "dee-critical-failure",
                "Критический сбой при обработке Dolby аудио",
                exception: ex);
        }
        finally
        {
            // Очищаем временную папку
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch (Exception ex)
            {
                Log.Write(
                    "dee.cleanup_failed",
                    LogLevel.Warning,
                    LogStatus.PartiallySucceeded,
                    "Не удалось удалить временную папку DEE",
                    ex,
                    "DeeRunner",
                    executionContext.ToLogContext(),
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Stage"] = "cleanup",
                        ["CleanupState"] = "failed"
                    });
            }
        }
    }

    private async Task<int> GetInputChannelsAsync(string filePath, ProcessExecutionContext? context)
    {
        try
        {
            var info = await _ffmpegRunner.GetVideoInfoAsync(filePath, context);
            if (info != null && info.RootElement.TryGetProperty("streams", out var streamsProp))
            {
                foreach (var stream in streamsProp.EnumerateArray())
                {
                    if (stream.TryGetProperty("codec_type", out var typeProp) &&
                        typeProp.GetString() == "audio" &&
                        stream.TryGetProperty("channels", out var channelsProp))
                    {
                        return channelsProp.GetInt32();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write(
                "dee.probe_failed",
                LogLevel.Debug,
                LogStatus.Skipped,
                "Не удалось определить количество аудиоканалов, используется стерео по умолчанию",
                ex,
                "DeeRunner",
                context?.ToLogContext(),
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = "probe",
                    ["InputName"] = Path.GetFileName(filePath)
                });
        }

        return 2;
    }

    private string GenerateXmlConfig(
        string wavPath,
        string outPath,
        string encoderMode,
        string bitrate,
        int channels,
        string drcProfile,
        int dialnorm,
        string tempDir)
    {
        string downmixConfig = channels switch
        {
            1 => "mono",
            2 => "stereo",
            6 => "5.1",
            _ => "off"
        };

        string shortTempDir = GetShortPath(tempDir, Log);
        string shortWavDir = GetShortPath(Path.GetDirectoryName(wavPath) ?? string.Empty, Log);
        string shortWavName = Path.GetFileName(wavPath);
        string shortOutDir = GetShortPath(Path.GetDirectoryName(outPath) ?? string.Empty, Log);
        string shortOutName = Path.GetFileNameWithoutExtension(wavPath) + (encoderMode == "dd" ? ".ac3" : ".ec3");

        return $@"<?xml version=""1.0""?>
<job_config>
  <input>
    <audio>
      <wav version=""1"">
        <file_name>""{shortWavName}""</file_name>
        <timecode_frame_rate>not_indicated</timecode_frame_rate>
        <offset>auto</offset>
        <ffoa>auto</ffoa>
        <storage>
          <local>
            <path>""{shortWavDir}""</path>
          </local>
        </storage>
      </wav>
    </audio>
  </input>
  <filter>
    <audio>
      <pcm_to_ddp version=""3"">
        <loudness>
          <measure_only>
            <metering_mode>1770-3</metering_mode>
            <dialogue_intelligence>true</dialogue_intelligence>
            <speech_threshold>20</speech_threshold>
          </measure_only>
        </loudness>
        <encoder_mode>{encoderMode}</encoder_mode>
        <bitstream_mode>complete_main</bitstream_mode>
        <downmix_config>{downmixConfig}</downmix_config>
        <data_rate>{bitrate}</data_rate>
        <timecode_frame_rate>not_indicated</timecode_frame_rate>
        <start>00:00:00.0</start>
        <end>end_of_file</end>
        <time_base>file_position</time_base>
        <prepend_silence_duration>0.0</prepend_silence_duration>
        <append_silence_duration>0.0</append_silence_duration>
        <lfe_on>true</lfe_on>
        <dolby_surround_mode>not_indicated</dolby_surround_mode>
        <dolby_surround_ex_mode>no</dolby_surround_ex_mode>
        <user_data>-1</user_data>
        <drc>
          <line_mode_drc_profile>{drcProfile}</line_mode_drc_profile>
          <rf_mode_drc_profile>{drcProfile}</rf_mode_drc_profile>
        </drc>
        <custom_dialnorm>{dialnorm}</custom_dialnorm>
        <lfe_lowpass_filter>true</lfe_lowpass_filter>
        <surround_90_degree_phase_shift>false</surround_90_degree_phase_shift>
        <surround_3db_attenuation>false</surround_3db_attenuation>
        <downmix>
          <loro_center_mix_level>-3</loro_center_mix_level>
          <loro_surround_mix_level>-3</loro_surround_mix_level>
          <ltrt_center_mix_level>-3</ltrt_center_mix_level>
          <ltrt_surround_mix_level>-3</ltrt_surround_mix_level>
          <preferred_downmix_mode>loro</preferred_downmix_mode>
        </downmix>
        <allow_hybrid_downmix>false</allow_hybrid_downmix>
        <embedded_timecodes>
          <starting_timecode>off</starting_timecode>
          <frame_rate>auto</frame_rate>
        </embedded_timecodes>
      </pcm_to_ddp>
    </audio>
  </filter>
  <output>
    <ec3 version=""1"">
      <file_name>""{shortOutName}""</file_name>
      <storage>
        <local>
          <path>""{shortOutDir}""</path>
        </local>
      </storage>
    </ec3>
  </output>
  <misc>
    <temp_dir>
      <clean_temp>true</clean_temp>
      <path>""{shortTempDir}""</path>
    </temp_dir>
  </misc>
</job_config>";
    }
}
