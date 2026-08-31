import { contextBridge, ipcRenderer } from 'electron'

const api = {
  getSnapshot: () => ipcRenderer.invoke('app:getSnapshot'),
  saveSettings: (settings: unknown) => ipcRenderer.invoke('settings:save', settings),
  selectGameDirectory: () => ipcRenderer.invoke('game:selectDirectory'),
  validateGame: (path: string) => ipcRenderer.invoke('game:validate', path),
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
  openEvidence: () => ipcRenderer.invoke('diagnostics:openEvidence'),
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
}

contextBridge.exposeInMainWorld('loopstructorDesktop', api)
