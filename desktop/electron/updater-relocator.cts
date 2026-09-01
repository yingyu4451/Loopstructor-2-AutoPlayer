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

export function isStagedUpdaterRun(argumentsToInspect: readonly string[]): boolean {
  return argumentsToInspect.some(argument => argument.toLowerCase() === '--desktop-staged-run')
}

export function isRuntimeOutsideTarget(runtimeExecutable: string, targetRoot: string): boolean {
  const runtime = path.resolve(runtimeExecutable)
  const target = path.resolve(targetRoot)
  return !isContained(target, runtime)
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
  fs.cpSync(source, destination, {
    recursive: true,
    errorOnExist: true,
    force: false,
    verbatimSymlinks: true,
    filter: current => {
      const stats = fs.lstatSync(current)
      if (stats.isSymbolicLink()) throw new Error(`Manager 目录包含不允许的符号链接：${current}`)
      return true
    },
  })
  if (!fs.existsSync(plan.executablePath)) {
    throw new Error('Electron 更新程序临时副本不完整。')
  }
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
