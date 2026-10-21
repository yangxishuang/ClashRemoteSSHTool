"""Profile library and bounded user-service removal; bundled before remote.py."""
PROFILE_LIMIT = 100


def parse_subscription_headers(headers):
    values = {}
    for key, value in re.findall(r"(upload|download|total|expire)\s*=\s*(\d+)", headers.get("subscription-userinfo", ""), re.I):
        values[key.lower()] = min(int(value), 9223372036854775807)
    return {"usage": values}


def file_hash(path):
    return hashlib.sha256(path.read_bytes()).hexdigest() if path.is_file() else ""


def profile_root(request):
    key = hashlib.sha256((str(config_path(request).resolve()) + "\n" + service_name(request)).encode()).hexdigest()[:16]
    root = HOME / ".local/share/clashonweb/profiles" / key
    try:
        root.resolve().relative_to(HOME.resolve())
    except ValueError:
        raise ValueError("配置库路径超出当前用户目录")
    if root.is_symlink():
        raise ValueError("配置库不能是符号链接")
    return root


def profile_file(request, identity, suffix):
    if not isinstance(identity, str) or not re.fullmatch(r"[a-f0-9]{32}", identity):
        raise ValueError("配置标识无效，请刷新列表")
    path = profile_root(request) / (identity + suffix)
    if path.is_symlink():
        raise ValueError("配置库文件不能是符号链接")
    return path


def read_catalog(request):
    path = profile_root(request) / "catalog.json"
    if not path.exists():
        return {"schema": 1, "entries": [], "activeId": "", "activeHash": ""}
    if path.is_symlink() or path.stat().st_size > 1024 * 1024:
        raise ValueError("配置目录文件无效，请检查服务器配置库")
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        if data.get("schema") != 1 or not isinstance(data.get("entries"), list) or len(data["entries"]) > PROFILE_LIMIT:
            raise ValueError()
        for entry in data["entries"]:
            profile_file(request, entry["id"], ".yaml")
        return data
    except (ValueError, KeyError, TypeError):
        raise RuntimeError("配置库索引损坏，已停止修改；请检查服务器catalog.json")


def save_catalog(request, catalog):
    root = profile_root(request)
    root.mkdir(parents=True, exist_ok=True, mode=0o700)
    os.chmod(root, 0o700)
    atomic_write(root / "catalog.json", json.dumps(catalog, ensure_ascii=False).encode())


def current_profile_id(request, catalog):
    digest = file_hash(config_path(request))
    if digest and digest == catalog.get("activeHash"):
        return catalog.get("activeId", "")
    # A managed TUN edit changes only runtime networking fields, not profile identity.
    # Keep the catalog untouched so independent rollback needs no second-file transaction.
    state = read_network_state(request)
    phase = state.get("phase")
    hashes = [state.get("afterHash")] if phase in ("pending", "active", "off") else [state.get("beforeHash")] if phase in ("rolled-back", "cancelled", "rollback-failed") else []
    if digest and digest in hashes and state.get("profileId") and state.get("profileId") == catalog.get("activeId") and state.get("catalogHash") == catalog.get("activeHash"):
        return state["profileId"]
    return ""


def safe_profile(entry, active=False):
    # This allow-list deliberately excludes source URL and configuration content.
    fields = ("id", "name", "kind", "sourceHost", "sourceName", "updatedAt", "nodeCount", "providerCount", "usage")
    return dict({key: entry[key] for key in fields if key in entry}, active=active)


def profiles_list(request):
    catalog = read_catalog(request)
    active = current_profile_id(request, catalog)
    entries = [safe_profile(entry, entry["id"] == active) for entry in catalog["entries"]]
    config = config_path(request)
    if config.is_file() and not active:
        entries.insert(0, {"id": "current", "name": "服务器当前配置", "kind": "existing", "active": True,
                           "updatedAt": config.stat().st_mtime, "nodeCount": 0, "providerCount": 0, "usage": {}})
    return {"profiles": entries, "activeId": active or ("current" if config.is_file() else "")}


