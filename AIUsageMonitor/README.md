# AI Usage Monitor

一个面向 Windows 的轻量 AI 用量桌面 HUD。目前支持：

- Codex ChatGPT 登录账户：通过本机 `codex app-server` 的 `account/rateLimits/read` 读取官方额度百分比、重置时间、计划和 Credits。
- DeepSeek API：通过官方 `/user/balance` 接口读取总余额、充值余额和赠送余额。
- 国内 GLM：读取普通按量付费账户的钱包余额与累计消费；订阅 Coding Plan 时，额外读取 5 小时 Token 余量、周余量、MCP 月余量、套餐等级和各窗口重置时间。
- 展开明细后点击“设置”，可分别启用 Codex、DeepSeek、GLM，并为 API Key 选择环境变量或手动输入。
- 未启用的供应商会同时从 316 × 92 小窗和展开详情中折叠隐藏，不显示占位行。
- 当日用量：316 × 92 小窗和展开详情都会显示 DeepSeek、GLM 的今日估算消费；首次采样不足一整天时明确标为“监控后”。
- 日/周/月折线图：展开面板可在 `DS` 与 `GLM` 间切换；DeepSeek 使用余额减少量，GLM 优先使用账户累计消费增量，分别以绿色、紫色曲线展示。
- 透明置顶 HUD、点击展开、直接拖动、屏幕边缘自动收起、托盘隐藏和鼠标穿透。

## 运行要求

- Windows 10/11。
- 只监控需要的供应商；Codex 监控需要已安装并登录 Codex Desktop/CLI。
- DeepSeek、GLM 可使用环境变量，也可在展开面板的“设置”中手动输入 Key。
- GLM 环境变量除设置中指定的名称外，还会依次兼容 `ZAI_CODING_CN_API_KEY`、`ZHIPUAI_API_KEY`、`ZHIPU_API_KEY`、`GLM_API_KEY`、`BIGMODEL_API_KEY`。

环境变量需要对新启动的进程可见。如果刚设置完，请重新启动终端，或注销并重新登录 Windows。

## 构建

无需安装 .NET SDK。项目使用 Windows 自带的 .NET Framework 编译器：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

生成文件：`bin\AIUsageMonitor.exe`

## 操作

- 左键单击 HUD：展开或收起详情。
- 直接按住主 HUD 或展开后的详情层拖动：两层会作为一组移动。
- 拖到屏幕左侧或右侧边缘：自动收起，仅保留 9 像素唤醒条；鼠标移入展开，移出后再次收起。
- 托盘菜单：显示/隐藏、鼠标穿透、立即刷新、退出。

## 安全

- 不读取或复制 Codex 的 `auth.json`；认证完全交给官方 `codex app-server`。
- DeepSeek、GLM Key 可从自定义环境变量读取；手动输入时使用 Windows DPAPI 按当前用户加密后保存。
- DeepSeek Key 只发送至 `https://api.deepseek.com/user/balance`；GLM Key 只发送至智谱域名下的三个只读端点：账户报告 `/api/biz/account/query-customer-account-report`、余额回退 `/api/paas/v4/balance`、套餐配额 `/api/monitor/usage/quota/limit`。
- GLM 账户报告和套餐接口目前没有写入智谱公开 API 文档，可能随平台调整；组件对账户报告失败、旧余额接口下线、裸 Key/Bearer 差异和部分数据不可用分别容错。
- GLM 套餐百分比表示“已用”，界面统一换算并显示“剩余”。普通按量付费 API Key 仍会显示账户余额，只是没有 Coding Plan 的 5 小时/周/MCP 套餐配额。
- Codex 未运行时保留最后一次有效额度和更新时间，不启动 Codex Desktop，也不会后台常驻 Codex 查询进程。
- 不将密钥、提示词、回复或账户标识写入磁盘和日志。
- DeepSeek 历史只保存采样时间、币种和余额，位于 `%LOCALAPPDATA%\LittleTools\AIUsageMonitor\usage-history.json`，自动保留最近 35 天；旧路径数据会自动迁移。
- GLM 历史只保存采样时间、币种、账户余额和累计消费，位于 `%LOCALAPPDATA%\LittleTools\AIUsageMonitor\glm-usage-history.json`，自动保留最近 35 天。

## 统计口径

- DeepSeek 官方余额接口不提供历史账单。曲线按相邻采样的余额下降量累计，并复用日/周/月边界前最后一条历史快照作为基线；因此关闭和重启监控不会清零已有历史。首次启用前的消费无法用普通 API Key 补查，界面会标为“监控后”。
- GLM 账户报告返回累计消费时，曲线按相邻采样的累计消费增量计算，不受充值影响；只能使用旧余额回退接口时，降级为余额下降估算。它也会复用边界前最后一条快照，但普通 API Key 无法还原首次监控前的历史金额。
- “今日约”表示存在跨日基线，金额仍是相邻快照差值估算；若程序长时间未运行，跨越零点的间隔消费无法精确分摊到两个自然日。
