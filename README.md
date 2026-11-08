# ProxyPicker

只让「你勾选的软件」走代理 —— 一个给 Clash / mihomo 系客户端用的**按进程分流**小面板。

单文件 exe，约 900 KB，不需要安装、不写注册表、不碰你的订阅。

![主界面](docs/img/main.png)

---

## 它解决什么问题

Clash 的规则是按「域名 / IP / 规则集」写的，而人脑子里想的是「我要让 Chrome 走代理，微信别走」。
两者之间差的是一层翻译：**进程 → 规则**。

ProxyPicker 干的就是这件事。点几个开关，点保存，程序把 `PROCESS-NAME` 规则插到你内核配置的
`rules:` 段首，然后热推一次（`PUT /configs`）。全程不重启客户端、不断其他连接，
按软件的代理名单立刻生效。

---

## 特点

- **改完不用重启浏览器**：保存时只关闭「本次改动到的软件」的旧连接，其他连接（下载、长连接）不断。
- **两种分流模式，随时可切**
  | 模式 | 勾选的软件 | 没勾选的软件 | 适合谁 |
  |---|---|---|---|
  | 只改勾的（新机器默认） | 走代理 | 完全按订阅原规则走 | 别人用 / 不想动订阅 |
  | 完全接管 | 走代理 | 一律直连 | 只想让少数软件出网 |
- **自己发现 + 自己补回**：每 30 秒核对一次「我插的规则还在不在」（收进托盘也不停）。
  换订阅、重启客户端、改设置都会把规则冲掉，这时会自己补回去并留一句提示。
- **顺手关掉 IPv6 泄漏**：TUN 的 IPv6 是内网段，Windows 不会拿它当公网源地址，
  IPv6 流量会从物理网卡直出、把真实 IP 露给双栈网站。程序在推送路径上强制 `ipv6: false` 并回读确认。
- **换电脑直接用**：自动识别 Clash Verge Rev / Clash Verge / Clash Nyanpasu / Mihomo Party /
  FlClash / Clash for Windows / 裸 mihomo。
- **看得见出口**：摘要卡上写的是「勾选的软件现在实际走哪个节点」，换节点 10 秒内自己更新。

![帮助面板](docs/img/help.png)

---

## 快速开始

1. 打开右侧 **Releases**，下载最新的 `ProxyPicker-分享版.zip`；
2. 解压，双击 `ProxyPicker.exe`（右下角出现图标，面板自动弹出）；
3. 勾上要走代理的软件 → 点右下角「保存并生效」。

**前提**：这台电脑上本来就有 Clash Verge 之类的客户端、并且正在运行。
这个工具**不带代理**，它只决定「谁走代理」。

> 第一次运行可能被 Windows SmartScreen 拦一下（exe 没有数字签名）：
> 点「更多信息」→「仍要运行」。源码全在这个仓库里，可以自己编译。

详细说明见 [docs/使用说明.md](docs/使用说明.md)。

---

## 常见问题

**Q：勾了还是不走代理？**
先看摘要卡右下角的规则数有没有变，再看底部状态是不是「名单已生效」。
如果客户端在你保存之后重新生成过配置，规则会被冲掉——等 30 秒它会自己补回，或者点一下右上角刷新。

**Q：怎么撤掉，回到原来的分流？**
在 Clash Verge 里对在用的配置点一次「更新 / 应用」，客户端重新生成配置时会把插入的规则冲掉。
也可以手动删掉 `rules:` 段首那几条 `PROCESS-NAME` 和紧跟的 `MATCH,DIRECT`。
彻底删干净：exe、`%LOCALAPPDATA%\ProxyPicker\`、客户端的 `pp_rules` 目录。

**Q：会把我的订阅传到网上吗？**
不会。程序只读本机配置文件和控制面接口，运行时唯一的对外请求是「检查更新」时发给 GitHub API 的一个 GET，
不带任何本机信息；把 `settings.json` 里的 `"updateCheck"` 改成 `false` 就完全不联网。

**Q：支持 macOS / Linux 吗？**
不支持。命名管道、注册表开机自启、WinForms 自绘界面都是 Windows 专用。

---

## 从源码构建

需要 .NET SDK（能编译 `net48` 目标即可）和 Windows：

```bat
dotnet build src\ProxyPicker.csproj -c Release
```

产物：`src\bin\Release\net48\ProxyPicker.exe`（单文件，无外部依赖）。

只读自检出口（不改任何配置）：

```bat
ProxyPicker.exe --selftest 自检.txt     :: 规则生成 23 项
ProxyPicker.exe --dryrun 配置.yaml 输出.yaml whitelist
ProxyPicker.exe --version 版本.txt
ProxyPicker.exe --checkupdate 更新.txt
```

---

## 目录

```
src/            C# 源码（net48，WinForms 自绘界面）
  Core/         发现客户端、读配置、生成规则、控制面通信、对账、偏好
  UI/           窗口、列表、托盘、主题、动效
  Assets/       ico（16/24/32/48/64/128/256 七帧 32 位）
docs/           使用说明与截图
```

---

## 许可

MIT，见 [LICENSE](LICENSE)。
