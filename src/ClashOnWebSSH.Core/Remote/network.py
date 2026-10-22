"""On-demand remote network controls. Uses the adapter's bounded config/API helpers."""
import ipaddress
import stat
import ssl

PROXY_MARKER = "# ClashOnWebSSH proxy environment"
TUN_MARKER = "# ClashOnWebSSH TUN permissions"
TUN_ROOT = pathlib.Path("/usr/local/lib/clashonweb-tun")
SITE_TARGETS = {
    "chatgpt": ("ChatGPT", "https://chatgpt.com/"),
    "github": ("GitHub", "https://github.com/"),
    "google": ("Google", "https://www.google.com/generate_204"),
    "grok": ("Grok", "https://grok.com/"),
}
if "NETWORK_ROLLBACK_SCRIPT" not in globals():
    NETWORK_ROLLBACK_SCRIPT = pathlib.Path(__file__).with_name("tun_rollback.py").read_text(encoding="utf-8")


def private_path(path):
    path = pathlib.Path(path)
    path.relative_to(HOME)
    current = HOME
    for part in path.relative_to(HOME).parts:
        current = current / part
        if current.is_symlink():
            raise ValueError("网络设置路径不能包含符号链接")
    return path


def proxy_env_path():
    return private_path(HOME / ".config/clashonweb/proxy-env.sh")


def shell_files():
    import pwd
    shell = pathlib.Path(pwd.getpwuid(os.getuid()).pw_shell).name
    if shell not in ("bash", "sh", "dash", "zsh"):
        raise RuntimeError("系统代理自动加载目前支持 Bash、sh 和 Zsh；当前登录 shell 不受支持")
    if shell == "zsh":
        if os.environ.get("ZDOTDIR") and pathlib.Path(os.environ["ZDOTDIR"]) != HOME:
            raise RuntimeError("暂不支持自定义 ZDOTDIR 的终端自动代理")
        return [private_path(HOME / ".zshenv")]
    files = [private_path(HOME / ".profile")]
    if shell == "bash":
        files.append(private_path(HOME / ".bashrc"))
        for name in (".bash_profile", ".bash_login"):
            path = private_path(HOME / name)
            if path.exists():
                files.append(path)
                break
    return files


def system_proxy_status():
    path = proxy_env_path()
    active = path.is_file() and path.read_text(encoding="utf-8").startswith(PROXY_MARKER + "\n")
    try:
        supported = bool(shell_files())
        active = active and all(p.is_file() and "# >>> ClashOnWebSSH proxy >>>" in p.read_text(encoding="utf-8") for p in shell_files())
    except RuntimeError:
        supported = False
    return {"enabled": active, "supported": supported, "scope": "当前用户新终端"}


def set_proxy_environment(request):
    enabled = request.get("enabled")
    if type(enabled) is not bool:
        raise ValueError("代理开关值必须为布尔值")
    path = proxy_env_path()
    files = shell_files()
    begin, end = "# >>> ClashOnWebSSH proxy >>>", "# <<< ClashOnWebSSH proxy <<<"
    block = begin + '\n[ ! -r "$HOME/.config/clashonweb/proxy-env.sh" ] || . "$HOME/.config/clashonweb/proxy-env.sh"\n' + end
    originals = {}
    writes = {}
    for target in [path] + files:
        if target.exists() and (not target.is_file() or target.stat().st_size > MAX_CONFIG):
            raise ValueError("终端配置不是可管理的普通小文件")
        originals[target] = (target.read_bytes(), target.stat().st_mode & 0o777) if target.exists() else None
    if path.exists() and not path.read_text(encoding="utf-8").startswith(PROXY_MARKER + "\n"):
        raise ValueError("代理环境文件已存在且不属于本应用，未覆盖")
    port = int(current_config(config_path(request)).get("mixed-port") or 7890)
    if not 1 <= port <= 65535:
        raise ValueError("远端代理端口无效")
    for target in files:
        text = originals[target][0].decode("utf-8") if originals[target] else ""
        if text.count(begin) != text.count(end) or text.count(begin) > 1 or (begin in text and text.index(end) < text.index(begin)):
            raise ValueError("终端配置中的代理标记不完整，请先检查文件")
        stripped = re.sub(re.escape(begin) + r"\n.*?" + re.escape(end) + r"\n?", "", text, flags=re.S)
        writes[target] = ((stripped.rstrip("\n") + "\n\n" + block + "\n") if enabled else stripped).encode("utf-8")
    writes[path] = (PROXY_MARKER + "\n" +
                   "export http_proxy='http://127.0.0.1:%d' https_proxy='http://127.0.0.1:%d' all_proxy='socks5h://127.0.0.1:%d'\n" % (port, port, port) +
                   "export HTTP_PROXY=\"$http_proxy\" HTTPS_PROXY=\"$https_proxy\" ALL_PROXY=\"$all_proxy\"\n" +
                   "export no_proxy='localhost,127.0.0.1,::1' NO_PROXY='localhost,127.0.0.1,::1'\n").encode("utf-8") if enabled else None
    changed = []
    try:
        for target, content in writes.items():
            now = (target.read_bytes(), target.stat().st_mode & 0o777) if target.exists() else None
            if now != originals[target]:
                raise RuntimeError("终端配置已被其他程序修改，未覆盖")
            if content is None:
                if target.exists():
                    target.unlink()
            elif content or originals[target] is not None:
                atomic_write(target, content, originals[target][1] if originals[target] else 0o600)
            changed.append(target)
    except Exception:
        for target in reversed(changed):
            unchanged_since_write = not target.exists() if writes[target] is None else target.is_file() and target.read_bytes() == writes[target]
            if unchanged_since_write:
                if originals[target] is None:
                    if target.exists():
                        target.unlink()
                else:
                    atomic_write(target, *originals[target])
        raise
    actual = system_proxy_status()
    if actual["enabled"] != enabled:
        raise RuntimeError("系统代理设置未确认，请刷新检查")
    return dict(actual, message="已为当前用户开启新终端代理；已有终端和程序保持原环境" if enabled else "已关闭新终端自动代理；已有终端需重新打开或自行取消环境变量")


