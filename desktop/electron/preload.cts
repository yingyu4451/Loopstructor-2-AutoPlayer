import { contextBridge, ipcRenderer } from 'electron'

const api = {
  isUpdater: process.argv.some((argument) => {
    const normalized = argument.toLowerCase()
    return normalized === '--updater' || normalized === '--loopstructor-updater'
  }),
  getSnapshot: () => ipcRenderer.invoke('app:getSnapshot'),
  saveSettings: (settings: unknown) => ipcRenderer.invoke('settings:save', settings),
  selectGameDirectory: () => ipcRenderer.invoke('game:selectDirectory'),
  validateGame: (path: string) => ipcRenderer.invoke('game:validate', path),
  selectUnityProject: () => ipcRenderer.invoke('editor:selectProject'),
  validateUnityProject: (path: string) => ipcRenderer.invoke('editor:validateProject', path),
  installEditorBridge: () => ipcRenderer.invoke('editor:installBridge'),
  uninstallEditorBridge: () => ipcRenderer.invoke('editor:uninstallBridge'),
  listEditorInstances: () => ipcRenderer.invoke('editor:listInstances'),
  connectEditor: (instanceId: string) => ipcRenderer.invoke('editor:connect', instanceId),
  disconnectEditor: () => ipcRenderer.invoke('editor:disconnect'),
  installPlugin: () => ipcRenderer.invoke('plugin:install'),
  setPluginEnabled: (enabled: boolean) => ipcRenderer.invoke('plugin:setEnabled', enabled),
  uninstallPlugin: () => ipcRenderer.invoke('plugin:uninstall'),
  launchGame: () => ipcRenderer.invoke('game:launch'),
  refreshConnection: () => ipcRenderer.invoke('connection:refresh'),
  cheatCommand: (command: string, args?: unknown) => ipcRenderer.invoke('cheat:command', command, args),
  queryAutomationSetup: () => ipcRenderer.invoke('automation:querySetup'),
  startAutomation: () => ipcRenderer.invoke('automation:start'),
  pauseAutomation: () => ipcRenderer.invoke('automation:pause'),
  resumeAutomation: () => ipcRenderer.invoke('automation:resume'),
  stopAutomation: () => ipcRenderer.invoke('automation:stop'),
  checkUpdates: () => ipcRenderer.invoke('update:check'),
  inspectUpdateProcesses: () => ipcRenderer.invoke('update:inspectProcesses'),
  closeGameForUpdate: () => ipcRenderer.invoke('update:closeGame'),
  applyUpdate: () => ipcRenderer.invoke('update:apply'),
  startUpdater: () => ipcRenderer.invoke('updater:start'),
  closeUpdater: () => ipcRenderer.invoke('updater:close'),
  openEvidence: () => ipcRenderer.invoke('diagnostics:openEvidence'),
  openSaveBackups: () => ipcRenderer.invoke('backups:open'),
  listSaveBackups: () => ipcRenderer.invoke('backups:list'),
  restoreSaveBackup: (backupId: string) => ipcRenderer.invoke('backups:restore', backupId),
  clearLogs: () => ipcRenderer.invoke('logs:clear'),
  minimize: () => ipcRenderer.invoke('window:minimize'),
  toggleMaximize: () => ipcRenderer.invoke('window:toggleMaximize'),
  close: () => ipcRenderer.invoke('window:close'),
  setZoom: (factor: number) => ipcRenderer.invoke('window:setZoom', factor),
  showSystemMenu: (point: { x: number; y: number }) => ipcRenderer.send('window:systemMenu', point),
  onHostEvent: (listener: (event: unknown) => void) => {
    const handler = (_event: Electron.IpcRendererEvent, message: unknown) => listener(message)
    ipcRenderer.on('host:event', handler)
    return () => ipcRenderer.removeListener('host:event', handler)
  },
  onUpdaterEvent: (listener: (event: unknown) => void) => {
    const handler = (_event: Electron.IpcRendererEvent, message: unknown) => listener(message)
    ipcRenderer.on('updater:event', handler)
    return () => ipcRenderer.removeListener('updater:event', handler)
  },
}

contextBridge.exposeInMainWorld('loopstructorDesktop', api)
