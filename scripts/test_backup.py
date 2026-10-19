#!/usr/bin/env python3
"""Run with python3 scripts/test_backup.py; no .NET process or live state is used."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
from unittest.mock import patch

import backup


def snapshot(path):
    path.mkdir()
    (path / "keys").mkdir()
    (path / "panelhaven.sqlite").write_bytes(b"test snapshot")
    (path / "keys" / "key.xml").write_text("<key />")


def successful(path, number):
    container = path / f"scheduled-20000101T000000000000Z-{number:032x}"
    container.mkdir()
    snapshot(container / "snapshot")
    (container / ".complete").write_text(backup.MARKER)
    return container


def check():
    old_umask = os.umask(0o077)
    try:
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory)
            root, data, app = base / "backups", base / "data", base / "app"
            root.mkdir(mode=0o700)
            data.mkdir()
            app.mkdir()
            dotnet, dll = app / "dotnet", app / "PanelHaven.Web.dll"
            dotnet.touch()
            dll.touch()
            settings = base / "settings.json"

            def configure(**changes):
                values = dict(enabled=True, destination=str(root), time="03:00", keep=2)
                values.update(changes)
                settings.write_text(json.dumps({"settings": {
                    "dataPath": str(data), "libraries": [], "backup": values}}))

            configure()
            oldest, newest = successful(root, 1), successful(root, 2)
            manual = root / "manual"
            snapshot(manual)
            unmarked = successful(root, 3)
            (unmarked / ".complete").unlink()
            linked = successful(root, 4)
            (linked / "snapshot" / "keys" / "key.xml").unlink()
            (linked / "snapshot" / "keys" / "outside.xml").symlink_to(manual / "panelhaven.sqlite")
            symlink = root / "scheduled-20000101T000000000000Z-00000000000000000000000000000005"
            symlink.symlink_to(manual, target_is_directory=True)
            arguments = ["backup.py", "--settings", str(settings), "--runtime", str(dotnet), "--assembly", str(dll)]
            with patch.object(backup.sys, "argv", arguments):
                with patch.object(backup.subprocess, "run", side_effect=subprocess.CalledProcessError(1, "backup")):
                    try:
                        backup.main()
                    except subprocess.CalledProcessError:
                        pass
                    else:
                        raise AssertionError("Failed CLI must fail the job")
                assert oldest.exists() and newest.exists(), "Failure must preserve old backups"

                def publish(command, *, check):
                    assert command[:4] == [str(dotnet), str(dll), "--DataPath", str(data)]
                    snapshot(Path(command[5]))

                with patch.object(backup.subprocess, "run", side_effect=publish):
                    backup.main()
                    assert not oldest.exists() and newest.exists()
                    assert len([p for p in root.iterdir() if backup.owned_backup(p)]) == 2
                    configure(keep=1)
                    backup.main()
                    assert not newest.exists()
                    assert len([p for p in root.iterdir() if backup.owned_backup(p)]) == 1
                assert manual.is_dir() and unmarked.is_dir() and linked.is_dir() and symlink.is_symlink()
                assert (manual / "panelhaven.sqlite").read_bytes() == b"test snapshot"
                for changes in ({"keep": 0}, {"keep": True}, {"keep": 3651},
                                {"destination": str(data)}, {"destination": "relative"},
                                {"destination": str(symlink)}, {"destination": str(app / "backups")}):
                    configure(**changes)
                    with patch.object(backup.subprocess, "run") as run:
                        try:
                            backup.main()
                        except ValueError:
                            pass
                        else:
                            raise AssertionError("Unsafe configuration was accepted")
                        run.assert_not_called()
                configure(enabled=False)
                with patch.object(backup.subprocess, "run") as run:
                    backup.main()
                    run.assert_not_called()
                configure()
                settings.chmod(0o644)
                try:
                    backup.main()
                except ValueError:
                    pass
                else:
                    raise AssertionError("Public settings were accepted")
    finally:
        os.umask(old_umask)
    print("Scheduled backup checks passed")


if __name__ == "__main__":
    check()
