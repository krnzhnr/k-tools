# -*- coding: utf-8 -*-
from app.scripts.audio_converter import AudioConverterScript
from app.scripts.audio_dee_downmixer import AudioDeeDownmixerScript


def test_audio_converter_aac_raw(mocker, temp_dir):
    """Проверка AAC без контейнера (FFmpeg, .aac)."""
    script = AudioConverterScript()
    mocker.patch.object(script._ffmpeg, "run", return_value=True)
    mocker.patch(
        "app.core.settings_manager.SettingsManager.overwrite_existing",
        return_value=True,
    )

    file_path = temp_dir / "input.wav"
    settings = {
        "target_format": "AAC",
        "use_m4a_container": False,
        "bitrate": "192k",
    }

    script.execute([file_path], settings)

    # Должен быть вызван ffmpeg
    script._ffmpeg.run.assert_called_once()
    call_kwargs = script._ffmpeg.run.call_args.kwargs

    # Расширение должно быть .aac
    assert str(call_kwargs["output_path"]).endswith(".aac")
    # Битрейт должен быть в аргументах
    assert "-b:a" in call_kwargs["extra_args"]
    assert "192k" in call_kwargs["extra_args"]


def test_audio_converter_aac_m4a(mocker, temp_dir):
    """Проверка AAC в контейнере (FFmpeg, .m4a)."""
    script = AudioConverterScript()
    mocker.patch.object(script._ffmpeg, "run", return_value=True)

    file_path = temp_dir / "input.wav"
    settings = {"target_format": "AAC", "use_m4a_container": True}

    script.execute([file_path], settings)

    # Расширение должно быть .m4a
    call_kwargs = script._ffmpeg.run.call_args.kwargs
    assert str(call_kwargs["output_path"]).endswith(".m4a")


def test_audio_converter_qaac_m4a(mocker, temp_dir):
    """Проверка QAAC в контейнере (QaacRunner, .m4a, adts=False)."""
    script = AudioConverterScript()
    mocker.patch.object(script._qaac, "run", return_value=True)

    file_path = temp_dir / "input.wav"
    settings = {
        "target_format": "QAAC",
        "use_m4a_container": True,
        "qaac_quality": "127",
    }

    script.execute([file_path], settings)

    # Должен быть вызван QaacRunner
    script._qaac.run.assert_called_once()
    call_kwargs = script._qaac.run.call_args.kwargs

    # Расширение .m4a, adts False
    assert str(call_kwargs["output_path"]).endswith(".m4a")
    assert call_kwargs["adts"] is False
    assert call_kwargs["tvbr"] == "127"


def test_audio_converter_qaac_raw(mocker, temp_dir):
    """Проверка QAAC без контейнера (QaacRunner, .aac, adts=True)."""
    script = AudioConverterScript()
    mocker.patch.object(script._qaac, "run", return_value=True)

    file_path = temp_dir / "input.wav"
    settings = {"target_format": "QAAC", "use_m4a_container": False}

    script.execute([file_path], settings)

    call_kwargs = script._qaac.run.call_args.kwargs
    assert str(call_kwargs["output_path"]).endswith(".aac")
    assert call_kwargs["adts"] is True


def test_audio_dee_downmixer_logic(mocker, temp_dir):
    """Проверка иконки и форматов даунмикса."""
    script = AudioDeeDownmixerScript()
    assert script.icon_name == "MIX_VOLUMES"

    mocker.patch.object(script._runner, "run", return_value=True)
    file_path = temp_dir / "input.mkv"

    # Проверка DDP
    settings_ddp = {"format": "Dolby Digital Plus (E-AC3)", "bitrate": "256"}
    script.execute([file_path], settings_ddp)
    call_kwargs_ddp = script._runner.run.call_args.kwargs
    assert call_kwargs_ddp["output_format"] == "ddp"
    assert str(call_kwargs_ddp["output_path"]).endswith(".eac3")

    # Проверка DD
    settings_dd = {"format": "Dolby Digital (AC3)", "bitrate": "192"}
    script.execute([file_path], settings_dd)
    call_kwargs_dd = script._runner.run.call_args.kwargs
    assert call_kwargs_dd["output_format"] == "dd"
    assert str(call_kwargs_dd["output_path"]).endswith(".ac3")


def test_settings_schema_visibility():
    """Проверка корректности схемы настроек (список в visible_if)."""
    script = AudioConverterScript()
    schema = script.settings_schema

    qaac_quality_field = next(f for f in schema if f.key == "qaac_quality")
    # Должен быть список ["QAAC"], а не просто строка
    assert isinstance(qaac_quality_field.visible_if["target_format"], list)
    assert "QAAC" in qaac_quality_field.visible_if["target_format"]
