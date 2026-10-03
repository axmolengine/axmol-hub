# Android 发行签名

本文是 README 中「Android 发行签名」的详细版。

![Android 发行设置](images/android-release.png)

在项目页打开 **Android 发行设置**，或在构建窗口选择 Android 和 Release 后继续：

1. 设置正式应用包名（Application ID）、版本代码（versionCode）和版本名称（versionName）。每次商店更新递增 versionCode。
2. 选择已有 JKS / PKCS12 密钥库，或点击"新建密钥库"创建 RSA 2048 位 JKS，默认有效期 10000 天；已有文件不覆盖。
3. 输入密钥别名、密钥库密码及私钥密码。私钥密码留空时使用密钥库密码；点击"验证并保存"。
4. 构建 Release，得到签名的 `app-release.apk` 和 `app-release.aab`。发行输出与 Debug 隔离。

## 凭据与配置存放

非密码配置保存在项目的 `.axmol-hub.android-release.json`，已由 Git 忽略。密码仅在当前会话使用，重启后重新输入；不会写入项目配置、Gradle 文件或命令参数。密钥库和密码需要单独备份，更新应用必须使用相应签名密钥。

## Release 校验

Release 原生代码使用优化构建，APK 关闭 debuggable。Hub 核对 APK 包名/版本、APK/AAB 证书 SHA-256、资源、签名和 16KB 对齐后才记录成功；AAB 使用私有 JDK 规范化签名，检查 JAR 文件与流式读取兼容性。更换发行配置或密钥库后必须重新构建。

## CLI 签名

CLI 复用同一非密码配置，从 `HUB_ANDROID_STORE_PASSWORD`、`HUB_ANDROID_KEY_PASSWORD` 环境变量读取本次签名密码；不要把密码放进提交的脚本或命令参数。

## 商店分发

Google Play 的 AAB 使用上传密钥，并需在 Play Console 配置 Play App Signing；自行分发可使用签名 APK。当前已用一次性测试密钥实际构建并校验 ARM64 Release；真机运行与商店提交尚未验收。参考 [Android 官方签名说明](https://developer.android.com/studio/publish/app-signing)。
