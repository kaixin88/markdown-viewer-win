#!/usr/bin/env bash
# 本机编译（免 GitHub Actions 往返）：直接用系统自带的 .NET Framework 编译器出 exe。
#
# 用法：  bash mdrepo/build_local.sh [输出 exe 路径]
#
# 踩过的三个坑，缺一个都编不出来：
#  1) Git Bash 会把 /nologo /r:... 这类开关当成路径去做转换 → 必须关掉参数路径转换
#     （MSYS2_ARG_CONV_EXCL / MSYS_NO_PATHCONV）。
#  2) 系统自带的 csc 是 pre-Roslyn 的 C# 5 编译器：**命令行里的正斜杠路径会被吃掉中间的目录段**
#     （实测 C:/a/b/c/Program.cs 被解析成 c:\a\c\Program.cs）。所以改用响应文件（@rsp），
#     并且路径统统写成反斜杠。
#  3) 该编译器只支持到 C# 5 —— 源码里不能出现 ?. / $"..." / nameof 等 C# 6+ 语法。
set -e
export MSYS2_ARG_CONV_EXCL='*'
export MSYS_NO_PATHCONV=1

SELF="${BASH_SOURCE[0]:-$0}"
HERE="${SELF%/*}"
if [ "$HERE" = "$SELF" ]; then HERE="."; fi
SRC="$HERE/MarkdownViewer"
OUT="${1:-$HERE/dist/MarkdownViewer.exe}"
mkdir -p "${OUT%/*}"

# 转成反斜杠的 Windows 路径（该编译器只认这种形式）
w() { printf '%s' "${1//\//\\}"; }
WSRC="$(w "$SRC")"
WOUT="$(w "$OUT")"
RSP="$HERE/build_local.rsp"

{
  echo "/nologo"
  echo "/target:winexe"
  echo "/platform:x64"
  echo "/optimize+"
  echo "/r:System.dll"
  echo "/r:System.Drawing.dll"
  echo "/r:System.Windows.Forms.dll"
  echo "/r:Microsoft.CSharp.dll"
  echo "/out:$WOUT"
  echo "/resource:$WSRC\\viewer.html,MarkdownViewer.viewer.html"
  echo "$WSRC\\Program.cs"
  echo "$WSRC\\MainForm.cs"
} > "$RSP"

CSC="/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
"$CSC" "@$(w "$RSP")" || { echo "---- 编译失败，响应文件内容： ----"; cat "$RSP"; exit 1; }
ls -l "$OUT"
