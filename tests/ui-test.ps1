# 界面端到端测试（Tauri 版）：启动本机假 HomePod 和真实应用，用 WebView2 CDP 点击连接、调音量、打开面板、断开。
# 全程只连 127.0.0.1，不碰真实 HomePod，不发声。
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Push-Location (Join-Path $root 'Desktop')
try {
  npm run e2e
  exit $LASTEXITCODE
} finally {
  Pop-Location
}