def takeover_plan(config, runtime, state, proxy_enabled, tun_enabled):
    actual = runtime.get("mode", config.get("mode", "rule"))
    if actual not in ("rule", "global", "direct"):
        raise RuntimeError("无法确认内核运行模式，未切换代理")
    owned_direct = state.get("directOverride") is True and actual == config.get("mode") == "direct"
    resume = state.get("resumeMode", "rule") if owned_direct else actual
    if resume not in ("rule", "global", "direct"):
        raise RuntimeError("保存的代理模式无效，未切换代理")
    enabled = proxy_enabled or tun_enabled
    return {"resumeMode": resume, "directOverride": not enabled, "appliedMode": resume if enabled else "direct"}


def takeover_status(config, runtime, state, proxy, api_error):
    mode = runtime.get("mode", "")
    both_off = not proxy["enabled"] and not runtime.get("tun", {}).get("enable", False) and not config.get("tun", {}).get("enable", False)
    return {"bothOff": both_off, "mode": mode, "configuredMode": config.get("mode", "rule"), "resumeMode": state.get("resumeMode", mode),
            "directConfirmed": not api_error and both_off and mode == config.get("mode") == "direct"}


def set_proxy_mode(request):
    import yaml
    mode = request.get("mode")
    if mode not in ("rule", "global", "direct"):
        raise ValueError("模式仅支持 rule、global、direct")
    path = private_path(config_path(request))
    original = path.read_bytes()
    permissions = path.stat().st_mode & 0o777
    config = load_yaml(original)
    runtime = api(request, "/configs")
    before_mode = runtime.get("mode")
    if before_mode not in ("rule", "global", "direct"):
        raise RuntimeError("无法确认内核运行模式，未修改")
    previous = read_network_state(request)
    candidate = copy.deepcopy(config)
    candidate["mode"] = mode
    content = original if candidate == config else yaml.safe_dump(candidate, allow_unicode=True, sort_keys=False).encode("utf-8")
    if content != original:
        with tempfile.TemporaryDirectory(prefix=".clashonweb-mode-check-", dir=path.parent) as staging:
            staged = pathlib.Path(staging) / "candidate.yaml"
            staged.write_bytes(content)
            os.chmod(staged, 0o600)
            verify_core(core_binary(request), staged, path.parent)
    before_hash = hashlib.sha256(original).hexdigest()
    after_hash = hashlib.sha256(content).hexdigest()
    if file_hash(path) != before_hash:
        raise RuntimeError("配置已被其他程序修改，未切换模式")
    state_path = private_path(network_state_path(request))
    old_state = state_path.read_bytes() if state_path.exists() else None
    catalog = read_catalog(request)
    # An explicit mode choice supersedes the automatic both-off DIRECT override.
    # The next last-entry-off transition still records this choice for resuming.
    state = dict(previous, resumeMode=mode, directOverride=False, appliedMode=mode,
                 phase="active" if runtime.get("tun", {}).get("enable") or config.get("tun", {}).get("enable") else "off",
                 beforeHash=before_hash, afterHash=after_hash,
                 profileId=current_profile_id(request, catalog), catalogHash=catalog.get("activeHash", ""))
    encoded_state = json.dumps(state).encode()
    try:
        if content != original:
            atomic_write(path, content, permissions)
        if before_mode != mode:
            api(request, "/configs", "PATCH", {"mode": mode})
        if api(request, "/configs").get("mode") != mode:
            raise RuntimeError("内核模式切换未确认")
        network_dir(request)
        if (state_path.read_bytes() if state_path.exists() else None) != old_state:
            raise RuntimeError("网络记录出现外部修改，未覆盖")
        atomic_write(state_path, encoded_state)
        # Close existing keepalive/CONNECT streams so they reconnect in the new mode.
        api(request, "/connections", "DELETE")
        if api(request, "/configs").get("mode") != mode or file_hash(path) != after_hash or state_path.read_bytes() != encoded_state:
            raise RuntimeError("模式最终状态未确认")
    except Exception as error:
        try:
            if file_hash(path) not in (before_hash, after_hash):
                raise RuntimeError("运行配置出现外部修改，未覆盖")
            actual = api(request, "/configs").get("mode")
            if actual not in (before_mode, mode):
                raise RuntimeError("内核模式出现外部修改，未覆盖")
            current_state = state_path.read_bytes() if state_path.exists() else None
            if current_state not in (old_state, encoded_state):
                raise RuntimeError("网络记录出现外部修改，未覆盖")
            atomic_write(path, original, permissions)
            if actual != before_mode:
                api(request, "/configs", "PATCH", {"mode": before_mode})
            if api(request, "/configs").get("mode") != before_mode or file_hash(path) != before_hash:
                raise RuntimeError("原模式未确认恢复")
            if old_state is None:
                if state_path.exists():
                    state_path.unlink()
            else:
                atomic_write(state_path, old_state)
        except Exception:
            raise RuntimeError("模式切换与恢复未能全部确认，请刷新或通过 SSH 检查；不要重复提交")
        raise RuntimeError("模式切换失败，原模式已恢复：" + str(error))
    return {"mode": mode, "message": "代理模式已切换并保存，已有代理连接将按新模式重连"}


