# -*- coding: utf-8 -*-
"""Тесты генерации команд FFmpeg для видео-процессора."""

import pytest
from unittest.mock import MagicMock, patch

from app.scripts.video_processor import VideoProcessorScript


@pytest.fixture
def vp():
    """Фикстура: экземпляр VideoProcessorScript с замоканным NVENC."""
    with patch.object(
        VideoProcessorScript, "__init__", lambda self: None
    ):
        script = VideoProcessorScript()
        script._ffmpeg = MagicMock()
        script._resolver = MagicMock()
        script._nvenc_available = True
    return script


# ─────────────────────────────────────────────
#  Хелперы
# ─────────────────────────────────────────────

def _build_video_args(vp, settings: dict) -> list[str]:
    """Собрать только видео-аргументы."""
    args: list[str] = []
    vp._append_video_args(args, settings)
    return args


def _build_audio_args(vp, settings: dict) -> list[str]:
    """Собрать только аудио-аргументы."""
    args: list[str] = []
    vp._append_audio_args(args, settings)
    return args


# ═════════════════════════════════════════════
#  NVENC: базовые режимы
# ═════════════════════════════════════════════

class TestNvencBasic:
    """Базовые комбинации NVENC."""

    def test_nvenc_vbr_hq_default(self, vp):
        """VBR_HQ с дефолтным пресетом и битрейтом."""
        args = _build_video_args(vp, {
            "encoder": "NVENC (GPU)",
            "nvenc_preset": "p7",
            "nvenc_rc": "vbr_hq",
            "v_bitrate": 4000,
        })
        assert "-c:v" in args
        assert "hevc_nvenc" in args
        assert args[args.index("-preset") + 1] == "p7"
        assert args[args.index("-rc") + 1] == "vbr_hq"
        assert "-b:v" in args
        assert "4000k" in args

    @pytest.mark.parametrize("rc", ["cbr", "vbr", "vbr_hq"])
    def test_nvenc_bitrate_modes(self, vp, rc):
        """Все режимы с битрейтом содержат -b:v, -minrate, -maxrate."""
        args = _build_video_args(vp, {
            "encoder": "NVENC (GPU)",
            "nvenc_rc": rc,
            "v_bitrate": 6000,
        })
        assert args[args.index("-rc") + 1] == rc
        assert "6000k" in args
        assert "-minrate" in args
        assert "-maxrate" in args
        assert "-bufsize" in args

    def test_nvenc_constqp(self, vp):
        """Режим constqp использует -qp вместо -b:v."""
        args = _build_video_args(vp, {
            "encoder": "NVENC (GPU)",
            "nvenc_rc": "constqp",
            "v_qp": 18,
        })
        assert args[args.index("-rc") + 1] == "constqp"
        assert args[args.index("-qp") + 1] == "18"
        assert "-b:v" not in args

    def test_nvenc_constqp_zero_fallback(self, vp):
        """QP=0 в constqp → fallback на 23."""
        args = _build_video_args(vp, {
            "encoder": "NVENC (GPU)",
            "nvenc_rc": "constqp",
            "v_qp": 0,
        })
        assert args[args.index("-qp") + 1] == "23"

    @pytest.mark.parametrize("preset", ["p1", "p4", "p7"])
    def test_nvenc_presets(self, vp, preset):
        """Различные пресеты NVENC."""
        args = _build_video_args(vp, {
            "encoder": "NVENC (GPU)",
            "nvenc_preset": preset,
            "nvenc_rc": "vbr_hq",
            "v_bitrate": 4000,
        })
        assert args[args.index("-preset") + 1] == preset


# ═════════════════════════════════════════════
#  NVENC: Lossless
# ═════════════════════════════════════════════

class TestNvencLossless:
    """Режим Lossless для NVENC."""

    def test_nvenc_lossless(self, vp):
        """Lossless: -rc constqp -tune lossless, без -b:v."""
        args = _build_video_args(vp, {
            "encoder": "NVENC (GPU)",
            "lossless": True,
            "v_qp": 0,
        })
        assert "-tune" in args
        assert "lossless" in args
        assert args[args.index("-rc") + 1] == "constqp"
        assert "-b:v" not in args

    def test_nvenc_lossless_ignores_rc(self, vp):
        """Lossless игнорирует nvenc_rc и v_bitrate."""
        args = _build_video_args(vp, {
            "encoder": "NVENC (GPU)",
            "lossless": True,
            "nvenc_rc": "vbr_hq",
            "v_bitrate": 8000,
        })
        assert "lossless" in args
        assert "8000k" not in args


