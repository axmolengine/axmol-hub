# Windows 安装包

在 Windows 上准备 .NET 8 SDK，使用仓库内固定版本 Inno Setup 清单准备编译器：

```powershell
dotnet run --project tests/AxmolHub.Checks -- artifacts/packaging-tools --prepare-installer
./installer/Build.ps1
./installer/Test.ps1 -Isolated
```

输出为 `artifacts/installer/AxmolHub-0.1.6-win-x64-setup.exe` 及 SHA-256。安装器支持中英文、选择目录、当前用户安装、开始菜单入口和可选桌面快捷方式。

安装包只包含自包含 Hub、清单和许可文件，不捆绑引擎、工具链、Debug CRT、测试数据或 `hub-settings.json`。用户无需 .NET SDK。升级保留设置，卸载保留用户数据、项目和单独登记的微软实例。

`--prepare-installer` 校验固定下载摘要及 Pyrsys B.V. Authenticode 签名，将编译器准备到工作区，不修改 PATH。中文翻译文件的原说明保持不变，构建时校验其原始摘要。

`Test.ps1 -Isolated` 使用独立 GUID、程序名、默认目录和快捷方式编译一次性检查包，验证安装、启动、中文目录、升级保留设置及卸载保留数据。结束后清理检查身份，保留现有 Hub；结果位于 `artifacts/installer-checks/`。

MSVC 安装仍需微软安装器和 Windows UAC，与 Hub 当前用户安装分开。发布包暂未配置发布者代码签名；正式发行前请检查所用 Inno Setup 版本的许可和商业使用要求，见 [Inno Setup](https://jrsoftware.org/isinfo.php) 与 [第三方声明](../THIRD_PARTY_NOTICES.md)。

`Build-Hosts.ps1` 发布其他宿主的自包含 CLI；`Build-Icon.ps1` 从源 PNG 生成 16–256 像素多尺寸 ICO。修改图标后先重新生成 ICO，再构建应用。