def snapshot_proxy_files():
    result = {}
    for path in [proxy_env_path()] + shell_files():
        if path.exists() and (not path.is_file() or path.stat().st_size > MAX_CONFIG):
            raise ValueError("终端配置不是可管理的普通小文件")
        result[path] = (path.read_bytes(), path.stat().st_mode & 0o777) if path.exists() else None
    return result


def restore_proxy_files(originals, expected):
    for path, before in originals.items():
        actual = (path.read_bytes(), path.stat().st_mode & 0o777) if path.exists() else None
        if actual == before:
            continue
        if actual != expected[path]:
            raise RuntimeError("终端配置出现外部修改，未覆盖")
        if before is None:
            path.unlink()
        else:
            atomic_write(path, *before)


def set_system_proxy(request):
    import yaml
    enabled = request.get("enabled")
    if type(enabled) is not bool:
        raise ValueError("代理开关值必须为布尔值")
    path = private_path(config_path(request))
    # Uninstalled servers can still remove their managed shell settings.
    if not path.is_file():
        if enabled:
            raise RuntimeError("请先部署并启动代理服务")
        result = set_proxy_environment(request)
        return dict(result, message="已关闭终端自动代理；当前未部署服务")
    original = path.read_bytes()
    config = load_yaml(original)
    runtime = api(request, "/configs")
    previous = read_network_state(request)
    plan = takeover_plan(config, runtime, previous, enabled, bool(runtime.get("tun", {}).get("enable")) or bool(config.get("tun", {}).get("enable")))
    candidate = copy.deepcopy(config)
    candidate["mode"] = plan["appliedMode"]
    content = original if candidate == config else yaml.safe_dump(candidate, allow_unicode=True, sort_keys=False).encode("utf-8")
    if content != original:
        with tempfile.TemporaryDirectory(prefix=".clashonweb-mode-check-", dir=path.parent) as staging:
            staged = pathlib.Path(staging) / "candidate.yaml"
            staged.write_bytes(content)
            os.chmod(staged, 0o600)
            verify_core(core_binary(request), staged, path.parent)
    originals = snapshot_proxy_files()
    expected = originals
    if file_hash(path) != hashlib.sha256(original).hexdigest():
        raise RuntimeError("配置已被其他程序修改，未切换代理")
    state_path = private_path(network_state_path(request))
    old_state = state_path.read_bytes() if state_path.exists() else None
    catalog = read_catalog(request)
    state = dict(previous, **plan)
    state.update(phase="active" if runtime.get("tun", {}).get("enable") or config.get("tun", {}).get("enable") else "off", beforeHash=hashlib.sha256(original).hexdigest(),
                 afterHash=hashlib.sha256(content).hexdigest(), profileId=current_profile_id(request, catalog), catalogHash=catalog.get("activeHash", ""))
    before_mode = runtime.get("mode", config.get("mode", "rule"))
    mode_changed = before_mode != plan["appliedMode"]
    try:
        if content != original:
            atomic_write(path, content)
        if mode_changed:
            api(request, "/configs", "PATCH", {"mode": plan["appliedMode"]})
        actual = api(request, "/configs")
        if actual.get("mode") != plan["appliedMode"]:
            raise RuntimeError("内核模式切换未确认")
        set_proxy_environment(request)
        expected = snapshot_proxy_files()
        network_dir(request)
        atomic_write(state_path, json.dumps(state).encode())
        # Existing CONNECT/keepalive sessions otherwise retain their old outbound.
        if mode_changed or plan["directOverride"]:
            api(request, "/connections", "DELETE")
        actual = api(request, "/configs")
        if actual.get("mode") != plan["appliedMode"] or file_hash(path) != state["afterHash"] or system_proxy_status()["enabled"] != enabled:
            raise RuntimeError("代理设置发生变化，未确认最终状态")
    except Exception as error:
        try:
            restore_proxy_files(originals, expected)
            if file_hash(path) not in (state["beforeHash"], state["afterHash"]):
                raise RuntimeError("运行配置出现外部修改，未覆盖")
            actual_mode = api(request, "/configs").get("mode")
            if actual_mode not in (before_mode, plan["appliedMode"]):
                raise RuntimeError("内核模式出现外部修改，未覆盖")
            atomic_write(path, original)
            if actual_mode != before_mode:
                api(request, "/configs", "PATCH", {"mode": before_mode})
            if api(request, "/configs").get("mode") != before_mode:
                raise RuntimeError("原模式未确认恢复")
            if old_state is None:
                if state_path.exists():
                    state_path.unlink()
            else:
                atomic_write(state_path, old_state)
        except Exception:
            raise RuntimeError("代理切换与恢复未能全部确认，请刷新或通过 SSH 检查；不要重复提交")
        raise RuntimeError("代理切换失败，原环境与模式已恢复：" + str(error))
    message = "系统代理已开启，已恢复原运行模式" if enabled else "系统代理已关闭；TUN 仍在接管流量" if not plan["directOverride"] else "两个开关均已关闭，已恢复直连；旧终端无需清理代理变量"
    return dict(system_proxy_status(), mode=plan["appliedMode"], message=message)


