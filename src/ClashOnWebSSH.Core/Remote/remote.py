"""One-shot SSH adapter. No daemon, no arbitrary command endpoint, no secrets in argv."""
import base64
import concurrent.futures
import contextlib
import copy
import gzip
import hashlib
import importlib.util
import io
import json
import os
import pathlib
import platform
import re
import secrets
import shlex
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import threading
import urllib.error
import urllib.parse
import urllib.request
import uuid

HOME = pathlib.Path.home()
MAX_CONFIG = 10 * 1024 * 1024
VERSION = "v1.19.31"
ASSETS = {
    "x86_64": ("amd64-compatible", "04cf9f09671704f839ddbee2e93069dc831a4123a75281e725d1d96ab9ac1afc"),
    "aarch64": ("arm64", "9e0f11afbf38426b8bd88fdc594678f8161c57eccb4e1b77acb12b493904f1d4"),
    "arm64": ("arm64", "9e0f11afbf38426b8bd88fdc594678f8161c57eccb4e1b77acb12b493904f1d4"),
}


def redact(value):
    text = str(value)
    text = re.sub(r"https?://[^\s\"'<>]+", lambda m: urllib.parse.urlsplit(m.group()).scheme + "://" + (urllib.parse.urlsplit(m.group()).hostname or "来源") + "/[已隐藏]", text, flags=re.I)
    text = re.sub(r"((?:password|passwd|token|secret|authorization)\s*[:=]\s*)[^\s,;]+", r"\1[已隐藏]", text, flags=re.I)
    return text[:50000]


def run(args, timeout=20, check=True, **kwargs):
    env = os.environ.copy()
    if hasattr(os, "getuid"):
        env.setdefault("XDG_RUNTIME_DIR", "/run/user/" + str(os.getuid()))
    result = subprocess.run(args, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout, env=env, **kwargs)
    if check and result.returncode:
        raise RuntimeError(redact(result.stderr or result.stdout or "命令失败"))
    return result


def config_path(request):
    value = request.get("config", "~/.config/clash-verge/config.yaml")
    if not isinstance(value, str) or len(value) > 1024 or any(ord(c) < 32 for c in value):
        raise ValueError("配置路径无效")
    path = pathlib.Path(value.replace("~/", str(HOME) + "/", 1)).expanduser()
    if not path.is_absolute():
        raise ValueError("配置路径必须为绝对路径或 ~/ 路径")
    try:
        path.resolve().relative_to(HOME.resolve())
    except ValueError:
        raise ValueError("首版只管理当前 SSH 用户主目录内的配置")
    return path


def service_name(request):
    name = request.get("service", "clash-verge.service")
    if not isinstance(name, str) or len(name) > 120 or not re.fullmatch(r"[a-zA-Z0-9_.@-]+\.service", name):
        raise ValueError("服务名无效")
    return name


def load_yaml(content):
    if len(content) > MAX_CONFIG:
        raise ValueError("订阅内容超过 10 MiB")
    try:
        import yaml
    except ImportError:
        raise RuntimeError("缺少 PyYAML，请先点击准备依赖")

    class UniqueLoader(yaml.SafeLoader):
        pass

    def mapping(loader, node, deep=False):
        result = {}
        loader.flatten_mapping(node)
        for key_node, value_node in node.value:
            key = loader.construct_object(key_node, deep=deep)
            if key in result:
                raise ValueError("YAML 存在重复字段")
            result[key] = loader.construct_object(value_node, deep=deep)
        return result

    UniqueLoader.add_constructor(yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG, mapping)
    try:
        data = yaml.load(content, Loader=UniqueLoader)
    except yaml.YAMLError:
        raise ValueError("YAML解析失败，请检查文件结构；原文未写入日志")
    if not isinstance(data, dict) or not any(k in data for k in ("proxies", "proxy-providers")):
        raise ValueError("需要 Clash/mihomo 完整 YAML 订阅，不支持节点串或 HTML 页面")
    budget = [100000]

    def visit(value, depth, stack):
        budget[0] -= 1
        if budget[0] < 0 or depth > 35:
            raise ValueError("YAML 结构过于复杂")
        if isinstance(value, (dict, list)):
            if id(value) in stack:
                raise ValueError("YAML 存在循环引用")
            stack = stack | {id(value)}
            for item in (value.values() if isinstance(value, dict) else value):
                visit(item, depth + 1, stack)
    visit(data, 0, set())
    return data


