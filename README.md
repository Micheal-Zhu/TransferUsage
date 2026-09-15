# Balance Dock

Windows 11 悬浮余额工具，面向基于 [New API](https://github.com/QuantumNous/new-api) 的中转站。

## 使用

1. 运行 `package.ps1`，发布产物位于 `发布`。
2. 双击 `发布\BalanceDock-Portable.exe`，点击右上角加号并输入任意站点页面 URL。
3. 在弹出的站点页面完成登录；检测到余额后会自动加入列表。

发布版站点配置保存在 `%LOCALAPPDATA%\BalanceDock-Portable`，与开发版数据隔离。账号密码不会由 Balance Dock 保存；登录会话由 WebView2 用户配置管理。

关闭悬浮窗会收进系统托盘。托盘菜单可切换始终置顶、开机启动，或彻底退出。

`package.ps1` 会先执行完整构建和自测，再将程序及 WebView2 依赖嵌入单文件启动器。打包清单是显式列出的运行时文件，不会读取或复制 `%LOCALAPPDATA%\BalanceDock` 下的 `stations.json`、`settings.json`、`WebView2` 用户数据或缓存。

详细的终端用户操作说明见 `使用教程.md`。
