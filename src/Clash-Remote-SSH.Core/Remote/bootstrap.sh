#!/bin/sh
set -eu
if [ "${1:-dependencies}" = linger ]; then
    target_user="${SUDO_USER:-$(id -un)}"
    command -v loginctl >/dev/null 2>&1 || { echo '系统缺少 loginctl'; exit 1; }
    loginctl enable-linger "$target_user"
    echo "已为 $target_user 启用用户服务脱离登录运行。"
    exit 0
fi
if command -v apt-get >/dev/null 2>&1; then
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -qq
    apt-get install -y python3 python3-yaml ca-certificates
elif command -v dnf >/dev/null 2>&1; then
    dnf install -y python3 python3-pyyaml ca-certificates
elif command -v yum >/dev/null 2>&1; then
    yum install -y python3 python3-pyyaml ca-certificates
else
    echo '自动依赖准备支持 Debian/Ubuntu 和 Fedora/RHEL 系列；请先手动安装 Python 3、PyYAML 和 CA 证书。'
    exit 1
fi
command -v systemctl >/dev/null 2>&1 || { echo '当前主机没有 systemd，暂不支持自动部署用户服务。'; exit 1; }
echo 'Python 3、YAML 与证书已准备完成。'
