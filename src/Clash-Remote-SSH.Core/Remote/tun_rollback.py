"""One-shot user-systemd rollback job. STATE_PATH is injected by the fixed adapter."""
import fcntl
import hashlib
import json
import os
import pathlib
import subprocess
import tempfile
import time
import urllib.request


def rollback(state_path):
    home = pathlib.Path.home().resolve()
    state_path = pathlib.Path(state_path)
    state_path.resolve().relative_to(home)
    state = json.loads(state_path.read_text(encoding="utf-8"))
    if state.get("phase") != "pending":
        return
    path = pathlib.Path(state["config"])
    path.resolve().relative_to(home)
    if path.is_symlink() or state_path.is_symlink():
        raise RuntimeError("回退目标包含符号链接")
    original = (state_path.parent / "before.yaml").read_bytes()
    if hashlib.sha256(original).hexdigest() != state["beforeHash"]:
        raise RuntimeError("回退备份校验失败")
    current_hash = hashlib.sha256(path.read_bytes()).hexdigest() if path.is_file() else ""
    restored = current_hash in (state["afterHash"], state["beforeHash"])
    if restored:
        fd, temporary = tempfile.mkstemp(prefix=".clashonweb-rollback-", dir=path.parent)
        try:
            with os.fdopen(fd, "wb") as output:
                output.write(original)
            os.chmod(temporary, 0o600)
            os.replace(temporary, path)
        finally:
            if os.path.exists(temporary):
                os.unlink(temporary)
        state["phase"] = "rollback-failed"
        try:
            result = subprocess.run(["systemctl", "--user", "restart", state["service"]], capture_output=True, timeout=35)
            if result.returncode == 0:
                opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
                for attempt in range(8):
                    try:
                        request = urllib.request.Request("http://127.0.0.1:%d/configs" % state["apiPort"], headers={"Authorization": "Bearer " + state.get("secret", "")})
                        with opener.open(request, timeout=2) as response:
                            actual = json.loads(response.read(1024 * 1024))
                        if bool(actual.get("tun", {}).get("enable")) == state.get("originalTun", False) and actual.get("mode", "rule") == state.get("originalMode", "rule"):
                            state["phase"] = "rolled-back"
                            state.update(state.get("previousTakeover", {}))
                            break
                    except Exception:
                        pass
                    time.sleep(0.5)
        except Exception:
            pass
    else:
        # Never overwrite an external editor's config; still try to release runtime TUN routes.
        try:
            request = urllib.request.Request("http://127.0.0.1:%d/configs" % state["apiPort"], data=b'{"tun":{"enable":false}}', method="PATCH",
                                             headers={"Content-Type": "application/json", "Authorization": "Bearer " + state.get("secret", "")})
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
            with opener.open(request, timeout=8):
                pass
            state["phase"] = "external-config-runtime-disabled"
        except Exception:
            state["phase"] = "rollback-failed"
    fd, temporary = tempfile.mkstemp(prefix=".state-", dir=state_path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as output:
            json.dump(state, output)
        os.chmod(temporary, 0o600)
        os.replace(temporary, state_path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


if __name__ == "__main__":
    lock_path = pathlib.Path.home() / ".cache/clashonweb/control.lock"
    with lock_path.open("a") as lock:
        fcntl.flock(lock.fileno(), fcntl.LOCK_EX)
        rollback(STATE_PATH)
