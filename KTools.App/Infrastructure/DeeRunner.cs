// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Core;

namespace KTools_App.Infrastructure;

/// <summary>
/// Синглтон-обертка для прямого запуска Dolby Encoding Engine (dee.exe) без использования Python-модуля deew.
/// Выполняет подготовку промежуточного аудио через FFmpeg, генерацию XML-конфигурации для DEE и запуск кодировщика.
/// Все комментарии и логирование выполнены строго на русском языке в соответствии с регламентом.
/// </summary>
public sealed class DeeRunner : AbstractProcessRunner
{
    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);

    /// <summary>
    /// Возвращает короткий путь в формате 8.3 для операционной системы Windows.
    /// Это необходимо, так как Dolby Encoding Engine не поддерживает пробелы и кириллицу в путях.
    /// </summary>
    /// <param name="path">Исходный длинный путь.</param>
    /// <returns>Короткий путь или исходный путь при невозможности конвертации.</returns>
    private static string GetShortPath(string path)
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
                string shortPath = sb.ToString();
                LogService.Instance.DebugLog($"Путь преобразован в формат 8.3: '{path}' -> '{shortPath}'", "DeeRunner");
                return shortPath;
            }

            if (result > sb.Capacity)
            {
                sb.EnsureCapacity((int)result);
                result = GetShortPathName(path, sb, result);
                if (result > 0)
                {
                    string shortPath = sb.ToString();
                    LogService.Instance.DebugLog($"Путь преобразован в формат 8.3 с увеличенным буфером: '{path}' -> '{shortPath}'", "DeeRunner");
                    return shortPath;
                }
            }

            LogService.Instance.Warn($"Не удалось получить короткий путь для '{path}'. Код ошибки: {Marshal.GetLastWin32Error()}", "DeeRunner");
        }
        catch (Exception ex)
        {
            LogService.Instance.Exception(ex, $"Исключение при получении короткого пути для '{path}'", "DeeRunner");
        }

        return path;
    }

    private static readonly Lazy<DeeRunner> LazyInstance =
        new(() => new DeeRunner());

    private DeeRunner() { }

    /// <summary>
    /// Возвращает единственный экземпляр класса DeeRunner.
    /// </summary>
    public static DeeRunner Instance => LazyInstance.Value;

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
    public async Task<bool> RunAsync(
        string inputPath,
        string outputPath,
        string bitrate,
        string outputFormat = "ddp",
        int downmixChannels = 2,
        string drcProfile = "film_standard",
        int dialnorm = -31,
        Action<double>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        LogService.Instance.Info($"Начало кодирования Dolby ({outputFormat.ToUpper()}) для файла: '{Path.GetFileName(inputPath)}'", "DeeRunner");

        // Создаем изолированную временную директорию для работы
        string tempDir = Path.Combine(PathManager.GetSettingsDirectory(), "temp_dee_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Не удалось создать временную директорию для DEE: {ex.Message}", "DeeRunner");
            return false;
        }

        string tempWavPath = Path.Combine(tempDir, "input.wav");
        string tempXmlPath = Path.Combine(tempDir, "job.xml");

        try
        {
            // Шаг 1. Конвертация исходного аудио во временный PCM WAV с помощью FFmpeg
            int inputChannels = await GetInputChannelsAsync(inputPath);
            
            // Если выходное число каналов не задано, наследуем количество входных
            int targetChannels = downmixChannels > 0 ? downmixChannels : inputChannels;
            
            // Защита от выхода за рамки ограничений Dolby
            if (targetChannels != 1 && targetChannels != 2 && targetChannels != 6 && targetChannels != 8)
            {
                targetChannels = 2; // По умолчанию стерео
            }

            LogService.Instance.Info($"Раскодирование входного файла во временный WAV ({targetChannels} каналов)...", "DeeRunner");

            var ffmpegArgs = new List<string>
            {
                "-ac", targetChannels.ToString(),
                "-y"
            };

            bool decodeSuccess = await FFmpegRunner.Instance.RunAsync(
                inputPath,
                tempWavPath,
                extraArgs: ffmpegArgs,
                overwrite: true,
                cancellationToken: cancellationToken
            );

            if (!decodeSuccess || !File.Exists(tempWavPath))
            {
                LogService.Instance.Error("Не удалось раскодировать исходный файл во временный WAV", "DeeRunner");
                return false;
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
            LogService.Instance.DebugLog("XML-конфигурация для Dolby Encoding Engine сгенерирована", "DeeRunner");

            // Шаг 3. Запуск dee.exe с сгенерированным XML
            string shortXmlPath = GetShortPath(tempXmlPath);
            string arguments = $"--xml \"{shortXmlPath}\"";

            string currentStep = "init";

            var result = await RunProcessAsync(
                "dee",
                arguments,
                onOutputLine: line =>
                {
                    LogService.Instance.DebugLog($"[DEE STDOUT] {line}", "DeeRunner");

                    if (line.Contains("Step: measuring"))
                    {
                        currentStep = "measuring";
                    }
                    else if (line.Contains("Step: encoding"))
                    {
                        currentStep = "encoding";
                    }

                    if (onProgress != null && line.Contains("Stage progress:"))
                    {
                        try
                        {
                            int idx = line.IndexOf("Stage progress:");
                            string part = line.Substring(idx + "Stage progress:".Length).Trim();
                            int commaIdx = part.IndexOf(',');
                            if (commaIdx != -1)
                            {
                                part = part.Substring(0, commaIdx).Trim();
                            }

                            if (double.TryParse(part, System.Globalization.CultureInfo.InvariantCulture, out double stageProgress))
                            {
                                double progressVal = 0.0;
                                if (currentStep == "measuring")
                                {
                                    double norm = (stageProgress - 25.0) / 75.0;
                                    if (norm < 0) norm = 0;
                                    if (norm > 1) norm = 1;
                                    progressVal = norm * 15.0;
                                }
                                else if (currentStep == "encoding")
                                {
                                    double norm = (stageProgress - 25.0) / 75.0;
                                    if (norm < 0) norm = 0;
                                    if (norm > 1) norm = 1;
                                    progressVal = 15.0 + (norm * 85.0);
                                }
                                else
                                {
                                    progressVal = stageProgress * 0.05; // В начале от 0% до 1.25%
                                }

                                onProgress(progressVal);
                            }
                        }
                        catch (Exception ex)
                        {
                            LogService.Instance.Exception(ex, "Ошибка расчета прогресса DEE", "DeeRunner");
                        }
                    }
                },
                onErrorLine: line => LogService.Instance.DebugLog($"[DEE STDERR] {line}", "DeeRunner"),
                cancellationToken: cancellationToken
            );

            if (!result.IsSuccess)
            {
                LogService.Instance.Error($"Ошибка при работе Dolby Encoding Engine (Код: {result.ExitCode})", "DeeRunner");
                return false;
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
                LogService.Instance.Info($"Кодирование Dolby успешно завершено: '{Path.GetFileName(outputPath)}'", "DeeRunner");
                return true;
            }

            LogService.Instance.Error("Завершено без ошибок, но выходной файл не был найден на диске", "DeeRunner");
            return false;
        }
        catch (Exception ex)
        {
            LogService.Instance.Exception(ex, "Критический сбой при обработке Dolby аудио", "DeeRunner");
            return false;
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
                LogService.Instance.DebugLog($"Не удалось удалить временную папку DEE: {ex.Message}", "DeeRunner");
            }
        }
    }

    private async Task<int> GetInputChannelsAsync(string filePath)
    {
        try
        {
            var info = await FFmpegRunner.Instance.GetVideoInfoAsync(filePath);
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
        catch
        {
            // В случае ошибок возвращаем стерео
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

        string shortTempDir = GetShortPath(tempDir);
        string shortWavDir = GetShortPath(Path.GetDirectoryName(wavPath) ?? string.Empty);
        string shortWavName = Path.GetFileName(wavPath);
        string shortOutDir = GetShortPath(Path.GetDirectoryName(outPath) ?? string.Empty);
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