def current_config(path):
    if not path.is_file():
        return {}
    try:
        import yaml
        data = yaml.safe_load(path.read_text(encoding="utf-8"))
        return data if isinstance(data, dict) else {}
    except ImportError:
        return {}
    except Exception:
        raise RuntimeError("当前运行配置无法解析，请先检查服务器配置文件")


def api(request, route, method="GET", body=None, timeout=12):
    port = int(request.get("apiPort", 9090))
    if not 1 <= port <= 65535:
        raise ValueError("API 端口无效")
    headers = {"Content-Type": "application/json"}
    secret = current_config(config_path(request)).get("secret")
    if secret:
        headers["Authorization"] = "Bearer " + str(secret)
    url = "http://127.0.0.1:" + str(port) + route
    req = urllib.request.Request(url, data=json.dumps(body, ensure_ascii=False).encode() if body is not None else None, headers=headers, method=method)
    # Never route control requests through the user's http_proxy environment.
    with urllib.request.build_opener(urllib.request.ProxyHandler({})).open(req, timeout=timeout) as response:
        raw = response.read(16 * 1024 * 1024)
        return json.loads(raw) if raw else {}


def service_info(request):
    if not shutil.which("systemctl"):
        return {"LoadState": "not-found", "ActiveState": "unavailable"}
    result = run(["systemctl", "--user", "show", service_name(request), "--property=LoadState,ActiveState,SubState,UnitFileState,FragmentPath,MainPID"], check=False)
    return dict(line.split("=", 1) for line in result.stdout.splitlines() if "=" in line)


def core_binary():
    for candidate in [shutil.which("verge-mihomo"), str(HOME / ".local/share/clashonweb/bin/mihomo"), shutil.which("mihomo"), "/usr/bin/verge-mihomo"]:
        if candidate and pathlib.Path(candidate).is_file():
            return str(candidate)
    return ""


def probe(request):
    service = service_info(request)
    config = config_path(request)
    binary = core_binary()
    user_manager = False
    if shutil.which("systemctl"):
        user_manager = run(["systemctl", "--user", "show-environment"], check=False).returncode == 0
    linger = False
    if shutil.which("loginctl"):
        linger = "Linger=yes" in run(["loginctl", "show-user", str(os.getuid()), "-p", "Linger"], check=False).stdout
    data = {
        "platform": platform.system(), "architecture": platform.machine(), "home": str(HOME),
        "service": service.get("ActiveState", "unknown"), "subState": service.get("SubState", ""),
        "enabled": service.get("UnitFileState", ""), "installed": bool(config.exists() or service.get("LoadState") == "loaded"),
        "coreAvailable": bool(binary),
        "configHash": file_hash(config), "servicePid": service.get("MainPID", ""),
        "binary": binary, "configExists": config.is_file(), "python": platform.python_version(),
        "yaml": importlib.util.find_spec("yaml") is not None, "systemdUser": user_manager, "linger": linger,
        "version": "", "mode": "", "apiAvailable": False, "apiError": "", "mixedPort": 7890,
    }
    try:
        version = api(request, "/version")
        config_data = api(request, "/configs")
        data.update(version=version.get("version", ""), mode=config_data.get("mode", ""), apiAvailable=True, mixedPort=config_data.get("mixed-port", 7890))
    except Exception:
        data["apiError"] = "管理 API 暂不可达，请检查服务、端口和配置"
    return data


def groups(request):
    proxies = api(request, "/proxies").get("proxies", {})
    result = []
    for name, value in proxies.items():
        if "all" not in value:
            continue
        result.append({"name": name, "type": value.get("type", ""), "current": value.get("now", ""),
                       "members": [{"name": item, "type": proxies.get(item, {}).get("type", ""),
                                    "current": item == value.get("now")} for item in value.get("all", [])]})
    return {"groups": result}


