export type RouteKey = 'game' | 'autoplay' | 'vehicles' | 'items' | 'relics' | 'battle' | 'objects' | 'spawn' | 'diagnostics' | 'settings'
export type ToastKind = 'success' | 'info' | 'warning' | 'error'

export interface ManagerSettings {
  gameRoot: string
  profileName: string
  continueExistingProfile: boolean
  gameMode: string
  overrideGameSpeed: boolean
  speedState: number
  maxRunMinutes: number
  skipStory: boolean
  decisionPriority: string | number
  uiScaleMode: 'system' | 'custom'
  customUiScalePercent: number
  characterCfgIndex: number
  activeRoute: RouteKey
  sidebarCollapsed: boolean
  gitHubOwner: string
  gitHubRepository: string
}

export interface GameValidation {
  isValid: boolean
  gameRoot: string
  executablePath: string
  assemblySha256: string
  assemblyMvid: string
  productName: string
  productVersion: string
  steamAppId: string
  errors: string[]
  warnings: string[]
}

export interface PluginStatus {
  state: 'notInstalled' | 'enabled' | 'disabled' | 'incomplete'
  bepInExPresent: boolean
  bepInExCompatible: boolean
  pluginVersion: string
  detail: string
}

export interface ConnectionState {
  trusted: boolean
  label: string
  reason: string
  processId?: number
  cheatAvailable: boolean
  autoplayActive: boolean
}

export interface AutoPlayerStatus {
  runState: string
  outcome: string
  stage: string
  stageDetail: string
  scene: string
  pluginVersion: string
  gameVersion: string
  unityVersion: string
  assemblySha256: string
  fingerprintAccepted: boolean
  runtimeContractAvailable: boolean
  evidenceDirectory: string
  artifactDirectory: string
  cheatModeEnabled: boolean
  cheatUsed: boolean
  enemyIdsVisible: boolean
  enemyBuffsVisible: boolean
  baseGodModeEnabled: boolean
  mapSkipEnabled: boolean
  lastMessage: string
  currentFps: number
  onePercentLowFps: number
  frameTimeP99Ms: number
  lastRuntimeCommand: string
  lastRuntimeCommandDurationMs: number
  wavesStarted: number
  wavesCompleted: number
  currentChapter: number
  currentMapLayer: number
  [key: string]: unknown
}

export interface HostLogEntry {
  timestampUtc: string
  level: string
  message: string
}

export interface UpdateStatus {
  success: boolean
  updateAvailable: boolean
  currentVersion: string
  latestVersion: string
  message: string
}

export interface HostSnapshot {
  protocolVersion: number
  version: string
  settings: ManagerSettings
  game?: GameValidation
  plugin?: PluginStatus
  connection: ConnectionState
  hello?: Record<string, unknown>
  status?: AutoPlayerStatus
  update?: UpdateStatus
  logs: HostLogEntry[]
}

export interface CatalogItem {
  id: string
  enumName?: string
  name?: string
  fallbackName?: string
  description?: string
  iconBase64?: string
  iconDataUrl?: string
  typeKey?: string
  typeName?: string
  typeOrder?: number
  familyKey?: string
  familyName?: string
  familyOrder?: number
  level?: number
  itemOrder?: number
  tags?: string[]
  count?: number
  [key: string]: unknown
}

export interface ControlResponse {
  id?: string
  success: boolean
  message: string
  status?: AutoPlayerStatus
  hello?: Record<string, unknown>
  data?: Record<string, any>
}

export interface DesktopApi {
  getSnapshot(): Promise<HostSnapshot>
  saveSettings(settings: ManagerSettings): Promise<ManagerSettings>
  selectGameDirectory(): Promise<unknown>
  validateGame(path: string): Promise<unknown>
  installPlugin(): Promise<unknown>
  setPluginEnabled(enabled: boolean): Promise<unknown>
  uninstallPlugin(): Promise<unknown>
  launchGame(): Promise<unknown>
  refreshConnection(): Promise<HostSnapshot>
  cheatCommand(command: string, args?: unknown): Promise<ControlResponse>
  stopAutomation(): Promise<ControlResponse>
  checkUpdates(): Promise<UpdateStatus>
  inspectUpdateProcesses(): Promise<{ gameRunning: boolean; processIds: number[] }>
  closeGameForUpdate(): Promise<{ success: boolean; remainingProcessIds: number[]; message: string }>
  applyUpdate(): Promise<{ success: boolean; message: string }>
  openEvidence(): Promise<{ path: string }>
  clearLogs(): Promise<unknown>
  minimize(): Promise<void>
  toggleMaximize(): Promise<void>
  close(): Promise<void>
  setZoom(factor: number): Promise<number>
  showSystemMenu(point: { x: number; y: number }): void
  onHostEvent(listener: (event: { event: string; payload: any }) => void): () => void
}
