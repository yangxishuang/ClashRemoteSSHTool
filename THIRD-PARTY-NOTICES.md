# 第三方组件与来源

应用0.2.1；2026-10-20。本应用是独立WPF界面及SSH动作适配，未复制MetaCubeXD/Zashboard前端，也未修改服务器现有clashctl。开源复用集中在SSH库、.NET和远端mihomo。

| 组件 | 版本 | 许可 / 来源 | 使用方式 |
| --- | --- | --- | --- |
| SSH.NET | 2026.0.0 | MIT；[官方源码](https://github.com/sshnet/SSH.NET/tree/2026.0.0)、[NuGet](https://www.nuget.org/packages/SSH.NET/2026.0.0) | 随EXE分发，密码SSH |
| BouncyCastle.Cryptography | 2.7.0 | MIT；[官方包](https://www.nuget.org/packages/BouncyCastle.Cryptography/2.7.0) | SSH.NET依赖 |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 | MIT；[官方包](https://www.nuget.org/packages/Microsoft.Extensions.Logging.Abstractions/8.0.3) | SSH.NET依赖 |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | MIT；[官方包](https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection.Abstractions/8.0.2) | 日志抽象依赖，无应用DI框架 |
| .NET / WindowsDesktop运行库 | 10.0.13 | MIT及随附第三方声明；[dotnet](https://github.com/dotnet) | 自包含分发 |
| Windows OpenSSH | 系统安装版本 | [官方项目](https://github.com/PowerShell/Win32-OpenSSH) | 外部系统工具，不随EXE分发 |
| mihomo | 新部署v1.19.31；已有服务保留原版 | GPL-3.0；[官方发布/对应源码](https://github.com/MetaCubeX/mihomo/releases/tag/v1.19.31) | 运行时从官方站下载到远端，不嵌入EXE |
| Python / PyYAML | 远端包管理器版本 | [Python](https://www.python.org/)、[PyYAML](https://pyyaml.org/) | 远端依赖，不嵌入EXE |

`licenses/`保留随分发依赖的版权、许可及第三方声明。SSH.NET许可来自固定标签官方LICENSE，其余从固定版本官方NuGet包提取。发布文件夹及压缩包保留这些文件。上游MIT许可不自动代表本仓库自编代码已声明MIT。

开发SDK为10.0.402，官方SDK压缩包已校验SHA-512；SDK及开发期Paramiko、PyYAML不随EXE分发。mihomo下载哈希固定在Remote/remote.py；更新内核版本时须同步官方哈希、来源、文档及安装测试。
