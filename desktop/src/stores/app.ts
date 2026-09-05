import { defineStore } from 'pinia'
import type { AutomationSetup, CatalogItem, ControlResponse, EditorBridgeInstance, HostLogEntry, HostSnapshot, ManagerSettings, RouteKey, SaveBackupCatalog, SaveBackupEntry } from '../types'
import { useUiStore } from './ui'

const mutationCommands = new Set([
  'cheat.grantVehicle', 'cheat.removeVehicle', 'cheat.grantDisposable', 'cheat.clearConsumables',
  'cheat.grantCatapultPoint', 'cheat.removeCatapultPoint', 'cheat.clearBackpackCatapultPoints',
  'cheat.removeFieldCatapultPoint', 'cheat.clearFieldCatapultPoints', 'cheat.setFieldCatapultDeleteMode',
  'cheat.setBaseGodMode', 'cheat.endWave', 'cheat.clearEnemies', 'cheat.skipRewardPopup',
  'cheat.modifyVehicle', 'cheat.setVehicleEnchantment', 'cheat.modifyEnemy', 'cheat.grantRelic',
  'cheat.grantAllRelics', 'cheat.removeRelic', 'cheat.removeAllRelics', 'cheat.spawnEnemy',
  'cheat.setMapSkipEnabled', 'cheat.setSpawnPointCapture', 'cheat.removeSpawnPoint', 'cheat.clearSpawnPoints',
])
const cheatRoutes = new Set<RouteKey>(['vehicles', 'items', 'relics', 'battle', 'objects', 'spawn'])

