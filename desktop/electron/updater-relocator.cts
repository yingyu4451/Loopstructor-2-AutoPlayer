import fs from 'node:fs'
import { randomUUID } from 'node:crypto'
import os from 'node:os'
import path from 'node:path'

const temporaryFolderName = 'LoopstructorAutoPlayerUpdater'
const copyPrefix = 'electron-'

export interface ElectronUpdaterRelocationPlan {
  destinationRoot: string
  executablePath: string
  workingDirectory: string
}

export interface ElectronUpdaterCleanupHandoffPlan {
  executablePath: string
  workingDirectory: string
  arguments: string[]
  environment: NodeJS.ProcessEnv
}

export async function applyUpdateAndScheduleManagerExit(
  applyUpdate: () => Promise<unknown>,
  scheduleExit: () => void,
): Promise<unknown> {
  const response = await applyUpdate()
  if (typeof response === 'object'
      && response !== null
      && (response as { success?: unknown }).success === true) {
    scheduleExit()
  }
  return response
}

export function isStagedUpdaterRun(argumentsToInspect: readonly string[]): boolean {
  return argumentsToInspect.some(argument => argument.toLowerCase() === '--desktop-staged-run')
}

export function isRuntimeOutsideTarget(runtimeExecutable: string, targetRoot: string): boolean {
  const runtime = path.resolve(runtimeExecutable)
  const target = path.resolve(targetRoot)
  return !isContained(target, runtime)
}

export function createElectronUpdaterCleanupHandoffPlan(
  targetRoot: string,
  currentRuntimeRoot: string,
  processId: number,
  version: string,
  environment: NodeJS.ProcessEnv = process.env,
): ElectronUpdaterCleanupHandoffPlan {
  const target = normalizeDirectory(targetRoot)
  const runtime = normalizeDirectory(currentRuntimeRoot)
  if (!Number.isSafeInteger(processId) || processId <= 0) throw new Error('更新窗口进程 ID 无效。')
  if (!version.trim() || version.startsWith('--')) throw new Error('更新版本号无效。')
  if (isContained(target, runtime) || isContained(runtime, target)) {
    throw new Error('更新窗口运行时必须位于安装目录之外。')
  }

  const executablePath = normalizeFile(path.join(
    target,
    'manager',
    'Loopstructor.AutoPlayer.Updater.exe',
  ))
  return {
    executablePath,
    workingDirectory: path.dirname(executablePath),
    arguments: [
      'cleanup',
      '--target', target,
      '--current-version', version,
      '--wait-pid', String(processId),
      '--restart-manager',
      '--json',
    ],
    environment: {
      ...environment,
      LOOPSTRUCTOR_AUTOPLAYER_CLEANUP_UPDATER_RUNTIME: runtime,
    },
  }
}

export function cleanupElectronUpdaterRuntimeCopy(
  candidateRoot: string,
  currentRuntimeRoot: string,
  temporaryBase = path.join(os.tmpdir(), temporaryFolderName),
): void {
  const candidate = path.resolve(candidateRoot)
  const safeTemporaryBase = path.resolve(temporaryBase)
  if (path.dirname(candidate) !== safeTemporaryBase
      || !path.basename(candidate).startsWith(copyPrefix)
      || path.resolve(currentRuntimeRoot) === candidate) {
    return
  }
  try {
    fs.rmSync(candidate, { recursive: true, force: true })
  } catch {
    // The previous updater can remain alive briefly after the new Manager starts.
  }
}

export function cleanupElectronUpdaterRuntimeCopies(
  currentRuntimeRoot: string,
  temporaryBase = path.join(os.tmpdir(), temporaryFolderName),
): void {
  cleanupElectronCopies(temporaryBase, currentRuntimeRoot, 0)
}

export function createElectronUpdaterRelocationPlan(
  sourceRoot: string,
  executablePath: string,
  temporaryBase = path.join(os.tmpdir(), temporaryFolderName),
): ElectronUpdaterRelocationPlan {
  const source = normalizeDirectory(sourceRoot)
  const executable = normalizeFile(executablePath)
  const safeTemporaryBase = path.resolve(temporaryBase)
  fs.mkdirSync(safeTemporaryBase, { recursive: true })
  const temporaryStats = fs.lstatSync(safeTemporaryBase)
  if (!temporaryStats.isDirectory() || temporaryStats.isSymbolicLink()) {
    throw new Error(`更新临时目录无效：${safeTemporaryBase}`)
  }
  ensureContained(source, executable, 'Electron 更新程序必须位于 Manager 目录中。')
  if (isContained(source, safeTemporaryBase) || isContained(safeTemporaryBase, source)) {
    throw new Error('Electron 更新程序的源目录与临时目录不能重叠。')
  }
  const destinationRoot = path.join(safeTemporaryBase, `${copyPrefix}${randomUUID().replaceAll('-', '')}`)
  return {
    destinationRoot: path.resolve(destinationRoot),
    executablePath: path.join(destinationRoot, path.relative(source, executable)),
    workingDirectory: destinationRoot,
  }
}

