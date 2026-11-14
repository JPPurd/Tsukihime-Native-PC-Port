"""The pre-commit hook refuses game data and, since v0.1.9, audio files.

The port makes its one sound of its own, the achievement chime, at run time, so no
audio file belongs in the repository; src/Tsuki.Cli/Commands/ReleaseRules.cs refuses
the same extensions in a release.
"""
from __future__ import annotations

import importlib.util
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
HOOK = REPO / "tools" / "setup" / "hooks" / "precommit_check.py"
AUDIO = [".wav", ".ogg", ".mp3", ".flac", ".opus", ".m4a", ".aac", ".wma", ".aif", ".aiff"]


@pytest.fixture
def hook():
    spec = importlib.util.spec_from_file_location("precommit_check", HOOK)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def staged(hook, monkeypatch, paths: list[str], size: int = 10) -> None:
    """The hook's git, answering as if `paths` were staged, each `size` bytes, with no added code lines."""
    def git(*args: str) -> str:
        if args[:2] == ("diff", "--cached") and "--name-only" in args:
            return "".join(p + "\n" for p in paths)
        if args[0] == "cat-file":
            return f"{size}\n"
        return ""
    monkeypatch.setattr(hook, "git", git)


@pytest.mark.parametrize("ext", AUDIO)
def test_an_audio_file_is_refused_anywhere_and_in_any_case(hook, monkeypatch, capsys, ext):
    paths = [f"tools/tests/achievement_chime{ext}", f"assets/sound/chime{ext.upper()}", f"tests/Tsuki.Tests.Data/ref{ext}"]
    staged(hook, monkeypatch, paths)
    assert hook.main() == 1
    err = capsys.readouterr().err
    for path in paths:
        assert f"{path}: game-data file type" in err


def test_the_files_the_repository_holds_still_pass(hook, monkeypatch, capsys):
    # sources, documents and the port's own art; a name that only holds an extension's letters is not that type
    staged(hook, monkeypatch, ["src/Tsuki.App/Play/AchievementChime.cs", "docs/sound.wav.md", "tools/tests/test_audio.py",
                               "assets/branding/tsukihime-logo.png", "README.md"])
    assert hook.main() == 0
    assert capsys.readouterr().err == ""


def test_the_hook_and_the_release_rules_refuse_the_same_audio_extensions(hook):
    rules = (REPO / "src" / "Tsuki.Cli" / "Commands" / "ReleaseRules.cs").read_text(encoding="utf-8")
    match = re.search(r"AudioExtensions\s*=\s*\[([^\]]*)\]", rules)
    assert match, "ReleaseRules.cs has no AudioExtensions list"
    in_rules = re.findall(r'"(\.[a-z0-9]+)"', match.group(1))
    assert sorted(in_rules) == sorted(AUDIO)
    assert set(AUDIO) <= hook.GAME_DATA_EXT


def test_a_real_commit_of_a_sound_is_refused(tmp_path):
    # the hook itself, run as git runs it, in a repository of its own with a sound and a document staged
    git = shutil.which("git")
    if git is None:
        pytest.skip("git is not on PATH")
    # no git setting of this machine or of an outer git run (a hook that runs these tests) reaches the repository of the test
    env = {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}
    env.update(GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=str(tmp_path / "gitconfig"), HOME=str(tmp_path), XDG_CONFIG_HOME=str(tmp_path))
    subprocess.run([git, "-c", "init.defaultBranch=main", "init", "-q", str(tmp_path / "repo")], check=True, env=env)
    repo = tmp_path / "repo"
    (repo / "docs").mkdir()
    (repo / "docs" / "notes.md").write_text("notes\n", encoding="utf-8")
    (repo / "tools").mkdir()
    (repo / "tools" / "achievement_chime.wav").write_bytes(b"RIFF\0\0\0\0WAVE")
    subprocess.run([git, "add", "docs/notes.md", "tools/achievement_chime.wav"], cwd=repo, check=True, env=env)
    run = subprocess.run([sys.executable, "-I", str(HOOK)], cwd=repo, env=env, capture_output=True, text=True, encoding="utf-8")
    assert run.returncode == 1
    assert "tools/achievement_chime.wav: game-data file type" in run.stderr
    assert "docs/notes.md" not in run.stderr

    # without the sound the same commit passes
    subprocess.run([git, "rm", "-q", "--cached", "tools/achievement_chime.wav"], cwd=repo, check=True, env=env)
    run = subprocess.run([sys.executable, "-I", str(HOOK)], cwd=repo, env=env, capture_output=True, text=True, encoding="utf-8")
    assert run.returncode == 0, run.stderr
