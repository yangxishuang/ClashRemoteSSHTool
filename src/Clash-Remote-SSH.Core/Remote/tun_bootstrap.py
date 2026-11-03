"""Fixed administrator preparation. No caller-supplied paths, commands or config."""
import gzip
import hashlib
import json
import os
import pathlib
import platform
import pwd
import shutil
import stat
import subprocess
import tempfile


def trusted(path):
    return path.is_file() and all(p.stat().st_uid == 0 and not p.stat().st_mode & 0o022 for p in [path] + list(path.parents))


def command(args):
    subprocess.run(args, check=True, timeout=360, stdout=subprocess.DEVNULL)


def prepare():
    if os.geteuid() != 0:
        raise RuntimeError("需要管理员权限")
    uid = int(os.environ.get("SUDO_UID", os.getuid()))
    account = pwd.getpwuid(uid)
    machine = platform.machine()
    architecture = "amd64" if machine in ("x86_64", "amd64") else "arm64" if machine in ("aarch64", "arm64") else ""
    if not architecture:
        raise RuntimeError("仅支持 Linux amd64 / arm64")
    for tool in ("setcap", "getcap", "setfacl"):
        if not shutil.which(tool):
            if shutil.which("apt-get"):
                command(["apt-get", "update"])
                command(["apt-get", "install", "-y", "libcap2-bin", "acl"])
            elif shutil.which("dnf") or shutil.which("yum"):
                command([shutil.which("dnf") or shutil.which("yum"), "install", "-y", "libcap", "acl"])
            else:
                raise RuntimeError("请管理员安装 libcap 和 ACL 工具")
            break
    destination = pathlib.Path("/usr/local/lib/clashonweb-tun") / str(uid)
    for parent in list(reversed(destination.parents)) + [destination]:
        if parent.is_symlink():
            raise RuntimeError("权限安装目录包含符号链接")
        if not parent.exists():
            parent.mkdir(mode=0o700 if parent == destination else 0o755)
        if parent.stat().st_uid != 0 or parent.stat().st_mode & 0o022:
            raise RuntimeError("权限安装目录不可信")
    source = next((p.resolve() for p in (pathlib.Path("/usr/bin/verge-mihomo"), pathlib.Path("/usr/bin/mihomo")) if trusted(p.resolve())), None)
    fd, temporary = tempfile.mkstemp(prefix=".mihomo-", dir=destination)
    try:
        with os.fdopen(fd, "wb") as output:
            if source:
                with source.open("rb") as input_file:
                    shutil.copyfileobj(input_file, output)
            else:
                asset = CORE_MANIFEST["assets"][machine]
                package = pathlib.Path(account.pw_dir) / ".cache/clashonweb/cores" / (CORE_MANIFEST["version"] + "-" + asset["asset"] + ".gz")
                # Verify bytes before parsing attacker-controlled compressed input as root.
                package_fd = os.open(package, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
                with os.fdopen(package_fd, "rb") as input_file:
                    if not stat.S_ISREG(os.fstat(input_file.fileno()).st_mode):
                        raise RuntimeError("内核缓存不是普通文件")
                    compressed = input_file.read(64 * 1024 * 1024 + 1)
                if len(compressed) > 64 * 1024 * 1024 or hashlib.sha256(compressed).hexdigest() != asset["sha256"]:
                    raise RuntimeError("内核缓存校验失败")
                import io
                with gzip.GzipFile(fileobj=io.BytesIO(compressed)) as archive:
                    data = archive.read(150 * 1024 * 1024 + 1)
                if len(data) > 150 * 1024 * 1024:
                    raise RuntimeError("内核解压超出限制")
                output.write(data)
        with open(temporary, "rb") as input_file:
            header = input_file.read(20)
        expected = 62 if architecture == "amd64" else 183
        if header[:6] != b"\x7fELF\x02\x01" or int.from_bytes(header[18:20], "little") != expected:
            raise RuntimeError("内核架构不匹配")
        os.chmod(temporary, 0o500)
        if uid != 0:
            command(["setfacl", "-m", "u:%d:r-x,g::---,m::r-x,o::---" % uid, temporary])
            command(["setfacl", "-m", "u:%d:r-x,g::---,m::r-x,o::---" % uid, str(destination)])
        command(["setcap", "cap_net_admin,cap_net_raw+ep", temporary])
        os.replace(temporary, destination / "mihomo")
        command(["loginctl", "enable-linger", account.pw_name])
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)
    return {"message": "独立内核权限已准备；未修改共享内核或运行配置"}


if __name__ == "__main__":
    try:
        print("@@CLASHSSH@@" + json.dumps({"ok": True, "message": "权限准备完成", "data": prepare()}), flush=True)
    except Exception:
        print("@@CLASHSSH@@" + json.dumps({"ok": False, "message": "TUN 管理员准备失败，请检查 sudo、包管理器、ACL 与文件能力支持；未修改运行配置"}), flush=True)
        raise SystemExit(1)