export function stageElectronUpdaterRuntime(
  sourceRoot: string,
  plan: ElectronUpdaterRelocationPlan,
): void {
  const source = normalizeDirectory(sourceRoot)
  const destination = path.resolve(plan.destinationRoot)
  fs.mkdirSync(path.dirname(destination), { recursive: true })
  cleanupElectronCopies(path.dirname(destination), source, 7 * 24 * 60 * 60 * 1000)
  fs.mkdirSync(destination, { recursive: false })
  try {
    stageDirectoryContents(source, destination, path.resolve(plan.executablePath))
  } catch (error) {
    try { fs.rmSync(destination, { recursive: true, force: true }) } catch { /* best effort */ }
    throw error
  }
  if (!fs.existsSync(plan.executablePath)) {
    throw new Error('Electron 更新程序临时副本不完整。')
  }
}

function stageDirectoryContents(source: string, destination: string, executablePath: string): void {
  for (const entry of fs.readdirSync(source, { withFileTypes: true })) {
    const current = path.join(source, entry.name)
    const target = path.join(destination, entry.name)
    const stats = fs.lstatSync(current)
    if (stats.isSymbolicLink()) throw new Error(`Manager 目录包含不允许的符号链接：${current}`)
    if (entry.isDirectory()) {
      fs.mkdirSync(target, { recursive: false })
      stageDirectoryContents(current, target, executablePath)
      continue
    }
    if (!entry.isFile()) throw new Error(`Manager 目录包含不支持的文件类型：${current}`)
    try {
      if (path.resolve(target) === executablePath) fs.copyFileSync(current, target, fs.constants.COPYFILE_EXCL)
      else fs.linkSync(current, target)
    } catch (error) {
      if (!shouldCopyInsteadOfLink(error)) throw error
      fs.copyFileSync(current, target, fs.constants.COPYFILE_EXCL)
    }
  }
}

function shouldCopyInsteadOfLink(error: unknown): boolean {
  const code = (error as NodeJS.ErrnoException | undefined)?.code
  return code === 'EXDEV' || code === 'EPERM' || code === 'EACCES' || code === 'ENOTSUP' || code === 'EMLINK'
}

function cleanupElectronCopies(temporaryBase: string, currentSource: string, minimumAgeMs: number): void {
  if (!fs.existsSync(temporaryBase)) return
  for (const entry of fs.readdirSync(temporaryBase, { withFileTypes: true })) {
    if (!entry.isDirectory() || !entry.name.startsWith(copyPrefix)) continue
    const candidate = path.join(temporaryBase, entry.name)
    try {
      if (path.resolve(candidate) === path.resolve(currentSource)) continue
      const age = Date.now() - fs.statSync(candidate).birthtimeMs
      if (age <= minimumAgeMs) continue
      fs.rmSync(candidate, { recursive: true, force: true })
    } catch {
      // A previous updater may still own the directory. It is safe to leave it for a later cleanup.
    }
  }
}

function normalizeDirectory(value: string): string {
  const normalized = path.resolve(value)
  const stats = fs.lstatSync(normalized)
  if (!stats.isDirectory() || stats.isSymbolicLink()) throw new Error(`目录无效：${normalized}`)
  return normalized
}

function normalizeFile(value: string): string {
  const normalized = path.resolve(value)
  const stats = fs.lstatSync(normalized)
  if (!stats.isFile() || stats.isSymbolicLink()) throw new Error(`文件无效：${normalized}`)
  return normalized
}

function isContained(parent: string, candidate: string): boolean {
  const relative = path.relative(parent, candidate)
  return relative === '' || (!relative.startsWith(`..${path.sep}`) && relative !== '..' && !path.isAbsolute(relative))
}

function ensureContained(parent: string, candidate: string, message: string): void {
  if (!isContained(parent, candidate) || path.resolve(parent) === path.resolve(candidate)) throw new Error(message)
}