def select_node(request):
    group, node = request.get("group"), request.get("node")
    proxies = api(request, "/proxies").get("proxies", {})
    if group not in proxies or proxies[group].get("type") != "Selector":
        raise ValueError("只有 Selector 分组可以手动切换")
    if not isinstance(node, str) or node not in proxies[group].get("all", []):
        raise ValueError("节点不在所选分组中")
    route = "/proxies/" + urllib.parse.quote(group, safe="")
    api(request, route, "PUT", {"name": node})
    actual = api(request, route).get("now")
    if actual != node:
        raise RuntimeError("内核实际选择与请求不一致，请刷新后确认")
    return {"group": group, "current": actual}


def delay(request):
    proxies = api(request, "/proxies").get("proxies", {})
    if request.get("node"):
        names = [request["node"]]
    else:
        names = proxies.get(request.get("group"), {}).get("all", [])
    if not names or len(names) > 2500 or any(name not in proxies for name in names):
        raise ValueError("测速范围无效或节点过多")
    target = "http://www.gstatic.com/generate_204"

    def single(name):
        try:
            route = "/proxies/" + urllib.parse.quote(name, safe="") + "/delay?" + urllib.parse.urlencode({"timeout": 5000, "url": target})
            result = api(request, route, timeout=7)
            return {"name": name, "delay": result.get("delay"), "error": ""}
        except Exception:
            return {"name": name, "delay": None, "error": "超时或不可达"}
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
        results = list(pool.map(single, names))
    return {"results": results, "target": target, "sampledAt": time.time()}


def validate_url(value):
    if not isinstance(value, str) or len(value) > 4096:
        raise ValueError("订阅地址无效")
    parsed = urllib.parse.urlsplit(value)
    if parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password:
        raise ValueError("请输入不含用户名密码前缀的 HTTPS 订阅地址")
    return value


class HttpsRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        validate_url(newurl)
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def download(url, limit=MAX_CONFIG, metadata=None, proxy_port=None):
    validate_url(url)
    request = urllib.request.Request(url, headers={"User-Agent": "clash-verge/v2.5.2"})
    proxies = [{}]
    if proxy_port:
        proxies.append({"http": "http://127.0.0.1:" + str(proxy_port), "https": "http://127.0.0.1:" + str(proxy_port)})
    for index, proxy in enumerate(proxies):
        opener = urllib.request.build_opener(urllib.request.ProxyHandler(proxy), HttpsRedirect())
        deadline = time.monotonic() + 90
        try:
            with opener.open(request, timeout=15 if len(proxies) > 1 and index == 0 else 30) as response:
                if metadata is not None:
                    metadata.update(parse_subscription_headers(response.headers))
                result = bytearray()
                while True:
                    chunk = response.read(min(65536, limit + 1 - len(result)))
                    if not chunk:
                        if metadata is not None:
                            metadata["downloadVia"] = "proxy" if proxy else "direct"
                        return bytes(result)
                    result.extend(chunk)
                    if len(result) > limit or time.monotonic() > deadline:
                        raise ValueError("下载内容过大或超时")
        except urllib.error.HTTPError:
            raise
        except (urllib.error.URLError, TimeoutError, ConnectionError, OSError):
            if index == len(proxies) - 1:
                raise RuntimeError("订阅下载失败：直连或服务器现有代理不可达，请检查网络和订阅来源")


def protect_config(new, old, request):
    result = copy.deepcopy(new)
    for key in ("external-controller-tls", "external-controller-unix", "external-controller-pipe", "external-ui-url", "external-ui", "listeners"):
        result.pop(key, None)
    result["external-controller"] = "127.0.0.1:" + str(int(request.get("apiPort", 9090)))
    result["secret"] = old.get("secret", secrets.token_urlsafe(32))
    result["allow-lan"] = False
    result["bind-address"] = "127.0.0.1"
    result["mixed-port"] = int(old.get("mixed-port") or 7890)
    result["port"] = 0
    result["socks-port"] = 0
    result["redir-port"] = 0
    result["tproxy-port"] = 0
    result["mode"] = old.get("mode", "rule")
    result["tun"] = copy.deepcopy(old.get("tun", {"enable": False}))
    if "dns" in old:
        result["dns"] = copy.deepcopy(old["dns"])
    elif isinstance(result.get("dns"), dict):
        result["dns"].pop("listen", None)
    return result


