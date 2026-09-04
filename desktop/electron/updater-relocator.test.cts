import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import {
  applyUpdateAndScheduleManagerExit,
  createElectronUpdaterCleanupHandoffPlan,
  createElectronUpdaterRelocationPlan,
  cleanupElectronUpdaterRuntimeCopy,
  cleanupElectronUpdaterRuntimeCopies,
  signalUpdaterWindowReady,
  stripUpdaterTransportArguments,
  isRuntimeOutsideTarget,
  isStagedUpdaterRun,
  stageElectronUpdaterRuntime,
} from './updater-relocator.cjs'

test('signals a visible updater window and strips transport-only arguments', () => {
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'loopstructor-updater-ready-test-'))
  try {
    const signalPath = path.join(temporary, 'ready-test.signal')
    const args = ['apply', '--window-ready-file', signalPath, '--desktop-staged-run', '--target', 'release']

    assert.equal(signalUpdaterWindowReady(args, temporary, 4321), signalPath)
    assert.equal(fs.readFileSync(signalPath, 'utf8'), '4321')
    assert.deepEqual(stripUpdaterTransportArguments(args), ['apply', '--target', 'release'])
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true })
  }
})

test('schedules Manager exit when the Host confirms updater startup', async () => {
  let exits = 0
  const response = await applyUpdateAndScheduleManagerExit(
    async () => ({ success: true, message: 'updater started' }),
    () => { exits += 1 },
  )

  assert.deepEqual(response, { success: true, message: 'updater started' })
  assert.equal(exits, 1)
})

test('keeps Manager open when updater startup is not confirmed', async () => {
  let exits = 0
  const response = await applyUpdateAndScheduleManagerExit(
    async () => ({ success: false, message: 'updater failed' }),
    () => { exits += 1 },
  )

  assert.deepEqual(response, { success: false, message: 'updater failed' })
  assert.equal(exits, 0)
})

test('hands cleanup to the installed updater after the Electron window exits', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'loopstructor-electron-cleanup-handoff-test-'))
  try {
    const target = path.join(root, 'release')
    const installedUpdater = path.join(target, 'manager', 'Loopstructor.AutoPlayer.Updater.exe')
    const temporaryRuntime = path.join(root, 'temporary', 'electron-runtime')
    fs.mkdirSync(path.dirname(installedUpdater), { recursive: true })
    fs.mkdirSync(temporaryRuntime, { recursive: true })
    fs.writeFileSync(installedUpdater, 'updater')

    const plan = createElectronUpdaterCleanupHandoffPlan(
      target,
      temporaryRuntime,
      4321,
      '0.6.70',
      { TEST_ENVIRONMENT: 'preserved' },
    )

    assert.equal(plan.executablePath, installedUpdater)
    assert.equal(plan.workingDirectory, path.dirname(installedUpdater))
    assert.deepEqual(plan.arguments, [
      'cleanup',
      '--target', target,
      '--current-version', '0.6.70',
      '--wait-pid', '4321',
      '--restart-manager',
      '--json',
    ])
    assert.equal(plan.environment.TEST_ENVIRONMENT, 'preserved')
    assert.equal(plan.environment.LOOPSTRUCTOR_AUTOPLAYER_CLEANUP_UPDATER_RUNTIME, temporaryRuntime)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})

test('copies the complete Electron runtime outside the installation tree', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'loopstructor-electron-relocator-test-'))
  try {
    const source = path.join(root, 'release', 'manager')
    const temporary = path.join(root, 'temporary')
    fs.mkdirSync(path.join(source, 'resources'), { recursive: true })
    const executable = path.join(source, 'Loopstructor-2-QA-Tool.exe')
    fs.writeFileSync(executable, 'manager')
    fs.writeFileSync(path.join(source, 'resources', 'app.asar'), 'asar')

    const plan = createElectronUpdaterRelocationPlan(source, executable, temporary)
    stageElectronUpdaterRuntime(source, plan)

    assert.ok(plan.destinationRoot.startsWith(path.resolve(temporary) + path.sep))
    assert.equal(fs.readFileSync(plan.executablePath, 'utf8'), 'manager')
    assert.equal(fs.readFileSync(path.join(plan.destinationRoot, 'resources', 'app.asar'), 'utf8'), 'asar')
    assert.equal(isStagedUpdaterRun(['apply', '--desktop-staged-run']), true)
    assert.equal(isStagedUpdaterRun(['apply']), false)
    assert.equal(isRuntimeOutsideTarget(plan.executablePath, path.join(root, 'release')), true)
    assert.equal(isRuntimeOutsideTarget(executable, path.join(root, 'release')), false)

    cleanupElectronUpdaterRuntimeCopy(plan.destinationRoot, source, temporary)
    assert.equal(fs.existsSync(plan.destinationRoot), false)

    const current = path.join(temporary, 'electron-current')
    fs.mkdirSync(current, { recursive: true })
    cleanupElectronUpdaterRuntimeCopy(current, current, temporary)
    assert.equal(fs.existsSync(current), true)
    cleanupElectronUpdaterRuntimeCopies(source, temporary)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})

test('copies the launcher executable while hard-linking the remaining runtime', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'loopstructor-electron-relocator-lock-test-'))
  try {
    const source = path.join(root, 'release', 'manager')
    const temporary = path.join(root, 'temporary')
    fs.mkdirSync(path.join(source, 'resources'), { recursive: true })
    const executable = path.join(source, 'Loopstructor-2-QA-Tool.exe')
    const asar = path.join(source, 'resources', 'app.asar')
    fs.writeFileSync(executable, 'manager')
    fs.writeFileSync(asar, 'asar')

    const plan = createElectronUpdaterRelocationPlan(source, executable, temporary)
    stageElectronUpdaterRuntime(source, plan)

    assert.notEqual(fs.statSync(plan.executablePath).ino, fs.statSync(executable).ino)
    assert.equal(fs.statSync(path.join(plan.destinationRoot, 'resources', 'app.asar')).ino, fs.statSync(asar).ino)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})