def trusted_system_core():
    for name in ("/usr/bin/verge-mihomo", "/usr/bin/mihomo"):
        path = pathlib.Path(name).resolve()
        try:
            if path.is_file() and all(item.stat().st_uid == 0 and item.stat().st_mode & 0o022 == 0 for item in [path] + list(path.parents)):
                return str(path)
        except OSError:
            pass
    return ""


def tun_capabilities(request):
    info = service_info(request)
    pid = str(info.get("MainPID", "0"))
    capability = False
    if pid.isdigit() and int(pid) > 0:
        try:
            fields = dict(line.split(":", 1) for line in pathlib.Path("/proc", pid, "status").read_text().splitlines() if ":" in line)
            capability = bool(int(fields.get("CapEff", "0").strip(), 16) & (1 << 12))
        except (OSError, ValueError):
            pass
    device = pathlib.Path("/dev/net/tun")
    present = device.exists() and stat.S_ISCHR(device.stat().st_mode)
    linger = "Linger=yes" in run(["loginctl", "show-user", str(os.getuid()), "-p", "Linger"], check=False).stdout if shutil.which("loginctl") else False
    timer = bool(shutil.which("systemd-run"))
    running = info.get("ActiveState") == "active"
    return {"capable": capability, "device": present, "linger": linger, "timer": timer,
            "running": running, "ready": bool(capability and present and linger and timer and running)}


def network_status(request):
    config = current_config(config_path(request))
    runtime = {}
    api_error = ""
    try:
        runtime = api(request, "/configs")
    except Exception:
        api_error = "管理接口暂不可达"
    caps = tun_capabilities(request)
    state = read_network_state(request)
    proxy = system_proxy_status()
    return {"systemProxy": proxy, "takeover": takeover_status(config, runtime, state, proxy, api_error), "tun": dict(caps,
            enabled=bool(runtime.get("tun", {}).get("enable", False)), configured=bool(config.get("tun", {}).get("enable", False)),
            pending=state.get("phase") == "pending", apiError=api_error),
            "architecture": platform.machine(), "trustedSystemCore": bool(trusted_system_core())}


