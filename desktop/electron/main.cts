import { app, BrowserWindow, dialog, ipcMain, Menu, nativeImage, session, shell } from 'electron'
import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process'
import { createInterface } from 'node:readline'
import path from 'node:path'
import fs from 'node:fs'
import { HostClient } from './host-client.cjs'

let window: BrowserWindow | undefined
let host: HostClient | undefined
let updaterProcess: ChildProcessWithoutNullStreams | undefined
let updaterFinished = false
const isUpdaterMode = process.argv.some(argument => argument.toLowerCase() === '--updater')
const updaterArgumentIndex = process.argv.findIndex(argument => argument.toLowerCase() === '--updater')
const updaterArguments = updaterArgumentIndex >= 0 ? process.argv.slice(updaterArgumentIndex + 1) : []

const userDataOverride = process.env.LOOPSTRUCTOR_AUTOPLAYER_DESKTOP_USER_DATA_ROOT
if (userDataOverride) app.setPath('userData', path.resolve(userDataOverride))

if (!isUpdaterMode) {
  const gotLock = app.requestSingleInstanceLock()
  if (!gotLock) app.quit()
}

function showExistingWindow(): void {
  if (!window) return
  if (window.isMinimized()) window.restore()
  window.show()
  window.focus()
}

if (!isUpdaterMode) app.on('second-instance', showExistingWindow)

app.whenReady().then(() => {
  if (isUpdaterMode) {
    createUpdaterWindow()
    return
  }
  const managerDirectory = path.dirname(process.execPath)
  const developmentRoot = path.resolve(__dirname, '..', '..')
  const distributionRoot = app.isPackaged ? path.dirname(managerDirectory) : developmentRoot
  host = new HostClient(managerDirectory, distributionRoot)
  host.start(process.pid)

  const iconPath = app.isPackaged
    ? path.join(process.resourcesPath, 'branding', 'manager-logo-256.png')
    : path.join(distributionRoot, 'assets', 'branding', 'manager-logo-256.png')
  window = new BrowserWindow({
    width: 1280,
    height: 860,
    minWidth: 980,
    minHeight: 680,
    frame: false,
    show: false,
    backgroundColor: '#12110E',
    icon: nativeImage.createFromPath(iconPath),
    webPreferences: {
      preload: path.join(__dirname, 'preload.cjs'),
      sandbox: true,
      contextIsolation: true,
      nodeIntegration: false,
      webSecurity: true,
      spellcheck: false,
    },
  })

  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }))
  window.webContents.on('preload-error', (_event, preloadPath, error) => {
    console.error(`Preload 加载失败：${preloadPath}`, error)
  })
  window.webContents.on('will-navigate', (event, target) => {
    const current = window?.webContents.getURL() ?? ''
    if (target !== current) event.preventDefault()
  })
  session.defaultSession.webRequest.onHeadersReceived((details, callback) => {
    callback({
      responseHeaders: {
        ...details.responseHeaders,
        'Content-Security-Policy': [
          "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'self'; object-src 'none'; frame-src 'none'; base-uri 'none'",
        ],
      },
    })
  })

  const rendererUrl = process.env.VITE_DEV_SERVER_URL
  if (rendererUrl) void window.loadURL(rendererUrl)
  else void window.loadFile(path.join(__dirname, '..', 'dist', 'index.html'))
  window.once('ready-to-show', () => window?.show())
  window.on('closed', () => { window = undefined })

  host.on('event', (event) => {
    window?.webContents.send('host:event', event)
    if ((event as { event?: string }).event === 'updateStarted') {
      setTimeout(() => app.quit(), 200)
    }
  })
  host.on('exit', (code) => window?.webContents.send('host:event', {
    event: 'hostExit',
    payload: { code },
  }))
  host.on('diagnostic', (message) => window?.webContents.send('host:event', {
    event: 'hostDiagnostic',
    payload: { message },
  }))

  registerIpc()
})