# ═════════════════════════════════════════════
#  NVENC: расширенные флаги
# ═════════════════════════════════════════════

class TestNvencAdvanced:
    """Расширенные параметры NVENC."""

    def test_nvenc_lookahead(self, vp):
        """Lookahead передаётся как -rc-lookahead."""
        args = _build_video_args(vp, {
            "encoder": "NVENC (GPU)",
            "nvenc_rc": "vbr_hq",
            "v_bitrate": 4000,
            "nv_lookahead": "32",
        })
        assert args[args.index("-rc-lookahead") + 1] == "32"

    def test_nvenc_lookahead_off(self, vp):
        """Lookahead=Выкл → без -rc-lookahead."""
        args = _build_video_args(vp, {
            "encoder": "NVENC (GPU)",
            "nvenc_rc": "vbr_hq",
            "v_bitrate": 4000,
            "nv_lookahead": "Выкл",
        })
        assert "-rc-lookahead" not in args

    def test_nvenc_spatial_aq(self, vp):
        """Spatial AQ включён → -spatial-aq 1."""
        args = _build_video_args(vp, {
            "encoder": "NVENC (GPU)",
            "nvenc_rc": "vbr_hq",
            "v_bitrate": 4000,
            "nv_aq": True,
        })
        assert "-spatial-aq" in args
        assert "1" in args[args.index("-spatial-aq") + 1]

    def test_nvenc_spatial_aq_off(self, vp):
        """Spatial AQ выключен → без -spatial-aq."""
        args = _build_video_args(vp, {
            "encoder": "NVENC (GPU)",
            "nvenc_rc": "vbr_hq",
            "v_bitrate": 4000,
            "nv_aq": False,
        })
        assert "-spatial-aq" not in args

    def test_nvenc_lossless_no_advanced(self, vp):
        """Lossless не добавляет lookahead/aq."""
        args = _build_video_args(vp, {
            "encoder": "NVENC (GPU)",
            "lossless": True,
            "nv_lookahead": "32",
            "nv_aq": True,
        })
        assert "-rc-lookahead" not in args
        assert "-spatial-aq" not in args


# ═════════════════════════════════════════════
#  x265: CRF
# ═════════════════════════════════════════════

class TestCpuCrf:
    """Режим CRF для x265."""

    def test_cpu_crf_default(self, vp):
        """CRF по умолчанию = 23."""
        args = _build_video_args(vp, {
            "encoder": "x265 (CPU)",
            "cpu_preset": "medium",
            "cpu_rc": "CRF",
            "cpu_crf": 23,
        })
        assert "libx265" in args
        assert args[args.index("-preset") + 1] == "medium"
        assert args[args.index("-crf") + 1] == "23"
        assert "-b:v" not in args

    @pytest.mark.parametrize("crf", [0, 15, 28, 51])
    def test_cpu_crf_values(self, vp, crf):
        """Различные значения CRF."""
        args = _build_video_args(vp, {
            "encoder": "x265 (CPU)",
            "cpu_rc": "CRF",
            "cpu_crf": crf,
        })
        assert args[args.index("-crf") + 1] == str(crf)

    @pytest.mark.parametrize(
        "preset",
        ["ultrafast", "fast", "medium", "slow", "veryslow"],
    )
    def test_cpu_presets(self, vp, preset):
        """Все пресеты CPU."""
        args = _build_video_args(vp, {
            "encoder": "x265 (CPU)",
            "cpu_preset": preset,
            "cpu_rc": "CRF",
            "cpu_crf": 23,
        })
        assert args[args.index("-preset") + 1] == preset


# ═════════════════════════════════════════════
#  x265: ABR (битрейт)
# ═════════════════════════════════════════════

