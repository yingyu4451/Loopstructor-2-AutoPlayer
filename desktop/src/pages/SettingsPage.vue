<script setup lang="ts">
import { computed, ref, toRaw, watch } from 'vue'
import type { ManagerSettings } from '../types'
import { MonitorCog, RefreshCw, Download, Github } from '../icons'
import { useAppStore } from '../stores/app'
import { useUiStore } from '../stores/ui'
const store = useAppStore()
const ui = useUiStore()
const api = window.loopstructorDesktop
const cloneSettings = (settings: ManagerSettings) => structuredClone(toRaw(settings))
const draft = ref(store.settings ? cloneSettings(store.settings) : undefined)
watch(() => store.settings, (settings) => { if (settings) draft.value = cloneSettings(settings) }, { deep: true })
const zoom = computed(() => draft.value?.uiScaleMode === 'custom' ? draft.value.customUiScalePercent / 100 : 1)
async function save() {
  if (!draft.value) return
  await api.setZoom(zoom.value)
  await store.saveSettings(draft.value)
}
async function installUpdate() {
  const update = store.snapshot?.update
  if (!update?.updateAvailable) return
  const processState = await ui.run(() => api.inspectUpdateProcesses())
  if (!processState) return
  if (processState.gameRunning) {
    const close = await ui.confirm({ title: '关闭游戏并更新', message: `检测到 Skyspine 仍在运行（PID ${processState.processIds.join('、')}）。是否请求游戏正常关闭后继续更新？`, confirmText: '关闭并更新', danger: true })
    if (!close) return
    const result = await ui.run(() => api.closeGameForUpdate())
    if (!result?.success) return
  } else {
    const proceed = await ui.confirm({ title: '安装更新', message: `将从 v${update.currentVersion} 更新到 v${update.latestVersion}。Updater 会在本窗口关闭后完成替换。`, confirmText: '开始更新' })
    if (!proceed) return
  }
  await ui.run(() => api.applyUpdate())
}
</script>

<template>
  <div class="settings-workspace">
    <section class="mechanical-section">
      <header class="section-heading"><div class="heading-with-icon"><MonitorCog :size="21" /><div><h2>界面大小</h2><p>Electron 处理系统 DPI；自定义倍率叠加在系统缩放上。</p></div></div></header>
      <div v-if="draft" class="scale-controls">
        <label class="radio-card"><input v-model="draft.uiScaleMode" type="radio" value="system" /><span><strong>跟随系统 DPI</strong><small>随显示器缩放自动调整</small></span></label>
        <label class="radio-card"><input v-model="draft.uiScaleMode" type="radio" value="custom" /><span><strong>自定义</strong><small>系统 DPI × 自定义百分比</small></span></label>
        <label class="range-field" :class="{ disabled: draft.uiScaleMode !== 'custom' }"><span>缩放 {{ draft.customUiScalePercent }}%</span><input v-model.number="draft.customUiScalePercent" type="range" min="75" max="200" step="5" :disabled="draft.uiScaleMode !== 'custom'" /></label>
        <button class="button primary compact" @click="save">应用界面设置</button>
      </div>
    </section>
    <section class="mechanical-section update-settings">
      <header class="section-heading"><div><h2>程序更新</h2><p>Manager 每次启动都会自动检查公开 GitHub Release。</p></div><Github :size="22" /></header>
      <div class="update-panel" :class="{ available: store.snapshot?.update?.updateAvailable }">
        <div><span>当前版本</span><strong>v{{ store.snapshot?.version }}</strong></div>
        <div><span>最新版本</span><strong>{{ store.snapshot?.update?.latestVersion ? `v${store.snapshot.update.latestVersion}` : '尚未检查' }}</strong></div>
        <p>{{ store.snapshot?.update?.message || '启动后正在后台检查更新。' }}</p>
        <div class="action-bar">
          <button class="button secondary" @click="api.checkUpdates()"><RefreshCw :size="17" />检查更新</button>
          <button class="button primary" :disabled="!store.snapshot?.update?.updateAvailable" @click="installUpdate"><Download :size="17" />安装更新</button>
        </div>
      </div>
    </section>
  </div>
</template>