export const useAppStore = defineStore('app', {
  state: () => ({
    snapshot: undefined as HostSnapshot | undefined,
    catalog: undefined as Record<string, any> | undefined,
    cheatState: undefined as Record<string, any> | undefined,
    vehicles: [] as Record<string, any>[],
    enemies: [] as Record<string, any>[],
    automationSetup: undefined as AutomationSetup | undefined,
    saveBackupCatalog: undefined as SaveBackupCatalog | undefined,
    currentRoute: 'game' as RouteKey,
    routeInitialized: false,
    connected: false,
    removeHostEvent: undefined as (() => void) | undefined,
  }),
  getters: {
    settings: (state): ManagerSettings | undefined => state.snapshot?.settings,
    route: (state): RouteKey => state.currentRoute,
    writeLocked: (state): boolean => state.snapshot?.connection.autoplayActive === true,
    cheatEnabled: (state): boolean => state.cheatState?.enabled === true || state.snapshot?.status?.cheatModeEnabled === true,
    catalogItems: (state) => (key: string): CatalogItem[] => (state.catalog?.[key] ?? []) as CatalogItem[],
  },
  actions: {
    async initialize() {
      const ui = useUiStore()
      this.removeHostEvent = window.loopstructorDesktop.onHostEvent((message) => {
        if (message.event === 'snapshot') this.applySnapshot(message.payload as HostSnapshot)
        else if (message.event === 'log' && this.snapshot) {
          this.snapshot.logs = [...this.snapshot.logs, message.payload as HostLogEntry].slice(-600)
        } else if (message.event === 'hostExit') ui.toast('.NET Host 已退出，请重新启动工具。', 'error')
        else if (message.event === 'hostDiagnostic') console.warn(message.payload?.message)
      })
      const snapshot = await ui.run(() => window.loopstructorDesktop.getSnapshot())
      if (snapshot) this.applySnapshot(snapshot)
    },
    applySnapshot(snapshot: HostSnapshot) {
      const becameConnected = !this.connected && snapshot.connection.trusted
      if (!this.routeInitialized) {
        this.currentRoute = snapshot.settings.activeRoute ?? 'game'
        this.routeInitialized = true
      }
      this.snapshot = snapshot
      this.connected = snapshot.connection.trusted
      if (becameConnected && cheatRoutes.has(this.currentRoute)) {
        void this.refreshCheat(false)
      }
    },
    async setRoute(route: RouteKey) {
      if (!this.snapshot) return
      this.currentRoute = route
      const settings = { ...this.snapshot.settings, activeRoute: route }
      this.snapshot.settings = settings
      try {
        await window.loopstructorDesktop.saveSettings(settings)
      } catch {
        // Navigation is renderer-owned. A failed preference write must not eject the user from the selected page.
      }
      if (this.connected && cheatRoutes.has(route)) await this.refreshCheat(false)
    },
    async saveSettings(settings: ManagerSettings, announce = true) {
      const ui = useUiStore()
      const payload = {
        ...settings,
        activeRoute: this.currentRoute,
        sidebarCollapsed: this.snapshot?.settings.sidebarCollapsed ?? settings.sidebarCollapsed,
      }
      const saved = await ui.run(() => window.loopstructorDesktop.saveSettings(payload), announce ? '设置已保存。' : undefined)
      if (saved && this.snapshot) {
        this.snapshot.settings = { ...saved, activeRoute: this.currentRoute }
      }
      return saved
    },
    async selectGame() {
      const ui = useUiStore()
      await ui.run(() => window.loopstructorDesktop.selectGameDirectory(), '游戏目录已验证。')
    },
    async selectUnityProject() {
      const inspection = await useUiStore().run(() => window.loopstructorDesktop.selectUnityProject(), 'Unity 工程已验证。')
      if (inspection && this.snapshot) this.snapshot.editorProject = inspection
      return inspection
    },
    async installEditorBridge() {
      const ui = useUiStore()
      const result = await ui.run(() => window.loopstructorDesktop.installEditorBridge())
      if (result?.inspection && this.snapshot) this.snapshot.editorProject = result.inspection
      if (result?.success) ui.toast(result.message, 'success')
      return result
    },
    async uninstallEditorBridge() {
      const ui = useUiStore()
      if (!await ui.confirm({
        title: '卸载 Editor 连接组件',
        message: '从所选 Unity 工程卸载 Loopstructor Editor 连接组件？这不会修改 Assets 或 Player 构建。',
        confirmText: '卸载连接组件',
        cancelText: '保留',
        danger: true,
      })) return undefined
      const result = await ui.run(() => window.loopstructorDesktop.uninstallEditorBridge())
      if (result?.inspection && this.snapshot) this.snapshot.editorProject = result.inspection
      if (result?.success) ui.toast(result.message, 'success')
      return result
    },
    async refreshEditorInstances() {
      try {
        const instances = await window.loopstructorDesktop.listEditorInstances()
        if (this.snapshot) this.snapshot.editorInstances = instances
        return instances
      } catch {
        return [] as EditorBridgeInstance[]
      }
    },
    async connectEditor(instanceId: string) {
      const connection = await useUiStore().run(() => window.loopstructorDesktop.connectEditor(instanceId), 'Unity Editor 已连接。')
      if (connection && this.snapshot) this.snapshot.editorConnection = connection
      return connection
    },
    async disconnectEditor() {
      const result = await useUiStore().run(() => window.loopstructorDesktop.disconnectEditor(), 'Unity Editor 已断开。')
      if (result && this.snapshot) this.snapshot.editorConnection = undefined
      return result
    },
    async installPlugin() {
      await useUiStore().run(() => window.loopstructorDesktop.installPlugin(), '插件已安装。')
    },
    async setPluginEnabled(enabled: boolean) {
      await useUiStore().run(() => window.loopstructorDesktop.setPluginEnabled(enabled), enabled ? '插件已启用。' : '插件已停用。')
    },
    async uninstallPlugin() {
      const ui = useUiStore()
      if (!await ui.confirm({ title: '卸载 AutoPlayer 插件', message: '仅删除 AutoPlayer 插件及其配置，保留共享 BepInEx 运行时。', confirmText: '卸载', danger: true })) return
      await ui.run(() => window.loopstructorDesktop.uninstallPlugin(), '插件已卸载。')
    },
    async launchGame() {
      await useUiStore().run(() => window.loopstructorDesktop.launchGame(), '游戏启动请求已发送。')
    },
    async refreshSaveBackups(announce = false) {
      const catalog = await useUiStore().run(
        () => window.loopstructorDesktop.listSaveBackups(),
        announce ? '存档列表已刷新。' : undefined,
      )
      if (catalog) this.saveBackupCatalog = catalog
      return catalog
    },
    async restoreSaveBackup(backup: SaveBackupEntry) {
      const ui = useUiStore()
      const confirmed = await ui.confirm({
        title: '关闭游戏并读取存档',
        message: `将读取第 ${backup.chapter} 章、第 ${backup.level} 关的备份。Manager 会先请求 Skyspine 正常关闭，恢复存档后再自动启动游戏。`,
        confirmText: '关闭游戏并读档',
        danger: true,
      })
      if (!confirmed) return undefined
      const result = await ui.run(() => window.loopstructorDesktop.restoreSaveBackup(backup.id))
      if (!result) return undefined
      this.saveBackupCatalog = {
        backups: result.backups,
        status: this.snapshot?.saveBackups ?? this.saveBackupCatalog?.status ?? {
          enabled: true, maximumBackups: 20, backupCount: result.backups.length,
          backupRoot: '', latestBackup: '', lastMessage: result.message, pending: false, busy: false,
        },
      }
      ui.toast(result.message, result.gameRestarted ? 'success' : 'warning')
      return result
    },
    async refreshConnection() {
      const snapshot = await useUiStore().run(() => window.loopstructorDesktop.refreshConnection())
      if (snapshot) this.applySnapshot(snapshot)
    },
    async installUpdate() {
      const ui = useUiStore()
      const update = this.snapshot?.update
      if (!update?.updateAvailable) return undefined
      const processState = await ui.run(() => window.loopstructorDesktop.inspectUpdateProcesses())
      if (!processState) return undefined
      const proceed = await ui.confirm({
        title: processState.gameRunning ? '关闭游戏与工具并更新' : '关闭工具并更新',
        message: processState.gameRunning
          ? `检测到 Skyspine 仍在运行（PID ${processState.processIds.join('、')}）。继续后会先请求游戏正常关闭；更新窗口显示后，当前 QA 工具窗口和后台 Host 会完全退出。是否继续？`
          : `将从 v${update.currentVersion} 更新到 v${update.latestVersion}。更新窗口显示后，当前 QA 工具窗口和后台 Host 会完全退出。是否关闭工具并继续更新？`,
        confirmText: processState.gameRunning ? '关闭游戏与工具并更新' : '关闭工具并更新',
        cancelText: '暂不更新',
        danger: processState.gameRunning,
      })
      if (!proceed) return undefined
      if (processState.gameRunning) {
        const result = await ui.run(() => window.loopstructorDesktop.closeGameForUpdate())
        if (!result?.success) return undefined
      }
      return await ui.run(() => window.loopstructorDesktop.applyUpdate())
    },
    async command(command: string, args: unknown = {}, announce = true): Promise<ControlResponse | undefined> {
      const ui = useUiStore()
      if (this.writeLocked && mutationCommands.has(command)) {
        ui.toast('自动游玩正在运行；停止现有自动游玩后才能修改游戏。', 'warning')
        return undefined
      }
      const response = await ui.run(() => window.loopstructorDesktop.cheatCommand(command, args))
      if (!response) return undefined
      if (response.status && this.snapshot) this.snapshot.status = response.status
      if (response.success) {
        if (announce && response.message) ui.toast(response.message, 'success')
      } else ui.toast(response.message || '作弊命令执行失败。', 'error')
      return response
    },
    async refreshCheat(announce = true) {
      const catalog = await this.command('cheat.queryCatalog', {}, false)
      if (catalog?.success && catalog.data) this.catalog = catalog.data
      const state = await this.command('cheat.queryState', {}, false)
      if (state?.success && state.data) this.cheatState = state.data
      if (announce && catalog?.success && state?.success) useUiStore().toast('作弊目录和当前状态已刷新。', 'success')
    },
    async refreshState(silent = false) {
      let response: ControlResponse | undefined
      if (silent) {
        try {
          response = await window.loopstructorDesktop.cheatCommand('cheat.queryState', {})
        } catch {
          return
        }
      } else {
        response = await this.command('cheat.queryState', {}, false)
      }
      if (response?.success && response.data) this.cheatState = response.data
    },
    async refreshVehicles() {
      const response = await this.command('cheat.queryVehicles', {}, false)
      if (response?.success) this.vehicles = response.data?.vehicles ?? []
    },
    async refreshEnemies() {
      const response = await this.command('cheat.queryEnemies', {}, false)
      if (response?.success) this.enemies = response.data?.enemies ?? []
    },
    async refreshAutomationSetup() {
      const response = await useUiStore().run(() => window.loopstructorDesktop.queryAutomationSetup())
      if (response?.success && response.data) this.automationSetup = response.data as AutomationSetup
      else this.automationSetup = undefined
      return response
    },
    async startAutomation() {
      return await this.automationCommand(() => window.loopstructorDesktop.startAutomation(), '自动游玩已开始。')
    },
    async pauseAutomation() {
      return await this.automationCommand(() => window.loopstructorDesktop.pauseAutomation(), '自动游玩已暂停。')
    },
    async resumeAutomation() {
      return await this.automationCommand(() => window.loopstructorDesktop.resumeAutomation(), '自动游玩已继续。')
    },
    async stopAutomation() {
      return await this.automationCommand(() => window.loopstructorDesktop.stopAutomation(), '自动游玩已停止。')
    },
    async automationCommand(call: () => Promise<ControlResponse>, successMessage: string) {
      const response = await useUiStore().run(call)
      if (!response) return undefined
      if (response.status && this.snapshot) this.snapshot.status = response.status
      useUiStore().toast(response.message || successMessage, response.success ? 'success' : 'error')
      return response
    },
  },
})