class TestCpuAbr:
    """Режим ABR для x265."""

    def test_cpu_abr(self, vp):
        """ABR: -b:v, -maxrate, -bufsize, без -crf."""
        args = _build_video_args(vp, {
            "encoder": "x265 (CPU)",
            "cpu_rc": "Битрейт (ABR)",
            "cpu_v_bitrate": 5000,
        })
        assert "-crf" not in args
        assert "5000k" in args
        assert "-maxrate" in args
        assert "10000k" in args  # max_br = 5000 * 2
        assert "-bufsize" in args

    def test_cpu_abr_fallback(self, vp):
        """ABR с невалидным битрейтом → fallback 4000."""
        args = _build_video_args(vp, {
            "encoder": "x265 (CPU)",
            "cpu_rc": "Битрейт (ABR)",
            "cpu_v_bitrate": "abc",
        })
        assert "4000k" in args


# ═════════════════════════════════════════════
#  x265: Lossless
# ═════════════════════════════════════════════

class TestCpuLossless:
    """Режим Lossless для x265."""

    def test_cpu_lossless(self, vp):
        """Lossless: -x265-params lossless=1, без -crf и -b:v."""
        args = _build_video_args(vp, {
            "encoder": "x265 (CPU)",
            "lossless": True,
        })
        assert "-crf" not in args
        assert "-b:v" not in args
        x265_idx = args.index("-x265-params")
        params = args[x265_idx + 1]
        assert "lossless=1" in params

    def test_cpu_lossless_ignores_crf(self, vp):
        """Lossless игнорирует cpu_rc и cpu_crf."""
        args = _build_video_args(vp, {
            "encoder": "x265 (CPU)",
            "lossless": True,
            "cpu_rc": "CRF",
            "cpu_crf": 18,
        })
        assert "-crf" not in args


# ═════════════════════════════════════════════
#  x265: расширенные параметры
# ═════════════════════════════════════════════

class TestCpuAdvanced:
    """Расширенные параметры x265."""

    def test_cpu_tune_grain(self, vp):
        """Tune grain → -tune grain."""
        args = _build_video_args(vp, {
            "encoder": "x265 (CPU)",
            "cpu_rc": "CRF",
            "cpu_crf": 23,
            "cpu_tune": "grain",
        })
        assert args[args.index("-tune") + 1] == "grain"

    def test_cpu_tune_none(self, vp):
        """Tune=Нет → без -tune."""
        args = _build_video_args(vp, {
            "encoder": "x265 (CPU)",
            "cpu_rc": "CRF",
            "cpu_crf": 23,
            "cpu_tune": "Нет",
        })
        assert "-tune" not in args

    @pytest.mark.parametrize("aq", ["0", "1", "2", "3"])
    def test_cpu_aq_mode(self, vp, aq):
        """AQ Mode передаётся в x265-params."""
        args = _build_video_args(vp, {
            "encoder": "x265 (CPU)",
            "cpu_rc": "CRF",
            "cpu_crf": 23,
            "cpu_aq_mode": aq,
        })
        x265_idx = args.index("-x265-params")
        assert f"aq-mode={aq}" in args[x265_idx + 1]

    def test_cpu_lookahead(self, vp):
        """Lookahead передаётся как rc-lookahead в x265-params."""
        args = _build_video_args(vp, {
            "encoder": "x265 (CPU)",
            "cpu_rc": "CRF",
            "cpu_crf": 23,
            "cpu_lookahead": "40",
        })
        x265_idx = args.index("-x265-params")
        assert "rc-lookahead=40" in args[x265_idx + 1]

    def test_cpu_lookahead_off(self, vp):
        """Lookahead=Выкл → без rc-lookahead."""
        args = _build_video_args(vp, {
            "encoder": "x265 (CPU)",
            "cpu_rc": "CRF",
            "cpu_crf": 23,
            "cpu_lookahead": "Выкл",
        })
        x265_idx = args.index("-x265-params")
        assert "rc-lookahead" not in args[x265_idx + 1]

    def test_cpu_all_advanced_combined(self, vp):
        """Все расширенные параметры x265 вместе."""
        args = _build_video_args(vp, {
            "encoder": "x265 (CPU)",
            "cpu_preset": "slow",
            "cpu_rc": "CRF",
            "cpu_crf": 18,
            "cpu_tune": "grain",
            "cpu_aq_mode": "3",
            "cpu_lookahead": "30",
        })
        assert args[args.index("-preset") + 1] == "slow"
        assert args[args.index("-crf") + 1] == "18"
        assert args[args.index("-tune") + 1] == "grain"
        x265_params = args[args.index("-x265-params") + 1]
        assert "aq-mode=3" in x265_params
        assert "rc-lookahead=30" in x265_params


