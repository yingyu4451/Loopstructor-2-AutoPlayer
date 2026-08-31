import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useUiStore } from '../src/stores/ui'

describe('UI message flow', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.useFakeTimers()
  })

  it('shows toast messages in FIFO order for three seconds each', async () => {
    const store = useUiStore()
    store.toast('first', 'success')
    store.toast('second', 'warning')

    expect(store.activeToast?.message).toBe('first')
    expect(store.toastQueue.map((item) => item.message)).toEqual(['second'])

    await vi.advanceTimersByTimeAsync(3000)
    expect(store.activeToast).toBeUndefined()
    await vi.advanceTimersByTimeAsync(180)
    expect(store.activeToast?.message).toBe('second')
  })

  it('resolves mechanical confirmations without losing the requested action', async () => {
    const store = useUiStore()
    const result = store.confirm({ title: '更新', message: '关闭游戏并继续？', danger: true })
    expect(store.confirmDialog?.confirmText).toBe('确认')
    store.resolveConfirm(true)
    await expect(result).resolves.toBe(true)
  })
})
