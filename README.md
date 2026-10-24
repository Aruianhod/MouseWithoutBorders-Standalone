# 无界鼠标独立版 / Mouse Without Borders Standalone

基于 Microsoft PowerToys Mouse Without Borders 的 Windows x64 独立构建。当前快照：**0.102.2933.10 · 开机登录自动启动**。这是个人维护的衍生项目，不是 Microsoft 官方发行版。

## 下载和使用

从本仓库 Releases 下载 `public-win-x64.zip`，完整解压到本机文件夹后运行 `MouseWithoutBorders.exe`。包含 .NET 运行时。两台电脑建议使用同一最新版本；历史版本保留用于追溯。

两端先完成鼠标配对与电脑排列，再配置需要的剪贴板、文件和音频功能。详细步骤见 [中文说明](README-zh-CN.txt)。默认管理员启动从 .9 引入，登录自启动从 .10 引入，需要时仍遵循 Windows 授权流程；不提供 UAC 安全桌面控制。

当前版本变更：增加当前用户登录后通过计划任务启动的可逆选项。

## 公开版本与历史

这些提交和 Release 来自开发过程中实际保存的 .1–.17 源码快照，本次按版本顺序整理发布。提交／Release 时间是公开整理时间，不是原开发时间；不补造开发日期。每个标签对应自身版本的源码，而非给同一份代码重复打标签。

公开分发重新构建，移除了私人局域网更新地址，默认不启用自动检查，编译源路径已规范化。因此文件哈希与最初局域网包不同。源码和分发物不包含配对信息、Windows 登录密码、私人密钥、用户配置或运行日志。

共享目录更新从 .7 引入，只支持配置好的本机／UNC 文件夹和固定公钥签名的清单；尚不支持直接通过 GitHub Releases 自动更新。普通用户从 Releases 手动下载整包升级。自行运营更新源需自行管理签名密钥并配置对应验证公钥；本仓库不提供签名私钥。

## 构建与验证

Windows x64、.NET SDK 10.0.402、PowerShell：

```powershell
./Build.ps1 -DotNet dotnet -Destination ./dist
./Test.ps1 -DotNet dotnet
```

NuGet 使用锁定依赖。详细功能及验证边界见 [CHANGES.md](CHANGES.md)。历史快照保留当时功能和限制，不能据此认为所有版本都通过了最新硬件测试。音频功能仍属实验性质；实际延迟受 Wi-Fi 和设备影响。

## 许可与上游

上游：Microsoft PowerToys `v0.102.2933.0`，提交 `9673c84ffac3bcc55e75fbecaedcda9e7d698530`。源码遵循 [MIT License](LICENSE)，保留 [上游声明](UPSTREAM-NOTICE.md) 和第三方许可。

音频所用 VB-Audio 虚拟驱动适用单独的专有／捐赠软件许可，不属于本项目 MIT 授权范围。附带的基础 VB-CABLE 为未经修改的官方原包；详情及来源见 Drivers 内的说明（从 .6 起）。请支持 [VB-Audio](https://vb-audio.com/Cable/)；Hi-Fi Cable 在需要时从官方来源下载。程序与发布包未进行 Windows Authenticode 签名。
