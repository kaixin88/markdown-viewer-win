# 文件查看器 / Markdown & 代码查看器 (Windows)

小巧、离线的 Windows 文件查看器。可设为 `.md / .txt / .sql / .py` 等多种文件的默认打开程序，双击即看，并支持**编辑保存**。

## 特点
- **单文件 exe，不足 1MB**，不捆绑任何运行时：Win10 / Win11 自带的 .NET Framework 4.8 直接运行。
- **完全离线**，数据不出本机，适合内网隔离环境。
- **多格式**：Markdown 用 [marked](https://github.com/markedjs/marked)（ES5 版，兼容系统 IE 内核）渲染；代码文件（`.txt/.sql/.py/.js/.cs/.java/.cpp/.go/.rs/.sh/.json/.yaml/.xml/.html/.css/.ini/.php/.rb...）自带轻量语法高亮（离线、零依赖）。
- **支持编辑**：点「编辑」即可修改内容，点「保存」（或 `Ctrl+S`）写回原文件。
- **护眼配色（可记忆）**：内置 9 种经典阅读配色（海天蓝 / 极光灰 / 蓝调浅灰 / 绿豆沙 / 青草绿 / 葛巾紫 / 暖白 / 纯白 / 夜间），**默认「海天蓝」护眼底色**（不再是黄色）；点工具栏「配色」打开面板，点击色块即切换，并支持**自定义背景/文字颜色（十六进制代码）**。
- **记住上次配色**：切换后自动保存，下次打开仍是上次的配色（设置存于本机 `AppData\MarkdownViewer\settings.json`）。
- 菜单「文件 → 打开」浏览文件；菜单「视图 → 切换主题」或工具栏「配色」按钮切换/面板。

## 下载
- Release（始终是最新构建）：https://github.com/kaixin88/markdown-viewer-win/releases/latest
- 或到 Actions 页下载每次构建的工件。

## 设为默认打开程序（双击即开）
1. 下载 `MarkdownViewer.exe`，放到任意固定目录（例如 `D:\Tools\`）。
2. 任选一个文件（如 `.md`/`.txt`/`.sql`/`.py`）→ 右键 → 「打开方式」→「选择其他应用」→ 勾选「始终使用此应用打开 `.xxx` 文件」→ 浏览到 `MarkdownViewer.exe`。
3. 之后双击该类文件都会用它打开。
   （也可在系统「设置 → 应用 → 默认应用 → 按文件类型指定」里把对应扩展名指向它。）
4. 想关联多种类型，重复第 2 步为每种扩展名各设一次即可。

## 使用提示
- 顶部工具栏：「编辑 / 保存」切换编辑态；「配色」打开配色面板（点色块切换、或填十六进制代码自定义背景/文字色）。`Ctrl+S` 或「保存」按钮写回原文件。
- 编辑代码或文档后，`Ctrl+S` 或「保存」按钮即写回原文件（保存前请确保文件可写）。
- 首次运行会在当前用户注册表写入浏览器仿真值（无需管理员），仅本机生效。

## 源码与构建
- `MarkdownViewer/`：.NET Framework 4.8 + WinForms 工程（C#）。
- `MarkdownViewer/viewer.html`：内嵌 marked + 自写语法高亮的渲染模板（嵌入资源）。
- 推送到 `main` 后，GitHub Actions 自动用 MSBuild 构建并把 exe 发布到 `releases/latest`。

## 许可
MIT
