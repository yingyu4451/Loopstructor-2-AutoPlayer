<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { gsap } from 'gsap'
import { CheckCircle2, Download, RefreshCw, XCircle } from '../icons'

interface ProgressSnapshot {
  stage?: string
  overallPercent?: number
  message?: string
  downloadedBytes?: number
  totalBytes?: number
  bytesPerSecond?: number
  canCancel?: boolean
  isFailure?: boolean
}

const api = window.loopstructorDesktop
const progress = ref<ProgressSnapshot>({ message: '正在准备更新…', overallPercent: 0 })
const result = ref<{ success?: boolean; message?: string; latestVersion?: string; usedIncrementalUpdate?: boolean }>()
const exitCode = ref<number | null>(null)
const logs = ref<string[]>([])
const started = ref(false)
const progressFill = ref<HTMLElement>()
let removeListener: (() => void) | undefined
let progressTween: gsap.core.Tween | undefined

const percent = computed(() => Math.max(0, Math.min(100, progress.value.overallPercent ?? 0)))
const isFailure = computed(() => progress.value.isFailure === true || result.value?.success === false || (exitCode.value !== null && exitCode.value !== 0))
const statusIcon = computed(() => isFailure.value ? XCircle : result.value?.success ? CheckCircle2 : Download)
const statusText = computed(() => {
  if (isFailure.value) return '更新未完成'
  if (result.value?.success) return '更新完成'
  return '正在更新'
})
const stageLabel = computed(() => {
  const labels: Record<string, string> = {
    '0': '准备中', '1': '检查更新', '2': '下载中', '3': '校验中', '4': '解压中',
    '5': '等待进程退出', '6': '安装中', '7': '重启中', '8': '已完成',
  }
  return labels[String(progress.value.stage ?? '')] || String(progress.value.stage || '准备中')
})

watch(percent, (value) => {
  if (!progressFill.value) return
  progressTween?.kill()
  const reduced = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true
  progressTween = gsap.to(progressFill.value, {
    scaleX: value / 100,
    duration: reduced ? 0 : 0.34,
    ease: 'power2.out',
  })
}, { immediate: true })

function formatBytes(value = 0): string {
  if (value < 1024) return `${value} B`
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`
  if (value < 1024 * 1024 * 1024) return `${(value / 1024 / 1024).toFixed(1)} MB`
  return `${(value / 1024 / 1024 / 1024).toFixed(2)} GB`
}

function formatRate(value = 0): string {
  return value > 0 ? `${formatBytes(value)}/s` : ''
}

function handleEvent(message: { event: string; payload: any }): void {
  if (message.event === 'progress') {
    progress.value = { ...progress.value, ...(message.payload as ProgressSnapshot) }
    return
  }
  if (message.event === 'result') {
    result.value = message.payload
    if (message.payload?.message) logs.value.push(message.payload.message)
    return
  }
  if (message.event === 'stderr' || message.event === 'log' || message.event === 'error') {
    const text = String(message.payload?.message ?? '')
    if (text.trim()) logs.value.push(text.trim())
    return
  }
  if (message.event === 'exit') {
    exitCode.value = typeof message.payload?.code === 'number' ? message.payload.code : null
  }
}

async function closeUpdater(): Promise<void> {
  if (await api.closeUpdater()) return
  logs.value.push('更新正在执行，完成前不能关闭窗口。')
}

onMounted(async () => {
  await nextTick()
  if (!window.matchMedia?.('(prefers-reduced-motion: reduce)').matches) {
    gsap.fromTo('.updater-card', { opacity: 0, y: 12 }, { opacity: 1, y: 0, duration: 0.32, ease: 'power2.out' })
  }
  removeListener = api.onUpdaterEvent(handleEvent)
  if (started.value) return
  started.value = true
  const response = await api.startUpdater()
  if (!response.success) {
    result.value = { success: false, message: response.message }
    logs.value.push(response.message)
  }
})

onBeforeUnmount(() => {
  removeListener?.()
  progressTween?.kill()
})
</script>

<template>
  <main class="updater-shell">
    <section class="updater-card mechanical-section" aria-live="polite">
      <header class="updater-heading">
        <div>
          <div class="eyebrow">LOOPSTRUCTOR AUTOPLAYER</div>
          <h1><component :is="statusIcon" :size="22" />{{ statusText }}</h1>
        </div>
        <span class="updater-stage">{{ stageLabel }}</span>
      </header>
      <p class="updater-message">{{ result?.message || progress.message || '正在准备更新…' }}</p>
      <div class="updater-progress-track" role="progressbar" :aria-valuenow="percent" aria-valuemin="0" aria-valuemax="100">
        <div ref="progressFill" class="updater-progress-fill" :class="{ failure: isFailure, completed: result?.success }" />
      </div>
      <div class="updater-progress-meta">
        <strong>{{ percent }}%</strong>
        <span v-if="progress.totalBytes">{{ formatBytes(progress.downloadedBytes) }} / {{ formatBytes(progress.totalBytes) }} <em>{{ formatRate(progress.bytesPerSecond) }}</em></span>
        <span v-else>{{ result?.usedIncrementalUpdate ? '增量更新' : '完整安装包' }}</span>
      </div>
      <div v-if="logs.length" class="updater-log" role="log">
        <p v-for="(line, index) in logs.slice(-5)" :key="`${index}-${line}`">{{ line }}</p>
      </div>
      <footer class="updater-actions">
        <span v-if="!isFailure && !result?.success" class="updater-hint"><RefreshCw :size="14" />更新过程中请勿关闭窗口</span>
        <button class="button secondary compact" :disabled="!isFailure && !result?.success" @click="closeUpdater">{{ result?.success ? '关闭' : '退出' }}</button>
      </footer>
    </section>
  </main>
</template>
