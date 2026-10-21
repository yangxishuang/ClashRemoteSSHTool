"""Pinned public GEO resources. Fixed names and bounded binary upload, no URL forwarding."""
import stat
if "GEODATA_MANIFEST" not in globals():
    GEODATA_MANIFEST = json.loads(pathlib.Path(__file__).with_name("geodata-manifest.json").read_text(encoding="utf-8"))


def geodata_cache_path(name, create=False):
    if name not in GEODATA_MANIFEST["assets"]:
        raise ValueError("不支持的 GeoData 资源")
    directory = globals().get("HOME", pathlib.Path.home()).resolve()
    for part in (".cache", "clashonweb", "geodata", GEODATA_MANIFEST["version"]):
        directory = directory / part
        if directory.is_symlink():
            raise ValueError("GeoData 缓存不能包含符号链接")
        if create:
            directory.mkdir(exist_ok=True, mode=0o700)
    target = directory / name
    if target.is_symlink():
        raise ValueError("GeoData 缓存不能是符号链接")
    return target


def cached_geodata(name):
    spec = GEODATA_MANIFEST["assets"][name]
    path = geodata_cache_path(name)
    if not path.is_file() or path.stat().st_size != spec["size"]:
        return None
    raw = path.read_bytes()
    return raw if hashlib.sha256(raw).hexdigest() == spec["sha256"] else None


def geodata_plan():
    return [dict(name=name, version=GEODATA_MANIFEST["version"], size=spec["size"], sha256=spec["sha256"],
                 cached=cached_geodata(name) is not None) for name, spec in GEODATA_MANIFEST["assets"].items()]


def receive_geodata_main():
    def accept(header):
        name = header.get("asset")
        spec = GEODATA_MANIFEST["assets"].get(name)
        if (not spec or header.get("version") != GEODATA_MANIFEST["version"] or header.get("size") != spec["size"]
                or header.get("sha256") != spec["sha256"]):
            raise ValueError("GeoData 上传清单或大小不匹配")
        return geodata_cache_path(name, create=True), spec["sha256"]
    try:
        result = {"ok": True, "message": "规则数据上传并校验完成", "data": receive_verified_blob(None, accept)}
    except Exception:
        result = {"ok": False, "message": "规则数据上传或校验失败", "data": {}}
    print("@@CLASHSSH@@" + json.dumps(result, ensure_ascii=False), flush=True)


def restore_geodata(changes):
    conflicts = []
    for path, previous, mode in reversed(changes):
        spec = GEODATA_MANIFEST["assets"][path.name]
        if path.is_symlink() or (path.exists() and (not path.is_file() or path.stat().st_size != spec["size"] or hashlib.sha256(path.read_bytes()).hexdigest() != spec["sha256"])):
            conflicts.append(path.name)
            continue
        if previous is None:
            if path.exists():
                path.unlink()
        else:
            atomic_write(path, previous, mode)
    if conflicts:
        raise RuntimeError("规则数据被外部修改，未覆盖，请核对部署状态：" + ", ".join(conflicts))


def prepare_geodata(request):
    changes = []
    try:
        for name in GEODATA_MANIFEST["assets"]:
            raw = cached_geodata(name)
            if raw is None:
                raise RuntimeError("规则数据缓存缺失或校验失败，请重新部署以上传")
            path = config_path(request).parent / name
            if path.is_symlink() or (path.exists() and (not path.is_file() or path.stat().st_size > MAX_CORE_PACKAGE)):
                raise ValueError("规则数据目标不是有界的普通文件")
            previous = path.read_bytes() if path.exists() else None
            mode = stat.S_IMODE(path.stat().st_mode) if previous is not None else 0o600
            if previous != raw:
                atomic_write(path, raw, 0o600)
                changes.append((path, previous, mode))
        return changes
    except Exception:
        restore_geodata(changes)
        raise