class SiteRedirectError(ValueError):
    pass


class SiteRedirect(urllib.request.HTTPRedirectHandler):
    def __init__(self, hostname):
        self.hostname = hostname
        self.count = 0

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        target = urllib.parse.urlsplit(newurl)
        self.count += 1
        if self.count > 4 or target.scheme != "https" or target.username or not (target.hostname == self.hostname or (target.hostname or "").endswith("." + self.hostname)):
            raise SiteRedirectError("网站重定向超出测试范围")
        redirected = super().redirect_request(req, fp, code, msg, headers, newurl)
        if redirected:
            redirected.set_proxy(req.host, "http")
            redirected.add_unredirected_header("Host", target.netloc)
        return redirected


def site_failure(error):
    # Classify locally; raw exception text can contain URLs and credentials.
    reason = error.reason if isinstance(error, urllib.error.URLError) else error
    if isinstance(reason, (TimeoutError, socket.timeout)):
        return "timeout", "连接超时（8 秒），请检查服务器出口或当前节点"
    if isinstance(reason, ssl.SSLCertVerificationError):
        return "certificate", "TLS 证书校验失败，请检查服务器时间及 CA 证书"
    if isinstance(reason, ssl.SSLError):
        return "tls", "TLS 握手失败，请检查服务器出口或当前节点"
    if isinstance(reason, socket.gaierror):
        return "dns", "DNS 解析失败，请检查服务器及内核 DNS"
    if isinstance(reason, ConnectionRefusedError):
        return "refused", "连接被拒绝，请检查远端代理服务及出口"
    if isinstance(reason, ConnectionResetError):
        return "reset", "连接被重置，请检查服务器出口或当前节点"
    if isinstance(reason, SiteRedirectError):
        return "redirect", "网站重定向超出测试范围"
    return "connection", "代理或网站连接失败，请检查服务器出口或当前节点"


def test_sites(request):
    selected = request.get("site", "all")
    if selected != "all" and selected not in SITE_TARGETS:
        raise ValueError("只支持预设的四个网站")
    port = int(current_config(config_path(request)).get("mixed-port") or 7890)
    if not 1 <= port <= 65535:
        raise ValueError("代理端口无效")
    def single(key):
        name, url = SITE_TARGETS[key]
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), SiteRedirect(urllib.parse.urlsplit(url).hostname))
        started = time.monotonic()
        try:
            probe = urllib.request.Request(url, headers={"User-Agent": "ClashOnWebSSH/0.4.0", "Range": "bytes=0-0"})
            # Explicit CONNECT avoids environment NO_PROXY silently bypassing mihomo.
            probe.set_proxy("127.0.0.1:" + str(port), "http")
            # set_proxy runs before urllib's header preparation here. Without an
            # explicit origin Host, urllib sends the loopback proxy address to
            # the website inside TLS (GitHub then redirects back to itself).
            probe.add_unredirected_header("Host", urllib.parse.urlsplit(url).netloc)
            with opener.open(probe, timeout=8) as response:
                status = response.status
        except urllib.error.HTTPError as error:
            status = error.code
            error.close()
        except Exception as error:
            kind, message = site_failure(error)
            return {"id": key, "name": name, "delay": None, "httpStatus": None, "errorKind": kind, "message": message}
        return {"id": key, "name": name, "delay": round((time.monotonic() - started) * 1000), "httpStatus": status,
                "message": "连接正常" if 200 <= status < 400 else "已响应，访问受限" if status in (401, 403, 429) else "网站返回错误"}
    keys = list(SITE_TARGETS) if selected == "all" else [selected]
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        return {"results": list(pool.map(single, keys)), "via": "远端当前网络设置", "sampledAt": time.time()}


def tun_dropin(request):
    return private_path(HOME / ".config/systemd/user" / (service_name(request) + ".d") / "70-clashonweb-tun.conf")


def tun_preparation_error(request, error):
    # Capture the failed invocation before restoring/restarting the original unit.
    # Read only fixed systemd properties: raw journals may contain subscription credentials.
    try:
        result = run(["systemctl", "--user", "show", service_name(request),
                      "--property=ExecMainCode,ExecMainStatus,Result"], timeout=10, check=False)
        fields = dict(line.split("=", 1) for line in result.stdout.splitlines() if "=" in line)
        if fields.get("ExecMainCode") == "1":  # CLD_EXITED; a signal number is not a systemd exit status.
            status = fields.get("ExecMainStatus")
            if status == "218":
                return "服务启动失败（218/CAPABILITIES），用户服务无法应用能力限制"
            if status == "203":
                return "服务启动失败（203/EXEC），请检查独立内核的执行权限、ACL及宿主能力限制"
            if status == "227":
                return "服务启动失败（227/NO_NEW_PRIVILEGES），无法设置 NoNewPrivileges"
            if status and status.isdigit() and status != "0":
                return "服务进程退出（状态码 " + status[:5] + "）"
    except Exception:
        pass
    if isinstance(error, subprocess.TimeoutExpired):
        return "服务重启或管理接口检查超时"
    return redact(error)[:240]


