"""Bounded binary stdin receiver and verified cache; no shell or client-chosen paths."""
import hashlib
import json
import os
import pathlib
import platform
import sys
import tempfile

if "CORE_MANIFEST" not in globals():
    CORE_MANIFEST = json.loads(pathlib.Path(__file__).with_name("kernel-manifest.json").read_text(encoding="utf-8"))

MAX_CORE_PACKAGE = 64 * 1024 * 1024


def core_asset():
    if platform.system() != "Linux" or platform.machine() not in CORE_MANIFEST["assets"]:
        raise RuntimeError("自动部署仅支持 Linux amd64/arm64")
    return CORE_MANIFEST["assets"][platform.machine()]


def core_cache_path(create=False):
    home = globals().get("HOME", pathlib.Path.home()).resolve()
    directory = home
    for part in (".cache", "clashonweb", "cores"):
        directory = directory / part
        if directory.is_symlink():
            raise RuntimeError("内核缓存路径不能是符号链接")
        if create:
            directory.mkdir(exist_ok=True, mode=0o700)
    if create:
        os.chmod(directory, 0o700)
    target = directory / (CORE_MANIFEST["version"] + "-" + core_asset()["asset"] + ".gz")
    if target.is_symlink():
        raise RuntimeError("内核缓存文件不能是符号链接")
    return target


def cached_core():
    target = core_cache_path()
    if not target.is_file() or not 0 < target.stat().st_size <= MAX_CORE_PACKAGE:
        return None
    packed = target.read_bytes()
    return packed if hashlib.sha256(packed).hexdigest() == core_asset()["sha256"] else None


def receive_core(stream=None):
    def accept(header):
        asset = core_asset()
        if (header.get("version") != CORE_MANIFEST["version"] or header.get("asset") != asset["asset"]
                or header.get("sha256") != asset["sha256"]):
            raise ValueError("内核上传版本、架构或大小不匹配")
        return core_cache_path(create=True), asset["sha256"]
    return receive_verified_blob(stream, accept)


def receive_verified_blob(stream, accept):
    stream = stream or sys.stdin.buffer
    temporary = None
    try:
        line = stream.readline(4097)
        if len(line) > 4096 or not line.endswith(b"\n"):
            raise ValueError("内核上传头无效")
        header = json.loads(line)
        size = header.get("size")
        if type(size) is not int or not 0 < size <= MAX_CORE_PACKAGE:
            raise ValueError("内核上传版本、架构或大小不匹配")
        target, expected = accept(header)
        digest = hashlib.sha256()
        with tempfile.NamedTemporaryFile(prefix=".upload-", dir=target.parent, delete=False) as output:
            temporary = pathlib.Path(output.name)
            remaining = size
            while remaining:
                chunk = stream.read(min(65536, remaining))
                if not chunk:
                    raise ValueError("内核上传中断，文件不完整")
                output.write(chunk)
                digest.update(chunk)
                remaining -= len(chunk)
            if stream.read(1):
                raise ValueError("内核上传长度超出声明")
            output.flush()
            os.fsync(output.fileno())
        if digest.hexdigest() != expected:
            raise ValueError("内核上传 SHA-256 不匹配")
        os.chmod(temporary, 0o600)
        os.replace(temporary, target)
        return {"version": header["version"], "asset": header["asset"], "size": size, "sha256": digest.hexdigest()}
    finally:
        if temporary and temporary.exists():
            temporary.unlink()


def receive_core_main():
    try:
        result = {"ok": True, "message": "内核上传并校验完成", "data": receive_core()}
    except Exception as error:
        result = {"ok": False, "message": str(error), "data": {}}
    print("@@CLASHSSH@@" + json.dumps(result, ensure_ascii=False), flush=True)
