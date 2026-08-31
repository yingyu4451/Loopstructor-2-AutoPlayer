import { defineStore } from 'pinia'
import type { AutomationSetup, CatalogItem, ControlResponse, HostLogEntry, HostSnapshot, ManagerSettings, RouteKey } from '../types'
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

export const useAppStore = defineStore('app', {
  state: () => ({
    snapshot: undefined as HostSnapshot | undefined,
    catalog: undefined as Record<string, any> | undefined,
    cheatState: undefined as Record<string, any> | undefined,
    vehicles: [] as Record<string, any>[],
    enemies: [] as Record<string, any>[],
    automationSetup: undefined as AutomationSetup | undefined,
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
      if (this.snapshot?.connection.trusted) await this.refreshCheat(false)
    },
    applySnapshot(snapshot: HostSnapshot) {
      if (!this.routeInitialized) {
        this.currentRoute = snapshot.settings.activeRoute ?? 'game'
        this.routeInitialized = true
      }
      this.snapshot = snapshot
      this.connected = snapshot.connection.trusted
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
      if (processState.gameRunning) {
        const close = await ui.confirm({
          title: '关闭游戏并更新',
          message: `检测到 Skyspine 仍在运行（PID ${processState.processIds.join('、')}）。是否请求游戏正常关闭后继续更新？`,
          confirmText: '关闭并更新',
          danger: true,
        })
        if (!close) return undefined
        const result = await ui.run(() => window.loopstructorDesktop.closeGameForUpdate())
        if (!result?.success) return undefined
      } else {
        const proceed = await ui.confirm({
          title: '安装更新',
          message: `将从 v${update.currentVersion} 更新到 v${update.latestVersion}。Updater 会在本窗口关闭后完成替换。`,
          confirmText: '开始更新',
        })
        if (!proceed) return undefined
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