def ensure_catalog(request, snapshot_current=False):
    root = profile_root(request)
    root.mkdir(parents=True, exist_ok=True, mode=0o700)
    os.chmod(root, 0o700)
    catalog = read_catalog(request)
    config = config_path(request)
    # Before the first import/removal, retain the original running configuration.
    if config.is_file() and (not catalog["entries"] or (snapshot_current and not current_profile_id(request, catalog))):
        if len(catalog["entries"]) >= PROFILE_LIMIT:
            raise ValueError("配置库已满，无法保存当前配置快照，请先删除不用的配置")
        if config.is_symlink() or config.stat().st_size > MAX_CONFIG:
            raise ValueError("当前配置不能安全保存，请先检查路径和大小")
        identity = uuid.uuid4().hex
        entry = {"id": identity, "name": "服务器原有配置", "kind": "file", "sourceName": "原有运行配置",
                 "updatedAt": config.stat().st_mtime, "nodeCount": 0, "providerCount": 0, "usage": {}}
        old = current_config(config)
        entry["nodeCount"] = len(old.get("proxies", [])) if isinstance(old.get("proxies"), list) else 0
        entry["providerCount"] = len(old.get("proxy-providers", {})) if isinstance(old.get("proxy-providers"), dict) else 0
        try:
            url = source_url(dict(request, url=""), config)
            atomic_write(profile_file(request, identity, ".url"), url.encode())
            entry.update(kind="url", sourceHost=urllib.parse.urlsplit(url).hostname)
        except ValueError:
            pass
        atomic_write(profile_file(request, identity, ".yaml"), config.read_bytes())
        catalog["entries"].append(entry)
        catalog.update(activeId=identity, activeHash=file_hash(config))
        save_catalog(request, catalog)
    return catalog


def find_profile(catalog, identity):
    for entry in catalog["entries"]:
        if entry["id"] == identity:
            return entry
    raise ValueError("配置已不存在，请刷新列表")


def profile_name(value):
    if not isinstance(value, str) or not value.strip() or len(value.strip()) > 80 or any(ord(c) < 32 for c in value):
        raise ValueError("配置名称需要1至80个字符，不能包含控制字符")
    return value.strip()


def profile_source(request):
    kind = request.get("kind", "url")
    metadata = {"kind": kind, "updatedAt": time.time(), "usage": {}}
    if kind == "url":
        url = validate_url(request.get("url"))
        metadata["sourceHost"] = urllib.parse.urlsplit(url).hostname
        if request.get("contentBase64"):
            raw = decode_profile_content(request["contentBase64"])
            metadata.update(parse_subscription_headers({"subscription-userinfo": str(request.get("subscriptionUserinfo", ""))[:2048]}))
        else:
            proxy_port = current_config(config_path(request)).get("mixed-port")
            raw = download(url, metadata=metadata, proxy_port=proxy_port)
    elif kind == "file":
        raw = decode_profile_content(request.get("contentBase64"))
        metadata["sourceName"] = str(request.get("fileName", "配置.yaml")).replace("\\", "/").rsplit("/", 1)[-1][:120]
    else:
        raise ValueError("导入方式必须为订阅链接或YAML文件")
    data = load_yaml(raw)
    metadata.update(nodeCount=len(data.get("proxies", [])) if isinstance(data.get("proxies"), list) else 0,
                    providerCount=len(data.get("proxy-providers", {})) if isinstance(data.get("proxy-providers"), dict) else 0)
    return raw, metadata


