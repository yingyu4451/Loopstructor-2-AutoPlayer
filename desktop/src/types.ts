export type RouteKey = 'game' | 'saves' | 'autoplay' | 'vehicles' | 'items' | 'relics' | 'battle' | 'objects' | 'spawn' | 'diagnostics' | 'settings'
export type ToastKind = 'success' | 'info' | 'warning' | 'error'
export type SkinId = 'skyspine'

export interface ManagerSettings {
  gameRoot: string
  unityProjectRoot?: string
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
  automaticSaveBackupEnabled?: boolean
  maximumSaveBackups?: number
  activeRoute: RouteKey
  sidebarCollapsed: boolean
  skinId?: SkinId
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

export interface UnityProjectInspection {
  path: string
  valid: boolean
  unityVersion: string
  bridgeInstalled: boolean
  message: string
}

export interface EditorBridgeInstance {
  instanceId: string
  kind: 'editor'
  processId: number
  displayName: string
  projectPath: string
  unityVersion: string
  gameVersion: string
  sceneName: string
  mode: 'editor-edit' | 'editor-play'
  runtimeReady: boolean
  lastSeenAt: string
}

export interface EditorBridgeConnection {
  success: boolean
  message: string
  instanceId: string
  processId: number
  mode: 'editor-edit' | 'editor-play'
  runtimeReady: boolean
  sceneName: string
  assemblySha256: string
}

export interface ConnectionState {
  trusted: boolean
  label: string
  reason: string
  processId?: number
  target?: 'none' | 'player' | 'editor'
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
  needsProcessRestart?: boolean
  timeline?: Array<{ timestampUtc: string; stage: string; kind: string; message: string }>
  [key: string]: unknown
}

export interface AutomationModeSetup {
  mode: string
  displayName: string
  available: boolean
  reason: string
}

export interface AutomationCharacterSetup {
  cfgIndex: number
  runtimeIndex: number
  difficultyIndex: number
  superModuleIndex: number
  displayName: string
  available?: boolean
  reason?: string
}

export interface AutomationSetup {
  modes: AutomationModeSetup[]
  characters: AutomationCharacterSetup[]
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

export interface SaveBackupStatus {
  enabled: boolean
  maximumBackups: number
  backupCount: number
  backupRoot: string
  latestBackup: string
  lastMessage: string
  lastBackupUtc?: string
  pending: boolean
  busy: boolean
}

export interface SaveBackupEntry {
  id: string
  chapter: number
  level: number
  createdAt: string
  fileCount: number
  totalBytes: number
  isLatest: boolean
}

export interface SaveBackupCatalog {
  backups: SaveBackupEntry[]
  status: SaveBackupStatus
}

export interface SaveRestoreResponse {
  success: boolean
  backupId: string
  targetDirectory: string
  gameRestarted: boolean
  message: string
  backups: SaveBackupEntry[]
}

export interface HostSnapshot {
  protocolVersion: number
  version: string
  settings: ManagerSettings
  game?: GameValidation
  plugin?: PluginStatus
  editorProject?: UnityProjectInspection
  editorInstances?: EditorBridgeInstance[]
  editorConnection?: EditorBridgeConnection
  connection: ConnectionState
  hello?: Record<string, unknown>
  status?: AutoPlayerStatus
  saveBackups?: SaveBackupStatus
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
  readonly isUpdater: boolean
  getSnapshot(): Promise<HostSnapshot>
  saveSettings(settings: ManagerSettings): Promise<ManagerSettings>
  selectGameDirectory(): Promise<unknown>
  validateGame(path: string): Promise<unknown>
  selectUnityProject(): Promise<UnityProjectInspection | null>
  validateUnityProject(path: string): Promise<UnityProjectInspection>
  installEditorBridge(): Promise<{ success: boolean; message: string; inspection?: UnityProjectInspection }>
  uninstallEditorBridge(): Promise<{ success: boolean; message: string; inspection?: UnityProjectInspection }>
  listEditorInstances(): Promise<EditorBridgeInstance[]>
  connectEditor(instanceId: string): Promise<EditorBridgeConnection>
  disconnectEditor(): Promise<{ success: boolean; message: string }>
  installPlugin(): Promise<unknown>
  setPluginEnabled(enabled: boolean): Promise<unknown>
  uninstallPlugin(): Promise<unknown>
  launchGame(): Promise<unknown>
  refreshConnection(): Promise<HostSnapshot>
  cheatCommand(command: string, args?: unknown): Promise<ControlResponse>
  queryAutomationSetup(): Promise<ControlResponse>
  startAutomation(): Promise<ControlResponse>
  pauseAutomation(): Promise<ControlResponse>
  resumeAutomation(): Promise<ControlResponse>
  stopAutomation(): Promise<ControlResponse>
  checkUpdates(): Promise<UpdateStatus>
  inspectUpdateProcesses(): Promise<{ gameRunning: boolean; processIds: number[] }>
  closeGameForUpdate(): Promise<{ success: boolean; remainingProcessIds: number[]; message: string }>
  applyUpdate(): Promise<{ success: boolean; message: string }>
  startUpdater(): Promise<{ success: boolean; message: string }>
  closeUpdater(): Promise<boolean>
  onUpdaterEvent(listener: (event: { event: string; payload: any }) => void): () => void
  openEvidence(): Promise<{ path: string }>
  openSaveBackups(): Promise<{ path: string }>
  listSaveBackups(): Promise<SaveBackupCatalog>
  restoreSaveBackup(backupId: string): Promise<SaveRestoreResponse>
  clearLogs(): Promise<unknown>
  minimize(): Promise<void>
  toggleMaximize(): Promise<void>
  close(): Promise<void>
  setZoom(factor: number): Promise<number>
  showSystemMenu(point: { x: number; y: number }): void
  onHostEvent(listener: (event: { event: string; payload: any }) => void): () => void
}
