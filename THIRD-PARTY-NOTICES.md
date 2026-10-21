# 第三方组件与来源

应用0.5.0；2026-10-21。本应用是独立WPF界面及SSH动作适配，未复制MetaCubeXD/Zashboard前端，也未修改服务器现有clashctl。开源复用集中在SSH库、.NET和远端mihomo。

| 组件 | 版本 | 许可 / 来源 | 使用方式 |
| --- | --- | --- | --- |
| SSH.NET | 2026.0.0 | MIT；[官方源码](https://github.com/sshnet/SSH.NET/tree/2026.0.0)、[NuGet](https://www.nuget.org/packages/SSH.NET/2026.0.0) | 随EXE分发，密码SSH |
| BouncyCastle.Cryptography | 2.7.0 | MIT；[官方包](https://www.nuget.org/packages/BouncyCastle.Cryptography/2.7.0) | SSH.NET依赖 |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 | MIT；[官方包](https://www.nuget.org/packages/Microsoft.Extensions.Logging.Abstractions/8.0.3) | SSH.NET依赖 |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | MIT；[官方包](https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection.Abstractions/8.0.2) | 日志抽象依赖，无应用DI框架 |
| .NET / WindowsDesktop运行库 | 10.0.13 | MIT及随附第三方声明；[dotnet](https://github.com/dotnet) | 自包含分发 |
| Windows OpenSSH | 系统安装版本 | [官方项目](https://github.com/PowerShell/Win32-OpenSSH) | 外部系统工具，不随EXE分发 |
| mihomo | 新部署v1.19.31；已有服务保留原版 | GPL-3.0；[官方发布/对应源码](https://github.com/MetaCubeX/mihomo/releases/tag/v1.19.31) | 未修改官方Linux包随EXE内嵌；在线失败后自动校验并SSH上传 |
| MetaCubeX/meta-rules-dat | 2026-10-21，eed74d50固定快照 | GPL-3.0；[上游](https://github.com/MetaCubeX/meta-rules-dat) | 四类GeoData内嵌，固定哈希后SSH上传 |
| Python / PyYAML | 远端包管理器版本 | [Python](https://www.python.org/)、[PyYAML](https://pyyaml.org/) | 远端依赖，不嵌入EXE |

`licenses/`保留随分发依赖的版权、许可及第三方声明。SSH.NET许可来自固定标签官方LICENSE，其余从固定版本官方NuGet包提取。发布文件夹及压缩包保留这些文件。上游MIT许可不自动代表本仓库自编代码已声明MIT。

开发SDK为10.0.402，官方SDK压缩包已校验SHA-512；SDK及开发期Paramiko、PyYAML不随EXE分发。mihomo版本/下载哈希共用Remote/kernel-manifest.json；更新内核版本时须同步官方哈希、来源、文档及安装测试。GH-Proxy和GHFast为第三方加速服务，未作为组件打包；详见docs/KERNEL-DEPLOYMENT.md。

界面设计参考：用户提供的D:/Codes/FlClash-main源码（GPL-3.0），阅读主题、表面、圆角、导航及过渡实现后使用WPF独立重绘；本轮不分发FlClash的Flutter组件、图标、字体或图片。参考文件及本项目参数见docs/FLCLASH-UI.md。

0.3.2另阅读FlClash的lib/models/profile.dart、lib/common/request.dart及lib/providers/state/proxies.dart，参考响应文件名和按模式过滤分组的行为，用C#/Python独立实现。新增交互规格与验证见docs/INTERACTION-AND-NAMING.md。

0.5.0增加内置Linux内核与GeoData原始文件，许可及固定源码/数据快照来源见licenses/UPSTREAM-SOURCES.md。原始包内容未改动，内核/规则数据清单是校验依据；源代码、资产和许可使用Git管理，发布目录保留声明。
