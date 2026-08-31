import { defineStore } from 'pinia'
import type { ToastKind } from '../types'

interface ToastMessage {
  id: number
  kind: ToastKind
  message: string
}

interface ConfirmDialog {
  title: string
  message: string
  confirmText: string
  cancelText: string
  danger: boolean
  resolve: (value: boolean) => void
}

export const useUiStore = defineStore('ui', {
  state: () => ({
    toastQueue: [] as ToastMessage[],
    activeToast: undefined as ToastMessage | undefined,
    toastSequence: 0,
    confirmDialog: undefined as ConfirmDialog | undefined,
    busyCount: 0,
  }),
  getters: {
    busy: (state) => state.busyCount > 0,
  },
  actions: {
    toast(message: string, kind: ToastKind = 'info') {
      if (!message.trim()) return
      this.toastQueue.push({ id: ++this.toastSequence, kind, message: message.trim() })
      this.showNextToast()
    },
    showNextToast() {
      if (this.activeToast || this.toastQueue.length === 0) return
      this.activeToast = this.toastQueue.shift()
      window.setTimeout(() => {
        this.activeToast = undefined
        window.setTimeout(() => this.showNextToast(), 180)
      }, 3000)
    },
    confirm(options: Partial<Omit<ConfirmDialog, 'resolve'>> & Pick<ConfirmDialog, 'title' | 'message'>) {
      return new Promise<boolean>((resolve) => {
        this.confirmDialog = {
          title: options.title,
          message: options.message,
          confirmText: options.confirmText ?? '确认',
          cancelText: options.cancelText ?? '取消',
          danger: options.danger ?? false,
          resolve,
        }
      })
    },
    resolveConfirm(value: boolean) {
      const dialog = this.confirmDialog
      this.confirmDialog = undefined
      dialog?.resolve(value)
    },
    async run<T>(action: () => Promise<T>, successMessage?: string): Promise<T | undefined> {
      this.busyCount++
      try {
        const result = await action()
        if (successMessage) this.toast(successMessage, 'success')
        return result
      } catch (error) {
        this.toast(error instanceof Error ? error.message : String(error), 'error')
        return undefined
      } finally {
        this.busyCount--
      }
    },
  },
})
