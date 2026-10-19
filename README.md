# 自动隐藏鼠标（VS Code 插件）

敲代码的时候，鼠标指针停在编辑器里有点碍眼？这个插件会在你打字的时候把它藏起来，鼠标稍微动一下又马上出现 (๑•̀ㅂ•́)و

只对 VS Code 生效。切到浏览器、终端或者其他窗口，指针立刻恢复正常，不会影响别的软件。

## 安装

双击 `auto-hide-cursor-0.0.1.vsix`，或者：

```powershell
code --install-extension "auto-hide-cursor-0.0.1.vsix"
```

装完重载一下窗口就好。

## 用起来是什么样

- 在编辑器里打字，或者用方向键移动光标 → 鼠标隐藏
- 鼠标移动超过 3 像素、按鼠标键、VS Code 失去焦点 → 立刻恢复
- 多显示器、多开 VS Code 都没问题
- 目前只支持 Windows

## 想调一下

设置里搜 `autoHideCursor`：

| 设置 | 默认 | 说明 |
| --- | --- | --- |
| `enabled` | `true` | 总开关 |
| `movementThreshold` | `3` | 鼠标移动多少像素后恢复 |
| `pollInterval` | `20` | 检测间隔（毫秒），越小恢复越快 |

命令面板里还有：

- `自动隐藏鼠标: 开 / 关`
- `自动隐藏鼠标: 立即显示鼠标`
- `自动隐藏鼠标: 强制恢复系统光标`

## 一点说明

Windows 没有“只隐藏某个窗口光标”的接口，所以插件只会在 **VS Code 在前台、鼠标也在 VS Code 窗口里** 的时候，临时把系统光标变透明。鼠标一动、点一下或者切窗口，就立刻恢复。

helper 还带了一个看门狗。万一 helper 自己崩了，看门狗会把光标还原，不会把你的鼠标搞丢 😌

真遇到光标不见了，命令面板执行「强制恢复系统光标」，或者双击 `restore-cursor.ps1`。

## 源码

- `extension.js`：插件入口，负责监听打字和跟 helper 通信
- `helper/AutoHideCursorHelper.cs`：Win32 helper 的源码
- `helper/AutoHideCursorHelper.exe`：已经编译好的 helper
- `build-helper.ps1`：重新编译 helper
- `package.json`：扩展信息

想自己打包的话：

```powershell
npx @vscode/vsce package --allow-missing-repository
```