function createUpdaterWindow(): void {
  const managerDirectory = path.dirname(process.execPath)
  const developmentRoot = path.resolve(__dirname, '..', '..')
  const distributionRoot = app.isPackaged ? path.dirname(managerDirectory) : developmentRoot
  const iconPath = app.isPackaged
    ? path.join(process.resourcesPath, 'branding', 'manager-logo-256.png')
    : path.join(distributionRoot, 'assets', 'branding', 'manager-logo-256.png')
  window = new BrowserWindow({
    width: 760,
    height: 600,
    minWidth: 680,
    minHeight: 520,
    frame: false,
    show: false,
    backgroundColor: '#12110E',
    icon: nativeImage.createFromPath(iconPath),
    webPreferences: {
      preload: path.join(__dirname, 'preload.cjs'),
      sandbox: true,
      contextIsolation: true,
      nodeIntegration: false,
      webSecurity: true,
      spellcheck: false,
      additionalArguments: ['--loopstructor-updater'],
    },
  })
  configureSecurity(window)
  window.on('close', (event) => {
    if (updaterProcess && !updaterFinished) event.preventDefault()
  })
  loadRenderer(window)
  window.once('ready-to-show', () => window?.show())
  window.on('closed', () => { window = undefined })
  registerUpdaterIpc(distributionRoot, managerDirectory)
}

function loadRenderer(target: BrowserWindow): void {
  const rendererUrl = process.env.VITE_DEV_SERVER_URL
  if (rendererUrl) void target.loadURL(rendererUrl)
  else void target.loadFile(path.join(__dirname, '..', 'dist', 'index.html'))
}

function configureSecurity(target: BrowserWindow): void {
  target.webContents.setWindowOpenHandler(() => ({ action: 'deny' }))
  target.webContents.on('preload-error', (_event, preloadPath, error) => {
    console.error(`Preload 加载失败：${preloadPath}`, error)
  })
  target.webContents.on('will-navigate', (event, targetUrl) => {
    const current = target.webContents.getURL()
    if (targetUrl !== current) event.preventDefault()
  })
  session.defaultSession.webRequest.onHeadersReceived((details, callback) => {
    callback({
      responseHeaders: {
        ...details.responseHeaders,
        'Content-Security-Policy': [
          "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'self'; object-src 'none'; frame-src 'none'; base-uri 'none'",
        ],
      },
    })
  })
}

function registerUpdaterIpc(distributionRoot: string, managerDirectory: string): void {
  ipcMain.handle('window:minimize', () => window?.minimize())
  ipcMain.handle('window:toggleMaximize', () => window?.isMaximized() ? window.unmaximize() : window?.maximize())
  ipcMain.handle('window:close', () => {
    if (updaterProcess && !updaterFinished) return false
    window?.close()
    return true
  })
  ipcMain.handle('updater:start', () => {
    if (updaterProcess) return { success: true, message: '更新已经在执行。' }
    const configured = process.env.LOOPSTRUCTOR_AUTOPLAYER_UPDATER_PATH
    const candidates = [
      configured,
      path.join(managerDirectory, 'Loopstructor.AutoPlayer.Updater.exe'),
      path.join(distributionRoot, 'src', 'Loopstructor.AutoPlayer.Updater', 'bin', 'Release', 'net8.0-windows', 'Loopstructor.AutoPlayer.Updater.exe'),
      path.join(distributionRoot, 'src', 'Loopstructor.AutoPlayer.Updater', 'bin', 'Debug', 'net8.0-windows', 'Loopstructor.AutoPlayer.Updater.exe'),
    ].filter((candidate): candidate is string => Boolean(candidate))
    const executable = candidates.find(candidate => fs.existsSync(candidate))
    if (!executable) return { success: false, message: '找不到 .NET 更新事务组件。' }
    const args = updaterArguments.filter(argument => argument.toLowerCase() !== '--updater')
    if (!args.some(argument => argument.toLowerCase() === 'apply')) args.unshift('apply')
    if (!args.some(argument => argument.toLowerCase() === '--json-stream')) args.push('--json-stream')
    updaterFinished = false
    let latestVersion = ''
    updaterProcess = spawn(executable, args, {
      cwd: path.dirname(executable),
      windowsHide: true,
      env: { ...process.env },
    })
    const lines = createInterface({ input: updaterProcess.stdout })
    lines.on('line', (line) => {
      try {
        const message = JSON.parse(line) as { event?: string; payload?: unknown }
        if (message.event) {
          if (message.event === 'result' && typeof (message.payload as { latestVersion?: unknown })?.latestVersion === 'string') {
            latestVersion = (message.payload as { latestVersion: string }).latestVersion
          }
          window?.webContents.send('updater:event', message)
        } else window?.webContents.send('updater:event', { event: 'result', payload: message })
      } catch {
        window?.webContents.send('updater:event', { event: 'log', payload: { message: line } })
      }
    })
    updaterProcess.stderr.on('data', (chunk) => {
      window?.webContents.send('updater:event', { event: 'stderr', payload: { message: String(chunk) } })
    })
    updaterProcess.on('error', (error) => {
      updaterFinished = true
      window?.webContents.send('updater:event', { event: 'error', payload: { message: error.message } })
    })
    updaterProcess.on('exit', (code, signal) => {
      updaterFinished = true
      updaterProcess = undefined
      window?.webContents.send('updater:event', { event: 'exit', payload: { code, signal } })
      if (code === 0) {
        const targetRoot = readUpdaterArgument(args, '--target')
        if (targetRoot) {
          startCleanupAndRestart(executable, targetRoot, latestVersion || readUpdaterArgument(args, '--current-version') || '0.0.0')
        } else {
          window?.webContents.send('updater:event', { event: 'error', payload: { message: '更新完成，但没有找到安装根目录，已停止自动重启。' } })
        }
      }
    })
    return { success: true, message: '更新事务已启动。' }
  })
  ipcMain.handle('updater:close', () => {
    if (updaterProcess && !updaterFinished) return false
    window?.close()
    return true
  })
}

