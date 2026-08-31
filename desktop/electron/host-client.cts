import { ChildProcessWithoutNullStreams, spawn } from 'node:child_process'
import { createInterface } from 'node:readline'
import { EventEmitter } from 'node:events'
import path from 'node:path'
import fs from 'node:fs'

interface PendingRequest {
  resolve: (value: unknown) => void
  reject: (reason: Error) => void
  timer: NodeJS.Timeout
}

export class HostClient extends EventEmitter {
  private process?: ChildProcessWithoutNullStreams
  private readonly pending = new Map<string, PendingRequest>()
  private requestSequence = 0

  constructor(
    private readonly managerDirectory: string,
    private readonly distributionRoot: string,
  ) {
    super()
  }

  start(parentProcessId: number): void {
    if (this.process) return
    const configured = process.env.LOOPSTRUCTOR_AUTOPLAYER_HOST_PATH
    const packaged = path.join(this.managerDirectory, 'Loopstructor.AutoPlayer.Host.exe')
    const development = path.join(
      this.distributionRoot,
      'src',
      'Loopstructor.AutoPlayer.Host',
      'bin',
      'Debug',
      'net8.0-windows',
      'Loopstructor.AutoPlayer.Host.exe',
    )
    const executable = configured || (fs.existsSync(packaged) ? packaged : development)
    if (!fs.existsSync(executable)) {
      throw new Error(`找不到 .NET Host：${executable}`)
    }

    this.process = spawn(executable, ['--parent-pid', String(parentProcessId)], {
      cwd: this.managerDirectory,
      windowsHide: true,
      env: {
        ...process.env,
        LOOPSTRUCTOR_AUTOPLAYER_DISTRIBUTION_ROOT: this.distributionRoot,
      },
    })
    const lines = createInterface({ input: this.process.stdout })
    lines.on('line', (line) => this.handleLine(line))
    this.process.stderr.on('data', (chunk) => this.emit('diagnostic', String(chunk)))
    this.process.on('exit', (code) => {
      const error = new Error(`.NET Host 已退出（代码 ${code ?? 'unknown'}）。`)
      for (const request of this.pending.values()) {
        clearTimeout(request.timer)
        request.reject(error)
      }
      this.pending.clear()
      this.process = undefined
      this.emit('exit', code)
    })
  }

  async invoke(method: string, params?: unknown, timeoutMs = 45000): Promise<unknown> {
    if (!this.process || !this.process.stdin.writable) throw new Error('.NET Host 尚未启动。')
    const id = `${process.pid}-${Date.now()}-${++this.requestSequence}`
    const promise = new Promise<unknown>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id)
        reject(new Error(`等待 Host 方法 ${method} 响应超时。`))
      }, timeoutMs)
      this.pending.set(id, { resolve, reject, timer })
    })
    this.process.stdin.write(`${JSON.stringify({ id, method, params: params ?? {} })}\n`)
    return promise
  }

  stop(): void {
    this.process?.stdin.end()
  }

  private handleLine(line: string): void {
    try {
      const message = JSON.parse(line) as {
        id?: string
        success?: boolean
        result?: unknown
        error?: string
        event?: string
        payload?: unknown
      }
      if (message.event) {
        this.emit('event', { event: message.event, payload: message.payload })
        return
      }
      if (!message.id) return
      const request = this.pending.get(message.id)
      if (!request) return
      clearTimeout(request.timer)
      this.pending.delete(message.id)
      if (message.success) request.resolve(message.result)
      else request.reject(new Error(message.error || 'Host 返回了未知错误。'))
    } catch (error) {
      this.emit('diagnostic', `Host 输出不是有效 JSON：${String(error)}`)
    }
  }
}
