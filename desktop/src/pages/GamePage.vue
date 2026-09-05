<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted } from 'vue'
import { FolderOpen, ShieldCheck, PlugZap, Plug, Trash2, Play, RefreshCw, MonitorCog } from '../icons'
import { useAppStore } from '../stores/app'
import { useUiStore } from '../stores/ui'

const store = useAppStore()
const ui = useUiStore()
const pluginState = computed(() => store.snapshot?.plugin?.state ?? 'notInstalled')
const pluginStateLabel = computed(() => ({
  enabled: '已启用', disabled: '已停用', incomplete: '需要修复', notInstalled: '未安装',
}[pluginState.value]))
const editorProject = computed(() => store.snapshot?.editorProject)
const editorConnection = computed(() => store.snapshot?.editorConnection)
const editorInstances = computed(() => {
  const selected = editorProject.value?.path.toLowerCase()
  return (store.snapshot?.editorInstances ?? []).filter(instance => !selected || instance.projectPath.toLowerCase() === selected)
})
let editorRefreshTimer: number | undefined

onMounted(() => {
  void store.refreshEditorInstances()
  editorRefreshTimer = window.setInterval(() => void store.refreshEditorInstances(), 2000)
})
onBeforeUnmount(() => {
  if (editorRefreshTimer) window.clearInterval(editorRefreshTimer)
})
</script>