function readUpdaterArgument(args: string[], name: string): string | undefined {
  const index = args.findIndex(argument => argument.toLowerCase() === name.toLowerCase())
  const value = index >= 0 ? args[index + 1] : undefined
  return value && !value.startsWith('--') ? value : undefined
}

function startCleanupAndRestart(updaterExecutable: string, targetRoot: string, version: string): void {
  const normalizedRoot = path.resolve(targetRoot)
  const cleanup = spawn(updaterExecutable, [
    'cleanup',
    '--target', normalizedRoot,
    '--current-version', version,
    '--json',
  ], {
    cwd: path.dirname(updaterExecutable),
    windowsHide: true,
    env: { ...process.env },
  })
  updaterFinished = false
  updaterProcess = cleanup
  cleanup.stdout.on('data', () => { /* Drain the compact cleanup result. */ })
  cleanup.stderr.on('data', (chunk) => {
    window?.webContents.send('updater:event', { event: 'stderr', payload: { message: String(chunk) } })
  })
  cleanup.on('error', (error) => {
    updaterFinished = true
    updaterProcess = undefined
    window?.webContents.send('updater:event', { event: 'error', payload: { message: `更新清理失败：${error.message}` } })
  })
  cleanup.on('exit', (code, signal) => {
    updaterFinished = true
    updaterProcess = undefined
    if (code !== 0) {
      window?.webContents.send('updater:event', { event: 'error', payload: { message: `更新清理失败（退出代码 ${code ?? signal ?? 'unknown'}）。` } })
      return
    }
    const managerEntry = path.join(normalizedRoot, 'Loopstructor.AutoPlayer.Manager.exe')
    if (!fs.existsSync(managerEntry)) {
      window?.webContents.send('updater:event', { event: 'error', payload: { message: '新版安装完成，但找不到根 Manager 入口。' } })
      return
    }
    try {
      const restarted = spawn(managerEntry, ['--restarted-after-update'], {
        cwd: normalizedRoot,
        detached: true,
        stdio: 'ignore',
        windowsHide: true,
      })
      restarted.unref()
      window?.webContents.send('updater:event', { event: 'restarted', payload: { version } })
      setTimeout(() => app.quit(), 900)
    } catch (error) {
      window?.webContents.send('updater:event', { event: 'error', payload: { message: `新版 Manager 启动失败：${String(error)}` } })
    }
  })
}