def prepare_tun_service(request):
    # The privileged bootstrap has only installed a root-owned, UID-restricted binary.
    # This unprivileged step changes only this user's reviewed unit override.
    uninstall_plan(request)
    binary = TUN_ROOT / str(os.getuid()) / "mihomo"
    if not binary.is_file() or binary.stat().st_uid != 0 or binary.stat().st_mode & 0o022:
        raise RuntimeError("尚未安装可信的独立 TUN 内核，请先准备管理员权限")
    cap = run(["getcap", str(binary)]).stdout
    if "cap_net_admin" not in cap or "cap_net_raw" not in cap:
        raise RuntimeError("独立内核的网络权限尚未准备完成")
    if current_config(config_path(request)).get("tun", {}).get("enable"):
        raise RuntimeError("准备权限前请先关闭配置中的 TUN")
    dropin = tun_dropin(request)
    old = dropin.read_bytes() if dropin.exists() else None
    if old and not old.startswith(TUN_MARKER.encode()):
        raise RuntimeError("TUN 服务覆盖文件已存在且不属于本应用")
    config = config_path(request)
    # A non-root user manager cannot drop the host capability bounding set.
    # File capabilities on the root-owned/UID-restricted executable grant only
    # NET_ADMIN/NET_RAW at exec; preserve inherited host/unit restrictions.
    content = (TUN_MARKER + "\n[Service]\nExecStart=\nExecStart=" + systemd_quote(binary) + " -d " + systemd_quote(config.parent) + " -f " + systemd_quote(config) +
               "\nNoNewPrivileges=no\n").encode()
    try:
        atomic_write(dropin, content)
        run(["systemctl", "--user", "daemon-reload"])
        run(["systemctl", "--user", "restart", service_name(request)], timeout=35)
        wait_api(request)
        caps = tun_capabilities(request)
        if not caps["ready"]:
            missing = [label for key, label in (("capable", "CAP_NET_ADMIN"), ("device", "/dev/net/tun"),
                       ("linger", "linger"), ("timer", "systemd-run"), ("running", "运行中的服务")) if not caps.get(key)]
            raise RuntimeError("服务启动后仍缺少：" + "、".join(missing))
        if dropin.read_bytes() != content:
            raise RuntimeError("TUN 服务覆盖出现外部修改")
    except Exception as error:
        reason = tun_preparation_error(request, error)
        try:
            current = dropin.read_bytes() if dropin.exists() else None
            if current not in (old, content):
                raise RuntimeError("服务覆盖出现外部修改，未覆盖")
            if old is None:
                if dropin.exists():
                    dropin.unlink()
            else:
                atomic_write(dropin, old)
            run(["systemctl", "--user", "daemon-reload"])
            run(["systemctl", "--user", "restart", service_name(request)], timeout=35)
            wait_api(request)
            if service_info(request).get("ActiveState") != "active" or (dropin.read_bytes() if dropin.exists() else None) != old:
                raise RuntimeError("原服务状态未确认")
        except Exception:
            raise RuntimeError("TUN 权限应用失败：" + reason + "；恢复未能确认，请通过 SSH 检查服务状态，勿重复开启") from error
        raise RuntimeError("TUN 权限应用失败：" + reason + "；已撤销本次服务覆盖，原服务及管理 API 已恢复") from error
    return dict(network_status(request), message="TUN 权限已准备，服务已重启；可使用概览开关启用 TUN")


def network_dir(request):
    directory = private_path(network_state_path(request).parent)
    directory.mkdir(parents=True, exist_ok=True, mode=0o700)
    os.chmod(directory, 0o700)
    return directory


def rollback_program(request):
    return "STATE_PATH = " + repr(str(network_state_path(request))) + "\n" + NETWORK_ROLLBACK_SCRIPT


