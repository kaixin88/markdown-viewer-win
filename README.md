# Markdown Viewer (Windows)

小巧、离线的 Windows Markdown 查看器。可设为 `.md` 的默认打开程序，双击文件即看。

## 特点
- **单文件 exe，不足 1MB**，不捆绑任何运行时：Win10 / Win11 自带的 .NET Framework 4.8 直接运行。
- **完全离线**，数据不出本机，适合内网隔离环境。
- 渲染复用 [marked](https://github.com/markedjs/marked)（ES5 版，兼容系统 IE 内核），排版接近 GitHub。
- 菜单「文件 → 打开」浏览文件；菜单「视图 → 切换主题」切换浅/深色。

## 下载
- Release（始终是最新构建）：https://github.com/kaixin88/markdown-viewer-win/releases/latest
- 或到 Actions 页下载每次构建的工件。

## 设为 .md 默认打开程序（双击即开）
1. 下载 `MarkdownViewer.exe`，放到任意固定目录（例如 `D:\Tools\`）。
2. 任选一个 `.md` 文件 → 右键 → 「打开方式」→「选择其他应用」→ 勾选「始终使用此应用打开 .md 文件」→ 浏览到 `MarkdownViewer.exe`。
3. 之后双击任意 `.md` 都会用它打开。
   （也可在系统「设置 → 应用 → 默认应用 → 按文件类型指定」里把 `.md` 指向它。）

## 源码与构建
- `MarkdownViewer/`：.NET Framework 4.8 + WinForms 工程（C#）。
- `MarkdownViewer/viewer.html`：内嵌 marked 的渲染模板（嵌入资源）。
- 推送到 `main` 后，GitHub Actions 自动 `dotnet build` 并把 exe 发布到 `releases/latest`。

## 许可
MIT