# ═════════════════════════════════════════════
#  10-бит
# ═════════════════════════════════════════════

class TestPixelFormat:
    """Тесты формата пикселей (8/10-бит)."""

    def test_nvenc_8bit(self, vp):
        """NVENC 8-бит → yuv420p."""
        args = _build_video_args(vp, {
            "encoder": "NVENC (GPU)",
            "force_10bit": False,
            "nvenc_rc": "vbr_hq",
            "v_bitrate": 4000,
        })
        assert "yuv420p" in args

    def test_nvenc_10bit(self, vp):
        """NVENC 10-бит → p010le (нативный формат)."""
        args = _build_video_args(vp, {
            "encoder": "NVENC (GPU)",
            "force_10bit": True,
            "nvenc_rc": "vbr_hq",
            "v_bitrate": 4000,
        })
        assert "p010le" in args
        assert "yuv420p10le" not in args

    def test_cpu_8bit(self, vp):
        """x265 8-бит → yuv420p."""
        args = _build_video_args(vp, {
            "encoder": "x265 (CPU)",
            "force_10bit": False,
            "cpu_rc": "CRF",
            "cpu_crf": 23,
        })
        assert "yuv420p" in args

    def test_cpu_10bit(self, vp):
        """x265 10-бит → yuv420p10le."""
        args = _build_video_args(vp, {
            "encoder": "x265 (CPU)",
            "force_10bit": True,
            "cpu_rc": "CRF",
            "cpu_crf": 23,
        })
        assert "yuv420p10le" in args


# ═════════════════════════════════════════════
#  Аудио
# ═════════════════════════════════════════════

class TestAudio:
    """Параметры аудио."""

    def test_audio_copy(self, vp):
        """Копирование аудио без перекодирования."""
        args = _build_audio_args(vp, {"audio_codec": "copy"})
        assert args == ["-c:a", "copy"]

    @pytest.mark.parametrize("codec", ["aac", "ac3", "flac"])
    def test_audio_encode(self, vp, codec):
        """Перекодирование аудио с битрейтом."""
        args = _build_audio_args(vp, {
            "audio_codec": codec,
            "audio_bitrate": "256k",
            "audio_channels": "Original",
        })
        assert args[1] == codec
        assert "-b:a" in args
        assert "256k" in args
        assert "-ac" not in args

    @pytest.mark.parametrize("channels", ["1", "2", "6"])
    def test_audio_channels(self, vp, channels):
        """Каналы аудио."""
        args = _build_audio_args(vp, {
            "audio_codec": "aac",
            "audio_bitrate": "320k",
            "audio_channels": channels,
        })
        assert args[args.index("-ac") + 1] == channels

    def test_audio_channels_original(self, vp):
        """Channels=Original → без -ac."""
        args = _build_audio_args(vp, {
            "audio_codec": "aac",
            "audio_bitrate": "320k",
            "audio_channels": "Original",
        })
        assert "-ac" not in args


# ═════════════════════════════════════════════
#  Комплексные комбинации
# ═════════════════════════════════════════════

