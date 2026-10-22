# Clash 遥控台 · SSH

Windows 本机图形 SSH 命令遥控器，采用 **C#＋WPF＋.NET 10 LTS**，管理 Linux 服务器上的 mihomo，无需部署 Web 后台。

**版本0.7.1，2026-10-22。** [打开新EXE](artifacts/ClashOnWebSSH/ClashOnWebSSH.exe) · [分发包](artifacts/ClashOnWebSSH.zip) · [使用说明](docs/USER-GUIDE.md) · [测试记录](docs/testing/VERIFICATION-20261022-0.7.1.md)

采用FlClash风格的中性表面、蓝色强调和左侧导航，支持深浅色主题及页面过渡。版本历史通过Git提交/标签管理，固定EXE与ZIP路径不随版本变化。

概览顶部的代理服务与代理模式各半宽，规则/全局/直连在同一组选项卡中横滑切换，保存远端配置并回读。网络设置与网站测试也各半宽，标题与内边距对齐，网站测试单行四项且移除标题下小字；原“恢复直连”由代理模式的“直连”项替代。

0.7.1修复TUN权限准备时用户服务出现218/CAPABILITIES：保留独立root内核的文件能力与UID ACL，取消用户unit中的CapabilityBoundingSet；失败保留具体原因，原服务及API均回读成功才显示已恢复。

网络选项卡内容区缩到约原来一半，保留开关及状态。点击TUN先回读条件，已就绪直接开启；缺权限再显示准备窗口，确认“准备并开启TUN”后自动继续，取消或准备失败不提交开启。

配置页点击整张卡片切换，当前配置整卡高亮；更新统一放在右上角“⋯”菜单，重复点击当前配置不会重启。

代理节点改为FlClash式卡片网格，随内容区宽度自动增减列数；整卡选择、右下角独立测速，长名称最多两行。大分组只创建可见区域及相邻行卡片，搜索后连续重排。

0.5.2将卡片高度由132缩小到112 DIP，收紧上下留白；顶部按钮改名为“测试延迟”，继续测试当前分组。

## 功能

- 系统 OpenSSH 免密登录及 SSH 密码登录；地址栏下拉选择成功连接过的服务器，保存用户名/端口/高级参数、加密密码或私钥路径。启动默认SSH页，填好后点击一次连接；可删除记录。
- 服务、版本、模式、端口、分组和当前节点查询，节点搜索。
- 规则/全局/直连模式切换，校验并保存运行配置，不重启服务；已有代理连接按新模式重连。
- Selector 节点切换、单节点/分组测速、服务启停/重启。
- 最近日志、远端代理连通检查、复制终端代理命令。
- 订阅链接/YAML导入、配置卡片、流量/到期、切换、更新、重命名及删除；切换保护固定设置并支持失败恢复。
- 无Clash主动打开部署向导；已有服务显示管理及卸载，配置库保留以便重新部署。
- 部署步骤卡片显示边框加载动画、当前任务和计时；管理员密码框自动识别root/免密sudo，使用猫咪应用图标。
- 内核由本机自动从加速源/官方源下载，校验并缓存后经SSH上传，显示字节进度；在线源不可用时自动使用内置内核；四类GeoData由本机内置数据准备并上传，手动包仅作为最后后备。
- 订阅自动读取来源名称，普通操作结果用应用内绿色/红色提示；折叠导航居中，默认分组遵循配置顺序和运行模式。

- 概览可开关系统代理与远端TUN；双关恢复直连，旧终端无需清理变量，重新开启恢复原模式。提供ChatGPT/GitHub/Google/Grok网站测试。
- SSH入口位于概览下方，尚未连接时可直接点击概览中的“点击连接”。

首次部署支持Linux amd64/arm64及systemd用户会话，已有系统内核可复用；新下载固定mihomo v1.19.31并验SHA-256，不覆盖配置/unit。多订阅合并、规则编辑、自动调度和多机平台不在范围。TUN按用户新增要求纳入0.4.0。

## 运行与构建

双击 EXE，无需另装 .NET 或本机 Python。免密模式使用 Windows OpenSSH 的已有配置；密码模式使用随附 SSH.NET。测试目标 `example.invalid`、用户名 `user`，需先登录校园网 VPN。

本机保存连接记录、密码模式主机指纹及主题偏好，位置 `%LOCALAPPDATA%\ClashOnWebSSH`。SSH密码通过Windows DPAPI当前用户加密，保存在connections.json；sudo密码、私钥内容及订阅URL不写本机设置。选择记录不自动连接，主机指纹照常核验。关闭窗口不停止远端代理。

安装 .NET SDK **10.0.402** 后：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build.ps1
python -m pip install -r tests/requirements.txt
powershell -ExecutionPolicy Bypass -File scripts/test.ps1
```

产物在`artifacts/ClashOnWebSSH/`，自包含win-x64单文件，约219MiB。构建支持-NoPublish、-OutputDirectory及-RestoreSource；本轮使用.tools/nuget-feed。固定目录更新同一EXE，运行中的窗口不会被强制关闭，连接设置沿用。

本轮279项检查通过（133项Python、62项C#本地/回环SSH、84项WPF）。真实服务器只读日志确认TUN准备失败为218/CAPABILITIES；修复用户unit不兼容的能力限制，失败保留原因且核验恢复。配置摘要/PID不变，未执行真实权限安装、重启或TUN切换；修复后的真实Linux启用仍待用户操作验收。

[文档目录](docs/README.md) · [设计](docs/LOCAL-SSH-DESIGN.md) · [按钮映射](docs/COMMAND-MAPPING.md) · [技术选择](docs/DESKTOP-TECHNOLOGY.md) · [第三方说明](THIRD-PARTY-NOTICES.md)

当前基线 **DESKTOP-SSH-1.6**。配置/卸载规格见[配置管理](docs/CONFIGURATION-PROFILES.md)，下载流程见[内核部署](docs/KERNEL-DEPLOYMENT.md)，名称及默认分组见[交互说明](docs/INTERACTION-AND-NAMING.md)。网络/代理模式见[网络规格](docs/NETWORK-CONTROLS.md)及[ADR-013](docs/adr/ADR-013-EXPLICIT-PROXY-MODES.md)。功能、现场差异和测试持续更新，旧平台方案保留于[历史快照](docs/archive/PRECODE-0.1/ARCHIVE-NOTICE.md)，不作为当前实施要求。
