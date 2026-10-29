#!/usr/bin/env python3
"""Linux scheduled live backups using the private managed server-settings document."""
import argparse
import datetime
import fcntl
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys
import uuid

NAME = re.compile(r"scheduled-\d{8}T\d{12}Z-[0-9a-f]{32}")
MARKER = "Panel Haven scheduled backup v1\n"


def absolute_path(value):
    path = Path(value)
    if not path.is_absolute() or ".." in path.parts:
        raise ValueError("Paths must be absolute and must not contain '..'.")
    if any(part.is_symlink() for part in (path, *path.parents)):
        raise ValueError(f"Symlink paths are not allowed: {path}")
    return path


def regular(path):
    return stat.S_ISREG(path.lstat().st_mode)


def owned_backup(path):
    # ponytail: inspect only our fixed, flat snapshot layout; never walk arbitrary trees.
    if not NAME.fullmatch(path.name) or path.is_symlink() or not path.is_dir():
        return False
    try:
        if {p.name for p in path.iterdir()} != {".complete", "snapshot"}:
            return False
        marker, snapshot = path / ".complete", path / "snapshot"
        if not regular(marker) or marker.read_text() != MARKER:
            return False
        if snapshot.is_symlink() or not snapshot.is_dir():
            return False
        if {p.name for p in snapshot.iterdir()} != {"panelhaven.sqlite", "keys"}:
            return False
        keys = snapshot / "keys"
        return (regular(snapshot / "panelhaven.sqlite")
                and not keys.is_symlink() and keys.is_dir()
                and all(p.suffix.lower() == ".xml" and regular(p) for p in keys.iterdir()))
    except (OSError, UnicodeError):
        return False


def configuration():
    parser = argparse.ArgumentParser()
    parser.add_argument("--settings", required=True)
    parser.add_argument("--runtime", required=True)
    parser.add_argument("--assembly")
    args = parser.parse_args()
    settings_path = absolute_path(args.settings)
    with os.fdopen(os.open(settings_path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK), "r") as stream:
        info = os.fstat(stream.fileno())
        if (not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid()
                or stat.S_IMODE(info.st_mode) != 0o600 or info.st_size > 128 * 1024):
            raise ValueError("Server settings must be a private, owned regular file (0600).")
        settings = json.load(stream)["settings"]
    backup = settings["backup"]
    if type(backup["enabled"]) is not bool:
        raise ValueError("Backup enabled must be a boolean.")
    if not backup["enabled"]:
        return None
    keep = backup["keep"]
    if type(keep) is not int or not 1 <= keep <= 3650:
        raise ValueError("Backup retention must be between 1 and 3650.")
    runtime = absolute_path(args.runtime)
    command = [str(runtime)]
    application = runtime
    if args.assembly:
        application = absolute_path(args.assembly)
        command.append(str(application))
    data, root = absolute_path(settings["dataPath"]), absolute_path(backup["destination"])
    if not runtime.is_file() or not application.is_file() or not data.is_dir():
        raise ValueError("The configured runtime, application and state directory must exist.")
    excluded = [data, Path(str(data) + ".state.lock"), Path(str(data) + ".backup.lock"),
                settings_path, Path(str(settings_path) + ".previous"), application.parent,
                Path(__file__).absolute(), *map(absolute_path, settings["libraries"]),
                *(absolute_path(settings[key]) for key in ("archivePath", "unsortedPath")
                  if settings.get(key) is not None)]
    if (root == Path("/") or root == Path.home()
            or any(root.is_relative_to(path) or path.is_relative_to(root) for path in excluded)):
        raise ValueError("Use a dedicated backup root separate from state, libraries, settings and application files.")
    return command, data, root, keep


def main():
    os.umask(0o077)
    options = configuration()
    if options is None:
        print("Scheduled backups are disabled.", flush=True)
        return
    command, data, root, keep = options
    root.mkdir(mode=0o700, exist_ok=True)
    info = root.stat()
    if info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) != 0o700:
        raise ValueError("The backup root must belong to this user and have permissions 0700.")
    if not shutil.rmtree.avoids_symlink_attacks:
        raise RuntimeError("This platform lacks symlink-safe directory removal.")
    # Keep the lock inode: unlinking it would allow concurrent runs to use different locks.
    lock_fd = os.open(root / ".scheduler.lock", os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
    with os.fdopen(lock_fd, "r+") as lock:
        if not stat.S_ISREG(os.fstat(lock.fileno()).st_mode):
            raise ValueError("Scheduler lock must be a regular file.")
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
        destination = root / f"scheduled-{stamp}-{uuid.uuid4().hex}"
        destination.mkdir(mode=0o700)
        subprocess.run([*command, "--DataPath", str(data),
                        "--backup-live", str(destination / "snapshot")], check=True)
        with (destination / ".complete").open("x") as marker:
            marker.write(MARKER)
            marker.flush()
            os.fsync(marker.fileno())
        if not owned_backup(destination):
            raise RuntimeError("Unexpected backup layout; retention was not run.")
        # Publish the marker and snapshot container durably before deleting any recovery point.
        for directory in (destination, root, root.parent):
            descriptor = os.open(directory, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
            try:
                os.fsync(descriptor)
            finally:
                os.close(descriptor)
        backups = sorted(p for p in root.iterdir() if owned_backup(p))
        # Always retain this successful snapshot, even if the wall clock moved backward.
        older = [p for p in backups if p != destination]
        for expired in older[:max(0, len(backups) - keep)]:
            shutil.rmtree(expired)
            print(f"Removed scheduled backup: {expired}", flush=True)
        print(f"Scheduled backup completed: {destination / 'snapshot'}", flush=True)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, TypeError, RuntimeError, subprocess.CalledProcessError) as error:
        sys.exit(f"Scheduled backup failed: {error}")
