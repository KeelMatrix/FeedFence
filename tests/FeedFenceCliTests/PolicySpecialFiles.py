"""Native Unix policy-object regression, using real restore and the selected CLI."""

import argparse
import datetime
import hashlib
import json
import os
import platform
from pathlib import Path
import signal
import socket
import stat
import subprocess
import tempfile
import time
import zipfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tool", required=True)
    parser.add_argument("--filter", default="")
    parser.add_argument("--evidence-json")
    parser.add_argument("--failure-control", action="store_true")
    args = parser.parse_args()
    if args.failure_control:
        raise RuntimeError("Intentional native policy runner failure control")
    if os.name != "posix" or not hasattr(os, "mkfifo") or not hasattr(socket, "AF_UNIX"):
        raise RuntimeError("Native Unix FIFO and socket capabilities are required")
    tool = Path(args.tool).resolve()
    command = ["dotnet", str(tool)] if tool.suffix == ".dll" else [str(tool)]
    print("Native filesystem host:", platform.system(), platform.release(), platform.machine())
    rows = []
    with tempfile.TemporaryDirectory(prefix="feedfence-policy-objects-") as temporary:
        root = Path(temporary)
        repository = root / "repository"
        app = repository / "src" / "nested" / "App"
        app.mkdir(parents=True)
        feed = root / "feed"
        feed.mkdir()
        with zipfile.ZipFile(feed / "Company.Secret.1.0.0.nupkg", "w") as package:
            package.writestr("Company.Secret.nuspec", '<package><metadata><id>Company.Secret</id><version>1.0.0</version><authors>Example</authors><description>Local fixture</description></metadata></package>')
        (root / "global.json").write_text('{"sdk":{"version":"8.0.425","rollForward":"latestPatch","allowPrerelease":false}}')
        project = app / "App.csproj"
        project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup><PackageReference Include="Company.Secret" Version="1.0.0"/></ItemGroup></Project>')
        config = repository / "NuGet.Config"
        config.write_text('<configuration><packageSources><clear/><add key="active" value="' + str(feed) + '"/></packageSources></configuration>')
        solution = repository / "src" / "Nested.sln"
        solution.write_text('Microsoft Visual Studio Solution File, Format Version 12.00\nProject("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "nested/App/App.csproj", "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBD}"\nEndProject\nGlobal\nEndGlobal\n')
        env = os.environ.copy()
        env.update(DOTNET_CLI_TELEMETRY_OPTOUT="1", KEELMATRIX_NO_TELEMETRY="1", DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1", DOTNET_CLI_HOME=str(root / "home"), NUGET_PACKAGES=str(root / "packages"), NUGET_HTTP_CACHE_PATH=str(root / "http"), NUGET_PLUGINS_CACHE_PATH=str(root / "plugins"), NUGET_SCRATCH=str(root / "scratch"))
        restored = subprocess.run(["dotnet", "restore", str(project), "--configfile", str(config), "--force", "--verbosity", "quiet"], cwd=repository, env=env, capture_output=True, text=True, timeout=60)
        if restored.returncode != 0:
            raise RuntimeError("Synthetic local-feed restore failed: " + restored.stdout + restored.stderr)
        policy = repository / "feedfence.json"
        protected = '{"version":1,"protectedPackages":[{"pattern":"Company.*","allowedSources":["company"]}]}'
        policy.write_text(protected)
        marker = repository / "src" / ".git"

        def snapshot():
            result = []
            # lstat identifies objects; read only regular bytes and never follow links.
            for directory in (repository, root / "objects", root / "logical"):
                if not directory.exists():
                    continue
                for parent, directories, files in os.walk(directory, followlinks=False):
                    for name in sorted(directories + files):
                        path = Path(parent) / name
                        metadata = path.lstat()
                        value = hashlib.sha256(path.read_bytes()).hexdigest() if stat.S_ISREG(metadata.st_mode) else os.readlink(path) if stat.S_ISLNK(metadata.st_mode) else None
                        result.append((str(path.relative_to(root)), metadata.st_mode, metadata.st_rdev, value))
            return sorted(result)

        def check(label, expected, override=None, targets=None):
            if args.filter and args.filter not in label:
                return
            for target in targets or (project, solution):
                for report_format in ("text", "json", "sarif"):
                    before = snapshot()
                    argv = command + ["check", str(target), "--config", str(config), "--format", report_format]
                    if override is not None:
                        argv += ["--policy", str(override)]
                    started_utc = datetime.datetime.now(datetime.timezone.utc).isoformat()
                    started = time.monotonic()
                    process = subprocess.Popen(argv, cwd=repository, env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, start_new_session=True)
                    timed_out = False
                    try:
                        stdout, stderr = process.communicate(timeout=3)
                    except subprocess.TimeoutExpired:
                        timed_out = True
                        os.killpg(process.pid, signal.SIGKILL)
                        stdout, stderr = process.communicate()
                    seconds = time.monotonic() - started
                    unchanged = before == snapshot()
                    if expected == 2:
                        report_ok = stdout == "" and "Analysis error:" in stderr and str(root) not in stderr and protected not in stderr
                    else:
                        if report_format == "text":
                            has_protected = "FF007" in stdout
                        elif not timed_out and process.returncode in (0, 1):
                            report = json.loads(stdout)
                            diagnostics = report["diagnostics"] if report_format == "json" else report["runs"][0]["results"]
                            has_protected = any(item.get("code", item.get("ruleId")) == "FF007" for item in diagnostics)
                        else:
                            has_protected = False
                        report_ok = stderr == "" and has_protected == (expected == 1)
                    passed = not timed_out and process.returncode == expected and report_ok and unchanged
                    rows.append(dict(label=label, target=target.name, format=report_format, argv=argv, startedUtc=started_utc, exit=process.returncode, expected=expected, timeout=timed_out, seconds=seconds, stdout=stdout, stderr=stderr, unchanged=unchanged, passed=passed))
                    if args.evidence_json:
                        Path(args.evidence_json).write_text(json.dumps(rows, indent=2))
                    if timed_out:
                        # Exit through fixture cleanup after killing the child group.
                        raise RuntimeError("Policy object command timed out: " + label)

        objects = root / "objects"
        objects.mkdir()

        def special(path, kind):
            if kind == "directory":
                path.mkdir()
                return None
            if kind == "fifo":
                os.mkfifo(path)  # No producer is ever started.
                return None
            bound = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            try:
                # Relative bind also works beneath macOS's long temporary paths.
                previous = Path.cwd()
                try:
                    os.chdir(path.parent)
                    bound.bind(path.name)
                finally:
                    os.chdir(previous)
                return bound
            except BaseException:
                bound.close()
                raise

        def remove(path, bound=None):
            if bound is not None:
                bound.close()
            if path.is_dir() and not path.is_symlink():
                path.rmdir()
            else:
                path.unlink(missing_ok=True)

        check("protected-before", 1)
        for kind in ("directory", "fifo", "socket"):
            policy.unlink()
            bound = special(policy, kind)
            try:
                check("policy-direct-" + kind, 2)
                check("policy-explicit-" + kind, 2, policy)
            finally:
                remove(policy, bound)
            for external in (False, True):
                entry = (objects if external else repository) / (kind + "-é-私.entry")
                bound = special(entry, kind)
                alias = repository / "alias"
                alias.symlink_to(entry)
                policy.symlink_to(entry)
                check("policy-linked-" + ("external" if external else "internal") + "-" + kind, 2)
                check("policy-explicit-linked-" + ("external" if external else "internal") + "-" + kind, 2, policy)
                policy.unlink()
                policy.symlink_to(alias)
                try:
                    label = "external" if external else "internal"
                    check("policy-chained-" + label + "-" + kind, 2)
                    check("policy-explicit-chained-" + label + "-" + kind, 2, policy)
                    check("policy-explicit-target-" + label + "-" + kind, 2, entry)
                finally:
                    policy.unlink()
                    alias.unlink()
                    remove(entry, bound)
            policy.write_text(protected)
        for kind in ("fifo", "socket"):
            bound = special(marker, kind)
            try:
                check("git-direct-" + kind, 2)
            finally:
                remove(marker, bound)
            entry = objects / ("git-" + kind)
            bound = special(entry, kind)
            alias = repository / "git-alias"
            alias.symlink_to(entry)
            marker.symlink_to(entry)
            check("git-linked-" + kind, 2)
            marker.unlink()
            marker.symlink_to(alias)
            try:
                check("git-chained-" + kind, 2)
            finally:
                marker.unlink()
                alias.unlink()
                remove(entry, bound)
            check("git-removed-" + kind, 1)
        for device in (Path("/dev/null"), Path("/dev/zero")):
            if not device.exists() or not stat.S_ISCHR(device.stat().st_mode):
                raise RuntimeError("Required existing character-device control unavailable: " + str(device))
            check("policy-explicit-device-" + device.name, 2, device)
            policy.unlink()
            policy.symlink_to(device)
            check("policy-device-alias-" + device.name, 2)
            check("policy-explicit-device-alias-" + device.name, 2, policy)
            policy.unlink()
            policy.write_text(protected)
            marker.symlink_to(device)
            check("git-device-alias-" + device.name, 2)
            marker.unlink()
        for name, link in (("dangling", "missing"), ("cycle", ".git")):
            marker.symlink_to(link)
            check("git-invalid-link-" + name, 2)
            marker.unlink()
        for kind in ("directory", "file"):
            entry = objects / ("valid-git-" + kind)
            if kind == "directory":
                entry.mkdir()
            else:
                entry.write_text("gitdir: ../metadata\n")
            marker.symlink_to(entry)
            check("git-valid-alias-" + kind + "-bounds-parent", 0)
            marker.unlink()
            if kind == "directory":
                marker.mkdir()
            else:
                marker.write_text("gitdir: ../metadata\n")
            check("git-valid-direct-" + kind + "-bounds-parent", 0)
            remove(marker)
            remove(entry)
        check("protected-after", 1)
        policy.unlink()
        check("legitimate-no-policy", 0)
        policy.write_text('{"version":1}')
        check("regular-empty-policy", 0)
        policy.write_text("{")
        check("regular-malformed-policy", 2)
        override = objects / "external.json"
        override.write_text(protected)
        check("regular-explicit-external", 1, override)
        policy.write_text(protected)
        logical = root / "logical"
        logical.mkdir()
        (logical / "src").symlink_to(repository / "src", target_is_directory=True)
        logical_targets = (logical / project.relative_to(repository), logical / solution.relative_to(repository))
        check("physical-ancestry-protected", 1, targets=logical_targets)
        for kind in ("fifo", "socket"):
            policy.unlink()
            bound = special(policy, kind)
            check("physical-ancestry-policy-" + kind, 2, targets=logical_targets)
            remove(policy, bound)
            logical_policy = logical / "feedfence.json"
            bound = special(logical_policy, kind)
            check("logical-ancestry-policy-" + kind, 2, targets=logical_targets)
            remove(logical_policy, bound)
            policy.write_text(protected)
            bound = special(logical / ".git", kind)
            check("logical-ancestry-git-" + kind, 2, targets=logical_targets)
            remove(logical / ".git", bound)
            bound = special(marker, kind)
            check("physical-ancestry-git-" + kind, 2, targets=logical_targets)
            remove(marker, bound)
        if not rows:
            raise RuntimeError("No cases selected")
    if args.evidence_json:
        Path(args.evidence_json).write_text(json.dumps(rows, indent=2))
    failed = [row for row in rows if not row["passed"]]
    for row in failed:
        print("FAIL:", row["label"], row["target"], row["format"], "exit=" + str(row["exit"]), "timeout=" + str(row["timeout"]), "unchanged=" + str(row["unchanged"]))
    print(("FAIL" if failed else "PASS") + ": native Unix policy objects; " + str(len(rows)) + " invocations/snapshots; " + str(len(failed)) + " failures; max=" + str(round(max(row["seconds"] for row in rows), 3)) + "s; command=" + tool.name)
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
