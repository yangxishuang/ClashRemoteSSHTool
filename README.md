# Clash 遥控台 · SSH

Windows 本机图形 SSH 命令遥控器，采用 **C#＋WPF＋.NET 10 LTS**，管理 Linux 服务器上的 mihomo，无需部署 Web 后台。

**版本0.3.0，2026-10-20。** [打开新EXE](artifacts/ClashOnWebSSH/ClashOnWebSSH.exe) · [分发包](artifacts/ClashOnWebSSH.zip) · [使用说明](docs/USER-GUIDE.md) · [测试记录](docs/testing/VERIFICATION-20261020-0.3.0.md)

采用FlClash风格的中性表面、蓝色强调和左侧导航，支持深浅色主题及页面过渡。版本历史通过Git提交/标签管理，固定EXE与ZIP路径不随版本变化。

## 功能

- 系统 OpenSSH 免密登录及 SSH 密码登录，密码不写入本机设置。
- 服务、版本、模式、端口、分组和当前节点查询，节点搜索。
- Selector 节点切换、单节点/分组测速、服务启停/重启。
- 最近日志、远端代理连通检查、复制终端代理命令。
- 订阅链接/YAML导入、配置卡片、流量/到期、切换、更新、重命名及删除；切换保护固定设置并支持失败恢复。
- 无Clash主动打开部署向导；已有服务显示管理及卸载，配置库保留以便重新部署。
- 部署步骤卡片显示边框加载动画、当前任务和计时；管理员密码框自动识别root/免密sudo，使用猫咪应用图标。

首次部署支持Linux amd64/arm64及systemd用户会话，已有系统内核可复用；新下载固定mihomo v1.19.31并验SHA-256，不覆盖配置/unit。多订阅合并、规则编辑、TUN、自动调度和多机平台不在范围。

## 运行与构建

双击 EXE，无需另装 .NET 或本机 Python。免密模式使用 Windows OpenSSH 的已有配置；密码模式使用随附 SSH.NET。测试目标 `example.invalid`、用户名 `user`，需先登录校园网 VPN。

本机保存连接设置、密码模式主机指纹及主题偏好，位置 `%LOCALAPPDATA%\ClashOnWebSSH`。SSH/sudo 密码及订阅 URL 不写本机设置。关闭窗口不停止远端代理。

安装 .NET SDK **10.0.402** 后：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build.ps1
python -m pip install -r tests/requirements.txt
powershell -ExecutionPolicy Bypass -File scripts/test.ps1
```

产物在`artifacts/ClashOnWebSSH/`，自包含win-x64单文件，约141MiB。构建支持-NoPublish、-OutputDirectory及-RestoreSource；本轮使用.tools/nuget-feed。旧EXE保留，关闭旧版后运行新EXE，连接设置沿用。

44项Python隔离、24项C#本地/协议及16项WPF交互检查通过；真实订阅216节点、本机下载/SSH上传/远端内核校验通过，现场导入见验证记录。未对现有服务执行配置切换、重启或卸载。

[文档目录](docs/README.md) · [设计](docs/LOCAL-SSH-DESIGN.md) · [按钮映射](docs/COMMAND-MAPPING.md) · [技术选择](docs/DESKTOP-TECHNOLOGY.md) · [第三方说明](THIRD-PARTY-NOTICES.md)

当前基线 **DESKTOP-SSH-0.7**。配置/卸载规格见[配置管理](docs/CONFIGURATION-PROFILES.md)。功能、现场差异和测试持续更新，旧平台方案保留于[历史快照](docs/archive/PRECODE-0.1/ARCHIVE-NOTICE.md)，不作为当前实施要求。