function invoke(method: string, params?: unknown): Promise<unknown> {
  if (!host) throw new Error('.NET Host 尚未启动。')
  return host.invoke(method, params)
}

function registerIpc(): void {
  ipcMain.handle('app:getSnapshot', () => invoke('app.getSnapshot'))
  ipcMain.handle('settings:save', (_event, settings) => invoke('settings.save', settings))
  ipcMain.handle('game:selectDirectory', async () => {
    const result = await dialog.showOpenDialog(window!, { properties: ['openDirectory'], title: '选择 Loopstructor 2: Skyspine 游戏目录' })
    return result.canceled || result.filePaths.length === 0 ? null : invoke('game.validate', { path: result.filePaths[0] })
  })
  ipcMain.handle('game:validate', (_event, gamePath: string) => invoke('game.validate', { path: gamePath }))
  ipcMain.handle('plugin:install', () => invoke('plugin.install'))
  ipcMain.handle('plugin:setEnabled', (_event, enabled: boolean) => invoke('plugin.setEnabled', { enabled }))
  ipcMain.handle('plugin:uninstall', () => invoke('plugin.uninstall'))
  ipcMain.handle('game:launch', () => invoke('game.launch'))
  ipcMain.handle('connection:refresh', () => invoke('connection.refresh'))
  ipcMain.handle('cheat:command', (_event, command: string, args: unknown) => invoke('cheat.command', { command, arguments: args ?? {} }))
  ipcMain.handle('automation:querySetup', () => invoke('automation.querySetup'))
  ipcMain.handle('automation:start', () => invoke('automation.start'))
  ipcMain.handle('automation:pause', () => invoke('automation.pause'))
  ipcMain.handle('automation:resume', () => invoke('automation.resume'))
  ipcMain.handle('automation:stop', () => invoke('automation.stop'))
  ipcMain.handle('update:check', () => invoke('update.check'))
  ipcMain.handle('update:inspectProcesses', () => invoke('update.inspectProcesses'))
  ipcMain.handle('update:closeGame', () => invoke('update.closeGame'))
  ipcMain.handle('update:apply', () => invoke('update.apply', { desktopProcessId: process.pid }))
  ipcMain.handle('diagnostics:openEvidence', () => invoke('diagnostics.openEvidence'))
  ipcMain.handle('backups:open', () => invoke('backups.open'))
  ipcMain.handle('backups:list', () => invoke('backups.list'))
  ipcMain.handle('backups:restore', (_event, backupId: string) => {
    if (!host) throw new Error('.NET Host 尚未启动。')
    return host.invoke('backups.restore', { backupId }, 120000)
  })
  ipcMain.handle('logs:clear', () => invoke('logs.clear'))
  ipcMain.handle('window:minimize', () => window?.minimize())
  ipcMain.handle('window:toggleMaximize', () => window?.isMaximized() ? window.unmaximize() : window?.maximize())
  ipcMain.handle('window:close', () => window?.close())
  ipcMain.handle('window:setZoom', (_event, factor: number) => {
    const normalized = Math.min(2, Math.max(0.75, factor))
    window?.webContents.setZoomFactor(normalized)
    return normalized
  })
  ipcMain.on('window:systemMenu', (_event, point: { x: number; y: number }) => {
    const menu = Menu.buildFromTemplate([
      { label: '还原', click: () => window?.restore(), enabled: window?.isMaximized() === true },
      { label: '最小化', click: () => window?.minimize() },
      { label: window?.isMaximized() ? '还原' : '最大化', click: () => window?.isMaximized() ? window.unmaximize() : window?.maximize() },
      { type: 'separator' },
      { label: '关闭', click: () => window?.close() },
    ])
    menu.popup({ window, x: Math.round(point.x), y: Math.round(point.y) })
  })
  ipcMain.handle('external:openRelease', (_event, url: string) => {
    if (!/^https:\/\/github\.com\/yingyu4451\/Loopstructor-2-AutoPlayer\//i.test(url)) {
      throw new Error('只允许打开 AutoPlayer 官方 GitHub 页面。')
    }
    return shell.openExternal(url)
  })
}

app.on('before-quit', () => host?.stop())
app.on('window-all-closed', () => app.quit())
