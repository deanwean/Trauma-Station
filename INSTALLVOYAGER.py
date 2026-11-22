#!/usr/bin/env python3

import shutil
import sys
from pathlib import Path
from tempfile import TemporaryDirectory


def main():
    root = Path(__file__).resolve().parent
    source = root / "Resources" / "Maps" / "voyager.yml"
    destination = root / "bin" / "Content.Server" / "data" / "Maps" / "voyager.yml"

    try:
        for directory in (source.parent, destination.parent):
            if not directory.is_dir():
                raise NotADirectoryError(f"Map directory does not exist: {directory}")

        # Finish copying before replacing the server map so a failed copy leaves it intact.
        with TemporaryDirectory(dir=destination.parent) as staging_directory:
            staged_map = Path(staging_directory) / destination.name
            shutil.copyfile(source, staged_map)
            staged_map.replace(destination)
    except OSError as error:
        print(f"Voyager installation failed: {error}", file=sys.stderr)
        return 1

    print(f"Installed Voyager at {destination}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
