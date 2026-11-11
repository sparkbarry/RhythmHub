<h1 align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="design/logo/lockup-dark.png">
    <img src="design/logo/lockup-light.png" alt="RhythmHub" width="340">
  </picture>
</h1>

Windows / macOS 桌面启动器，负责节奏游戏（MajdataPlay、OurTaikoPlay 等 Unity 游戏）与谱面编辑器 OurTaikoEdit 的安装、更新、校验、启动与回滚。
游戏本体在各自仓库中构建；本仓库包含启动器、发布工具和发布流水线。

## 下载安装

从 [Releases](https://github.com/sparkbarry/RhythmHub/releases/latest) 下载，或直接用 CDN 上始终指向最新版的地址：

| 平台 | 推荐 | 其他 |
|---|---|---|
| Windows（x64） | [`RhythmHub-win-Setup.exe`](https://distrib.sparkbarry.com/RhythmHub/Windows/RhythmHub-win-Setup.exe)（Release 中为 `RhythmHub_<版本>_x64-setup.exe`） | `_x64-portable.zip`：便携版，解压即用 |
| macOS（Apple 芯片） | [`RhythmHub-osx-Setup.pkg`](https://distrib.sparkbarry.com/RhythmHub/MacOS/RhythmHub-osx-Setup.pkg)（Release 中为 `RhythmHub_<版本>_aarch64.pkg`） | `_aarch64-portable.zip`：便携版，解压出 `RhythmHub.app` |

Release 里的 `*.nupkg` 和 `releases.*.json` 是启动器自更新用的数据，不需要下载。

安装后启动器会自动更新：后台下载新版，底部出现「重启更新」，不点的话下次启动时应用。

**首次打开被拦截**：安装包暂未签名。

- macOS：先打开一次，看到「无法打开」后到「系统设置 → 隐私与安全性」底部点「仍要打开」。
- Windows：SmartScreen 提示时点「更多信息 → 仍要运行」。

每台电脑只需一次，之后的自动更新不受影响。

## 使用

- 左侧选游戏，右侧「安装 / 更新 / 开始游戏」。下载可暂停、断点续传；只下载变化的文件。
- **更新频道**：每个游戏可选「稳定版」（游戏仓库打 `v*` 标签发布）或「每日构建（Nightly）」（主分支每次推送）。
  切换后会装上该频道当前的版本，切回稳定版即装回稳定版。
- 新版本启动失败时可点「恢复上一版本」。
- **安装位置**：「安装位置」一栏点「更改…」可为每个游戏选文件夹，已安装的游戏会连同存档一起搬过去。
  游戏直接从这个文件夹运行，它保存在里面的设置和谱面跨版本保留；版本缓存和下载在其中的隐藏文件夹 `.rhythmhub/` 里。
- 数据目录：Windows `%LOCALAPPDATA%\RhythmHub`，macOS `~/Library/Application Support/RhythmHub`；
  放设置、日志，以及未改过位置的游戏（`games/{游戏}`）。「打开日志」可查看日志。

## 结构

```text
src/
  RhythmHub.Launcher/   Avalonia UI（MVVM），组合根，games.json 游戏目录
  RhythmHub.Updater/    平台无关更新核心：签名清单、逐文件增量下载、SHA-256、事务化组装安装、回滚
  RhythmHub.Platform/   Windows / macOS 差异：默认目录、启动、进程检测、包结构检查
tests/
  RhythmHub.Updater.Tests/
tools/
  RhythmHub.ReleaseTool/  stage（游戏 CI 生成文件清单）、publish（校验并签名发布）、keygen、fake-game
  ci/                     go-live.sh（游戏版本上线）、launcher-upload.sh（启动器上传到自更新源）
.github/
  actions/upload-build/   游戏仓库 CI 调用的上传发布 Action
  workflows/              launcher.yml（启动器构建发布）、publish-game.yml（手动发布 / 回滚游戏）、
                          test-pipeline.yml（发布链路回归测试）、release-tool.yml（预编译发布工具）
build/macos/            启动器 macOS 签名用的 entitlements
docs/
  update-protocol.md    更新协议与本地目录约定
  operations.md         发布基础设施维护手册：AWS / CDN、Secrets、发布回滚、接入新游戏
infra/aws/              CI 访问 AWS 的 CloudFormation 模板
```

## 开发

需要 .NET 10 SDK。

```bash
dotnet build
dotnet test
dotnet run --project src/RhythmHub.Launcher
```

### 本地模拟 S3 跑通完整流程

```bash
T="dotnet run --project tools/RhythmHub.ReleaseTool --"

# 1. 生成本地测试签名密钥（只用于本机）
$T keygen --out /tmp/rhythmhub-dev.key          # 记下输出的 public key
export RHYTHMHUB_SIGNING_KEY=$(cat /tmp/rhythmhub-dev.key)

# 2. 生成假游戏；stage 相当于游戏 CI（--copy-to 代替 aws s3 sync），publish 相当于本仓库的发布工作流
$T fake-game --platform MacOS --name FakeGame --out /tmp/fake-v1 --label v1
$T stage --game MajdataPlay --platform MacOS --version 1.0.0 --input /tmp/fake-v1 \
  --entrypoint FakeGame.app --out /tmp/1.0.0.unsigned.json --copy-to dev-cdn
$T publish --feed dev-cdn --game MajdataPlay --platform MacOS --version 1.0.0 --key-id dev-local

# 3. 起一个静态服务器
python3 -m http.server 8787 --bind 127.0.0.1 --directory dev-cdn

# 4. 用独立数据目录和测试公钥启动（Debug 构建）
RHYTHMHUB_FEED_URL=http://127.0.0.1:8787/ RHYTHMHUB_INSTALL_ROOT=/tmp/rhythmhub-dev \
RHYTHMHUB_DEV_PUBLIC_KEY="dev-local=<上面的 public key>" \
  dotnet run --project src/RhythmHub.Launcher
```

再用另一个版本号 `stage` + `publish` 测试增量更新；`fake-game --crash true` 生成一个启动即崩溃的版本，可测试“恢复上一版本”。
对旧版本再跑一次 `publish` 就是服务端回滚。

### 发布签名密钥

1. `keygen` 生成正式密钥对，私钥存为 CI Secret `RHYTHMHUB_SIGNING_KEY`，**不要提交到仓库**。
2. 把公钥填进 [ReleaseKeys.cs](src/RhythmHub.Launcher/Services/ReleaseKeys.cs)。未配置任何公钥的 Release 构建会拒绝所有清单。

### 发布启动器

推送 `v1.x` 或更高的标签即发布（`v0.x` 是旧 Tauri 版 MajdataHub 的标签，不再使用）：

```bash
git tag v1.0.2 && git push origin v1.0.2
```

`Launcher` 工作流会测试、打包并上传到 CDN 和 GitHub Release；版本号不能重复使用。
平时推送和 PR 只测试和打包，不发布。详见 [docs/operations.md §8](docs/operations.md)。

### 游戏接入健康检查

把 [docs/unity/RhythmHubHealth.cs](docs/unity/RhythmHubHealth.cs) 放进游戏工程，在进入标题画面时调用
`RhythmHubHealth.ReportReady()`，然后把 `games.json` 中该游戏的 `healthCheck` 改为 `true`。

## 进度

- [x] 阶段 A：多游戏数据模型、清单获取与校验、下载、SHA-256、版本目录与 `current.json`、启动、基础 UI、日志
- [x] 阶段 B：Manifest Ed25519 签名（RFC 8785）、Range/If-Range 续传与重试、`pending.json` 中断恢复、安装大小校验、单实例锁、启动健康标记与回滚提示
- [x] 协议 v2：S3 上按版本平铺文件、签名的频道指针 + 文件清单、文件级增量更新（计划书 V1.1）
- [x] 阶段 C：S3 + CloudFront 发布、游戏 CI 自动上线（OurTaikoPlay、MajdataPlay）、Velopack 打包与自更新、启动器 CI、稳定版 / 每日构建频道切换
- [ ] 启动器代码签名 / 公证（macOS 需要 Developer ID 证书，Windows 暂不签）、应用图标
- [ ] 游戏 Mac 版公证

当前状态以 [docs/operations.md §0.3](docs/operations.md) 为准。
