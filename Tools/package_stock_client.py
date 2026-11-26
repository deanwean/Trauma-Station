#!/usr/bin/env python3
"""Package committed content for the stock SS14 launcher and install it for local hosting.

Run `python Tools/package_stock_client.py` after committing client or resource changes.
Requires Git, the repository's .NET SDK and an initialized QuietToolbox submodule.
Restart the server after installation so it generates its download manifest from the new zip.
"""

import os
import shutil
import subprocess
import tarfile
import tempfile
from pathlib import Path
from zipfile import ZipFile


ROOT = Path(__file__).resolve().parent.parent
STOCK_ENGINE = "https://github.com/space-wizards/RobustToolbox.git"


def git_value(directory, *args):
    return subprocess.check_output(["git", *args], cwd=directory, text=True).strip()


def package():
    revision = git_value(ROOT, "rev-parse", "HEAD")
    quiet_pin = git_value(ROOT, "rev-parse", f"{revision}:RobustToolbox")
    tag = git_value(ROOT / "RobustToolbox", "describe", "--tags", "--abbrev=0", quiet_pin)
    print(f"Building committed content {revision} for stock RobustToolbox {tag}", flush=True)

    with tempfile.TemporaryDirectory(prefix="ss14-client-") as temporary:
        stage = Path(temporary) / "content"
        # The packager clears bin and release. Export only the content commit into staging.
        stage.mkdir()
        with subprocess.Popen(["git", "archive", revision], cwd=ROOT, stdout=subprocess.PIPE) as export:
            with tarfile.open(fileobj=export.stdout, mode="r|") as snapshot:
                snapshot.extractall(stage, filter="data")
            if export.wait():
                raise subprocess.CalledProcessError(export.returncode, export.args)
        subprocess.run(
            ["git", "-c", "core.longpaths=true", "clone", "--branch", tag, "--depth", "1",
             "--recurse-submodules", STOCK_ENGINE, "RobustToolbox"],
            cwd=stage, check=True,
        )

        # Match the dependency substitution in Tools/switch_robust_toolbox.sh.
        for project in stage.glob("Content.*/*.csproj"):
            source = project.read_bytes()
            if b"TraumaStation.ImageSharp" in source:
                project.write_bytes(source.replace(b"TraumaStation.ImageSharp", b"SixLabors.ImageSharp"))

        # ClientPackaging applies USE_ROBUST when building the individual content modules.
        subprocess.run(
            ["dotnet", "run", "--project", "Content.Packaging", "--configuration", "Release",
             "--", "client", "--no-wipe-release"],
            cwd=stage, check=True,
        )
        archive = stage / "release" / "SS14.Client.zip"
        with ZipFile(archive) as client:
            for entry in ("manifest.yml", "Assemblies/Content.Client.dll", "Assemblies/Content.Trauma.Client.dll"):
                if not client.getinfo(entry).file_size:
                    raise ValueError(f"Empty entry in client package: {entry}")
            bad_entry = client.testzip()
            if bad_entry is not None:
                raise ValueError(f"Failed CRC check in client package: {bad_entry}")

        output = ROOT / "bin" / "Content.Server" / "Content.Client.zip"
        output.parent.mkdir(parents=True, exist_ok=True)
        # Replace on the destination volume so the server can't read a partial zip.
        with tempfile.NamedTemporaryFile(dir=output.parent, prefix=".stock-client-", delete=False) as pending:
            staged_zip = Path(pending.name)
        try:
            shutil.copyfile(archive, staged_zip)
            if git_value(ROOT, "rev-parse", "HEAD") != revision:
                raise RuntimeError("The content checkout changed during packaging. Run the script for the new HEAD.")
            os.replace(staged_zip, output)
        finally:
            staged_zip.unlink(missing_ok=True)
        print(f"Installed {output}. Restart the server to serve this client.")


if __name__ == "__main__":
    package()
