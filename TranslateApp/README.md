# Little Tools · Translate

透明置顶的 Windows 悬浮翻译工具，视觉风格与 AI Usage Monitor 一致。

## 操作

- `Shift + Backspace`：显示或隐藏翻译窗口。
- 输入文字后按 `Enter`：翻译。
- 点击左上角语言方向：切换语言。
- 按住窗口空白区域直接拖动：移动窗口。
- `Esc`：隐藏窗口。
- 托盘菜单：显示/隐藏、开机自启、退出。

## API 配置

程序按以下顺序寻找百度翻译配置：

1. 环境变量 `BAIDU_TRANSLATE_APP_ID` 和 `BAIDU_TRANSLATE_SECRET_KEY`。
2. 可执行文件旁的 `appsettings.json`。
3. 项目目录中的 `appsettings.json`。
4. `%LOCALAPPDATA%\LittleTools\TranslateApp\appsettings.json`。
5. 同一磁盘根目录旧版 `TranslateApp\appsettings.json`（仅兼容迁移）。

将 `appsettings.example.json` 复制为 `appsettings.json` 后填入 AppId 和 SecretKey。密钥不会写入日志或嵌入 EXE。

## 构建

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

输出：`bin\TranslateApp.exe`