class TestFullCombinations:
    """Комплексные тесты — видео + аудио вместе."""

    def test_nvenc_vbr_aac_10bit(self, vp):
        """NVENC VBR_HQ + AAC 320k + 10-бит."""
        settings = {
            "encoder": "NVENC (GPU)",
            "nvenc_preset": "p7",
            "nvenc_rc": "vbr_hq",
            "v_bitrate": 8000,
            "force_10bit": True,
            "nv_lookahead": "32",
            "nv_aq": True,
            "audio_codec": "aac",
            "audio_bitrate": "320k",
            "audio_channels": "2",
        }
        v = _build_video_args(vp, settings)
        a = _build_audio_args(vp, settings)

        assert "hevc_nvenc" in v
        assert "p010le" in v
        assert "8000k" in v
        assert "-rc-lookahead" in v
        assert "-spatial-aq" in v

        assert "aac" in a
        assert "320k" in a
        assert a[a.index("-ac") + 1] == "2"

    def test_cpu_crf_flac_grain(self, vp):
        """x265 CRF + FLAC + grain + lookahead."""
        settings = {
            "encoder": "x265 (CPU)",
            "cpu_preset": "slow",
            "cpu_rc": "CRF",
            "cpu_crf": 18,
            "force_10bit": True,
            "cpu_tune": "grain",
            "cpu_aq_mode": "3",
            "cpu_lookahead": "40",
            "audio_codec": "flac",
            "audio_bitrate": "320k",
            "audio_channels": "6",
        }
        v = _build_video_args(vp, settings)
        a = _build_audio_args(vp, settings)

        assert "libx265" in v
        assert "yuv420p10le" in v
        assert v[v.index("-crf") + 1] == "18"
        assert v[v.index("-tune") + 1] == "grain"

        x265_params = v[v.index("-x265-params") + 1]
        assert "aq-mode=3" in x265_params
        assert "rc-lookahead=40" in x265_params

        assert "flac" in a
        assert a[a.index("-ac") + 1] == "6"

    def test_cpu_abr_copy_audio(self, vp):
        """x265 ABR + копирование аудио."""
        settings = {
            "encoder": "x265 (CPU)",
            "cpu_rc": "Битрейт (ABR)",
            "cpu_v_bitrate": 3000,
            "audio_codec": "copy",
        }
        v = _build_video_args(vp, settings)
        a = _build_audio_args(vp, settings)

        assert "-crf" not in v
        assert "3000k" in v
        assert "-maxrate" in v
        assert a == ["-c:a", "copy"]

    def test_nvenc_lossless_copy(self, vp):
        """NVENC Lossless + копирование аудио."""
        settings = {
            "encoder": "NVENC (GPU)",
            "lossless": True,
            "audio_codec": "copy",
        }
        v = _build_video_args(vp, settings)
        a = _build_audio_args(vp, settings)

        assert "lossless" in v
        assert "-b:v" not in v
        assert a == ["-c:a", "copy"]

    def test_cpu_lossless_ac3(self, vp):
        """x265 Lossless + AC3 448k."""
        settings = {
            "encoder": "x265 (CPU)",
            "lossless": True,
            "audio_codec": "ac3",
            "audio_bitrate": "448k",
            "audio_channels": "6",
        }
        v = _build_video_args(vp, settings)
        a = _build_audio_args(vp, settings)

        x265_params = v[v.index("-x265-params") + 1]
        assert "lossless=1" in x265_params
        assert "-crf" not in v

        assert "ac3" in a
        assert "448k" in a
        assert a[a.index("-ac") + 1] == "6"


# ═════════════════════════════════════════════
#  Граничные случаи
# ═════════════════════════════════════════════

class TestEdgeCases:
    """Граничные и fallback-сценарии."""

    def test_missing_encoder_defaults_cpu(self, vp):
        """Без указания энкодера → x265."""
        args = _build_video_args(vp, {})
        assert "libx265" in args

    def test_invalid_crf_fallback(self, vp):
        """Невалидный CRF → fallback 23."""
        args = _build_video_args(vp, {
            "encoder": "x265 (CPU)",
            "cpu_rc": "CRF",
            "cpu_crf": "abc",
        })
        assert args[args.index("-crf") + 1] == "23"

    def test_invalid_nvenc_bitrate_fallback(self, vp):
        """Невалидный битрейт NVENC → fallback 4000."""
        args = _build_video_args(vp, {
            "encoder": "NVENC (GPU)",
            "nvenc_rc": "vbr_hq",
            "v_bitrate": "xyz",
        })
        assert "4000k" in args

    def test_missing_audio_codec(self, vp):
        """Без указания аудио-кодека → copy."""
        args = _build_audio_args(vp, {})
        assert args == ["-c:a", "copy"]

    def test_x265_params_colon_joined(self, vp):
        """x265-params собираются через двоеточие."""
        args = _build_video_args(vp, {
            "encoder": "x265 (CPU)",
            "cpu_rc": "CRF",
            "cpu_crf": 20,
            "cpu_aq_mode": "3",
            "cpu_lookahead": "30",
        })
        x265_params = args[args.index("-x265-params") + 1]
        parts = x265_params.split(":")
        assert len(parts) >= 2
        assert any("aq-mode" in p for p in parts)
        assert any("rc-lookahead" in p for p in parts)
