# 内置部署资源来源

本应用随EXE附带以下上游未修改资源，保留上游许可；资源版本、哈希及大小为仓库Remote清单中的固定值。

- mihomo v1.19.31（Linux amd64-compatible/arm64）：[官方发行版](https://github.com/MetaCubeX/mihomo/releases/tag/v1.19.31)、[对应源码](https://github.com/MetaCubeX/mihomo/tree/v1.19.31)、[源码归档](https://github.com/MetaCubeX/mihomo/archive/refs/tags/v1.19.31.tar.gz)。构建资料/依赖清单随该源码中的Makefile及go.mod/go.sum提供。许可见mihomo-v1.19.31-GPL-3.0.txt。
- MetaCubeX/meta-rules-dat：GeoSite/GeoIP/Country/ASN，固定release分支提交[eed74d50aa6083d028110ed3feaf37f5e3070d9d](https://github.com/MetaCubeX/meta-rules-dat/tree/eed74d50aa6083d028110ed3feaf37f5e3070d9d)，[固定快照归档](https://github.com/MetaCubeX/meta-rules-dat/archive/eed74d50aa6083d028110ed3feaf37f5e3070d9d.tar.gz)、[上游生成项目及数据来源说明](https://github.com/MetaCubeX/meta-rules-dat)。原始GeoLite2-ASN.mmdb按mihomo需要命名为ASN.mmdb，其他数据仅统一目标文件名，内容未修改。许可见meta-rules-dat-GPL-3.0.txt。

原始数据下载地址和逐文件SHA-256见src/ClashOnWebSSH.Core/Remote/geodata-manifest.json；内核见kernel-manifest.json。嵌入只是分发方式，文件校验及远端内核仍使用这些原始内容。
