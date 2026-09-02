import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import {
  createElectronUpdaterRelocationPlan,
  cleanupElectronUpdaterRuntimeCopy,
  cleanupElectronUpdaterRuntimeCopies,
  isRuntimeOutsideTarget,
  isStagedUpdaterRun,
  stageElectronUpdaterRuntime,
} from './updater-relocator.cjs'

test('copies the complete Electron runtime outside the installation tree', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'loopstructor-electron-relocator-test-'))
  try {
    const source = path.join(root, 'release', 'manager')
    const temporary = path.join(root, 'temporary')
    fs.mkdirSync(path.join(source, 'resources'), { recursive: true })
    const executable = path.join(source, 'Loopstructor.AutoPlayer.Manager.exe')
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
    const executable = path.join(source, 'Loopstructor.AutoPlayer.Manager.exe')
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