def atomic_write(path, content, mode=0o600):
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix=".clashonweb-", dir=str(path.parent))
    try:
        with os.fdopen(fd, "wb") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(temporary, mode)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def source_url(request, config):
    if request.get("url"):
        return validate_url(request["url"])
    saved = config.with_name("subscription.url")
    if saved.is_file():
        return validate_url(saved.read_text(encoding="utf-8").strip())
    manual = HOME / "userdata/Yxs/clash-verge-使用手册.md"
    if manual.is_file():
        match = re.search(r'https://[^\s"\x27<>]+/subscribe\?[^\s"\x27<>]+', manual.read_text(encoding="utf-8"))
        if match:
            return validate_url(match.group(0))
    raise ValueError("尚未保存订阅来源，请输入 HTTPS 订阅地址")


def verify_core(binary, path, directory):
    run([binary, "-t", "-d", str(directory), "-f", str(path)], timeout=150)


def wait_api(request):
    for _ in range(20):
        try:
            return api(request, "/version", timeout=2)
        except Exception:
            time.sleep(0.5)
    raise RuntimeError("服务启动后管理 API 未就绪")


def apply_configuration(request, raw):
    import yaml
    config = config_path(request)
    if not config.is_file() or config.is_symlink():
        raise ValueError("现有配置不存在，或是符号链接；请先检查环境")
    binary = core_binary()
    if not binary:
        raise RuntimeError("未找到 mihomo，请先部署")
    original = config.read_bytes()
    original_hash = hashlib.sha256(original).digest()
    old = current_config(config)
    candidate = protect_config(load_yaml(raw), old, request)
    content = yaml.safe_dump(candidate, allow_unicode=True, sort_keys=False).encode("utf-8")
    with tempfile.TemporaryDirectory(prefix=".clashonweb-check-", dir=str(config.parent)) as staging:
        staged = pathlib.Path(staging) / "candidate.yaml"
        staged.write_bytes(content)
        os.chmod(staged, 0o600)
        verify_core(binary, staged, config.parent)
    if hashlib.sha256(config.read_bytes()).digest() != original_hash:
        raise RuntimeError("配置被其他程序修改，已取消替换，请刷新后重试")
    backup = config.with_name("config.clashonweb.backup.yaml")
    atomic_write(backup, original)
    was_active = service_info(request).get("ActiveState") == "active"
    atomic_write(config, content)
    try:
        run(["systemctl", "--user", "restart", service_name(request)], timeout=30)
        wait_api(request)
    except Exception:
        atomic_write(config, original)
        try:
            run(["systemctl", "--user", "restart" if was_active else "stop", service_name(request)], timeout=30)
            if was_active:
                wait_api(request)
        except Exception:
            raise RuntimeError("新配置启动失败，原文件已恢复但服务恢复失败。请通过 SSH 检查用户服务和备份")
        raise RuntimeError("新配置启动失败，已恢复原配置与原服务运行状态")
    return {"status": "updated", "backup": str(backup), "version": api(request, "/version").get("version", "")}


def update(request):
    config = config_path(request)
    url = source_url(request, config)
    result = apply_configuration(request, download(url))
    if request.get("rememberSource"):
        atomic_write(config.with_name("subscription.url"), url.encode())
    return result


def check_free_port(port):
    with socket.socket() as sock:
        try:
            sock.bind(("127.0.0.1", port))
        except OSError:
            raise RuntimeError("目标端口已被占用，停止安装以避免影响既有服务")


def systemd_quote(value):
    return '"' + str(value).replace("\\", "\\\\").replace('"', '\\"').replace("%", "%%") + '"'


