<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { FileText, Activity, Trash2, FolderOpen } from '../icons'
import { useAppStore } from '../stores/app'
const store = useAppStore()
const api = window.loopstructorDesktop
const active = ref<'logs' | 'status'>('logs')
const logHost = ref<HTMLElement>()
const logs = computed(() => store.snapshot?.logs ?? [])
watch(() => logs.value.length, async () => { await nextTick(); if (logHost.value) logHost.value.scrollTop = logHost.value.scrollHeight })
const statusRows = computed(() => {
  const status = store.snapshot?.status
  return [
    ['连接', store.snapshot?.connection.label], ['游戏版本', status?.gameVersion || store.snapshot?.game?.productVersion],
    ['插件版本', status?.pluginVersion || store.snapshot?.plugin?.pluginVersion], ['运行状态', status?.runState],
    ['当前场景', status?.scene], ['结果', status?.outcome], ['章节 / 地图层', status ? `${status.currentChapter} / ${status.currentMapLayer}` : '-'],
    ['帧率', status ? `${status.currentFps?.toFixed?.(1) ?? 0} FPS / Low ${status.onePercentLowFps?.toFixed?.(1) ?? 0}` : '-'],
    ['运行时调用', status ? `${status.lastRuntimeCommand || '-'} · ${status.lastRuntimeCommandDurationMs || 0} ms` : '-'],
    ['程序集', store.snapshot?.game?.assemblySha256], ['证据目录', status?.evidenceDirectory || status?.artifactDirectory],
  ]
})
</script>

<template>
  <div class="diagnostics-workspace mechanical-section">
    <header class="diagnostic-tabs">
      <div class="segmented-control">
        <button :class="{ active: active === 'logs' }" @click="active = 'logs'"><FileText :size="16" />运行日志</button>
        <button :class="{ active: active === 'status' }" @click="active = 'status'"><Activity :size="16" />运行状态</button>
      </div>
      <div class="header-actions">
        <button class="button secondary compact" @click="api.openEvidence()"><FolderOpen :size="16" />证据目录</button>
        <button v-if="active === 'logs'" class="button secondary compact" @click="api.clearLogs()"><Trash2 :size="16" />清空</button>
      </div>
    </header>
    <div v-if="active === 'logs'" ref="logHost" class="log-console">
      <div v-for="(entry, index) in logs" :key="`${entry.timestampUtc}-${index}`" :class="`log-${entry.level}`">
        <time>{{ new Date(entry.timestampUtc).toLocaleTimeString('zh-CN', { hour12: false }) }}</time><span>{{ entry.level.toUpperCase() }}</span><p>{{ entry.message }}</p>
      </div>
      <p v-if="logs.length === 0" class="empty-state">尚无运行日志。</p>
    </div>
    <div v-else class="status-grid">
      <div v-for="row in statusRows" :key="row[0]"><span>{{ row[0] }}</span><strong>{{ row[1] || '-' }}</strong></div>
    </div>
  </div>
</template>