def decode_profile_content(value):
    if not isinstance(value, str) or len(value) > (MAX_CONFIG * 4 // 3 + 8):
        raise ValueError("YAML文件超过10MiB或内容无效")
    try:
        return base64.b64decode(value, validate=True)
    except ValueError:
        raise ValueError("YAML文件传输内容无效")


def profiles_source(request):
    entry = find_profile(read_catalog(request), request.get("profileId"))
    if entry["kind"] != "url":
        raise ValueError("此配置没有订阅链接")
    return {"url": validate_url(profile_file(request, entry["id"], ".url").read_text(encoding="utf-8"))}


def profiles_preview(request):
    raw, metadata = profile_source(request)
    if request.get("validateCore"):
        metadata["coreValidated"] = validate_profile_content(request, raw)
    return metadata


def validate_profile_content(request, raw):
    import yaml
    candidate = protect_config(load_yaml(raw), current_config(config_path(request)), request)
    binary = core_binary(request)
    # A bare core may still lack its GEO files. Fresh installs validate after
    # DeployAsync has transferred the pinned resources, rather than downloading
    # them during subscription import.
    if binary and config_path(request).is_file():
        with tempfile.TemporaryDirectory(prefix="clashonweb-profile-check-") as directory:
            for name in ("Country.mmdb", "GeoSite.dat", "GeoIP.dat", "geosite.dat", "geoip.dat", "ASN.mmdb"):
                cached = config_path(request).parent / name
                if cached.is_file() and cached.stat().st_size <= 256 * 1024 * 1024:
                    shutil.copyfile(cached, pathlib.Path(directory) / name)
            staged = pathlib.Path(directory) / "candidate.yaml"
            staged.write_text(yaml.safe_dump(candidate, allow_unicode=True, sort_keys=False), encoding="utf-8")
            os.chmod(staged, 0o600)
            verify_core(binary, staged, pathlib.Path(directory))
        return True
    return False


def profiles_mutate(request):
    action = request["action"]
    if action == "profiles.import":
        name = profile_name(request.get("name"))
        raw, metadata = profile_source(request)
        validate_profile_content(request, raw)
        catalog = ensure_catalog(request)
        if len(catalog["entries"]) >= PROFILE_LIMIT:
            raise ValueError("最多保存100个配置，请先删除不用的配置")
        identity = uuid.uuid4().hex
        entry = dict(metadata, id=identity, name=name)
        atomic_write(profile_file(request, identity, ".yaml"), raw)
        if entry["kind"] == "url":
            atomic_write(profile_file(request, identity, ".url"), request["url"].encode())
        catalog["entries"].append(entry)
        save_catalog(request, catalog)
        return {"profile": safe_profile(entry), "message": "已导入，点击使用此配置后生效"}
    catalog = ensure_catalog(request, snapshot_current=action == "profiles.apply")
    identity = request.get("profileId")
    entry = find_profile(catalog, identity)
    active = current_profile_id(request, catalog)
    if action == "profiles.apply":
        result = apply_configuration(request, profile_file(request, identity, ".yaml").read_bytes())
        catalog.update(activeId=identity, activeHash=file_hash(config_path(request)))
        save_catalog(request, catalog)
        return dict(result, profile=safe_profile(entry, True))
    if action == "profiles.update":
        if entry["kind"] != "url":
            raise ValueError("YAML文件没有远程订阅来源，请重新导入文件")
        suggested = profile_name(request["suggestedName"]) if entry["name"] == "新的订阅配置" and request.get("suggestedName") else None
        url = validate_url(profile_file(request, identity, ".url").read_text(encoding="utf-8"))
        raw, metadata = profile_source(dict(request, kind="url", url=url))
        validate_profile_content(request, raw)
        if active == identity:
            apply_configuration(request, raw)
            catalog["activeHash"] = file_hash(config_path(request))
        atomic_write(profile_file(request, identity, ".yaml"), raw)
        entry.update(metadata)
        if suggested:
            entry["name"] = suggested
        save_catalog(request, catalog)
        return {"profile": safe_profile(entry, active == identity), "applied": active == identity}
    if action == "profiles.rename":
        entry["name"] = profile_name(request.get("name"))
    elif action == "profiles.delete":
        if active == identity:
            raise ValueError("不能删除正在使用的配置，请先切换到其他配置")
        catalog["entries"].remove(entry)
        save_catalog(request, catalog)
        for suffix in (".yaml", ".url"):
            path = profile_file(request, identity, suffix)
            if path.is_file():
                path.unlink()
        return {"deleted": identity}
    else:
        raise ValueError("不支持的配置操作")
    save_catalog(request, catalog)
    return {"profile": safe_profile(entry, active == identity)}


def uninstall_plan(request):
    info = service_info(request)
    unit = HOME / ".config/systemd/user" / service_name(request)
    fragment = info.get("FragmentPath", "")
    if fragment and pathlib.Path(fragment) != unit:
        raise RuntimeError("当前unit不在本用户标准服务目录，不能自动卸载；请使用对应系统包管理器")
    config = config_path(request)
    paths = [(unit, "用户服务unit"), (config, "当前运行配置")]
    override = unit.parent / (unit.name + ".d") / "70-clashonweb-tun.conf"
    if override.is_file() and override.read_text().startswith("# ClashOnWebSSH TUN permissions"):
        paths.append((override, "本应用的 TUN 权限覆盖文件"))
    owned_binary = HOME / ".local/share/clashonweb/bin/mihomo"
    if unit.is_file():
        text = unit.read_text(encoding="utf-8")
        commands = re.findall(r"^ExecStart=(.+)$", text.replace("\\\n", ""), re.M)
        try:
            arguments = [part.replace("%h", str(HOME)) for part in shlex.split(commands[-1])] if commands else []
        except ValueError:
            arguments = []
        used_config = None
        for index, argument in enumerate(arguments[:-1]):
            if argument in ("-f", "--config"):
                used_config = pathlib.Path(arguments[index + 1])
        if used_config is None:
            for index, argument in enumerate(arguments[:-1]):
                if argument == "-d":
                    used_config = pathlib.Path(arguments[index + 1]) / "config.yaml"
        if used_config is None or used_config.resolve() != config.resolve():
            raise RuntimeError("无法确认unit使用了当前配置路径，已停止卸载；请核对高级设置和ExecStart")
        if re.search(r"^ExecStart=(?:" + re.escape(systemd_quote(owned_binary)) + "|" + re.escape(str(owned_binary)) + r")(?:\s|$)", text, re.M):
            paths.append((owned_binary, "本应用安装的独立内核"))
    elif config.is_file():
        load_yaml(config.read_bytes())
    files = []
    for path, label in paths:
        try:
            path.resolve().relative_to(HOME.resolve())
        except ValueError:
            raise ValueError("卸载路径超出用户目录，已拒绝")
        if path.is_symlink():
            raise ValueError("卸载目标包含符号链接，已拒绝")
        if path.exists() and not path.is_file():
            raise ValueError("卸载目标不是普通文件，已拒绝")
        if path.is_file():
            files.append({"path": str(path), "label": label, "size": path.stat().st_size, "sha256": file_hash(path)})
    if not files and info.get("LoadState") != "loaded":
        raise ValueError("没有可卸载的当前用户服务")
    binary = core_binary(request)
    plan = {"service": service_name(request), "files": files, "wasActive": info.get("ActiveState") == "active",
            "wasEnabled": info.get("UnitFileState", ""), "loaded": info.get("LoadState") == "loaded",
            "retainedBinary": binary if binary and binary not in [item["path"] for item in files] else "",
            "retainedProfiles": "保留已导入配置与当前配置快照，可重新部署"}
    plan["confirmationToken"] = hashlib.sha256(json.dumps(plan, sort_keys=True).encode()).hexdigest()
    return plan


def uninstall(request):
    plan = uninstall_plan(request)
    if request.get("confirmService") != service_name(request) or request.get("confirmationToken") != plan["confirmationToken"]:
        raise ValueError("卸载确认已过期或不匹配，请重新查看移除清单")
    catalog = ensure_catalog(request, snapshot_current=True)
    removed = []
    try:
        if plan["loaded"]:
            run(["systemctl", "--user", "disable", "--now", service_name(request)], timeout=30)
            if service_info(request).get("ActiveState") not in ("inactive", "failed"):
                raise RuntimeError("服务未确认停止，未删除文件")
        for item in plan["files"]:
            path = pathlib.Path(item["path"])
            if file_hash(path) != item["sha256"]:
                raise RuntimeError("卸载目标已被其他程序修改，已停止移除")
            content, mode = path.read_bytes(), path.stat().st_mode & 0o777
            path.unlink()
            removed.append((path, content, mode))
        run(["systemctl", "--user", "daemon-reload"])
    except Exception:
        try:
            for path, content, mode in removed:
                atomic_write(path, content, mode)
            run(["systemctl", "--user", "daemon-reload"])
            if plan["wasEnabled"].startswith("enabled"):
                args = ["systemctl", "--user", "enable"]
                if plan["wasEnabled"] == "enabled-runtime":
                    args.append("--runtime")
                run(args + [service_name(request)])
            if plan["wasActive"]:
                run(["systemctl", "--user", "start", service_name(request)])
                wait_api(request)
        except Exception:
            raise RuntimeError("卸载未完成，恢复也失败；请通过SSH检查当前unit和配置")
        raise RuntimeError("卸载未完成，已恢复本次删除文件及原服务状态")
    catalog.update(activeId="", activeHash="")
    save_catalog(request, catalog)
    return {"status": "removed", "retainedBinary": plan["retainedBinary"],
            "message": "当前用户服务与运行配置已移除；已导入配置保留。" + ("系统共享内核保留，重新部署时可以复用。" if plan["retainedBinary"] else "")}
