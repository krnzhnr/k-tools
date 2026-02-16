import pytest
from pathlib import Path
from unittest.mock import MagicMock
from app.scripts.muxer import MuxerScript

def test_muxer_grouping_and_execution(mocker, temp_dir):
    script = MuxerScript()
    
    # Mock runner
    mocker.patch.object(script._runner, 'run', return_value=True)
    
    # Create dummy files
    video = temp_dir / "movie.mkv"
    audio = temp_dir / "movie.ac3"
    subs = temp_dir / "movie.srt"
    
    files = [video, audio, subs]
    
    # Execute
    settings = {
        "subs_title": "Rus",
        "clean_tracks": True
    }
    
    # Mock existence checks inside execute (if any) - MuxerScript checks output_path.exists()
    # verify output path doesn't exist
    mocker.patch("pathlib.Path.exists", return_value=False)
    mocker.patch("pathlib.Path.mkdir") # Mock mkdir
    
    results = script.execute(files, settings)
    
    assert len(results) == 1
    assert "Собрано" in results[0]
    
    # Verify runner was called
    script._runner.run.assert_called_once()
    
    # Verify arguments passed to runner
    call_args = script._runner.run.call_args[1]
    output_path = call_args["output_path"]
    inputs = call_args["inputs"]
    title = call_args["title"]
    
    assert output_path.name == "movie.mkv"
    assert title == "movie"
    assert len(inputs) == 3 # Video + Audio + Subs
    
    # Check video args (clean_tracks=True)
    video_input = next(i for i in inputs if i["path"] == video)
    assert "--no-audio" in video_input["args"]
    assert "--no-subtitles" in video_input["args"]
    
    # Check subs args
    subs_input = next(i for i in inputs if i["path"] == subs)
    assert "--track-name" in subs_input["args"]
    assert "0:Rus" in subs_input["args"]

def test_muxer_skip_existing(mocker, temp_dir):
    script = MuxerScript()
    mocker.patch.object(script._runner, 'run', return_value=True)
    
    video = temp_dir / "movie.mkv"
    files = [video]
    
    # Mock existence to True for output file
    # We need to be careful because execute checks file_path.exists too? 
    # No, execute iterates provided files.
    # It checks output_path.exists().
    
    mocker.patch("pathlib.Path.exists", return_value=True) # Output exists
    mocker.patch("pathlib.Path.mkdir")
    
    # Принудительно выключаем перезапись для теста пропуска
    mocker.patch("app.core.settings_manager.SettingsManager.overwrite_existing", new_callable=mocker.PropertyMock, return_value=False)
    
    results = script.execute(files, {})
    
    assert len(results) == 1
    assert "Пропущен" in results[0]
    script._runner.run.assert_not_called()
