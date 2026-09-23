# Monitor fixtures

Same idea as `Stock/Fixtures`: every file here is replayed by the parser tests and
by the `--monitor-smoke` render smoke, so both run offline and deterministically.

## Recorded live (verbatim body of one real request, 2026-09-23, through this
machine's session proxy)

| File | Request | Notes |
| --- | --- | --- |
| `deepseek-balance.json` | `GET https://api.deepseek.com/user/balance` with the machine's real key | Real HTTP 200: one CNY balance. **Real success path.** |
| `glm-report-wallet.json` | `GET .../api/biz/account/query-customer-account-report` with the real key as `Bearer` | Real HTTP 200: `availableBalance`, `totalSpendAmount`, `currency` absent (→ ¥). **Real success path.** |
| `glm-quota-no-coding-plan.json` | `GET .../api/monitor/usage/quota/limit` with the real key as `Bearer` | Real HTTP 200 with `code 500` "当前用户不存在coding plan" — the pay-as-you-go account this machine has. |
| `glm-balance-v4-404.json` | `GET .../api/paas/v4/balance` with the real key as `Bearer` | Real HTTP 404 `{"status":404,"error":"Not Found","path":"/v4/balance"}` — **the old fallback endpoint is gone in production**, so the v4 fallback cannot succeed any more. |
| `deepseek-auth-failed.txt` | `GET https://api.deepseek.com/user/balance`, invalid key | Real HTTP 401 body `Authentication Fails (governor)`. |
| `glm-quota-auth-failed.json` | `GET .../api/monitor/usage/quota/limit`, no key | Real HTTP 200 body, `code 1001` "Header中未收到Authorization参数". |
| `glm-report-auth-failed.json` | `GET .../api/biz/account/query-customer-account-report`, no key | Same body, real HTTP 200. |
| `glm-balance-auth-failed.json` | `GET .../api/paas/v4/balance`, no key | Real HTTP 401, `{"error":{"code":"1001",...}}` — the `error.message` shape `IsAuthenticationFailure` also handles. |
| `codex-rate-limits.json` | `codex app-server --stdio` → `account/rateLimits/read` | Real reply from codex-cli 0.153.4 with this machine's logged-in `CODEX_HOME`. Redacted to the `result.rateLimits` object (`accountId`, `installationId` and reset-credit ids removed, per the module's "no account identifiers on disk" promise). |
| `codex-auth-required.json` | same, with an empty `CODEX_HOME` | Real reply: `codex account authentication required to read rate limits`. |

## Hand built (no live sample obtainable)

| File | Covers | Why hand built |
| --- | --- | --- |
| `glm-quota-limits.json` | A Coding Plan: 5h `TOKENS_LIMIT` (unit 3, number 5), weekly `TOKENS_LIMIT` (unit 6), `TIME_LIMIT` (MCP month) and `level: pro`. | This account has no Coding Plan (`glm-quota-no-coding-plan.json` is what the real host answers), so the plan branch can only be replayed. Field set taken from the Windows parser, `AIUsageMonitor/Program.cs` L1052-L1141. |
| `glm-quota-no-plan.json` | A successful answer with `limits: []`. | Same reason; the real no-plan answer is an error code instead. |
| `glm-balance-v4.json` | The v4 balance parser (`available_balance` + `total_balance`). | The endpoint now answers 404, so the parser is only reachable through this fixture; the real 404 is `glm-balance-v4-404.json`. |
| `glm-wallet-usd.json` | Non-CNY currency symbol mapping. | No USD account is available. |
| `deepseek-balance-multi.json` | Two currencies joined into one line (`¥… / $…`). | The real key only has a CNY balance. |
| `deepseek-empty-balance.json` | A drained account (`is_available: false`) → the "余额不足" row. | The real account is not drained. |
