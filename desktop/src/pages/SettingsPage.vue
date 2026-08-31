<script setup lang="ts">
import { computed, ref, toRaw, watch } from 'vue'
import type { ManagerSettings } from '../types'
import { MonitorCog, RefreshCw, Download, Github, Save, FolderOpen } from '../icons'
import { useAppStore } from '../stores/app'
const store = useAppStore()
const api = window.loopstructorDesktop
const cloneSettings = (settings: ManagerSettings): ManagerSettings => ({
  ...structuredClone(toRaw(settings)),
  automaticSaveBackupEnabled: settings.automaticSaveBackupEnabled ?? true,
  maximumSaveBackups: settings.maximumSaveBackups ?? 20,
})
const draft = ref(store.settings ? cloneSettings(store.settings) : undefined)
const dirty = ref(false)
watch(() => store.settings, (settings) => {
  if (settings && !dirty.value) draft.value = cloneSettings(settings)
})
const zoom = computed(() => draft.value?.uiScaleMode === 'custom' ? draft.value.customUiScalePercent / 100 : 1)
function markDirty() {
  dirty.value = true
}
async function save() {
  if (!draft.value) return
  const saved = await store.saveSettings(cloneSettings(draft.value))
  if (!saved) return
  dirty.value = false
  draft.value = cloneSettings(saved)
  await api.setZoom(zoom.value)
}
</script>

<template>
  <div class="settings-workspace">
    <section class="mechanical-section">
      <header class="section-heading"><div class="heading-with-icon"><MonitorCog :size="21" /><div><h2>界面大小</h2><p>Electron 处理系统 DPI；自定义倍率叠加在系统缩放上。</p></div></div></header>
      <div v-if="draft" class="scale-controls">
        <label class="radio-card"><input v-model="draft.uiScaleMode" type="radio" value="system" @change="markDirty" /><span><strong>跟随系统 DPI</strong><small>随显示器缩放自动调整</small></span></label>
        <label class="radio-card"><input v-model="draft.uiScaleMode" type="radio" value="custom" @change="markDirty" /><span><strong>自定义</strong><small>系统 DPI × 自定义百分比</small></span></label>
        <label class="range-field" :class="{ disabled: draft.uiScaleMode !== 'custom' }"><span>缩放 {{ draft.customUiScalePercent }}%</span><input v-model.number="draft.customUiScalePercent" type="range" min="75" max="200" step="5" :disabled="draft.uiScaleMode !== 'custom'" @input="markDirty" /></label>
        <button class="button primary compact" @click="save">应用界面设置</button>
      </div>
    </section>
    <section class="mechanical-section save-backup-settings">
      <header class="section-heading">
        <div class="heading-with-icon"><Save :size="22" /><div><h2>存档保险库</h2><p>进入新的章节关卡后，后台等待存档稳定再创建快照，不占用游戏主线程。</p></div></div>
        <span class="backup-counter">{{ store.snapshot?.saveBackups?.backupCount ?? 0 }} / {{ draft?.maximumSaveBackups ?? 20 }}</span>
      </header>
      <div v-if="draft" class="backup-controls">
        <label class="switch-row">
          <span><strong>自动备份存档</strong><small>仅备份正式玩家存档；测试存档保持隔离</small></span>
          <input v-model="draft.automaticSaveBackupEnabled" type="checkbox" @change="markDirty" />
        </label>
        <label class="backup-limit" :class="{ disabled: !draft.automaticSaveBackupEnabled }">
          <span>最多保留</span>
          <input v-model.number="draft.maximumSaveBackups" type="number" min="1" max="100" step="1" :disabled="!draft.automaticSaveBackupEnabled" @input="markDirty" />
          <span>个步骤存档</span>
        </label>
        <div class="backup-summary">
          <div class="backup-status">
            <span :class="{ active: draft.automaticSaveBackupEnabled }">{{ store.snapshot?.saveBackups?.busy ? '正在创建安全快照' : store.snapshot?.saveBackups?.pending ? '等待游戏写入稳定' : draft.automaticSaveBackupEnabled ? '自动守护中' : '已关闭' }}</span>
            <p>{{ store.snapshot?.saveBackups?.lastMessage || '备份将按“章节 + 关卡号 + 日期”命名。' }}</p>
          </div>
          <div class="backup-actions">
            <button class="button primary compact" @click="save">保存备份设置</button>
            <button class="button secondary compact" @click="api.openSaveBackups()"><FolderOpen :size="16" />打开备份目录</button>
          </div>
        </div>
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
          <button class="button primary" :disabled="!store.snapshot?.update?.updateAvailable" @click="store.installUpdate()"><Download :size="17" />安装更新</button>
        </div>
      </div>
    </section>
  </div>
</template>