def schedule_rollback(request, state, original):
    directory = network_dir(request)
    atomic_write(directory / "before.yaml", original)
    atomic_write(directory / "rollback.py", rollback_program(request).encode())
    atomic_write(network_state_path(request), json.dumps(state).encode())
    try:
        run(["systemd-run", "--user", "--unit=" + state["timer"], "--on-active=75s", "--timer-property=AccuracySec=1s", "--collect",
             sys.executable, str(directory / "rollback.py")], timeout=15)
    except Exception:
        state["phase"] = "cancelled"
        atomic_write(network_state_path(request), json.dumps(state).encode())
        raise RuntimeError("无法安排 TUN 自动回退，未改变网络设置")


def tun_candidate(config):
    result = copy.deepcopy(config)
    tun = result.setdefault("tun", {})
    if not isinstance(tun, dict):
        raise ValueError("当前 TUN 配置格式无效")
    bypass = list(tun.get("route-exclude-address", []) or [])
    bypass += ["127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "::1/128", "fc00::/7", "fe80::/10"]
    connection = os.environ.get("SSH_CONNECTION", "").split()
    if not connection:
        raise RuntimeError("无法确认 SSH 客户端地址，未开启 TUN")
    peer = ipaddress.ip_address(connection[0])
    bypass.append(str(peer) + ("/32" if peer.version == 4 else "/128"))
    tun.update(enable=True, device="cws" + str(os.getuid()), **{"auto-route": True, "auto-detect-interface": True, "auto-redirect": False, "strict-route": False,
                              "route-exclude-address": list(dict.fromkeys(bypass)), "dns-hijack": ["any:53", "tcp://any:53"]})
    tun.setdefault("stack", "system")
    # An include-only route supplied by a subscription must not narrow the managed full-TUN switch.
    for key in ("route-address", "route-address-set", "inet4-route-address", "inet6-route-address"):
        tun.pop(key, None)
    dns = result.setdefault("dns", {})
    if not isinstance(dns, dict):
        raise ValueError("当前 DNS 配置格式无效")
    if not dns.get("enable"):
        dns.update(enable=True, **{"enhanced-mode": "redir-host"})
        dns.setdefault("nameserver", ["https://1.1.1.1/dns-query", "https://8.8.8.8/dns-query"])
    return result


def verify_tun_device(config):
    name = config.get("tun", {}).get("device", "")
    if not re.fullmatch(r"cws[0-9]{1,10}", name):
        raise RuntimeError("TUN 设备名称未确认")
    try:
        flags = int(pathlib.Path("/sys/class/net", name, "flags").read_text().strip(), 16)
        if not flags & 1:
            raise ValueError()
    except (OSError, ValueError):
        raise RuntimeError("内核未创建并启用 TUN 设备")


def set_tun(request):
    import yaml
    enabled = request.get("enabled")
    if type(enabled) is not bool:
        raise ValueError("TUN 开关值必须为布尔值")
    path = config_path(request)
    if path.is_symlink() or not path.is_file():
        raise ValueError("当前运行配置不存在或为符号链接")
    original = path.read_bytes()
    config = load_yaml(original)
    runtime = api(request, "/configs")
    state = read_network_state(request)
    plan = takeover_plan(config, runtime, state, system_proxy_status()["enabled"], enabled)
    if bool(runtime.get("tun", {}).get("enable")) == enabled and bool(config.get("tun", {}).get("enable")) == enabled and runtime.get("mode", "rule") == config.get("mode", "rule") == plan["appliedMode"]:
        return {"enabled": enabled, "transaction": "", "message": "TUN 已开启" if enabled else "TUN 已关闭"}
    if enabled:
        if not tun_capabilities(request)["ready"]:
            raise RuntimeError("TUN 运行条件尚未满足，请先准备权限并启用脱离登录运行")
        candidate = tun_candidate(config)
    else:
        candidate = copy.deepcopy(config)
        candidate.setdefault("tun", {})["enable"] = False
        if state.get("phase") == "active" and state.get("appliedDnsHash") == hashlib.sha256(json.dumps(config.get("dns"), sort_keys=True).encode()).hexdigest():
            if state.get("hadDns"):
                candidate["dns"] = state["originalDns"]
            else:
                candidate.pop("dns", None)
    candidate["mode"] = plan["appliedMode"]
    content = yaml.safe_dump(candidate, allow_unicode=True, sort_keys=False).encode("utf-8")
    with tempfile.TemporaryDirectory(prefix=".clashonweb-tun-check-", dir=path.parent) as staging:
        staged = pathlib.Path(staging) / "candidate.yaml"
        staged.write_bytes(content)
        os.chmod(staged, 0o600)
        verify_core(core_binary(request), staged, path.parent)
    if file_hash(path) != hashlib.sha256(original).hexdigest():
        raise RuntimeError("配置已被其他程序修改，未切换 TUN")
    transaction = uuid.uuid4().hex
    catalog = read_catalog(request)
    new_state = dict(plan, phase="pending" if enabled else "off", transaction=transaction)
    new_state.update({"service": service_name(request), "config": str(path),
                 "previousTakeover": {key: state.get(key) for key in ("resumeMode", "directOverride", "appliedMode")}, "originalMode": config.get("mode", "rule"),
                 "profileId": current_profile_id(request, catalog), "catalogHash": catalog.get("activeHash", ""),
                 "beforeHash": hashlib.sha256(original).hexdigest(), "afterHash": hashlib.sha256(content).hexdigest(),
                 "timer": "clashonweb-tun-" + transaction, "deadline": time.time() + 75,
                 "apiPort": int(request.get("apiPort", 9090)), "secret": config.get("secret", ""), "originalTun": bool(config.get("tun", {}).get("enable")),
                 "hadDns": "dns" in config, "originalDns": config.get("dns"),
                 "appliedDnsHash": hashlib.sha256(json.dumps(candidate.get("dns"), sort_keys=True).encode()).hexdigest()})
    if enabled:
        schedule_rollback(request, new_state, original)
    try:
        atomic_write(path, content)
        run(["systemctl", "--user", "restart", service_name(request)], timeout=30)
        wait_api(request)
        actual = api(request, "/configs")
        if bool(actual.get("tun", {}).get("enable")) != enabled or actual.get("mode", "rule") != plan["appliedMode"]:
            raise RuntimeError("内核未确认 TUN 或直连状态")
        if enabled:
            verify_tun_device(candidate)
    except Exception:
        try:
            if file_hash(path) not in (new_state["afterHash"], new_state["beforeHash"]):
                raise RuntimeError("配置已被其他程序修改")
            atomic_write(path, original)
            run(["systemctl", "--user", "restart", service_name(request)], timeout=30)
            wait_api(request)
            if enabled:
                new_state["phase"] = "rolled-back"
                new_state.update(new_state["previousTakeover"])
                atomic_write(network_state_path(request), json.dumps(new_state).encode())
                run(["systemctl", "--user", "stop", new_state["timer"] + ".timer"], check=False)
        except Exception:
            raise RuntimeError("TUN 切换未确认，立即恢复也未确认；自动回退仍将尝试，请稍后刷新或通过 SSH 检查")
        raise RuntimeError("TUN 切换失败，原配置及服务已恢复")
    if not enabled:
        network_dir(request)
        atomic_write(network_state_path(request), json.dumps(new_state).encode())
    return {"enabled": enabled, "transaction": transaction if enabled else "", "mode": plan["appliedMode"],
            "message": "TUN 已暂时开启，等待客户端确认" if enabled else "两个开关均已关闭，已恢复直连；旧终端无需清理代理变量" if plan["directOverride"] else "TUN 已关闭，系统代理仍开启"}


def confirm_tun(request):
    state = read_network_state(request)
    if state.get("phase") != "pending" or request.get("transaction") != state.get("transaction") or time.time() >= state.get("deadline", 0) - 5:
        raise RuntimeError("TUN 确认已过期，请等待自动回退后刷新")
    actual = api(request, "/configs")
    if file_hash(config_path(request)) != state.get("afterHash") or not actual.get("tun", {}).get("enable") or actual.get("mode", "rule") != state.get("appliedMode", "rule"):
        raise RuntimeError("TUN 状态发生变化，未取消自动回退")
    verify_tun_device(current_config(config_path(request)))
    run(["systemctl", "--user", "stop", state["timer"] + ".timer"])
    # The rollback service shares control.lock. If its timer already fired, stopping
    # the timer alone cannot cancel the running service; reject this late confirmation.
    running = run(["systemctl", "--user", "is-active", state["timer"] + ".service"], check=False).stdout.strip()
    if running in ("active", "activating"):
        raise RuntimeError("自动回退已经开始，请稍后刷新")
    state["phase"] = "active"
    atomic_write(network_state_path(request), json.dumps(state).encode())
    return {"enabled": True, "message": "TUN 已开启，SSH 连通已确认"}


def network_execute(request):
    action = request.get("action")
    if action == "network.status":
        return network_status(request)
    if action == "network.test":
        return test_sites(request)
    with control_lock():
        if action != "network.tun.confirm":
            ensure_network_settled(request)
        if action == "network.system-proxy":
            return set_system_proxy(request)
        if action == "network.mode":
            return set_proxy_mode(request)
        if action == "network.tun.prepare":
            return prepare_tun_service(request)
        if action == "network.tun.set":
            return set_tun(request)
        if action == "network.tun.confirm":
            return confirm_tun(request)
        raise ValueError("不支持的网络动作")