def deploy(request):
    import yaml
    state = probe(request)
    if state["installed"]:
        raise RuntimeError("检测到已有配置或服务，已取消安装；请使用现有服务管理功能")
    if not state["systemdUser"]:
        raise RuntimeError("systemd 用户会话不可用。请确认 SSH 登录启用了 PAM/systemd 用户会话")
    machine = platform.machine()
    if platform.system() != "Linux" or machine not in ASSETS:
        raise RuntimeError("自动部署仅支持 Linux amd64/arm64 与 systemd 用户服务")
    if int(request.get("apiPort", 9090)) == 7890:
        raise ValueError("管理 API 不能与代理共用 7890 端口")
    check_free_port(int(request.get("apiPort", 9090)))
    check_free_port(7890)
    config = config_path(request)
    unit = HOME / ".config/systemd/user" / service_name(request)
    existing_binary = core_binary()
    binary = pathlib.Path(existing_binary) if existing_binary else HOME / ".local/share/clashonweb/bin/mihomo"
    if unit.exists() or config.exists() or (not existing_binary and binary.exists()):
        raise RuntimeError("目标文件已存在，不覆盖安装")
    executable = None
    if not existing_binary:
        arch, digest = ASSETS[machine]
        url = "https://github.com/MetaCubeX/mihomo/releases/download/" + VERSION + "/mihomo-linux-" + arch + "-" + VERSION + ".gz"
        packed = download(url, 64 * 1024 * 1024)
        if hashlib.sha256(packed).hexdigest() != digest:
            raise RuntimeError("官方内核 SHA-256 不匹配，已停止安装")
        with gzip.GzipFile(fileobj=io.BytesIO(packed)) as archive:
            executable = archive.read(150 * 1024 * 1024 + 1)
        if len(executable) > 150 * 1024 * 1024:
            raise RuntimeError("解压后的内核异常大")
    subscription = None
    if request.get("profileId"):
        catalog = read_catalog(request)
        entry = find_profile(catalog, request["profileId"])
        raw = profile_file(request, entry["id"], ".yaml").read_bytes()
    else:
        subscription = source_url(request, config)
        raw = download(subscription)
    candidate = protect_config(load_yaml(raw), {}, request)
    content = yaml.safe_dump(candidate, allow_unicode=True, sort_keys=False).encode("utf-8")
    created = []
    started = False
    try:
        if executable is not None:
            atomic_write(binary, executable, 0o700)
            created.append(binary)
        config.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix=".clashonweb-install-", dir=str(config.parent)) as staging:
            staged = pathlib.Path(staging) / "candidate.yaml"
            staged.write_bytes(content)
            os.chmod(staged, 0o600)
            verify_core(str(binary), staged, config.parent)
        atomic_write(config, content)
        created.append(config)
        text = "[Unit]\nDescription=ClashOnWebSSH mihomo user proxy\nAfter=network-online.target\n\n[Service]\nType=simple\nExecStart=" + systemd_quote(binary) + " -d " + systemd_quote(config.parent) + " -f " + systemd_quote(config) + "\nRestart=on-failure\nRestartSec=3\nUMask=0077\n\n[Install]\nWantedBy=default.target\n"
        atomic_write(unit, text.encode())
        created.append(unit)
        run(["systemctl", "--user", "daemon-reload"])
        started = True
        run(["systemctl", "--user", "enable", "--now", service_name(request)], timeout=30)
        installed_version = wait_api(request)
        if request.get("rememberSource") and subscription:
            atomic_write(config.with_name("subscription.url"), subscription.encode())
    except Exception:
        if started:
            run(["systemctl", "--user", "disable", "--now", service_name(request)], check=False)
        for path in reversed(created):
            if path.is_file():
                path.unlink()
        if created:
            run(["systemctl", "--user", "daemon-reload"], check=False)
        raise
    if request.get("profileId"):
        catalog["activeId"] = entry["id"]
        catalog["activeHash"] = file_hash(config)
        save_catalog(request, catalog)
    return {"status": "installed", "version": installed_version.get("version", VERSION), "linger": state["linger"],
            "message": "安装并启动成功" if state["linger"] else "安装并启动成功；尚未启用 linger，请在向导中配置脱离登录运行"}