<template>
  <div class="page-grid game-page grid min-h-0">
    <section class="mechanical-section card game-location">
      <header class="section-heading">
        <div><h2>游戏构建</h2><p>只连接经过路径、产品身份和程序集指纹验证的 Skyspine。</p></div>
        <button class="btn btn-outline btn-sm button secondary compact" :disabled="ui.busy" @click="store.refreshConnection"><RefreshCw :size="16" />刷新连接</button>
      </header>
      <div class="path-row">
        <div class="path-display">
          <span>{{ store.snapshot?.game?.gameRoot || '尚未选择游戏目录' }}</span>
          <small v-if="store.snapshot?.game">{{ store.snapshot.game.productVersion }} · {{ store.snapshot.game.assemblySha256.slice(0, 12) }}</small>
        </div>
        <button class="btn btn-primary button primary" :disabled="ui.busy" @click="store.selectGame"><FolderOpen :size="17" />选择目录</button>
      </div>
      <div class="validation-strip" :class="store.snapshot?.game ? 'valid' : 'waiting'">
        <ShieldCheck :size="18" />
        <span>{{ store.snapshot?.game ? '游戏构建与运行时合同已验证' : '选择游戏目录后才能管理插件' }}</span>
      </div>
    </section>

    <section class="card mechanical-section editor-section">
      <header class="section-heading">
        <div class="heading-with-icon"><MonitorCog :size="21" /><div><h2 translate="no">Unity Editor</h2><p>{{ editorConnection?.runtimeReady ? 'Play Mode 运行控制已就绪' : editorConnection?.success ? 'Edit Mode 已连接' : editorProject?.bridgeInstalled ? '连接组件已安装' : '等待选择工程' }}</p></div></div>
        <button v-tooltip="'刷新 Editor 实例'" class="btn btn-square btn-ghost btn-sm icon-button" aria-label="刷新 Editor 实例" :disabled="ui.busy" @click="store.refreshEditorInstances"><RefreshCw :size="17" /></button>
      </header>
      <div class="path-row">
        <div class="path-display">
          <span>{{ editorProject?.path || '尚未选择 Unity 工程' }}</span>
          <small v-if="editorProject?.valid">Unity {{ editorProject.unityVersion }} · {{ editorProject.bridgeInstalled ? '连接组件已安装' : '连接组件未安装' }}</small>
        </div>
        <button class="btn btn-outline button secondary" :disabled="ui.busy" @click="store.selectUnityProject"><FolderOpen :size="17" />选择工程</button>
      </div>
      <div class="editor-bridge-actions">
        <button class="btn btn-primary button primary" :disabled="!editorProject?.valid || ui.busy" @click="store.installEditorBridge"><PlugZap :size="17" />{{ editorProject?.bridgeInstalled ? '更新连接组件' : '安装连接组件' }}</button>
        <button class="btn btn-error btn-sm button danger compact" :disabled="!editorProject?.bridgeInstalled || ui.busy" @click="store.uninstallEditorBridge"><Trash2 :size="16" />卸载连接组件</button>
        <span v-if="editorProject" class="editor-bridge-state" aria-live="polite">{{ editorProject.message }}</span>
      </div>
      <div class="editor-instance-list" aria-live="polite">
        <div v-for="instance in editorInstances" :key="instance.instanceId" class="editor-instance-row">
          <span class="signal-dot" :class="instance.runtimeReady ? 'is-online' : 'is-waiting'" />
          <div class="editor-instance-copy">
            <strong>{{ instance.displayName }}</strong>
            <small>PID {{ instance.processId }} · {{ instance.mode === 'editor-play' ? 'Play Mode' : 'Edit Mode' }} · {{ instance.sceneName || '未打开场景' }}</small>
          </div>
          <button
            v-if="editorConnection?.instanceId !== instance.instanceId"
            class="btn btn-outline btn-sm button secondary compact"
            :disabled="ui.busy"
            @click="store.connectEditor(instance.instanceId)"
          ><Plug :size="16" />连接</button>
          <button v-else class="btn btn-error btn-sm button danger compact" :disabled="ui.busy" @click="store.disconnectEditor">断开</button>
        </div>
        <p v-if="editorInstances.length === 0" class="empty-state">未发现运行中的 Unity Editor 实例。</p>
      </div>
    </section>

    <section class="card mechanical-section plugin-section">
      <header class="section-heading">
        <div><h2>AutoPlayer 插件</h2><p>{{ store.snapshot?.plugin?.detail || '尚未读取插件状态' }}</p></div>
        <span class="status-badge" :class="{ online: pluginState === 'enabled', warning: pluginState === 'incomplete' }">{{ pluginStateLabel }}</span>
      </header>
      <div class="plugin-facts">
        <div><span>插件版本</span><strong>{{ store.snapshot?.plugin?.pluginVersion || '-' }}</strong></div>
        <div><span>BepInEx</span><strong>{{ store.snapshot?.plugin?.bepInExCompatible ? '固定版本已验证' : '未验证' }}</strong></div>
        <div><span>控制通道</span><strong>{{ store.snapshot?.connection.label || '等待 Host' }}</strong></div>
      </div>
      <div class="action-bar">
        <button class="btn btn-primary button primary" :disabled="!store.snapshot?.game || ui.busy" @click="store.installPlugin"><PlugZap :size="17" />安装或修复</button>
        <button
          class="btn btn-outline button secondary"
          :disabled="!store.snapshot?.plugin || pluginState === 'notInstalled' || ui.busy"
          @click="store.setPluginEnabled(pluginState === 'disabled')"
        ><Plug :size="17" />{{ pluginState === 'disabled' ? '启用插件' : '停用插件' }}</button>
        <button class="btn btn-error btn-sm button danger compact" :disabled="pluginState === 'notInstalled' || ui.busy" @click="store.uninstallPlugin"><Trash2 :size="16" />卸载</button>
      </div>
    </section>

    <section class="launch-band" :class="{ ready: pluginState === 'enabled' }">
      <div>
        <span class="signal-dot" :class="store.snapshot?.connection.trusted ? 'is-online' : 'is-waiting'" />
        <div><strong>{{ store.snapshot?.connection.label || '等待连接' }}</strong><small>{{ store.snapshot?.connection.reason || '游戏运行后会自动建立可信连接' }}</small></div>
      </div>
      <button class="btn btn-primary button launch" :disabled="pluginState !== 'enabled' || ui.busy" @click="store.launchGame"><Play :size="18" fill="currentColor" />启动游戏</button>
    </section>
  </div>
</template>
