import assert from 'node:assert/strict'
import { EventEmitter } from 'node:events'
import test from 'node:test'
import { stopChildProcess } from './host-client.cjs'

class FakeChildProcess extends EventEmitter {
  exitCode: number | null = null
  killCalls = 0
  readonly stdin = {
    writable: true,
    end: () => {
      this.stdin.writable = false
      setTimeout(() => {
        this.exitCode = 0
        this.emit('exit', 0)
      }, 10)
    },
  }

  kill(): boolean {
    this.killCalls += 1
    this.exitCode = 1
    this.emit('exit', 1)
    return true
  }
}

class StubbornChildProcess extends FakeChildProcess {
  override readonly stdin = {
    writable: true,
    end: () => { this.stdin.writable = false },
  }
}

test('waits for the Host process to exit after closing stdin', async () => {
  const child = new FakeChildProcess()

  await stopChildProcess(child as never, 100, 100)

  assert.equal(child.exitCode, 0)
  assert.equal(child.killCalls, 0)
})

test('terminates the owned Host when graceful shutdown times out', async () => {
  const child = new StubbornChildProcess()

  await stopChildProcess(child as never, 10, 100)

  assert.equal(child.exitCode, 1)
  assert.equal(child.killCalls, 1)
})