@contextlib.contextmanager
def control_lock():
    import fcntl
    directory = HOME / ".cache/clashonweb"
    directory.mkdir(parents=True, exist_ok=True, mode=0o700)
    with (directory / "control.lock").open("a") as lock:
        try:
            fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise RuntimeError("另一项远控操作正在执行，请稍后刷新")
        yield


def execute(request):
    action = request.get("action")
    service_name(request)
    config_path(request)
    if action == "probe":
        return probe(request)
    if action == "groups":
        return groups(request)
    if action == "profiles.list":
        return profiles_list(request)
    if action == "profiles.source":
        return profiles_source(request)
    if action == "profiles.preview":
        return profiles_preview(request)
    if action == "uninstall.plan":
        return uninstall_plan(request)
    if action == "delay":
        return delay(request)
    if action == "logs":
        lines = max(1, min(200, int(request.get("lines", 50))))
        output = run(["journalctl", "--user", "-u", service_name(request), "-n", str(lines), "--no-pager", "-o", "short-iso"], check=False)
        return {"text": redact(output.stdout or output.stderr)}
    if action == "connectivity":
        port = int(current_config(config_path(request)).get("mixed-port") or 7890)
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({"https": "http://127.0.0.1:" + str(port)}))
        start = time.monotonic()
        with opener.open("https://www.gstatic.com/generate_204", timeout=12) as response:
            return {"httpStatus": response.status, "elapsedMs": round((time.monotonic() - start) * 1000), "target": "https://www.gstatic.com/generate_204"}
    if action not in ("select", "mode", "service", "update", "deploy", "profiles.import", "profiles.apply", "profiles.update", "profiles.rename", "profiles.delete", "uninstall"):
        raise ValueError("不支持的远端动作")
    with control_lock():
        if action.startswith("profiles."):
            return profiles_mutate(request)
        if action == "uninstall":
            return uninstall(request)
        if action == "select":
            return select_node(request)
        if action == "mode":
            mode = request.get("mode")
            if mode not in ("rule", "global", "direct"):
                raise ValueError("模式无效")
            api(request, "/configs", "PATCH", {"mode": mode})
            actual = api(request, "/configs").get("mode")
            if actual != mode:
                raise RuntimeError("模式切换未确认")
            return {"mode": actual}
        if action == "service":
            operation = request.get("operation")
            if operation not in ("start", "stop", "restart"):
                raise ValueError("不支持的服务操作")
            run(["systemctl", "--user", operation, service_name(request)], timeout=30)
            actual = service_info(request)
            if operation != "stop":
                wait_api(request)
            elif actual.get("ActiveState") not in ("inactive", "failed"):
                raise RuntimeError("服务尚未确认停止")
            return actual
        if action == "update":
            return update(request)
        return deploy(request)


def main():
    stopped = threading.Event()
    output_lock = threading.Lock()
    def heartbeat():
        while not stopped.wait(8):
            with output_lock:
                if not stopped.is_set():
                    print("@@CLASHSSH_PROGRESS@@", flush=True)
    threading.Thread(target=heartbeat, daemon=True).start()
    try:
        max_request = MAX_CONFIG * 4 // 3 + 65536
        raw = sys.stdin.read(max_request + 1)
        if len(raw) > max_request:
            raise ValueError("请求过大")
        request = json.loads(raw)
        if not isinstance(request, dict):
            raise ValueError("请求无效")
        result = execute(request)
        response = {"ok": True, "message": "完成", "data": result}
    except urllib.error.HTTPError as error:
        response = {"ok": False, "message": "远端 HTTP 请求失败（状态码 " + str(error.code) + "），请检查订阅或管理接口", "data": {}}
    except subprocess.TimeoutExpired:
        response = {"ok": False, "message": "远端命令超时，请读取实际状态后再决定是否重试", "data": {}}
    except Exception as error:
        response = {"ok": False, "message": redact(error), "data": {}}
    with output_lock:
        stopped.set()
        print("@@CLASHSSH@@" + json.dumps(response, ensure_ascii=False), flush=True)


if "profiles_list" not in globals() and "__file__" in globals():
    exec(compile(pathlib.Path(__file__).with_name("profiles.py").read_text(encoding="utf-8"), "profiles.py", "exec"))

if __name__ == "__main__":
    main()
